package com.velocity.downloader.service

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.velocity.downloader.MainActivity
import com.velocity.downloader.R
import com.velocity.downloader.core.BandwidthLimiter
import com.velocity.downloader.core.DownloadEngine
import com.velocity.downloader.core.DownloadRecord
import com.velocity.downloader.core.DownloadStatus
import com.velocity.downloader.core.DownloadStore
import com.velocity.downloader.core.Formats
import com.velocity.downloader.core.Folders
import com.velocity.downloader.core.Http
import com.velocity.downloader.core.OutputStore
import com.velocity.downloader.core.Settings
import com.velocity.downloader.core.UrlAnalyzer
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.util.Collections
import java.util.concurrent.ConcurrentHashMap

/**
 * Keeps transfers running while Velocity is in the background. It owns the queue, the foreground notification and the
 * partial files; every transfer is one coroutine, so pausing stops the bytes without losing what is already on disk
 * and a process death simply re-queues the rows the store kept.
 */
class DownloadService : Service() {

    companion object {
        const val ACTION_ENQUEUE = "com.velocity.downloader.action.ENQUEUE"
        const val ACTION_PAUSE = "com.velocity.downloader.action.PAUSE"
        const val ACTION_RESUME = "com.velocity.downloader.action.RESUME"
        const val ACTION_CANCEL = "com.velocity.downloader.action.CANCEL"
        const val ACTION_RETRY = "com.velocity.downloader.action.RETRY"
        const val ACTION_RESUME_PENDING = "com.velocity.downloader.action.RESUME_PENDING"

        private const val EXTRA_RECORD = "record"
        private const val EXTRA_ID = "id"
        private const val CHANNEL_ONGOING = "velocity-ongoing"
        private const val CHANNEL_DONE = "velocity-done"
        private const val ONGOING_ID = 1001
        private const val MIN_SEGMENTED = 2L * 1024 * 1024

        fun enqueue(context: Context, record: DownloadRecord) = send(context, ACTION_ENQUEUE) {
            it.putExtra(EXTRA_RECORD, record.toJson().toString())
        }

        fun pause(context: Context, id: String) = send(context, ACTION_PAUSE) { it.putExtra(EXTRA_ID, id) }

        fun resume(context: Context, id: String) = send(context, ACTION_RESUME) { it.putExtra(EXTRA_ID, id) }

        fun retry(context: Context, id: String) = send(context, ACTION_RETRY) { it.putExtra(EXTRA_ID, id) }

        fun cancel(context: Context, id: String) = send(context, ACTION_CANCEL) { it.putExtra(EXTRA_ID, id) }

        /** Brings back anything the system stopped while the app was closed, and picks up queued rows. */
        fun resumePending(context: Context) = send(context, ACTION_RESUME_PENDING) { }

        private fun send(context: Context, action: String, fill: (Intent) -> Unit) {
            val intent = Intent(context, DownloadService::class.java).setAction(action)
            fill(intent)
            runCatching { ContextCompat.startForegroundService(context, intent) }
        }
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val jobs = ConcurrentHashMap<String, Job>()
    private val pausedIds = Collections.synchronizedSet(mutableSetOf<String>())
    private val cancelledIds = Collections.synchronizedSet(mutableSetOf<String>())
    private val main = Handler(Looper.getMainLooper())
    private val partsDir by lazy { File(filesDir, "parts").apply { mkdirs() } }
    private val settings by lazy { Settings.get(this) }
    /** A change to the simultaneous-download limit starts waiting rows immediately. */
    private val settingsListener: (String) -> Unit = { main.post { pump() } }

    private var wakeLock: PowerManager.WakeLock? = null
    private var foreground = false
    private var lastNotificationAt = 0L

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        Http.installCookies()
        createChannels()
        settings.addListener(settingsListener)
        val store = DownloadStore.get(this)
        // Anything the previous process left mid-transfer keeps its bytes and goes back into the queue.
        store.list()
            .filter { it.status == DownloadStatus.DOWNLOADING || it.status == DownloadStatus.ANALYZING || it.status == DownloadStatus.QUEUED }
            .forEach { store.update(it.copy(status = DownloadStatus.QUEUED, speedBytesPerSecond = 0)) }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val store = DownloadStore.get(this)
        when (intent?.action) {
            ACTION_ENQUEUE -> {
                val json = intent.getStringExtra(EXTRA_RECORD)
                val record = json?.let { runCatching { DownloadRecord.fromJson(JSONObject(it)) }.getOrNull() }
                if (record != null && store.find(record.id) == null) {
                    // The same link already in the list is not downloaded twice; its existing row keeps working.
                    val duplicate = store.list().any {
                        it.url == record.url && (it.isActive || it.status == DownloadStatus.PAUSED)
                    }
                    if (!duplicate) store.add(record.copy(status = DownloadStatus.QUEUED))
                }
            }
            ACTION_RESUME_PENDING -> {
                store.list()
                    .filter { it.status == DownloadStatus.DOWNLOADING || it.status == DownloadStatus.ANALYZING }
                    .forEach { store.update(it.copy(status = DownloadStatus.QUEUED, speedBytesPerSecond = 0)) }
            }
            ACTION_PAUSE -> intent.getStringExtra(EXTRA_ID)?.let { pause(it) }
            ACTION_RESUME -> intent.getStringExtra(EXTRA_ID)?.let { resume(it, clearError = false) }
            ACTION_RETRY -> intent.getStringExtra(EXTRA_ID)?.let { resume(it, clearError = true) }
            ACTION_CANCEL -> intent.getStringExtra(EXTRA_ID)?.let { cancel(it) }
        }

        startAsForeground()
        pump()
        return START_STICKY
    }

