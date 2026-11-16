using Velocity.Core.Interfaces;
using Velocity.Core.Models;
using Velocity.Data.Repositories;

namespace Velocity.Services;

public sealed class DownloadManager(IDownloadEngine engine, DownloadRepository repository, SettingsService settings)
{
    private readonly object _gate = new();
    private readonly List<DownloadItem> _queue = [];
    private readonly Dictionary<long, Task> _running = [];
    private readonly Dictionary<long, SemaphoreSlim> _persistGates = [];
    private readonly HashSet<long> _deleted = [];
    // The file each running download owns. Two downloads of the same name are two different files; without an
    // owner per path they write the same .part, the second one dies on the share violation, and the survivor is a
    // mix of both transfers. That is what made several simultaneous downloads look like they "fail".
    private readonly Dictionary<string, long> _destinationOwners = new(StringComparer.OrdinalIgnoreCase);
    private int _active;

    /// <summary>Raised for every download added to the manager, including scheduled and browser captures.</summary>
    public event EventHandler<DownloadItem>? ItemRegistered;

    public async Task<IReadOnlyList<DownloadRecord>> LoadHistoryAsync(CancellationToken cancellationToken = default) => await repository.GetRecentAsync(cancellationToken);

    public DownloadItem CreateHistoryItem(DownloadRecord record)
    {
        // The stored file name and connection count are part of the resume contract: the engine must look for
        // exactly the same .part file the previous session was writing.
        var directory = Path.GetDirectoryName(record.DestinationPath) ?? string.Empty;
        var item = new DownloadItem(record.Id, new DownloadRequest(record.Url, directory, record.FileName, settings.Current.DefaultConnectionCount, Headers: record.Headers))
        {
            Priority = record.Priority,
            DestinationPath = record.DestinationPath,
            ETag = record.ETag,
            LastModified = record.LastModified,
            FinalUrl = record.FinalUrl,
            ResumeSupported = record.ResumeSupported
        };
        item.FileName = record.FileName;
        item.DownloadedBytes = record.DownloadedBytes;
        item.TotalBytes = record.TotalBytes;
        // Any stored value means the user chose something for this file: a positive ceiling, or -1 for "no limit".
        item.SpeedLimitIsExplicit = record.SpeedLimitBytesPerSecond != 0;
        item.SpeedLimitKilobytesPerSecond = Math.Max(0, record.SpeedLimitBytesPerSecond / 1024);
        // Transfers cannot survive an application restart, so anything left "running" is shown as paused and can be resumed.
        item.WasActive = record.Status is DownloadStatus.Downloading or DownloadStatus.Queued or DownloadStatus.Resuming;
        item.Status = item.WasActive ? DownloadStatus.Paused : record.Status;

        // The database is only a summary; the partial file is the truth about how many bytes survived a restart.
        SyncBytesFromPartial(item);

        return item;
    }

    /// <summary>
    /// Re-queues every download that was still transferring when the previous session ended. The partial file
    /// and its checkpoint decide where each transfer continues from; nothing restarts at zero here.
    /// </summary>
    public void ResumeInterrupted(IEnumerable<DownloadItem> items)
    {
        foreach (var item in items)
        {
            if (!item.WasActive || item.Status != DownloadStatus.Paused) continue;
            item.PauseRequested = false;
            item.Status = DownloadStatus.Resuming;
            lock (_gate) if (!_queue.Contains(item)) _queue.Add(item);
        }

        ProcessQueue();
    }

    /// <summary>Applies an IDM-style per-download speed ceiling. 0 follows the app-wide setting.</summary>
    public void SetSpeedLimit(DownloadItem item, int kilobytesPerSecond)
    {
        var clamped = Math.Clamp(kilobytesPerSecond, 0, 102400);
        // The choice is remembered as this file's own: "0" now means full speed, not "follow the app-wide limit",
        // because the user picked it here.
        item.SpeedLimitIsExplicit = true;
        item.SpeedLimitKilobytesPerSecond = clamped;

        // The ceiling is a live object shared with the engine, so a running transfer picks the new value (or no
        // value at all) on its next buffer. Nothing is cancelled, restarted or re-queued: removing a limit goes
        // straight back to full speed.
        item.LiveLimit.Set(EffectiveLimitBytesPerSecond(item));
        _ = PersistAsync(item);
    }

