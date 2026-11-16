using System.Diagnostics;
using Velocity.Core.Models;
using Velocity.Core.Tests.Support;
using Velocity.Data.Repositories;
using Velocity.DownloadEngine;
using Velocity.Services;

namespace Velocity.Core.Tests;

/// <summary>
/// Measures what the transfer actually does, not what the UI says. The ceiling has to hold on the real bytes of a
/// multi-connection download, a mid-flight change has to take effect on the running transfer, and removing it has to
/// restore full speed even when an app-wide ceiling exists.
/// </summary>
public sealed class SpeedLimitAccuracyTests
{
    [Fact]
    public async Task ARunningDownloadStaysUnderItsCeiling()
    {
        using var database = new TestDatabase();
        using var server = new RangeTestServer(Payload(8 * 1024 * 1024, 51), chunkDelayMs: 0);

        var manager = CreateManager(database);
        var item = await manager.StartAsync(new DownloadRequest($"{server.BaseUrl}/files/ceiling.bin", database.Folder, "ceiling.bin", Connections: 4));
        // 512 KB/s across four connections means one shared ceiling, not one per connection.
        manager.SetSpeedLimit(item, 512);

        await WaitUntilAsync(() => item.DownloadedBytes > 0, TimeSpan.FromSeconds(20));
        var watch = Stopwatch.StartNew();
        var from = item.DownloadedBytes;
        await Task.Delay(4000);
        var kilobytesPerSecond = (item.DownloadedBytes - from) / 1024d / watch.Elapsed.TotalSeconds;

        // The server is a local socket with no artificial delay, so only the limiter can hold the transfer back.
        Assert.True(kilobytesPerSecond <= 512 * 1.15,
            $"the transfer ran at {kilobytesPerSecond:0} KB/s against a 512 KB/s ceiling");
        Assert.True(kilobytesPerSecond >= 512 * 0.6,
            $"the transfer stalled at {kilobytesPerSecond:0} KB/s instead of running near its 512 KB/s ceiling");
        Assert.Equal(DownloadStatus.Downloading, item.Status);
        manager.Pause(item);
    }

    [Fact]
    public async Task ChoosingNoLimitForAFileOverridesTheAppWideCeiling()
    {
        using var database = new TestDatabase();
        using var server = new RangeTestServer(Payload(16 * 1024 * 1024, 52), chunkDelayMs: 0);

        var settings = new SettingsService(new SettingsRepository(database.Path));
        settings.Current.DefaultConnectionCount = 4;
        settings.Current.MaximumActiveDownloads = 4;
        settings.Current.GlobalSpeedLimitKilobytesPerSecond = 256;

        var repository = new DownloadRepository(database.Path);
        var manager = new DownloadManager(new HttpDownloadEngine(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }), repository, settings);

        var item = await manager.StartAsync(new DownloadRequest($"{server.BaseUrl}/files/nolimit.bin", database.Folder, "nolimit.bin", Connections: 4));
        // "No limit" on this file, while Settings still carries a 256 KB/s app-wide ceiling.
        manager.SetSpeedLimit(item, 0);

        // 16 MB at the 256 KB/s app-wide ceiling would need over a minute; with no limit on this file it has to
        // finish almost at once, which is exactly what "removed the limit → full speed" means.
        var watch = Stopwatch.StartNew();
        await WaitUntilAsync(() => item.Status == DownloadStatus.Completed, TimeSpan.FromSeconds(10));

        Assert.True(watch.Elapsed.TotalSeconds < 10,
            $"the app-wide 256 KB/s ceiling still applied after the file was set to no limit: {watch.Elapsed.TotalSeconds:0.0}s for 16 MB");
        Assert.Equal(16 * 1024 * 1024, new FileInfo(item.DestinationPath).Length);

        // The choice is remembered as this file's own, so a restart does not silently re-apply the app-wide ceiling.
        var record = Assert.Single(await manager.LoadHistoryAsync(), row => row.FileName == "nolimit.bin");
        Assert.Equal(-1, record.SpeedLimitBytesPerSecond);
    }

    private static DownloadManager CreateManager(TestDatabase database)
    {
        var repository = new DownloadRepository(database.Path);
        var settings = new SettingsService(new SettingsRepository(database.Path));
        settings.Current.DefaultConnectionCount = 4;
        settings.Current.MaximumActiveDownloads = 4;
        var engine = new HttpDownloadEngine(new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        return new DownloadManager(engine, repository, settings);
    }

    private static byte[] Payload(int size, int seed)
    {
        var bytes = new byte[size];
        var random = new Random(seed);
        random.NextBytes(bytes);
        return bytes;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException($"the condition was still false after {timeout.TotalSeconds:0} seconds");
    }
}