    override fun onDestroy() {
        // The transfers stop with the process, but their rows and partial files stay, so the next start resumes them.
        settings.removeListener(settingsListener)
        scope.cancel()
        releaseWakeLock()
        super.onDestroy()
    }

    /* ─────────────────────────── queue ─────────────────────────── */

    private fun pump() {
        val store = DownloadStore.get(this)
        val waiting = store.list()
            .filter { it.status == DownloadStatus.QUEUED && !jobs.containsKey(it.id) }
            .sortedBy { it.createdAt }

        for (record in waiting) {
            if (jobs.size >= settings.maxConcurrent) break
            startJob(record)
        }

        updateOngoingNotification()
        stopIfIdle()
    }

    private fun startJob(record: DownloadRecord) {
        pausedIds.remove(record.id)
        cancelledIds.remove(record.id)
        acquireWakeLock()
        jobs[record.id] = scope.launch {
            try {
                transfer(record)
            } catch (exception: CancellationException) {
                throw exception
            } catch (exception: Throwable) {
                fail(record.id, exception.message ?: "The download stopped unexpectedly.")
            } finally {
                jobs.remove(record.id)
                main.post {
                    updateOngoingNotification(force = true)
                    pump()
                }
            }
        }
    }

    private suspend fun transfer(initial: DownloadRecord) = withContext(Dispatchers.IO) {
        val store = DownloadStore.get(this@DownloadService)
        var record = store.find(initial.id) ?: initial
        if (record.status == DownloadStatus.PAUSED || cancelledIds.contains(record.id)) return@withContext

        // Everything the link did not tell us is learned here, once per transfer: name, length, range support.
        if (record.totalBytes <= 0 || record.fileName.isBlank()) {
            store.update(record.copy(status = DownloadStatus.ANALYZING))
            val info = try {
                UrlAnalyzer.analyze(record.url)
            } catch (exception: Exception) {
                fail(record.id, exception.message ?: "The link could not be read.")
                return@withContext
            }
            record = record.copy(
                finalUrl = info.finalUrl,
                // The server's own name (Content-Disposition, then the redirected path) beats a guess made from the link.
                fileName = info.fileName.ifBlank { record.fileName },
                totalBytes = info.totalBytes,
                contentType = info.contentType,
                etag = info.etag,
                lastModified = info.lastModified,
                supportsRange = info.supportsRange
            )
            store.update(record)
        }

        if (settings.skipWebPages && record.contentType?.startsWith("text/html", true) == true) {
            fail(
                record.id,
                "Velocity is set not to download web pages. Turn off \"Do not download web pages\" in Downloads settings to save this link."
            )
            return@withContext
        }

        val part = partFile(record.id)
        val connections = if (record.supportsRange && record.totalBytes >= MIN_SEGMENTED) 4 else 1
        BandwidthLimiter.bytesPerSecond = settings.speedLimit
        val engine = DownloadEngine(part, metaFile = File(partsDir, "${record.id}.meta"), connections = connections)

        var speed = record.speedBytesPerSecond
        var sampleAt = System.currentTimeMillis()
        var sampleBytes = record.downloadedBytes

        store.update(record.copy(status = DownloadStatus.DOWNLOADING, error = null, connections = connections, speedBytesPerSecond = 0))

        val outcome = engine.run(
            url = record.url,
            expectedTotal = record.totalBytes,
            expectedEtag = record.etag,
            savedSegments = record.segments,
            isPaused = { pausedIds.contains(record.id) || cancelledIds.contains(record.id) }
        ) { downloaded, usedConnections ->
            val now = System.currentTimeMillis()
            val elapsed = now - sampleAt
            if (elapsed >= 700) {
                val instant = if (elapsed > 0) (downloaded - sampleBytes) * 1000 / elapsed else 0
                speed = if (speed <= 0) instant.coerceAtLeast(0) else (speed * 2 + instant) / 3
                sampleAt = now
                sampleBytes = downloaded
            }
            store.update(
                record.copy(
                    downloadedBytes = downloaded,
                    speedBytesPerSecond = speed,
                    connections = usedConnections,
                    status = DownloadStatus.DOWNLOADING
                )
            )
            updateOngoingNotification()
        }

        if (cancelledIds.remove(record.id)) {
            deleteParts(record.id)
            store.remove(record.id)
            return@withContext
        }

        when {
            outcome.completed -> finish(record, outcome)
            outcome.paused -> {
                store.update(
                    record.copy(
                        status = DownloadStatus.PAUSED,
                        downloadedBytes = outcome.downloadedBytes,
                        totalBytes = if (outcome.totalBytes > 0) outcome.totalBytes else record.totalBytes,
                        segments = outcome.segments,
                        speedBytesPerSecond = 0
                    )
                )
            }
            else -> fail(record.id, outcome.error ?: "The download stopped.")
        }
    }

