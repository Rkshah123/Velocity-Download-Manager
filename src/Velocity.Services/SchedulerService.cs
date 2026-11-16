using Velocity.Core.Models;

namespace Velocity.Services;

/// <summary>Runs downloads at a future time while the application stays open.</summary>
public sealed class SchedulerService(DownloadManager manager)
{
    private readonly object _gate = new();
    private readonly List<ScheduleEntry> _entries = [];

    public IReadOnlyList<ScheduleEntry> Scheduled
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    /// <summary>Queues a download for <paramref name="startAt"/>. Returns immediately; the wait runs in the background.</summary>
    public ScheduleEntry Schedule(DownloadRequest request, DateTimeOffset startAt)
    {
        var entry = new ScheduleEntry(request, startAt);
        lock (_gate) _entries.Add(entry);
        _ = RunAsync(entry);
        return entry;
    }

    private async Task RunAsync(ScheduleEntry entry)
    {
        try
        {
            var delay = entry.StartAt - DateTimeOffset.Now;
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
            await manager.StartAsync(entry.Request);
        }
        catch (Exception)
        {
            // A scheduled transfer that cannot start must not take the application down.
        }
        finally
        {
            lock (_gate) _entries.Remove(entry);
        }
    }
}

public sealed class ScheduleEntry(DownloadRequest request, DateTimeOffset startAt)
{
    public DownloadRequest Request { get; } = request;
    public DateTimeOffset StartAt { get; } = startAt;
    public string Url => Request.Url;
    public string FileName => Request.FileName ?? DownloadItem.SuggestFileName(Request.Url);
}
