using System.Security.Cryptography;
using Velocity.Core.Models;
using Velocity.Core.Tests.Support;
using Velocity.Data.Repositories;
using Velocity.DownloadEngine;
using Velocity.Services;

namespace Velocity.Core.Tests;

/// <summary>
/// The behaviour behind the "no artificial limits" and "IDM-style per-download limit" requirements: every
/// transfer the user starts runs at once, a ceiling applies to the file it was set on, and a limited transfer
/// still resumes from its checkpoint instead of starting over.
/// </summary>
public sealed class SpeedLimitAndConcurrencyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task EveryDownloadRunsAtOnceWhenNoCeilingIsConfigured()
    {
        using var database = new TestDatabase();
        var servers = new List<RangeTestServer>();
        var payloads = new List<byte[]>();
        try
        {
            const int count = 6;
            for (var index = 0; index < count; index++)
            {
                var payload = Payload(1024 * 1024, index);
                payloads.Add(payload);
                servers.Add(new RangeTestServer(payload, chunkDelayMs: 25));
            }

            // 0 is the "no limit" setting: six transfers must all be in flight, not queued behind a cap.
            var manager = CreateManager(database, connections: 1, maximumActive: 0);
            var items = new List<DownloadItem>();
            for (var index = 0; index < count; index++)
                items.Add(await manager.StartAsync(new DownloadRequest($"{servers[index].BaseUrl}/files/parallel{index}.bin", database.Folder, $"parallel{index}.bin", Connections: 1)));

            await WaitUntilAsync(() => items.All(item => item.Status == DownloadStatus.Downloading), TimeSpan.FromSeconds(20));
            Assert.Equal(count, items.Count(item => item.Status == DownloadStatus.Downloading));

            await WaitUntilAsync(() => items.All(item => item.Status == DownloadStatus.Completed), Timeout);
            for (var index = 0; index < count; index++)
                Assert.Equal(Sha256(payloads[index]), Sha256(await File.ReadAllBytesAsync(items[index].DestinationPath)));
        }
        finally
        {
            foreach (var server in servers) server.Dispose();
        }
    }

    [Fact]
    public async Task APerDownloadLimitSlowsOnlyThatFile()
    {
        using var database = new TestDatabase();
        var limitedPayload = Payload(4 * 1024 * 1024, 11);
        var freePayload = Payload(4 * 1024 * 1024, 12);
        using var limitedServer = new RangeTestServer(limitedPayload, chunkDelayMs: 0);
        using var freeServer = new RangeTestServer(freePayload, chunkDelayMs: 0);

        var manager = CreateManager(database, connections: 4, maximumActive: 0);

        // Queued first so the ceiling is in place before the transfer starts, exactly like the dialog does.
        var limited = await manager.QueueAsync(new DownloadRequest($"{limitedServer.BaseUrl}/files/limited.bin", database.Folder, "limited.bin", Connections: 4));
        manager.SetSpeedLimit(limited, 256);
        var startedAt = DateTime.UtcNow;
        manager.Resume(limited);

        var free = await manager.StartAsync(new DownloadRequest($"{freeServer.BaseUrl}/files/free.bin", database.Folder, "free.bin", Connections: 4));

        await WaitUntilAsync(() => free.Status == DownloadStatus.Completed, Timeout);
        try
        {
            await WaitUntilAsync(() => limited.DownloadedBytes > 0, TimeSpan.FromSeconds(15));
        }
        catch (Xunit.Sdk.FailException)
        {
            Assert.Fail($"limited download made no progress: status={limited.Status} error={limited.Error} speed={limited.BytesPerSecond} requests={limitedServer.Requests.Count}");
        }

        var seconds = Math.Max(0.25, (DateTime.UtcNow - startedAt).TotalSeconds);
        var averageBytesPerSecond = limited.DownloadedBytes / seconds;

        Assert.Equal(DownloadStatus.Downloading, limited.Status);
        Assert.True(averageBytesPerSecond < 400 * 1024,
            $"a 256 KB/s ceiling produced {averageBytesPerSecond / 1024:0} KB/s");
        Assert.True(limited.DownloadedBytes < 2 * 1024 * 1024, "the ceiling must keep the limited file behind the free one");
        Assert.Equal(256, limited.SpeedLimitKilobytesPerSecond);
        Assert.Contains("Limited to", limited.Details);

        manager.Pause(limited);
        await WaitUntilAsync(() => limited.Status == DownloadStatus.Paused, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task ALimitedSegmentedDownloadResumesFromItsCheckpoint()
    {
        using var database = new TestDatabase();
        var payload = Payload(6 * 1024 * 1024, 21);
        using var server = new RangeTestServer(payload, chunkDelayMs: 20);

        var manager = CreateManager(database, connections: 4, maximumActive: 0);
        var item = await manager.StartAsync(new DownloadRequest($"{server.BaseUrl}/files/limited-resume.bin", database.Folder, "limited-resume.bin", Connections: 4));

        await WaitUntilAsync(() => item.DownloadedBytes > 1024 * 1024, TimeSpan.FromSeconds(30));
        manager.Pause(item);
        await WaitUntilAsync(() => item.Status == DownloadStatus.Paused, TimeSpan.FromSeconds(20));

        var requestsBeforeResume = server.Requests.Count;

        // A ceiling used to force a single stream, which made a preallocated multi-connection partial
        // impossible to resume: the file already had its full length, so the resume looked complete.
        manager.SetSpeedLimit(item, 2048);
        manager.Resume(item);

        try
        {
            await WaitUntilAsync(() => item.Status == DownloadStatus.Completed, Timeout);
        }
        catch (Xunit.Sdk.FailException)
        {
            Assert.Fail($"resume did not finish: status={item.Status} error={item.Error} bytes={item.DownloadedBytes}/{item.TotalBytes} requests={server.Requests.Count}");
        }

        // The engine probes with 0-0 first; every real range after the pause must start past the beginning.
        var resumedRanges = server.Requests.Skip(requestsBeforeResume)
            .Where(request => request.Range is not null && request.Range != "bytes=0-0")
            .Select(request => request.Range!)
            .ToList();
        Assert.NotEmpty(resumedRanges);
        Assert.DoesNotContain(resumedRanges, range => range.StartsWith("bytes=0-", StringComparison.Ordinal));
        Assert.Equal(Sha256(payload), Sha256(await File.ReadAllBytesAsync(item.DestinationPath)));
    }

    [Fact]
    public void TheCheckpointFallsBackToThePreviousCopy()
    {
        using var folder = new TemporaryFolder();
        var path = Path.Combine(folder.Path, "file.part.meta");

        DownloadCheckpoint.Write(path, new DownloadCheckpoint(1, "http://host/file", null, "file", 1000, 200, "\"v1\"", null, true, 4, [50, 50, 50, 50]));
        DownloadCheckpoint.Write(path, new DownloadCheckpoint(1, "http://host/file", null, "file", 1000, 600, "\"v1\"", null, true, 4, [150, 150, 150, 150]));

        Assert.Equal(600, DownloadCheckpoint.Read(path)!.DownloadedBytes);

        // A power cut can lose the newest file even though it was written; the backup must still answer.
        File.Delete(path);
        var recovered = DownloadCheckpoint.Read(path);
        Assert.NotNull(recovered);
        Assert.Equal(200, recovered!.DownloadedBytes);
    }

    [Fact]
    public async Task DeletingAnActiveDownloadStopsItAndRemovesEveryPartial()
    {
        using var database = new TestDatabase();
        var payload = Payload(8 * 1024 * 1024, 31);
        using var server = new RangeTestServer(payload, chunkDelayMs: 25);

        var manager = CreateManager(database, connections: 4, maximumActive: 0);
        var item = await manager.StartAsync(new DownloadRequest($"{server.BaseUrl}/files/delete-me.bin", database.Folder, "delete-me.bin", Connections: 4));

        await WaitUntilAsync(() => item.DownloadedBytes > 256 * 1024, TimeSpan.FromSeconds(30));
        Assert.Equal(DownloadStatus.Downloading, item.Status);

        var error = await manager.DeleteAsync(item, deleteFile: true);

        Assert.Null(error);
        Assert.NotEqual(DownloadStatus.Downloading, item.Status);
        Assert.Empty(await manager.LoadHistoryAsync());
        foreach (var suffix in new[] { "", ".part", ".part.meta", ".part.meta.prev", ".part.meta.tmp" })
            Assert.False(File.Exists(Path.Combine(database.Folder, "delete-me.bin" + suffix)), $"'{suffix}' was left behind");
    }

    [Fact]
    public async Task APerDownloadLimitSurvivesARestart()
    {
        using var database = new TestDatabase();
        var payload = Payload(2 * 1024 * 1024, 41);
        using var server = new RangeTestServer(payload, chunkDelayMs: 20);

        var manager = CreateManager(database, connections: 4, maximumActive: 0);
        var item = await manager.QueueAsync(new DownloadRequest($"{server.BaseUrl}/files/kept-limit.bin", database.Folder, "kept-limit.bin", Connections: 4));
        manager.SetSpeedLimit(item, 512);
        await Task.Delay(400);

        var record = (await new DownloadRepository(database.Path).GetRecentAsync()).Single();
        Assert.Equal(512 * 1024, record.SpeedLimitBytesPerSecond);

        var reopened = CreateManager(database, connections: 4, maximumActive: 0);
        var restored = reopened.CreateHistoryItem(record);
        Assert.Equal(512, restored.SpeedLimitKilobytesPerSecond);
        Assert.True(restored.HasSpeedLimit);
    }

    private static DownloadManager CreateManager(TestDatabase database, int connections, int maximumActive)
    {
        var settings = new SettingsService(new SettingsRepository(database.Path));
        settings.LoadAsync().GetAwaiter().GetResult();
        settings.Current.DefaultDownloadDirectory = database.Folder;
        settings.Current.DefaultConnectionCount = connections;
        settings.Current.MaximumActiveDownloads = maximumActive;
        settings.Current.RetryLimit = 0;
        var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        return new DownloadManager(new HttpDownloadEngine(client), new DownloadRepository(database.Path), settings);
    }

    private static byte[] Payload(int size, int seed)
    {
        var payload = new byte[size];
        new Random(seed).NextBytes(payload);
        return payload;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail("The condition was not reached before the timeout.");
    }
}
