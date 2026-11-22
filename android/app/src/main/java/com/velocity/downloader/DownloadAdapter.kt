package com.velocity.downloader

import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.ProgressBar
import android.widget.TextView
import androidx.recyclerview.widget.RecyclerView
import com.velocity.downloader.core.DownloadRecord
import com.velocity.downloader.core.DownloadStatus
import com.velocity.downloader.core.Formats

enum class DownloadAction { PAUSE, RESUME, RETRY, CANCEL, REMOVE, OPEN }

/** Renders the list and turns each row's two buttons into the action that fits its current state. */
class DownloadAdapter(private val onAction: (DownloadRecord, DownloadAction) -> Unit) :
    RecyclerView.Adapter<DownloadAdapter.Row>() {

    private var items: List<DownloadRecord> = emptyList()

    fun submit(records: List<DownloadRecord>) {
        items = records.sortedByDescending { it.createdAt }
        notifyDataSetChanged()
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): Row {
        val view = LayoutInflater.from(parent.context).inflate(R.layout.item_download, parent, false)
        return Row(view)
    }

    override fun getItemCount(): Int = items.size

    override fun onBindViewHolder(holder: Row, position: Int) {
        holder.bind(items[position])
    }

    inner class Row(view: View) : RecyclerView.ViewHolder(view) {
        private val name: TextView = view.findViewById(R.id.name)
        private val status: TextView = view.findViewById(R.id.status)
        private val progress: ProgressBar = view.findViewById(R.id.progress)
        private val meta: TextView = view.findViewById(R.id.meta)
        private val primary: Button = view.findViewById(R.id.primaryAction)
        private val secondary: Button = view.findViewById(R.id.secondaryAction)

        fun bind(item: DownloadRecord) {
            name.text = item.fileName

            val percent = if (item.totalBytes > 0) "${item.progress}%" else ""
            val sizes = when {
                item.totalBytes > 0 -> "${Formats.bytes(item.downloadedBytes)} / ${Formats.bytes(item.totalBytes)}"
                item.downloadedBytes > 0 -> Formats.bytes(item.downloadedBytes)
                else -> ""
            }

            status.text = when (item.status) {
                DownloadStatus.QUEUED -> "Queued"
                DownloadStatus.ANALYZING -> "Analysing the link…"
                DownloadStatus.DOWNLOADING -> listOfNotNull(
                    percent,
                    sizes,
                    Formats.speed(item.speedBytesPerSecond).takeIf { it.isNotEmpty() },
                    Formats.remaining(Formats.eta(item.downloadedBytes, item.totalBytes, item.speedBytesPerSecond)).takeIf { it.isNotEmpty() }
                ).joinToString("  •  ").ifBlank { "Downloading…" }
                DownloadStatus.PAUSED -> "Paused" + if (sizes.isNotEmpty()) "  •  $sizes" else ""
                DownloadStatus.COMPLETED -> "Completed  •  " + Formats.bytes(if (item.totalBytes > 0) item.totalBytes else item.downloadedBytes)
                DownloadStatus.FAILED -> item.error ?: "Failed"
                DownloadStatus.CANCELLED -> "Cancelled"
            }

            meta.text = when {
                item.status == DownloadStatus.DOWNLOADING && item.totalBytes > 0 ->
                    "${item.progress}%  •  " + Formats.speed(item.speedBytesPerSecond)
                item.status == DownloadStatus.DOWNLOADING && item.totalBytes <= 0 -> "Size unknown to the server"
                item.totalBytes > 0 -> Formats.bytes(item.totalBytes)
                else -> ""
            }

            progress.progress = when (item.status) {
                DownloadStatus.COMPLETED -> 100
                else -> item.progress
            }
            progress.isIndeterminate = item.status == DownloadStatus.DOWNLOADING && item.totalBytes <= 0

            when (item.status) {
                DownloadStatus.QUEUED, DownloadStatus.ANALYZING, DownloadStatus.DOWNLOADING -> {
                    primary.visibleText("Pause")
                    primary.isEnabled = item.status == DownloadStatus.DOWNLOADING
                    primary.setOnClickListener { onAction(item, DownloadAction.PAUSE) }
                    secondary.visibleText("Cancel")
                    secondary.setOnClickListener { onAction(item, DownloadAction.CANCEL) }
                }
                DownloadStatus.PAUSED -> {
                    primary.visibleText("Resume")
                    primary.isEnabled = true
                    primary.setOnClickListener { onAction(item, DownloadAction.RESUME) }
                    secondary.visibleText("Cancel")
                    secondary.setOnClickListener { onAction(item, DownloadAction.CANCEL) }
                }
                DownloadStatus.FAILED -> {
                    primary.visibleText("Retry")
                    primary.isEnabled = true
                    primary.setOnClickListener { onAction(item, DownloadAction.RETRY) }
                    secondary.visibleText("Remove")
                    secondary.setOnClickListener { onAction(item, DownloadAction.REMOVE) }
                }
                DownloadStatus.COMPLETED -> {
                    primary.visibleText("Open")
                    primary.isEnabled = true
                    primary.setOnClickListener { onAction(item, DownloadAction.OPEN) }
                    secondary.visibleText("Remove")
                    secondary.setOnClickListener { onAction(item, DownloadAction.REMOVE) }
                }
                DownloadStatus.CANCELLED -> {
                    primary.visibleText("Retry")
                    primary.isEnabled = true
                    primary.setOnClickListener { onAction(item, DownloadAction.RETRY) }
                    secondary.visibleText("Remove")
                    secondary.setOnClickListener { onAction(item, DownloadAction.REMOVE) }
                }
            }
        }

        private fun Button.visibleText(label: String) {
            text = label
            visibility = View.VISIBLE
        }
    }
}
