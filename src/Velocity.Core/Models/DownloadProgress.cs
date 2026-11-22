namespace Velocity.Core.Models;

/// <param name="ConnectionBytes">
/// Bytes each connection has written, in connection order. Null for a single-stream transfer, which has one
/// connection anyway; a segmented transfer reports one entry per segment so a window can list them.
/// </param>
public sealed record DownloadProgress(
    long DownloadedBytes,
    long? TotalBytes,
    double BytesPerSecond,
    string? FilePath = null,
    IReadOnlyList<long>? ConnectionBytes = null);
