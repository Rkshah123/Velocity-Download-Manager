using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Velocity.Core.Models;
using Velocity.Data.Database;
using Velocity.Services;

// Process-level harness for the cases a unit test cannot prove: several real downloads at once in one
// process, a hard kill in the middle of them, and recovery in a brand-new process over the same database.

var options = Options.Parse(args);
if (options is null)
{
    Console.WriteLine("""
        Usage:
          start  --db <db> --folder <dir> --urls <u1,u2,u3,u4> [--connections 4] [--active 4] [--pause-at <bytes>]
          speed  --db <db> --folder <dir> --urls <u> [--connections 4] [--limit <KB/s>] [--newlimit <KB/s>] [--nolimit] [--speed <global KB/s>] [--seconds 8]
          resume --db <db> --folder <dir>
          report --db <db>
        """);
    return 2;
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!);
Directory.CreateDirectory(options.Folder);

var provider = ApplicationBootstrapper.BuildServiceProvider(options.DatabasePath);
await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync(options.DatabasePath);

var settings = provider.GetRequiredService<SettingsService>();
await settings.LoadAsync();
settings.Current.DefaultDownloadDirectory = options.Folder;
settings.Current.MaximumActiveDownloads = options.MaximumActive;
settings.Current.DefaultConnectionCount = options.Connections;
settings.Current.RetryLimit = 1;
settings.Current.GlobalSpeedLimitKilobytesPerSecond = options.SpeedKilobytes;

var manager = provider.GetRequiredService<DownloadManager>();

switch (options.Mode)
{
    case "start": return await StartAsync();
    case "speed": return await SpeedAsync();
    case "resume": return await ResumeAsync();
    case "report": return await ReportAsync();
    default:
        Console.WriteLine($"Unknown mode '{options.Mode}'.");
        return 2;
}

async Task<int> StartAsync()
{
    var items = new List<DownloadItem>();
    foreach (var url in options.Urls)
        items.Add(await manager.StartAsync(new DownloadRequest(url, options.Folder, Connections: options.Connections)));

    await PrintAsync(items);

    if (options.PauseAt is long pauseAt)
    {
        await UntilAsync(() => items.Sum(item => item.DownloadedBytes) >= pauseAt, TimeSpan.FromSeconds(60));
        foreach (var item in items) manager.Pause(item);
        await UntilAsync(() => items.All(item => item.Status is DownloadStatus.Paused or DownloadStatus.Completed or DownloadStatus.Failed), TimeSpan.FromSeconds(30));
        Console.WriteLine("paused:");
        await PrintAsync(items);
        return 0;
    }

    var finished = await UntilAsync(() => items.All(item => item.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled), TimeSpan.FromMinutes(5));
    Console.WriteLine("finished:");
    await PrintAsync(items);
    return finished && items.All(item => item.Status == DownloadStatus.Completed) ? 0 : 1;
}

// Measures what a real transfer actually does: aggregate throughput per second with the ceiling in force, then
// again after the ceiling is removed mid-flight (the user's "change/remove while downloading" case).
async Task<int> SpeedAsync()
{
    var items = new List<DownloadItem>();
    foreach (var url in options.Urls)
        items.Add(await manager.StartAsync(new DownloadRequest(url, options.Folder, Connections: options.Connections)));

    // --limit is the per-download ceiling the Speed limit dialog sets; --speed is the app-wide one in Settings;
    // --nolimit presses "No limit" on the file, which must beat the app-wide ceiling.
    foreach (var item in items)
    {
        if (options.NoLimit) manager.SetSpeedLimit(item, 0);
        else if (options.LimitKilobytes > 0) manager.SetSpeedLimit(item, options.LimitKilobytes);
    }
    Console.WriteLine($"connections={options.Connections} per-file limit={options.LimitKilobytes} KB/s global={options.SpeedKilobytes} KB/s files={items.Count}");

    await Task.Delay(700);
    var removed = false;
    var samples = new List<double>();
    var watch = System.Diagnostics.Stopwatch.StartNew();
    long previous = items.Sum(item => item.DownloadedBytes);
    var sampleAt = System.Diagnostics.Stopwatch.StartNew();
    while (watch.Elapsed.TotalSeconds < options.Seconds)
    {
        await Task.Delay(500);
        if (sampleAt.ElapsedMilliseconds < 1000) continue;
        sampleAt.Restart();

        var now = items.Sum(item => item.DownloadedBytes);
        var rate = (now - previous) / 1024d;
        previous = now;
        samples.Add(rate);
        var phase = removed ? "unlimited" : "limited";
        Console.WriteLine($"{watch.Elapsed.TotalSeconds:0.0}s  {rate:0} KB/s  ({phase})  bytes={now}");

        if (!removed && options.LimitKilobytes > 0 && watch.Elapsed.TotalSeconds >= options.Seconds / 2.0)
        {
            // Mid-flight change: a new value, or 0 to remove the ceiling. Nothing is paused or restarted.
            foreach (var item in items) manager.SetSpeedLimit(item, options.NewLimitKilobytes);
            removed = true;
            Console.WriteLine(options.NewLimitKilobytes > 0
                ? $"-- limit changed to {options.NewLimitKilobytes} KB/s while running --"
                : "-- limit removed, nothing paused or restarted --");
        }
    }

    Console.WriteLine("per file:");
    await PrintAsync(items);
    foreach (var item in items) manager.Pause(item);
    return 0;
}

