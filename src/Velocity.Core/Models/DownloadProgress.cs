namespace Velocity.Core.Models;

public sealed record DownloadProgress(long DownloadedBytes, long? TotalBytes, double BytesPerSecond, string? FilePath = null);
