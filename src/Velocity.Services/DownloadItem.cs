using System.ComponentModel;
using System.Runtime.CompilerServices;
using Velocity.Core.Models;

namespace Velocity.Services;

public sealed class DownloadItem : INotifyPropertyChanged
{
    private static readonly string[] StatusDependents =
    [
        nameof(StatusText), nameof(CanPause), nameof(CanResume), nameof(CanRetry), nameof(CanLimit),
        nameof(ShowStatusAction), nameof(CanOpenFile), nameof(CanOpenFolder),
        nameof(TimeRemaining), nameof(Details), nameof(IsBusy)
    ];

    private DownloadStatus _status;
    private string _fileName;
    private long _downloadedBytes;
    private long? _totalBytes;
    private double _bytesPerSecond;
    private int _speedLimitKilobytesPerSecond;
    private string? _error;
    private string? _notice;

    internal CancellationTokenSource? CancellationSource { get; set; }
    internal bool PauseRequested { get; set; }
    internal int Attempts { get; set; }

    /// <summary>True when the previous session was still transferring; the manager resumes those after a restart.</summary>
    internal bool WasActive { get; set; }

    /// <summary>
    /// The live ceiling the running engine reads. Changing it — including removing it — takes effect on the
    /// next buffer, so a speed-limit change never stops or restarts the transfer.
    /// </summary>
    internal SpeedLimit LiveLimit { get; } = new();

    // Resume metadata reported by the engine and stored with the download.
    internal string? ETag { get; set; }
    internal string? LastModified { get; set; }
    internal string? FinalUrl { get; set; }
    internal bool ResumeSupported { get; set; }

    public DownloadItem(long id, DownloadRequest request)
    {
        Id = id;
        Request = request;
        Url = request.Url;
        _fileName = string.IsNullOrWhiteSpace(request.FileName) ? SuggestFileName(request.Url) : request.FileName!.Trim();
        DestinationDirectory = request.DestinationDirectory;
        DestinationPath = Path.Combine(request.DestinationDirectory, _fileName);
        _status = DownloadStatus.Queued;
    }

    public long Id { get; internal set; }
    internal DownloadRequest Request { get; set; }
    public string Url { get; }

    public string FileName
    {
        get => _fileName;
        internal set
        {
            if (!Set(ref _fileName, value)) return;
            OnPropertyChanged(nameof(ExtensionLabel));
            OnPropertyChanged(nameof(FileCategory));
        }
    }

    public string DestinationDirectory { get; }
    public string DestinationPath { get; internal set; }

    public DownloadStatus Status
    {
        get => _status;
        internal set
        {
            if (!Set(ref _status, value)) return;
            foreach (var dependent in StatusDependents) OnPropertyChanged(dependent);
            if (value != DownloadStatus.Downloading) BytesPerSecond = 0;
        }
    }