async Task<int> ResumeAsync()
{
    // A new process over the same database and folder: this is what "reopen Velocity" does.
    var history = await manager.LoadHistoryAsync();
    Console.WriteLine($"history rows: {history.Count}");

    var items = history
        .Where(record => record.Status != DownloadStatus.Completed)
        .Select(manager.CreateHistoryItem)
        .ToList();

    foreach (var item in items)
        Console.WriteLine($"recovered {item.FileName}: {item.Status} {item.DownloadedBytes}/{item.TotalBytes}");

    // The desktop app does exactly this on startup: anything that was mid-transfer continues on its own, from
    // the byte its partial file and checkpoint reached.
    manager.ResumeInterrupted(items);
    Console.WriteLine($"auto-resumed: {items.Count(item => item.Status is DownloadStatus.Resuming or DownloadStatus.Downloading)} of {items.Count}");

    var finished = await UntilAsync(() => items.All(item => item.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled), TimeSpan.FromMinutes(5));
    Console.WriteLine("after resume:");
    await PrintAsync(items);
    return finished && items.All(item => item.Status == DownloadStatus.Completed) ? 0 : 1;
}

async Task<int> ReportAsync()
{
    var history = await manager.LoadHistoryAsync();
    Console.WriteLine($"history rows: {history.Count}");
    foreach (var record in history)
        Console.WriteLine($"{record.Id}\t{record.Status}\t{record.DownloadedBytes}/{record.TotalBytes}\t{record.FileName}");
    return 0;
}

async Task PrintAsync(IEnumerable<DownloadItem> items)
{
    foreach (var item in items)
    {
        var hash = item.Status == DownloadStatus.Completed && File.Exists(item.DestinationPath)
            ? Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(item.DestinationPath)))[..16].ToLowerInvariant()
            : "-";
        Console.WriteLine($"{item.FileName}\t{item.Status}\t{item.DownloadedBytes}/{item.TotalBytes}\t{item.Progress:0.0}%\tsha256:{hash}\t{item.Error}");
    }
}

static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (condition()) return true;
        await Task.Delay(100);
    }
    return condition();
}

internal sealed record Options(string Mode, string DatabasePath, string Folder, string[] Urls, int Connections, int MaximumActive, int SpeedKilobytes, long? PauseAt, int Seconds = 8, int LimitKilobytes = 0, bool NoLimit = false, int NewLimitKilobytes = 0)
{
    public static Options? Parse(string[] args)
    {
        if (args.Length == 0) return null;
        var mode = args[0];
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index + 1 < args.Length; index += 2) values[args[index].TrimStart('-')] = args[index + 1];

        if (!values.TryGetValue("db", out var database)) return null;
        var folder = values.TryGetValue("folder", out var provided) ? provided : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(database))!, "out");
        if (mode is not "report" && !values.ContainsKey("folder")) return null;

        var urls = values.TryGetValue("urls", out var list) ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        var connections = values.TryGetValue("connections", out var c) && int.TryParse(c, out var parsedConnections) ? parsedConnections : 4;
        var active = values.TryGetValue("active", out var a) && int.TryParse(a, out var parsedActive) ? parsedActive : 4;
        var speed = values.TryGetValue("speed", out var s) && int.TryParse(s, out var parsedSpeed) ? parsedSpeed : 0;
        long? pauseAt = values.TryGetValue("pause-at", out var p) && long.TryParse(p, out var parsedPause) ? parsedPause : null;

        var seconds = values.TryGetValue("seconds", out var sec) && int.TryParse(sec, out var parsedSeconds) ? parsedSeconds : 8;
        var limit = values.TryGetValue("limit", out var l) && int.TryParse(l, out var parsedLimit) ? parsedLimit : 0;
        var noLimit = values.ContainsKey("nolimit");
        var newLimit = values.TryGetValue("newlimit", out var n) && int.TryParse(n, out var parsedNewLimit) ? parsedNewLimit : 0;
        return new Options(mode, database, folder, urls, Math.Clamp(connections, 1, 16), Math.Clamp(active, 0, 64), Math.Clamp(speed, 0, 102400), pauseAt, Math.Clamp(seconds, 2, 120), Math.Clamp(limit, 0, 102400), noLimit, Math.Clamp(newLimit, 0, 102400));
    }
}