    /// <summary>
    /// Re-reads the app-wide ceiling for every download, so changing it in Settings also changes the transfers that
    /// are already running instead of only the ones started afterwards.
    /// </summary>
    public void RefreshSpeeds(IEnumerable<DownloadItem> items)
    {
        foreach (var item in items) item.LiveLimit.Set(EffectiveLimitBytesPerSecond(item));
    }

    public Task<DownloadItem> StartAsync(DownloadRequest request, CancellationToken cancellationToken = default) =>
        AddAsync(request, startImmediately: true, cancellationToken);

    public Task<DownloadItem> QueueAsync(DownloadRequest request, CancellationToken cancellationToken = default) =>
        AddAsync(request, startImmediately: false, cancellationToken);

    private async Task<DownloadItem> AddAsync(DownloadRequest request, bool startImmediately, CancellationToken cancellationToken)
    {
        var item = new DownloadItem(0, request);
        item.Id = await repository.AddAsync(
            new DownloadRecord(0, item.FileName, item.Url, Path.Combine(request.DestinationDirectory, item.FileName), null, 0, DownloadStatus.Queued,
                SpeedLimitBytesPerSecond: (int)Math.Clamp(request.SpeedLimitBytesPerSecond, 0, int.MaxValue),
                Headers: request.Headers),
            cancellationToken);

        if (startImmediately)
        {
            lock (_gate) _queue.Add(item);
        }
        else
        {
            item.PauseRequested = true;
            item.Status = DownloadStatus.Paused;
        }

        ItemRegistered?.Invoke(this, item);

        if (startImmediately) ProcessQueue();
        _ = PersistAsync(item);

        return item;
    }

    public void Pause(DownloadItem item)
    {
        // A download that is waiting for a free slot (queued, or resumed but not yet started) pauses here;
        // it has no running task to notice the request.
        if (item.Status is DownloadStatus.Queued or DownloadStatus.Resuming)
        {
            lock (_gate) _queue.Remove(item);
            item.PauseRequested = true;
            item.Status = DownloadStatus.Paused;
            _ = PersistAsync(item);
            return;
        }

        if (item.Status != DownloadStatus.Downloading) return;
        item.PauseRequested = true;
        RequestStop(item);
    }

    public void Resume(DownloadItem item)
    {
        if (item.Status is not (DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Cancelled)) return;
        item.PauseRequested = false;
        item.Error = null;
        item.Attempts = 0;
        item.Status = DownloadStatus.Resuming;
        lock (_gate)
        {
            if (!_queue.Contains(item)) _queue.Add(item);
        }
        ProcessQueue();
    }

    /// <summary>
    /// Stops every transfer that can still run, exactly like pausing them one by one: partial files and
    /// checkpoints are kept, so resuming continues from the last byte rather than starting over.
    /// </summary>
    public void PauseAll(IEnumerable<DownloadItem> items)
    {
        foreach (var item in items.ToList()) Pause(item);
    }

    /// <summary>Restarts everything that was paused. Failed transfers are left alone: they need a real retry.</summary>
    public void ResumeAll(IEnumerable<DownloadItem> items)
    {
        foreach (var item in items.ToList())
        {
            if (item.Status == DownloadStatus.Paused) Resume(item);
        }
    }

