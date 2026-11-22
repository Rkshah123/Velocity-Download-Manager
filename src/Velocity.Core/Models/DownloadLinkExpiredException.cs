namespace Velocity.Core.Models;

/// <summary>
/// A resume was refused: the stored link no longer serves the bytes already on disk, or it cannot continue from
/// the saved position. Nothing is deleted when this is raised — the partial file and its checkpoint stay exactly
/// where they are, so the user can still choose to download the file again.
/// </summary>
public sealed class DownloadLinkExpiredException(string? detail = null) : Exception(DefaultMessage)
{
    /// <summary>The exact sentence the UI shows for a link that can no longer be resumed.</summary>
    public const string DefaultMessage = "Download link is broken or expired. You need to download this file again.";

    /// <summary>What the server did, kept next to the message instead of replacing it.</summary>
    public string? Detail { get; } = detail;
}
