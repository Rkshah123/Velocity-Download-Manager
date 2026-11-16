using System.Security.Cryptography;
using Velocity.Core.Models;
using Velocity.Core.Tests.Support;
using Velocity.Data.Repositories;
using Velocity.DownloadEngine;
using Velocity.Services;

namespace Velocity.Core.Tests;

/// <summary>
/// Several downloads that were given the same file name — the case the browser hands over when a site serves the
/// same episode from two mirrors, or two clicks on the same link. They are different files, so each one must keep
/// its own partial file and land under its own name instead of one of them failing on the share violation.
/// </summary>
public sealed class SameNameConcurrencyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    [Fact]
    public async Task DownloadsSharingOneNameAllCompleteAndKeepTheirOwnBytes()
    {
        using var database = new TestDatabase();
        var servers = new List<RangeTestServer>();
        var payloads = new List<byte[]>();
        try
        {
            // Different content behind one name: a mixed-up file would be visible as a hash mismatch.
            for (var index = 0; index < 4; index++)
            {
                var payload = Payload(2 * 1024 * 1024 + index * 1024, index + 40);
                payloads.Add(payload);
                servers.Add(new RangeTestServer(payload, chunkDelayMs: 15));
            }

            var manager = CreateManager(database);
            var items = new List<DownloadItem>();
            for (var index = 0; index < servers.Count; index++)
                items.Add(await manager.StartAsync(new DownloadRequest(
                    $"{servers[index].BaseUrl}/files/same{index}.bin", database.Folder, "episode.mkv", Connections: 4)));

            await WaitUntilAsync(() => items.All(item => item.Status is DownloadStatus.Completed or DownloadStatus.Failed), Timeout);

            Assert.DoesNotContain(items, item => item.Status == DownloadStatus.Failed);
            Assert.Equal(4, items.Select(item => item.DestinationPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());

            for (var index = 0; index < items.Count; index++)
            {
                var saved = await File.ReadAllBytesAsync(items[index].DestinationPath);
                Assert.Equal(Sha256(payloads[index]), Sha256(saved));
            }

            await Task.Delay(300);
            var history = await manager.LoadHistoryAsync();
            Assert.Equal(4, history.Count);
            Assert.All(history, record => Assert.Equal(DownloadStatus.Completed, record.Status));
            Assert.Equal(4, history.Select(record => record.DestinationPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
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

        Assert.Fail("The downloads did not finish before the timeout.");
    }
}