    private suspend fun finish(record: DownloadRecord, outcome: DownloadEngine.Outcome) = withContext(Dispatchers.IO) {
        val store = DownloadStore.get(this@DownloadService)
        val part = partFile(record.id)
        val size = if (outcome.downloadedBytes > 0) outcome.downloadedBytes else part.length()
        val finished = record.copy(
            totalBytes = if (outcome.totalBytes > 0) outcome.totalBytes else size,
            downloadedBytes = size,
            etag = outcome.etag ?: record.etag,
            lastModified = outcome.lastModified ?: record.lastModified,
            contentType = outcome.contentType ?: record.contentType,
            supportsRange = record.supportsRange || outcome.supportsRange,
            segments = emptyList(),
            status = DownloadStatus.DOWNLOADING,
            speedBytesPerSecond = 0,
            error = null
        )
        store.update(finished)
        try {
            val uri = OutputStore.save(
                this@DownloadService,
                finished,
                part,
                Folders.subfolder(settings, finished),
                settings.useServerTime
            )
            deleteParts(record.id)
            val completed = finished.copy(
                status = DownloadStatus.COMPLETED,
                savedUri = uri.toString(),
                completedAt = System.currentTimeMillis()
            )
            store.update(completed)
            notifyCompleted(completed)
            // "Automatically remove completed downloads from download list": the file stays, the row goes.
            if (settings.removeCompleted) store.remove(completed.id)
        } catch (exception: Exception) {
            fail(record.id, "The finished file could not be saved: ${exception.message}")
        }
    }

    private fun fail(id: String, message: String) {
        val store = DownloadStore.get(this)
        val record = store.find(id) ?: return
        store.update(record.copy(status = DownloadStatus.FAILED, error = message, speedBytesPerSecond = 0))
    }

    /* ─────────────────────────── user actions ─────────────────────────── */

    private fun pause(id: String) {
        val store = DownloadStore.get(this)
        val record = store.find(id) ?: return
        pausedIds.add(id)
        if (record.status == DownloadStatus.QUEUED) {
            store.update(record.copy(status = DownloadStatus.PAUSED))
        }
    }

    private fun resume(id: String, clearError: Boolean) {
        val store = DownloadStore.get(this)
        val record = store.find(id) ?: return
        if (record.status == DownloadStatus.COMPLETED) return
        pausedIds.remove(id)
        cancelledIds.remove(id)
        store.update(
            record.copy(
                status = DownloadStatus.QUEUED,
                error = if (clearError) null else record.error,
                speedBytesPerSecond = 0
            )
        )
    }

    private fun cancel(id: String) {
        val store = DownloadStore.get(this)
        val record = store.find(id) ?: return
        cancelledIds.add(id)
        pausedIds.add(id)
        if (!jobs.containsKey(id)) {
            deleteParts(id)
            store.remove(id)
            cancelledIds.remove(id)
        }
        updateOngoingNotification(force = true)
        main.post { stopIfIdle() }
    }

    /* ─────────────────────────── notifications ─────────────────────────── */

