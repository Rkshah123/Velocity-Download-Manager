namespace Velocity.Core.Models;

public sealed record DownloadResult(
    string FilePath,
    long DownloadedBytes,
    long? TotalBytes,
    string? FinalUrl = null,
    string? ETag = null,
    string? LastModified = null,
    bool ResumeSupported = false);
