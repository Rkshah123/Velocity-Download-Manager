using Velocity.Core.Models;
using Velocity.Core.Tests.Support;
using Velocity.Data.Repositories;
using Velocity.DownloadEngine;
using Velocity.Services;

namespace Velocity.Core.Tests;

/// <summary>
/// Removing a per-download ceiling has to restore full speed on the transfer that is already running.
/// The engine applies a ceiling when a transfer starts, so changing it means re-entering the same partial
/// file — the download must continue from where it was, not stop and not start over.
/// </summary>
public sealed class SpeedLimitRemovalTests
{
    [Fact]
    public async Task RemovingTheLimitLetsARunningDownloadFinish()
    {
        using var database = new TestDatabase();
        var payload = Payload(2 * 1024 * 1024, 31);
        using var server = new RangeTestServer(payload, chunkDelayMs: 0);

        var manager = CreateManager(database);
        var item = await manager.QueueAsync(new DownloadRequest($"{server.BaseUrl}/files/unlimited.bin", database.Folder, "unlimited.bin", Connections: 1));
        manager.SetSpeedLimit(item, 256);
        manager.Resume(item);

        // Prove the ceiling is really in force before it is removed.
        await WaitUntilAsync(() => item.DownloadedBytes > 0, TimeSpan.FromSeconds(20));
        await Task.Delay(600);
        var limited = item.DownloadedBytes;
        Assert.True(limited < payload.Length, $"the limit did not slow the transfer: {limited} of {payload.Length} bytes already written");

        manager.SetSpeedLimit(item, 0);

        await WaitUntilAsync(() => item.Status == DownloadStatus.Completed, TimeSpan.FromSeconds(45));

        // The bytes already on disk were kept: the transfer continued, it did not start a second file or zero.
        Assert.True(item.DownloadedBytes >= limited, $"the transfer went backwards: {limited} -> {item.DownloadedBytes}");
        Assert.Equal(payload.Length, new FileInfo(item.DestinationPath).Length);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(item.DestinationPath))));
    }

    [Fact]
    public async Task RemovingTheLimitDoesNotQueueASecondCopy()
    {
        using var database = new TestDatabase();
        var payload = Payload(1024 * 1024, 32);
        using var server = new RangeTestServer(payload, chunkDelayMs: 0);

        var manager = CreateManager(database);
        var item = await manager.QueueAsync(new DownloadRequest($"{server.BaseUrl}/files/one-file.bin", database.Folder, "one-file.bin", Connections: 1));
        manager.SetSpeedLimit(item, 128);
        manager.Resume(item);

        await WaitUntilAsync(() => item.DownloadedBytes > 0, TimeSpan.FromSeconds(20));
        manager.SetSpeedLimit(item, 0);
        await WaitUntilAsync(() => item.Status == DownloadStatus.Completed, TimeSpan.FromSeconds(45));

        // Exactly one finished file and no leftover partial or sidecar next to it.
        var files = Directory.GetFiles(database.Folder, "one-file.bin*");
        Assert.Single(files);
        Assert.EndsWith("one-file.bin", files[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnAppWideLimitChangeReachesARunningDownload()
    {
        using var database = new TestDatabase();
        var payload = Payload(4 * 1024 * 1024, 33);
        // A deliberately slow server, so the transfer is still running when the ceiling arrives.
        using var server = new RangeTestServer(payload, chunkDelayMs: 20);

        var manager = CreateManager(database);
        var item = await manager.StartAsync(new DownloadRequest($"{server.BaseUrl}/files/global.bin", database.Folder, "global.bin", Connections: 1));
        await WaitUntilAsync(() => item.Status == DownloadStatus.Downloading && item.DownloadedBytes > 0, TimeSpan.FromSeconds(20));

        // Applying the app-wide ceiling to transfers that are already running is what Settings does after Save.
        manager.SetSpeedLimit(item, 64);
        await Task.Delay(800);
        var limited = item.DownloadedBytes;
        Assert.True(limited < payload.Length, "the new ceiling did not reach the running transfer");

        manager.SetSpeedLimit(item, 0);
        await WaitUntilAsync(() => item.Status == DownloadStatus.Completed, TimeSpan.FromSeconds(45));
        Assert.Equal(payload.Length, new FileInfo(item.DestinationPath).Length);
    }

    private static DownloadManager CreateManager(TestDatabase database)
    {
        var repository = new DownloadRepository(database.Path);
        var settings = new SettingsService(new SettingsRepository(database.Path));
        settings.Current.DefaultConnectionCount = 1;
        settings.Current.MaximumActiveDownloads = 1;
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