    public long DownloadedBytes
    {
        get => _downloadedBytes;
        internal set
        {
            if (!Set(ref _downloadedBytes, value)) return;
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(DownloadedSize));
            OnPropertyChanged(nameof(PercentText));
            OnPropertyChanged(nameof(Details));
            OnPropertyChanged(nameof(TimeRemaining));
        }
    }

    public long? TotalBytes
    {
        get => _totalBytes;
        internal set
        {
            if (!Set(ref _totalBytes, value)) return;
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(TotalSize));
            OnPropertyChanged(nameof(PercentText));
            OnPropertyChanged(nameof(Details));
            OnPropertyChanged(nameof(TimeRemaining));
        }
    }

    /// <summary>
    /// True once the user set a ceiling for this file — including choosing "No limit", which must mean no limit
    /// even when an app-wide ceiling exists. Until then the download follows the app-wide setting.
    /// </summary>
    internal bool SpeedLimitIsExplicit { get; set; }

    /// <summary>
    /// This download's own ceiling in KB/s, like IDM's per-file limit. <c>0</c> means no limit at all once the user
    /// has chosen a value for this file; before that it follows the app-wide setting.
    /// </summary>
    public int SpeedLimitKilobytesPerSecond
    {
        get => _speedLimitKilobytesPerSecond;
        internal set
        {
            if (!Set(ref _speedLimitKilobytesPerSecond, value)) return;
            OnPropertyChanged(nameof(HasSpeedLimit));
            OnPropertyChanged(nameof(SpeedLimitText));
            OnPropertyChanged(nameof(Details));
        }
    }

    public bool HasSpeedLimit => SpeedLimitKilobytesPerSecond > 0;
    public string SpeedLimitText => SpeedLimitKilobytesPerSecond > 0 ? $"Limited to {ByteSize.Format(SpeedLimitKilobytesPerSecond * 1024L)}/s" : string.Empty;

    public double BytesPerSecond
    {
        get => _bytesPerSecond;
        internal set
        {
            if (!Set(ref _bytesPerSecond, value)) return;
            OnPropertyChanged(nameof(Speed));
            OnPropertyChanged(nameof(TimeRemaining));
            OnPropertyChanged(nameof(Details));
        }
    }

    public string? Error { get => _error; internal set => Set(ref _error, value); }

    public double Progress => TotalBytes is > 0 ? Math.Clamp((double)DownloadedBytes / TotalBytes.Value * 100, 0, 100) : 0;
    public string PercentText => TotalBytes is > 0 ? $"{Progress:0}%" : "—";
    public string DownloadedSize => ByteSize.Format(DownloadedBytes);
    public string TotalSize => TotalBytes is null ? "Unknown size" : ByteSize.Format(TotalBytes.Value);
    public string Speed => BytesPerSecond <= 0 ? "—" : $"{ByteSize.Format((long)BytesPerSecond)}/s";
    public string FileCategory => FileTypeInfo.Category(FileName);
    public string ExtensionLabel => FileTypeInfo.Label(FileName);
    public string DestinationFolder => DestinationDirectory;
    public bool IsBusy => Status == DownloadStatus.Downloading;

    /// <summary>Queue priority: a higher number starts before a lower one, and 0 keeps the order things were added in.</summary>
    public int Priority { get; internal set; }

    /// <summary>Best-effort note shown for downloads that had to restart (for example a server without range support).</summary>
    public string? Notice
    {
        get => _notice;
        internal set
        {
            if (!Set(ref _notice, value)) return;
            OnPropertyChanged(nameof(Details));
        }
    }
    public string StatusText => Status switch
    {
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Downloading => "Downloading",
        DownloadStatus.Paused => "Paused",
        DownloadStatus.Resuming => "Resuming",
        DownloadStatus.Completed => "Completed",
        DownloadStatus.Failed => "Failed",
        _ => "Cancelled"
    };

    public string Details
    {
        get
        {
            var size = TotalBytes is long total
                ? $"{DownloadedSize} of {ByteSize.Format(total)}"
                : $"{DownloadedSize} downloaded";
            var parts = new List<string> { size };
            if (Status == DownloadStatus.Downloading && BytesPerSecond > 0) parts.Add(Speed);
            if (HasSpeedLimit) parts.Add(SpeedLimitText);
            var remaining = TimeRemaining;
            if (remaining.Length > 0) parts.Add(remaining);
            if (!string.IsNullOrWhiteSpace(Notice) && Status != DownloadStatus.Completed) parts.Add(Notice!);
            return string.Join("  ·  ", parts);
        }
    }

    public string TimeRemaining
    {
        get
        {
            if (Status != DownloadStatus.Downloading || TotalBytes is not long total || BytesPerSecond <= 0) return string.Empty;
            var seconds = Math.Max(0, total - DownloadedBytes) / BytesPerSecond;
            if (seconds <= 0) return "Finishing…";
            if (seconds >= 3600) return $"{seconds / 3600:0.#} h left";
            if (seconds >= 60) return $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s left";
            return $"{(int)seconds}s left";
        }
    }

    public bool CanPause => Status is DownloadStatus.Downloading or DownloadStatus.Queued or DownloadStatus.Resuming;
    public bool CanResume => Status is DownloadStatus.Paused or DownloadStatus.Cancelled;
    public bool CanRetry => Status == DownloadStatus.Failed;

    /// <summary>A ceiling only means something for a transfer that can still run.</summary>
    public bool CanLimit => Status is DownloadStatus.Downloading or DownloadStatus.Queued or DownloadStatus.Resuming or DownloadStatus.Paused;
    public bool ShowStatusAction => CanPause || CanResume || CanRetry;
    public bool CanOpenFile => Status == DownloadStatus.Completed;
    public bool CanOpenFolder => Status is DownloadStatus.Completed or DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Cancelled;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Derives a readable file name from a URL when the user did not supply one.</summary>
    public static string SuggestFileName(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            string? candidate = null;
            try { candidate = Uri.UnescapeDataString(Path.GetFileName(uri.LocalPath)); }
            catch (UriFormatException) { candidate = Path.GetFileName(uri.LocalPath); }
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
            return string.IsNullOrWhiteSpace(uri.Host) ? "download" : uri.Host;
        }
        return "download";
    }
}