    private fun createChannels() {
        val manager = getSystemService(NotificationManager::class.java) ?: return
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_ONGOING, getString(R.string.channel_downloads), NotificationManager.IMPORTANCE_LOW).apply {
                description = "Transfers Velocity is running"
                setShowBadge(false)
            }
        )
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_DONE, getString(R.string.channel_complete), NotificationManager.IMPORTANCE_DEFAULT).apply {
                description = "Downloads that have finished"
            }
        )
    }

    private fun startAsForeground() {
        if (foreground) return
        ServiceCompat.startForeground(
            this,
            ONGOING_ID,
            buildOngoingNotification(),
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC else 0
        )
        foreground = true
    }

    private fun updateOngoingNotification(force: Boolean = false) {
        if (!foreground) return
        val now = System.currentTimeMillis()
        if (!force && now - lastNotificationAt < 700) return
        lastNotificationAt = now
        getSystemService(NotificationManager::class.java)?.notify(ONGOING_ID, buildOngoingNotification())
    }

    private fun buildOngoingNotification() = run {
        val store = DownloadStore.get(this)
        val active = store.list().filter { it.isActive }
        val running = active.firstOrNull { it.status == DownloadStatus.DOWNLOADING } ?: active.firstOrNull()
        val title = when {
            active.isEmpty() -> "Velocity"
            active.size == 1 && running != null -> running.fileName
            else -> "${active.size} downloads"
        }
        val text = when {
            running == null -> "Preparing the download…"
            running.status == DownloadStatus.ANALYZING -> getString(R.string.analyzing)
            running.totalBytes > 0 -> "${running.progress}%  •  ${Formats.bytes(running.downloadedBytes)} / ${Formats.bytes(running.totalBytes)}"
            running.downloadedBytes > 0 -> Formats.bytes(running.downloadedBytes)
            else -> "Starting…"
        }
        val builder = NotificationCompat.Builder(this, CHANNEL_ONGOING)
            .setSmallIcon(R.drawable.ic_stat_velocity)
            .setContentTitle(title)
            .setContentText(text)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setShowWhen(false)
            .setContentIntent(openAppIntent())
        if (running != null && running.isActive) {
            builder.setProgress(100, running.progress, running.totalBytes <= 0)
            builder.addAction(
                0,
                if (running.status == DownloadStatus.DOWNLOADING || running.status == DownloadStatus.QUEUED) "Pause" else "Resume",
                actionIntent(ONGOING_ID + 1, if (running.status == DownloadStatus.DOWNLOADING || running.status == DownloadStatus.QUEUED) ACTION_PAUSE else ACTION_RESUME, running.id)
            )
            builder.addAction(0, "Cancel", actionIntent(ONGOING_ID + 2, ACTION_CANCEL, running.id))
        } else {
            builder.setProgress(0, 0, true)
        }
        builder.build()
    }

    private fun notifyCompleted(record: DownloadRecord) {
        val manager = getSystemService(NotificationManager::class.java) ?: return
        val notification = NotificationCompat.Builder(this, CHANNEL_DONE)
            .setSmallIcon(R.drawable.ic_stat_velocity)
            .setContentTitle("Download complete")
            .setContentText(record.fileName)
            .setStyle(NotificationCompat.BigTextStyle().bigText("${record.fileName}\n${Formats.bytes(record.totalBytes)}"))
            .setAutoCancel(true)
            .setContentIntent(openAppIntent())
            .build()
        manager.notify(record.id.hashCode(), notification)
    }

    private fun openAppIntent(): PendingIntent {
        val intent = Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        return PendingIntent.getActivity(this, 0, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
    }

    private fun actionIntent(requestCode: Int, action: String, id: String): PendingIntent {
        val intent = Intent(this, DownloadService::class.java).setAction(action).putExtra(EXTRA_ID, id)
        return PendingIntent.getService(this, requestCode, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
    }

    private fun stopIfIdle() {
        val store = DownloadStore.get(this)
        if (jobs.isEmpty() && store.list().none { it.isActive }) {
            releaseWakeLock()
            if (foreground) {
                ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
                foreground = false
            }
            stopSelf()
        }
    }

    private fun partFile(id: String) = File(partsDir, "$id.part")

    private fun deleteParts(id: String) {
        partFile(id).delete()
        File(partsDir, "$id.meta").delete()
    }

    private fun acquireWakeLock() {
        if (wakeLock?.isHeld == true) return
        val manager = getSystemService(PowerManager::class.java) ?: return
        wakeLock = manager.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "velocity:downloads").apply {
            setReferenceCounted(false)
            acquire(60L * 60L * 1000L)
        }
    }

    private fun releaseWakeLock() {
        runCatching { wakeLock?.takeIf { it.isHeld }?.release() }
        wakeLock = null
    }
}
