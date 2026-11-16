using System.Security.Cryptography;
using Velocity.Core.Models;
using Velocity.Core.Tests.Support;
using Velocity.Data.Repositories;
using Velocity.DownloadEngine;
using Velocity.Services;

namespace Velocity.Core.Tests;

/// <summary>
/// Pause, resume, cancel and retry while several other downloads are running. The controls must act on the one
/// download they were given and leave every other transfer alone — the reported failures came from exactly this
/// area, where a download that lost its slot also lost its progress or its file.
/// </summary>
public sealed class ConcurrentControlTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task PauseResumeCancelAndRetryEachActOnOneOfSeveralRunningDownloads()
    {
        using var database = new TestDatabase();
        var servers = new List<RangeTestServer>();
        var payloads = new List<byte[]>();
        try
        {
            for (var index = 0; index < 5; index++)
            {
                var payload = Payload(2 * 1024 * 1024, index + 70);
                payloads.Add(payload);
                servers.Add(new RangeTestServer(payload, chunkDelayMs: 30));
            }

            var manager = CreateManager(database);
            var items = new List<DownloadItem>();
            for (var index = 0; index < servers.Count; index++)
                items.Add(await manager.StartAsync(new DownloadRequest(
                    $"{servers[index].BaseUrl}/files/control{index}.bin", database.Folder, $"control{index}.bin", Connections: 4)));

            await WaitUntilAsync(() => items.All(item => item.Status == DownloadStatus.Downloading), Timeout);

            // Pause two of the five while the other three keep transferring.
            manager.Pause(items[1]);
            manager.Pause(items[3]);
            await WaitUntilAsync(() => items[1].Status == DownloadStatus.Paused && items[3].Status == DownloadStatus.Paused, Timeout);

            var pausedBytes = (items[1].DownloadedBytes, items[3].DownloadedBytes);
            var runningBytes = items[2].DownloadedBytes;
            await Task.Delay(600);

            Assert.Equal(pausedBytes.Item1, items[1].DownloadedBytes);
            Assert.Equal(pausedBytes.Item2, items[3].DownloadedBytes);
            Assert.True(items[2].DownloadedBytes > runningBytes, "the downloads that were not paused stopped as well");

            // Resume one of them: it finishes from where it stopped, not from zero.
            manager.Resume(items[1]);
            await WaitUntilAsync(() => items[1].Status is DownloadStatus.Completed or DownloadStatus.Failed, Timeout);
            Assert.Equal(DownloadStatus.Completed, items[1].Status);
            Assert.Equal(Sha256(payloads[1]), Sha256(await File.ReadAllBytesAsync(items[1].DestinationPath)));

            // Cancel the other one: it ends cancelled and leaves no finished file behind.
            manager.Cancel(items[3]);
            await WaitUntilAsync(() => items[3].Status == DownloadStatus.Cancelled, Timeout);
            Assert.False(File.Exists(items[3].DestinationPath));

            // The downloads that were never touched still finish.
            await WaitUntilAsync(() => items.All(item => item.Status is DownloadStatus.Completed or DownloadStatus.Cancelled), Timeout);
            foreach (var index in new[] { 0, 2, 4 })
            {
                Assert.Equal(DownloadStatus.Completed, items[index].Status);
                Assert.Equal(Sha256(payloads[index]), Sha256(await File.ReadAllBytesAsync(items[index].DestinationPath)));
            }

            // A retry after a real failure: the server cuts the connection, the download fails, and the same item
            // completes once the server behaves again.
            var flaky = new RangeTestServer(Payload(1024 * 1024, 99), chunkDelayMs: 0) { DropAfterBytes = 64 * 1024 };
            try
            {
                var failing = await manager.StartAsync(new DownloadRequest(
                    $"{flaky.BaseUrl}/files/flaky.bin", database.Folder, "flaky.bin", Connections: 2));
                await WaitUntilAsync(() => failing.Status == DownloadStatus.Failed, TimeSpan.FromSeconds(60));

                flaky.DropAfterBytes = null;
                manager.Retry(failing);
                await WaitUntilAsync(() => failing.Status is DownloadStatus.Completed or DownloadStatus.Failed, TimeSpan.FromSeconds(60));
                Assert.Equal(DownloadStatus.Completed, failing.Status);
                Assert.Equal(1024 * 1024, new FileInfo(failing.DestinationPath).Length);
            }
            finally
            {
                flaky.Dispose();
            }
        }
        finally
        {
            foreach (var server in servers) server.Dispose();
        }
    }

    private static DownloadManager CreateManager(TestDatabase database)
    {
        var settings = new SettingsService(new SettingsRepository(database.Path));
        settings.LoadAsync().GetAwaiter().GetResult();
        settings.Current.DefaultDownloadDirectory = database.Folder;
        settings.Current.DefaultConnectionCount = 4;
        settings.Current.MaximumActiveDownloads = 0;
        settings.Current.RetryLimit = 1;
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