    /// <summary>
    /// Moves the given downloads ahead of everything else that is waiting, keeping their own relative order.
    /// Only items that are still waiting are touched; a running transfer keeps its slot.
    /// </summary>
    public void Prioritize(IReadOnlyList<DownloadItem> items)
    {
        var waiting = items.Where(item => item.Status is DownloadStatus.Queued or DownloadStatus.Resuming or DownloadStatus.Paused).ToList();
        if (waiting.Count == 0) return;

        int ceiling;
        lock (_gate) ceiling = _queue.Count == 0 ? 0 : _queue.Max(item => item.Priority);
        var next = Math.Max(ceiling, waiting.Max(item => item.Priority)) + waiting.Count;
        foreach (var item in waiting)
        {
            item.Priority = next--;
            _ = PersistAsync(item);
        }

        ProcessQueue();
    }

    public void Retry(DownloadItem item)
    {
        item.Attempts = 0;
        Resume(item);
    }

    public void Cancel(DownloadItem item)
    {
        lock (_gate) _queue.Remove(item);
        item.PauseRequested = false;
        RequestStop(item);
        // A queued or paused download has no running task to notice the cancellation, so the state must change here.
        if (item.Status is DownloadStatus.Queued or DownloadStatus.Resuming or DownloadStatus.Paused) item.Status = DownloadStatus.Cancelled;
        _ = PersistAsync(item);
    }

    /// <summary>Keeps the active-download limit in sync after settings change.</summary>
    public void RefreshQueue() => ProcessQueue();

    /// <summary>
    /// Deletes partial files and their checkpoints that no download in the list owns — what a cancelled, deleted or
    /// failed transfer leaves behind. They are never read again, so keeping them would only litter the download
    /// folder. A paused transfer owns its own files and is left alone.
    /// </summary>
    public int CleanupAbandonedPartials(IEnumerable<DownloadItem> items)
    {
        var owned = new HashSet<string>(
            items.Where(item => !string.IsNullOrWhiteSpace(item.DestinationPath)).Select(item => item.DestinationPath + ".part"),
            StringComparer.OrdinalIgnoreCase);

        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(settings.Current.DefaultDownloadDirectory)) folders.Add(settings.Current.DefaultDownloadDirectory);
        foreach (var item in items)
        {
            var folder = Path.GetDirectoryName(item.DestinationPath);
            if (!string.IsNullOrWhiteSpace(folder)) folders.Add(folder);
        }

        var removed = 0;
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;

            string[] files;
            try { files = Directory.GetFiles(folder, "*.part*"); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }

