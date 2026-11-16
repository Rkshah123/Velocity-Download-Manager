namespace Velocity.Core.Models;

/// <param name="Mirrors">
/// Other addresses that serve the identical file. They are used only after the engine proves they have the same
/// length and the same validator, so bytes from two different versions can never be mixed into one file.
/// </param>
/// <param name="Headers">
/// Extra request headers — a <c>Referer</c>, a session <c>Cookie</c>, an <c>Authorization</c> token or anything
/// else a host insists on. They are sent on every request the download makes, including the probe and each
/// segment, so a protected file keeps working through a resume instead of failing halfway.
/// </param>
/// <param name="LiveLimit">
/// The ceiling object the running transfer reads on every buffer. When it is supplied the engine follows it for
/// the whole transfer, so changing or removing the limit never restarts the download.
/// </param>
public sealed record DownloadRequest(string Url, string DestinationDirectory, string? FileName = null, int Connections = 4, long SpeedLimitBytesPerSecond = 0, IReadOnlyList<string>? Mirrors = null, IReadOnlyDictionary<string, string>? Headers = null, SpeedLimit? LiveLimit = null);
