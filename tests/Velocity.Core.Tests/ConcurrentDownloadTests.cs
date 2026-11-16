using System.Security.Cryptography;
using Velocity.Core.Models;
using Velocity.Core.Tests.Support;
using Velocity.Data.Repositories;
using Velocity.DownloadEngine;
using Velocity.Services;

namespace Velocity.Core.Tests;

/// <summary>
/// Four independent transfers driven through the real <see cref="DownloadManager"/> and the real HTTP engine:
/// each item must own its own task, connection, cancellation token, file and byte offset.
/// </summary>
public sealed class ConcurrentDownloadTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task FourDownloadsRunInParallelAndStayIntact()
    {
        using var database = new TestDatabase();
        var servers = new List<RangeTestServer>();
        var payloads = new List<byte[]>();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var payload = Payload(2 * 1024 * 1024, index);
                payloads.Add(payload);
                servers.Add(new RangeTestServer(payload, chunkDelayMs: 20));
            }

            var manager = CreateManager(database, connections: 1, maximumActive: 4);
            var items = new List<DownloadItem>();
            for (var index = 0; index < 4; index++)
                items.Add(await manager.StartAsync(new DownloadRequest($"{servers[index].BaseUrl}/files/file{index}.bin", database.Folder, $"file{index}.bin", Connections: 1)));

            await WaitUntilAsync(() => items.All(item => item.Status == DownloadStatus.Downloading), TimeSpan.FromSeconds(10));

            // All four must be in flight at the same time, not queued behind one another.
            Assert.All(items, item =>
            {
                var server = servers[items.IndexOf(item)];
                Assert.True(server.InFlight > 0 || server.Requests.Count > 0, $"{item.FileName} never reached its server");
            });

            await WaitUntilAsync(() => items.All(item => item.Status == DownloadStatus.Completed), Timeout);
            Assert.All(items, item => Assert.Equal(DownloadStatus.Completed, item.Status));

            // The saved history must agree with the transfer, even when a progress save was still in flight.
            await Task.Delay(300);
            var history = await manager.LoadHistoryAsync();
            Assert.Equal(4, history.Count);
            Assert.All(history, record => Assert.Equal(DownloadStatus.Completed, record.Status));
            Assert.All(history, record => Assert.Equal(record.TotalBytes, record.DownloadedBytes));

            for (var index = 0; index < 4; index++)
                Assert.Equal(Sha256(payloads[index]), Sha256(await File.ReadAllBytesAsync(items[index].DestinationPath)));
        }
        finally
        {
            foreach (var server in servers) server.Dispose();
        }
    }

    [Fact]
    public async Task PausingOneDownloadLeavesTheOthersProgressing()
    {
        using var database = new TestDatabase();
        var servers = new List<RangeTestServer>();
        var payloads = new List<byte[]>();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var payload = Payload(4 * 1024 * 1024, index);
                payloads.Add(payload);
                servers.Add(new RangeTestServer(payload, chunkDelayMs: 25));
            }

            var manager = CreateManager(database, connections: 1, maximumActive: 4);
            var items = new List<DownloadItem>();
            for (var index = 0; index < 4; index++)
                items.Add(await manager.StartAsync(new DownloadRequest($"{servers[index].BaseUrl}/files/pause{index}.bin", database.Folder, $"pause{index}.bin", Connections: 1)));

            await WaitUntilAsync(() => items[0].DownloadedBytes > payloads[0].Length / 4, TimeSpan.FromSeconds(15));
            manager.Pause(items[0]);
            await WaitUntilAsync(() => items[0].Status == DownloadStatus.Paused, TimeSpan.FromSeconds(10));

            var before = items.Skip(1).Select(item => item.DownloadedBytes).ToArray();
            await Task.Delay(600);
            var after = items.Skip(1).Select(item => item.DownloadedBytes).ToArray();
            for (var index = 0; index < before.Length; index++)
                Assert.True(after[index] > before[index], $"download {index + 1} stopped when download 0 was paused");

            var pausedBytes = new FileInfo(Path.Combine(database.Folder, "pause0.bin.part")).Length;
            Assert.True(pausedBytes > 0 && pausedBytes < payloads[0].Length, "the pause must keep a partial file");
            await WaitUntilAsync(() => items[0].DownloadedBytes == pausedBytes, TimeSpan.FromSeconds(5));

            manager.Resume(items[0]);
            await WaitUntilAsync(() => items.All(item => item.Status == DownloadStatus.Completed), Timeout);

            Assert.Equal($"bytes={pausedBytes}-", servers[0].Requests[^1].Range);
            for (var index = 0; index < 4; index++)
                Assert.Equal(Sha256(payloads[index]), Sha256(await File.ReadAllBytesAsync(items[index].DestinationPath)));
        }
        finally
        {
            foreach (var server in servers) server.Dispose();
        }
    }

    [Fact]
    public async Task TwoPausedDownloadsResumeIndependentlyWhileTheOthersKeepRunning()
    {
        using var database = new TestDatabase();
        var servers = new List<RangeTestServer>();
        var payloads = new List<byte[]>();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var payload = Payload(4 * 1024 * 1024, index + 10);
                payloads.Add(payload);
                servers.Add(new RangeTestServer(payload, chunkDelayMs: 25));
            }

            var manager = CreateManager(database, connections: 1, maximumActive: 4);
            var items = new List<DownloadItem>();
            for (var index = 0; index < 4; index++)
                items.Add(await manager.StartAsync(new DownloadRequest($"{servers[index].BaseUrl}/files/multi{index}.bin", database.Folder, $"multi{index}.bin", Connections: 1)));

            await WaitUntilAsync(() => items[0].DownloadedBytes > payloads[0].Length / 8 && items[1].DownloadedBytes > payloads[1].Length / 8, TimeSpan.FromSeconds(15));
            manager.Pause(items[0]);
            manager.Pause(items[1]);
            await WaitUntilAsync(() => items[0].Status == DownloadStatus.Paused && items[1].Status == DownloadStatus.Paused, TimeSpan.FromSeconds(10));

            var firstPaused = new FileInfo(Path.Combine(database.Folder, "multi0.bin.part")).Length;
            var secondPaused = new FileInfo(Path.Combine(database.Folder, "multi1.bin.part")).Length;
            await WaitUntilAsync(() => items[0].DownloadedBytes == firstPaused && items[1].DownloadedBytes == secondPaused, TimeSpan.FromSeconds(5));

            manager.Resume(items[1]);
            await WaitUntilAsync(() => items[1].Status == DownloadStatus.Completed, Timeout);
            Assert.Equal(DownloadStatus.Paused, items[0].Status);
            Assert.Equal(firstPaused, items[0].DownloadedBytes);

            manager.Resume(items[0]);
            await WaitUntilAsync(() => items.All(item => item.Status == DownloadStatus.Completed), Timeout);

            Assert.Equal($"bytes={firstPaused}-", servers[0].Requests[^1].Range);
            Assert.Contains(servers[1].Requests, request => request.Range == $"bytes={secondPaused}-");
            for (var index = 0; index < 4; index++)
                Assert.Equal(Sha256(payloads[index]), Sha256(await File.ReadAllBytesAsync(items[index].DestinationPath)));
        }
        finally
        {
            foreach (var server in servers) server.Dispose();
        }
    }

    [Fact]
    public async Task CancellingOneDownloadDoesNotDisturbTheOthers()
    {
        using var database = new TestDatabase();
        var servers = new List<RangeTestServer>();
        var payloads = new List<byte[]>();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var payload = Payload(3 * 1024 * 1024, index + 20);
                payloads.Add(payload);
                servers.Add(new RangeTestServer(payload, chunkDelayMs: 25));
            }

            var manager = CreateManager(database, connections: 1, maximumActive: 4);
            var items = new List<DownloadItem>();
            for (var index = 0; index < 4; index++)
                items.Add(await manager.StartAsync(new DownloadRequest($"{servers[index].BaseUrl}/files/cancel{index}.bin", database.Folder, $"cancel{index}.bin", Connections: 1)));

            await WaitUntilAsync(() => items[3].DownloadedBytes > 0, TimeSpan.FromSeconds(15));
            manager.Cancel(items[3]);
            await WaitUntilAsync(() => items[3].Status == DownloadStatus.Cancelled, TimeSpan.FromSeconds(10));

            await WaitUntilAsync(() => items.Take(3).All(item => item.Status == DownloadStatus.Completed), Timeout);
            for (var index = 0; index < 3; index++)
                Assert.Equal(Sha256(payloads[index]), Sha256(await File.ReadAllBytesAsync(items[index].DestinationPath)));
            Assert.False(File.Exists(items[3].DestinationPath));
        }
        finally
        {
            foreach (var server in servers) server.Dispose();
        }
    }

    [Fact]
    public async Task PausedDownloadsDoNotOccupyActiveSlots()
    {
        using var database = new TestDatabase();
        var servers = new List<RangeTestServer>();
        try
        {
            var payload = Payload(4 * 1024 * 1024, 30);
            for (var index = 0; index < 4; index++) servers.Add(new RangeTestServer(payload, chunkDelayMs: 25));

            var manager = CreateManager(database, connections: 1, maximumActive: 2);
            var first = await manager.StartAsync(new DownloadRequest($"{servers[0].BaseUrl}/files/slot0.bin", database.Folder, "slot0.bin", Connections: 1));
            await WaitUntilAsync(() => first.DownloadedBytes > payload.Length / 8, TimeSpan.FromSeconds(15));

            var second = await manager.StartAsync(new DownloadRequest($"{servers[1].BaseUrl}/files/slot1.bin", database.Folder, "slot1.bin", Connections: 1));
            var third = await manager.StartAsync(new DownloadRequest($"{servers[2].BaseUrl}/files/slot2.bin", database.Folder, "slot2.bin", Connections: 1));
            Assert.Equal(DownloadStatus.Queued, third.Status);

            manager.Pause(first);
            await WaitUntilAsync(() => first.Status == DownloadStatus.Paused, TimeSpan.FromSeconds(10));

            // The freed slot must go to the queued download, not stay blocked by the paused one.
            await WaitUntilAsync(() => third.Status == DownloadStatus.Downloading, TimeSpan.FromSeconds(10));
            Assert.Equal(DownloadStatus.Downloading, third.Status);
            Assert.Equal(DownloadStatus.Downloading, second.Status);
        }
        finally
        {
            foreach (var server in servers) server.Dispose();
        }
    }

    [Fact]
    public async Task ANewManagerRecoversFourUnfinishedDownloadsWithoutDuplicates()
    {
        using var database = new TestDatabase();
        var servers = new List<RangeTestServer>();
        var payloads = new List<byte[]>();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var payload = Payload(4 * 1024 * 1024, index + 40);
                payloads.Add(payload);
                servers.Add(new RangeTestServer(payload, chunkDelayMs: 25));
            }

            var firstRun = CreateManager(database, connections: 1, maximumActive: 4);
            var items = new List<DownloadItem>();
            for (var index = 0; index < 4; index++)
                items.Add(await firstRun.StartAsync(new DownloadRequest($"{servers[index].BaseUrl}/files/recover{index}.bin", database.Folder, $"recover{index}.bin", Connections: 1)));

            await WaitUntilAsync(() => items.All(item => item.DownloadedBytes > 512 * 1024), TimeSpan.FromSeconds(20));
            foreach (var item in items) firstRun.Pause(item);
            await WaitUntilAsync(() => items.All(item => item.Status == DownloadStatus.Paused), TimeSpan.FromSeconds(15));

            var partialSizes = items.Select(item => new FileInfo(Path.Combine(database.Folder, item.FileName + ".part")).Length).ToArray();
            Assert.All(partialSizes, size => Assert.True(size > 0));

            // Reopen the application: a brand-new manager over the same database and folder.
            var secondRun = CreateManager(database, connections: 1, maximumActive: 4);
            var history = await secondRun.LoadHistoryAsync();
            Assert.Equal(4, history.Count);

            var recovered = history
                .Select(record => secondRun.CreateHistoryItem(record))
                .OrderBy(item => item.FileName, StringComparer.Ordinal)
                .ToList();
            Assert.Equal(4, recovered.Count);

            foreach (var item in recovered)
            {
                Assert.Equal(DownloadStatus.Paused, item.Status);
                var partial = new FileInfo(Path.Combine(database.Folder, item.FileName + ".part"));
                Assert.True(partial.Exists, $"{item.FileName}.part disappeared");
                Assert.Equal(partial.Length, item.DownloadedBytes);
                Assert.Equal(partialSizes[items.FindIndex(original => original.FileName == item.FileName)], partial.Length);
            }

            foreach (var item in recovered) secondRun.Resume(item);
            await WaitUntilAsync(() => recovered.All(item => item.Status == DownloadStatus.Completed), Timeout);

            Assert.Equal(4, (await secondRun.LoadHistoryAsync()).Count);
            foreach (var item in recovered)
            {
                var expected = payloads[servers.FindIndex(server => item.Url.StartsWith(server.BaseUrl, StringComparison.Ordinal))];
                Assert.Equal(Sha256(expected), Sha256(await File.ReadAllBytesAsync(item.DestinationPath)));
            }
        }
        finally
        {
            foreach (var server in servers) server.Dispose();
        }
    }

    [Fact]
    public async Task FourSegmentedDownloadsPauseAndResumeIndependently()
    {
        using var database = new TestDatabase();
        var servers = new List<RangeTestServer>();
        var payloads = new List<byte[]>();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var payload = Payload(6 * 1024 * 1024, index + 50);
                payloads.Add(payload);
                servers.Add(new RangeTestServer(payload, chunkDelayMs: 15));
            }

            // Four connections per download is the shipped default, so this is the realistic path.
            var manager = CreateManager(database, connections: 4, maximumActive: 4);
            var items = new List<DownloadItem>();
            for (var index = 0; index < 4; index++)
                items.Add(await manager.StartAsync(new DownloadRequest($"{servers[index].BaseUrl}/files/seg{index}.bin", database.Folder, $"seg{index}.bin", Connections: 4)));

            await WaitUntilAsync(() => items.All(item => item.DownloadedBytes > 256 * 1024), TimeSpan.FromSeconds(20));
            manager.Pause(items[1]);
            manager.Pause(items[2]);
            await WaitUntilAsync(() => items[1].Status == DownloadStatus.Paused && items[2].Status == DownloadStatus.Paused, TimeSpan.FromSeconds(15));

            var before = new[] { items[0].DownloadedBytes, items[3].DownloadedBytes };
            await Task.Delay(600);
            Assert.True(items[0].DownloadedBytes > before[0] || items[0].Status == DownloadStatus.Completed, "download 1 stalled when 2 and 3 were paused");
            Assert.True(items[3].DownloadedBytes > before[1] || items[3].Status == DownloadStatus.Completed, "download 4 stalled when 2 and 3 were paused");

            Assert.True(File.Exists(Path.Combine(database.Folder, "seg1.bin.part")), "a paused segmented download must keep its partial file");
            Assert.True(items[1].DownloadedBytes < payloads[1].Length, "a paused segmented download must not report itself finished");

            manager.Resume(items[2]);
            await WaitUntilAsync(() => items[2].Status == DownloadStatus.Completed, Timeout);
            Assert.Equal(DownloadStatus.Paused, items[1].Status);

            manager.Resume(items[1]);
            await WaitUntilAsync(() => items.All(item => item.Status == DownloadStatus.Completed), Timeout);

            for (var index = 0; index < 4; index++)
                Assert.Equal(Sha256(payloads[index]), Sha256(await File.ReadAllBytesAsync(items[index].DestinationPath)));
        }
        finally
        {
            foreach (var server in servers) server.Dispose();
        }
    }

    [Fact]
    public async Task RecoveredSegmentedDownloadsReportTheirRealProgress()
    {
        using var database = new TestDatabase();
        var payload = Payload(6 * 1024 * 1024, 60);
        using var server = new RangeTestServer(payload, chunkDelayMs: 20);
        {
            var firstRun = CreateManager(database, connections: 4, maximumActive: 4);
            var item = await firstRun.StartAsync(new DownloadRequest($"{server.BaseUrl}/files/seg-recover.bin", database.Folder, "seg-recover.bin", Connections: 4));

            await WaitUntilAsync(() => item.DownloadedBytes > 512 * 1024, TimeSpan.FromSeconds(20));
            firstRun.Pause(item);
            await WaitUntilAsync(() => item.Status == DownloadStatus.Paused, TimeSpan.FromSeconds(15));
            var reported = item.DownloadedBytes;

            var secondRun = CreateManager(database, connections: 4, maximumActive: 4);
            var recovered = secondRun.CreateHistoryItem((await secondRun.LoadHistoryAsync()).Single());

            Assert.Equal(DownloadStatus.Paused, recovered.Status);
            Assert.Equal(reported, recovered.DownloadedBytes);
            Assert.True(recovered.DownloadedBytes < recovered.TotalBytes, "a resumed segmented download must not look complete");

            secondRun.Resume(recovered);
            await WaitUntilAsync(() => recovered.Status == DownloadStatus.Completed, Timeout);
            Assert.Equal(Sha256(payload), Sha256(await File.ReadAllBytesAsync(Path.Combine(database.Folder, "seg-recover.bin"))));
        }
    }

    [Fact]
    public async Task AResumedDownloadIsResumingNotQueuedAndCanBePausedOrCancelled()
    {
        using var database = new TestDatabase();
        var payload = Payload(4 * 1024 * 1024, 80);
        using var first = new RangeTestServer(payload, chunkDelayMs: 25);
        using var second = new RangeTestServer(payload, chunkDelayMs: 25);

        var manager = CreateManager(database, connections: 1, maximumActive: 1);
        var running = await manager.StartAsync(new DownloadRequest($"{first.BaseUrl}/files/running.bin", database.Folder, "running.bin", Connections: 1));
        var waiting = await manager.StartAsync(new DownloadRequest($"{second.BaseUrl}/files/waiting.bin", database.Folder, "waiting.bin", Connections: 1));

        await WaitUntilAsync(() => running.Status == DownloadStatus.Downloading, TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Queued, waiting.Status);

        manager.Pause(waiting);
        Assert.Equal(DownloadStatus.Paused, waiting.Status);

        manager.Resume(waiting);
        Assert.Equal(DownloadStatus.Resuming, waiting.Status);
        Assert.Equal("Resuming", waiting.StatusText);

        manager.Pause(waiting);
        Assert.Equal(DownloadStatus.Paused, waiting.Status);

        manager.Resume(waiting);
        manager.Cancel(waiting);
        Assert.Equal(DownloadStatus.Cancelled, waiting.Status);

        await WaitUntilAsync(() => running.Status == DownloadStatus.Completed, Timeout);
        Assert.False(File.Exists(Path.Combine(database.Folder, "waiting.bin")));
        Assert.Empty(second.Requests);
    }

    [Fact]
    public void TheCheckpointSurvivesARoundTrip()
    {
        using var folder = new TemporaryFolder();
        var path = Path.Combine(folder.Path, "file.part.meta");
        var checkpoint = new DownloadCheckpoint(1, "http://host/file", "http://host/real", "file", 900, 450, "\"v1\"", null, true, 4, [100, 150, 100, 100]);

        DownloadCheckpoint.Write(path, checkpoint);
        var read = DownloadCheckpoint.Read(path);

        Assert.NotNull(read);
        Assert.Equal(4, read!.Segments);
        Assert.Equal(450, read.DownloadedBytes);
        Assert.Equal(900, read.TotalBytes);
        Assert.Equal(checkpoint.Written, read.Written);
        Assert.Equal("\"v1\"", read.Validator);
    }

    [Fact]
    public async Task ACrashBeforeTheFirstSaveDoesNotReportAPreallocatedPartAsFinished()
    {
        using var database = new TestDatabase();
        var payload = Payload(6 * 1024 * 1024, 70);
        using var server = new RangeTestServer(payload, chunkDelayMs: 20);
        {
            var firstRun = CreateManager(database, connections: 4, maximumActive: 4);
            var item = await firstRun.StartAsync(new DownloadRequest($"{server.BaseUrl}/files/crash-prealloc.bin", database.Folder, "crash-prealloc.bin", Connections: 4));

            await WaitUntilAsync(() => item.DownloadedBytes > 512 * 1024, TimeSpan.FromSeconds(20));
            firstRun.Pause(item);
            await WaitUntilAsync(() => item.Status == DownloadStatus.Paused, TimeSpan.FromSeconds(15));

            // Simulate dying before the first progress save: the row still says zero bytes, while the
            // preallocated partial file already has its full length on disk.
            var repository = new DownloadRepository(database.Path);
            var record = (await repository.GetRecentAsync()).Single();
            await repository.UpdateAsync(record with { DownloadedBytes = 0 });

            Assert.Equal(payload.Length, new FileInfo(Path.Combine(database.Folder, "crash-prealloc.bin.part")).Length);

            var secondRun = CreateManager(database, connections: 4, maximumActive: 4);
            var recovered = secondRun.CreateHistoryItem((await secondRun.LoadHistoryAsync()).Single());

            Assert.Equal(DownloadStatus.Paused, recovered.Status);
            Assert.True(recovered.DownloadedBytes > 0, "the checkpoint holds the bytes that are really there");
            Assert.True(recovered.DownloadedBytes < payload.Length, "a paused preallocated partial must not look complete");

            secondRun.Resume(recovered);
            await WaitUntilAsync(() => recovered.Status == DownloadStatus.Completed, Timeout);
            Assert.Equal(Sha256(payload), Sha256(await File.ReadAllBytesAsync(Path.Combine(database.Folder, "crash-prealloc.bin"))));
        }
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