            foreach (var path in files)
            {
                var partial = PartialOwner(path);
                if (partial is null || owned.Contains(partial)) continue;
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A file another process holds is simply retried on the next start.
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// The ownable partial path a file belongs to: <c>movie.mkv.part</c>, <c>movie.mkv.part.meta</c>,
    /// <c>movie.mkv.part.meta.prev</c> and <c>movie.mkv.part.meta.tmp</c> all belong to <c>movie.mkv.part</c>.
    /// </summary>
    private static string? PartialOwner(string path)
    {
        var value = path;
        foreach (var suffix in new[] { ".meta.prev", ".meta.tmp", ".meta" })
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) value = value[..^suffix.Length];
        }
        return value.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ? value : null;
    }

    /// <summary>Removes a download from the queue and history. Returns a message when the file on disk could not be deleted.</summary>
    public async Task<string?> DeleteAsync(DownloadItem item, bool deleteFile, CancellationToken cancellationToken = default)
    {
        Task? running;
        lock (_gate)
        {
            _deleted.Add(item.Id);
            _queue.Remove(item);
            running = _running.GetValueOrDefault(item.Id);
        }

        item.PauseRequested = false;
        item.CancellationSource?.Cancel();
        if (running is not null)
        {
            try { await running.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }

        string? fileError = null;
        if (deleteFile)
        {
            foreach (var path in new[] { item.DestinationPath, item.DestinationPath + ".part", item.DestinationPath + ".part.meta", item.DestinationPath + ".part.meta.prev", item.DestinationPath + ".part.meta.tmp" })
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    fileError = $"The file could not be deleted from disk: {exception.Message}";
                }
            }
        }

        try
        {
            await repository.DeleteAsync(item.Id, cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                _deleted.Remove(item.Id);
                _persistGates.Remove(item.Id);
            }
        }

        return fileError;
    }

    private void ProcessQueue()
    {
        while (true)
        {
            DownloadItem? next = null;
            lock (_gate)
            {
                // 0 means "as many at once as the user starts": no artificial ceiling.
                var ceiling = settings.Current.MaximumActiveDownloads;
                if (ceiling > 0 && _active >= ceiling) return;

                var deferred = new List<DownloadItem>();
                while (_queue.Count > 0 && next is null)
                {
                    // Highest priority first; equal priorities keep the order they were added in.
                    var best = 0;
                    for (var index = 1; index < _queue.Count; index++)
                    {
                        if (_queue[index].Priority > _queue[best].Priority) best = index;
                    }

                    var candidate = _queue[best];
                    _queue.RemoveAt(best);

                    // Cancelled, deleted or paused items are dropped for good; a queued item and a resumed
                    // item are both waiting for a slot.
                    if (candidate.Status is not (DownloadStatus.Queued or DownloadStatus.Resuming) || _deleted.Contains(candidate.Id)) continue;

                    // An item whose previous task is still unwinding must not be started twice (two writers on
                    // one file), and must not be dropped either: keep it and retry once that task has finished.
                    if (_running.ContainsKey(candidate.Id))
                    {
                        deferred.Add(candidate);
                        continue;
                    }

                    next = candidate;
                }

                if (deferred.Count > 0) _queue.InsertRange(0, deferred);
                if (next is null) return;
                ClaimDestination(next);
                _active++;
            }

            var task = RunAsync(next);
            lock (_gate) _running[next.Id] = task;
            // The entry must disappear as soon as the transfer is over, otherwise a later resume of the same
            // item would look like it is already running and never take a slot. The scan is repeated here
            // because a deferred item was waiting for exactly this moment.
            _ = task.ContinueWith(_ =>
            {
                lock (_gate) _running.Remove(next.Id);
                ProcessQueue();
            }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Gives a starting download the exclusive right to its destination file, renaming it to "name (2).ext" when
    /// another download already owns that path. Called while the queue lock is held, so two downloads can never
    /// claim the same name.
    /// </summary>
    private void ClaimDestination(DownloadItem item)
    {
        var requested = item.DestinationPath;
        var path = requested;
        for (var index = 2; IsDestinationTaken(path, item.Id); index++)
            path = NumberedPath(requested, index);

        if (!string.Equals(path, requested, StringComparison.Ordinal))
        {
            item.DestinationPath = path;
            item.FileName = Path.GetFileName(path);
            item.Request = item.Request with { FileName = item.FileName };
            item.Notice = $"Another download already used that name, so this one was saved as {item.FileName}.";
        }

        _destinationOwners[path] = item.Id;
    }

    private bool IsDestinationTaken(string path, long itemId) =>
        _destinationOwners.TryGetValue(path, out var owner) && owner != itemId;

    /// <summary><c>folder\movie.mkv</c> becomes <c>folder\movie (2).mkv</c>; the extension stays where it was.</summary>
    private static string NumberedPath(string path, int index)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        return Path.Combine(directory, $"{stem} ({index}){extension}");
    }

    /// <summary>Hands the destination back once the transfer is over, so a later download may use the name.</summary>
    private void ReleaseDestination(DownloadItem item)
    {
        var path = item.DestinationPath;
        if (_destinationOwners.TryGetValue(path, out var owner) && owner == item.Id) _destinationOwners.Remove(path);
    }

    private async Task RunAsync(DownloadItem item)
    {
        using var cts = new CancellationTokenSource();
        item.CancellationSource = cts;
        item.Status = DownloadStatus.Downloading;
        item.Error = null;
        try
        {
            var lastPersist = DateTime.MinValue;
            var progress = new Progress<DownloadProgress>(value =>
            {
                // Progress callbacks are delivered asynchronously, so a report can arrive after the transfer was
                // paused or cancelled and would otherwise overwrite the final byte count with a stale one.
                if (item.Status is not DownloadStatus.Downloading) return;

                // The engine may pick the file name from Content-Disposition on the first response. The new path is
                // claimed exactly like the first one, so a server-chosen name cannot land on another download's file.
                if (!string.IsNullOrWhiteSpace(value.FilePath) && !string.Equals(value.FilePath, item.DestinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    lock (_gate)
                    {
                        ReleaseDestination(item);
                        item.DestinationPath = value.FilePath;
                        item.FileName = Path.GetFileName(value.FilePath);
                        item.Request = item.Request with { FileName = item.FileName };
                        ClaimDestination(item);
                    }
                }

                if (value.DownloadedBytes < item.DownloadedBytes && item.DownloadedBytes > 0 && value.DownloadedBytes == 0)
                    item.Notice = "This server does not support resuming, so the download restarted from the beginning.";

                item.DownloadedBytes = value.DownloadedBytes;
                item.TotalBytes = value.TotalBytes;
                item.BytesPerSecond = value.BytesPerSecond;
                if ((DateTime.UtcNow - lastPersist).TotalSeconds < 3) return;
                lastPersist = DateTime.UtcNow;
                _ = PersistAsync(item);
            });

            var limit = EffectiveLimitBytesPerSecond(item);
            // The ceiling is handed to the engine as a live object, not a number: a change made while this
            // transfer runs is read on its next buffer.
            item.LiveLimit.Set(limit);
            // Keep the request pointed at the same file so a resume never starts a second partial copy.
            item.Request = item.Request with { FileName = item.FileName };
            var result = await engine.DownloadAsync(
                item.Request with { SpeedLimitBytesPerSecond = limit, LiveLimit = item.LiveLimit },
                progress,
                cts.Token);

            // The file stays exactly where the user pointed the download. No category folder, no per-extension
            // subdirectory: Velocity creates a folder only when it has to write a file into one.
            var finalPath = result.FilePath;
            item.DestinationPath = finalPath;
            item.FileName = Path.GetFileName(finalPath);
            item.DownloadedBytes = result.DownloadedBytes;
            item.TotalBytes = result.TotalBytes;
            item.FinalUrl = result.FinalUrl;
            item.ETag = result.ETag;
            item.LastModified = result.LastModified;
            item.ResumeSupported = result.ResumeSupported;
            item.Notice = null;
            item.Attempts = 0;
            item.Status = DownloadStatus.Completed;
            await PersistAsync(item);
        }
        catch (OperationCanceledException)
        {
            item.Status = item.PauseRequested ? DownloadStatus.Paused : DownloadStatus.Cancelled;
            // Progress reports are throttled, so bytes written after the last report would otherwise be lost
            // from the display (the engine still resumes from the true file length).
            if (item.PauseRequested) SyncBytesFromPartial(item);
            if (!IsDeleted(item)) await PersistAsync(item);
        }
        catch (Exception exception)
        {
            item.Error = exception.Message;
            var retryable = exception is not ArgumentException;
            if (retryable && !IsDeleted(item) && item.Attempts < Math.Max(0, settings.Current.RetryLimit))
            {
                // A host that refuses (or rate-limits) many parallel connections fails the segmented attempt even
                // though the same file arrives happily over one. Each retry halves the connections, so several
                // downloads from one server still finish instead of ending up as failed rows.
                if (item.Request.Connections > 1)
                    item.Request = item.Request with { Connections = Math.Max(1, item.Request.Connections / 2) };

                item.Attempts++;
                item.Status = DownloadStatus.Queued;
                await PersistAsync(item);

                // Give the slot back and queue the retry after a short backoff: a failing host must not
                // starve the other downloads of their active slots.
                var delay = TimeSpan.FromSeconds(Math.Min(5, item.Attempts));
                _ = Task.Delay(delay).ContinueWith(_ =>
                {
                    if (IsDeleted(item)) return;
                    lock (_gate) _queue.Add(item);
                    ProcessQueue();
                }, TaskScheduler.Default);
            }
            else
            {
                item.Status = DownloadStatus.Failed;
                if (!IsDeleted(item)) await PersistAsync(item);
            }
        }
        finally
        {
            item.CancellationSource = null;
            lock (_gate)
            {
                ReleaseDestination(item);
                _active--;
            }
            ProcessQueue();
        }
    }

    /// <summary>
    /// Saves the download's current state. Writes for one item are serialised and read the state at write
    /// time, so a progress save queued a moment before completion can never land afterwards and leave the
    /// history row saying "Downloading" for a finished file.
    /// </summary>
    private async Task PersistAsync(DownloadItem item)
    {
        var gate = PersistGate(item.Id);
        try
        {
            await gate.WaitAsync();
            await repository.UpdateAsync(new DownloadRecord(
                item.Id, item.FileName, item.Url, item.DestinationPath, item.TotalBytes, item.DownloadedBytes, item.Status,
                item.FinalUrl, item.ETag, item.LastModified, item.ResumeSupported,
                // -1 records "the user chose no limit for this file", which is different from "not chosen yet" (0).
                item.SpeedLimitIsExplicit
                    ? item.SpeedLimitKilobytesPerSecond > 0 ? item.SpeedLimitKilobytesPerSecond * 1024 : -1
                    : 0,
                item.Request.Headers, item.Priority));
        }
        catch (Exception)
        {
            // Progress persistence is best-effort; a database hiccup must never abort a transfer.
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim PersistGate(long id)
    {
        lock (_gate)
        {
            if (!_persistGates.TryGetValue(id, out var gate)) _persistGates[id] = gate = new SemaphoreSlim(1, 1);
            return gate;
        }
    }

    private bool IsDeleted(DownloadItem item)
    {
        lock (_gate) return _deleted.Contains(item.Id);
    }

    /// <summary>
    /// The ceiling actually given to the engine: the download's own limit when the user set one, otherwise
    /// the app-wide setting. 0 means unlimited.
    /// </summary>
    private long EffectiveLimitBytesPerSecond(DownloadItem item)
    {
        var kilobytes = item.SpeedLimitIsExplicit
            ? Math.Max(0, item.SpeedLimitKilobytesPerSecond)
            : item.SpeedLimitKilobytesPerSecond > 0
                ? item.SpeedLimitKilobytesPerSecond
                : Math.Max(0, settings.Current.GlobalSpeedLimitKilobytesPerSecond);
        return Math.Max(0, kilobytes) * 1024L;
    }

    private static void RequestStop(DownloadItem item)
    {
        try { item.CancellationSource?.Cancel(); }
        catch (ObjectDisposedException) { /* the transfer already finished */ }
    }

    /// <summary>
    /// Makes the reported byte count agree with what is actually on disk. A multi-connection transfer
    /// preallocates the full length, so for those the checkpoint is the only trustworthy count; a single
    /// stream simply appends, so its file length is the count.
    /// </summary>
    private static void SyncBytesFromPartial(DownloadItem item)
    {
        var partial = item.DestinationPath + ".part";
        try
        {
            if (!File.Exists(partial)) return;
            var checkpoint = DownloadCheckpoint.Read(DownloadCheckpoint.PathFor(partial));

            if (checkpoint is { Segments: > 1, TotalBytes: > 0 })
            {
                item.DownloadedBytes = Math.Clamp(checkpoint.DownloadedBytes, 0, checkpoint.TotalBytes);
                if (item.TotalBytes is null or <= 0) item.TotalBytes = checkpoint.TotalBytes;
                return;
            }

            var length = new FileInfo(partial).Length;
            var total = item.TotalBytes ?? (checkpoint is { TotalBytes: > 0 } known ? known.TotalBytes : null);
            item.DownloadedBytes = total is long expected && expected > 0 ? Math.Min(length, expected) : length;
            if (item.TotalBytes is null or <= 0 && total is long discovered) item.TotalBytes = discovered;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A locked or vanished partial simply leaves the last reported count in place.
        }
    }
}
