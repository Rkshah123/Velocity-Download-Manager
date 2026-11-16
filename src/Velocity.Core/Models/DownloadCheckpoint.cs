using System.Text.Json;

namespace Velocity.Core.Models;

/// <summary>
/// The resume checkpoint stored next to a partial file as <c>&lt;file&gt;.part.meta</c>.
///
/// It is the only reliable record of how many bytes of a multi-connection transfer are real: those files are
/// preallocated to their full length, so the file size alone cannot say how much of it has been written.
/// The engine writes it; the manager reads it to report progress after a restart.
/// </summary>
public sealed record DownloadCheckpoint(
    int Version,
    string Url,
    string? FinalUrl,
    string FileName,
    long TotalBytes,
    long DownloadedBytes,
    string? ETag,
    string? LastModified,
    bool RangeSupported,
    int Segments,
    long[] Written)
{
    /// <summary>The validator sent back as <c>If-Range</c>: an ETag is preferred over a date.</summary>
    public string? Validator => ETag ?? LastModified;

    public static string PathFor(string partialPath) => partialPath + ".meta";

    /// <summary>
    /// Reads the checkpoint, falling back to the previous copy. A power failure can lose the newest write
    /// even though the file existed a moment earlier; the backup is what keeps a 70% download resumable.
    /// </summary>
    public static DownloadCheckpoint? Read(string path) => ReadFile(path) ?? ReadFile(path + ".prev");

    private static DownloadCheckpoint? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var checkpoint = JsonSerializer.Deserialize<DownloadCheckpoint>(File.ReadAllText(path));
            return checkpoint is { Version: > 0 } ? checkpoint : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes atomically and durably: the data is flushed to the disk before it replaces the live checkpoint, and
    /// the previous version is kept as <c>.prev</c>. A half-written checkpoint is never mistaken for a real one,
    /// and a power cut cannot leave the transfer without any checkpoint at all.
    /// </summary>
    public static void Write(string path, DownloadCheckpoint value)
    {
        try
        {
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                try { File.Replace(temporary, path, path + ".prev", ignoreMetadataErrors: true); }
                catch (Exception replaceFailure) when (replaceFailure is IOException or PlatformNotSupportedException)
                {
                    File.Move(temporary, path, true);
                }
            }
            else
            {
                File.Move(temporary, path, true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing a checkpoint only costs a few bytes on the next resume; it must never fail the transfer.
        }
    }
}
