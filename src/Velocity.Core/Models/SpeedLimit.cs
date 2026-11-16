namespace Velocity.Core.Models;

/// <summary>
/// A speed ceiling that can change while a transfer is running.
///
/// The engine reads <see cref="BytesPerSecond"/> every time it paces a read, so raising it, lowering it or
/// removing it (0 = unlimited) takes effect on the next buffer — without pausing, cancelling or restarting the
/// download. One instance belongs to one download and is shared by all of its connections, so the ceiling applies
/// to the combined speed.
/// </summary>
public sealed class SpeedLimit(long bytesPerSecond = 0)
{
    private long _bytesPerSecond = Math.Max(0, bytesPerSecond);

    /// <summary>The current ceiling in bytes per second. <c>0</c> means no ceiling at all.</summary>
    public long BytesPerSecond => Interlocked.Read(ref _bytesPerSecond);

    public bool Enabled => BytesPerSecond > 0;

    public void Set(long value) => Interlocked.Exchange(ref _bytesPerSecond, Math.Max(0, value));
}
