namespace Velocity.Core.Models;

public sealed record DownloadRecord(
    long Id,
    string FileName,
    string Url,
    string DestinationPath,
    long? TotalBytes,
    long DownloadedBytes,
    DownloadStatus Status,
    string? FinalUrl = null,
    string? ETag = null,
    string? LastModified = null,
    bool ResumeSupported = false,
    int SpeedLimitBytesPerSecond = 0,
    IReadOnlyDictionary<string, string>? Headers = null,
    int Priority = 0);
