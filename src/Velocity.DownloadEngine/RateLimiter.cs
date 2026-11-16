using Velocity.Core.Models;

namespace Velocity.DownloadEngine;

/// <summary>
/// Paces a transfer to a ceiling that may change while it runs. The ceiling is read from <see cref="SpeedLimit"/>
/// on every wait, so a value the user changes — or removes — applies to the very next buffer instead of at the
/// next connection: nothing is paused, cancelled or restarted.
/// </summary>
internal sealed class RateLimiter(SpeedLimit limit)
{
    // Credit never accumulates for more than half a second, so a pause in the transfer cannot be followed by a
    // long unthrottled burst.
    private readonly object _gate = new();
    private double _credit;
    private long _lastRefill = Environment.TickCount64;

    /// <summary>Waits until <paramref name="bytes"/> more bytes may be written.</summary>
    /// <param name="tick">Called between slices, so a stall guard is not fooled by a deliberately slow transfer.</param>
    public async Task WaitAsync(int bytes, CancellationToken cancellationToken, Action? tick = null)
    {
        var rate = limit.BytesPerSecond;
        if (rate <= 0)
        {
            // No ceiling: the credit is dropped so that re-applying a limit later starts from a clean state
            // instead of a large accumulated burst.
            lock (_gate) Reset(0);
            return;
        }

        // The requested bytes are charged exactly once. Going negative is fine: a read larger than the burst
        // simply waits for that debt to be refilled.
        lock (_gate)
        {
            Refill(rate);
            _credit -= bytes;
        }

        while (true)
        {
            rate = limit.BytesPerSecond;
            if (rate <= 0)
            {
                lock (_gate) Reset(0);
                return;
            }

            double waitMilliseconds;
            lock (_gate)
            {
                Refill(rate);
                if (_credit >= 0) return;
                waitMilliseconds = Math.Clamp(-_credit / (rate / 1000d), 1, 250);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(waitMilliseconds), cancellationToken);
            tick?.Invoke();
        }
    }

    private void Refill(long rate)
    {
        var now = Environment.TickCount64;
        var elapsed = now - _lastRefill;
        if (elapsed <= 0) return;

        // A ceiling the user chose must hold at every instant, not only on average: the credit that may be spent
        // in one go is a small slice of a second's allowance. Half a second of slack (the previous value) let a
        // 1 MB/s limit show 1.5 MB/s over a one-second window. The floor keeps very low ceilings workable and the
        // ceiling keeps a fast one smooth; both stay well under the rate itself.
        var burst = Math.Clamp(rate / 20d, 4 * 1024, 64 * 1024);
        _credit = Math.Min(burst, _credit + rate / 1000d * elapsed);
        _lastRefill = now;
    }

    private void Reset(double credit)
    {
        _credit = credit;
        _lastRefill = Environment.TickCount64;
    }
}
