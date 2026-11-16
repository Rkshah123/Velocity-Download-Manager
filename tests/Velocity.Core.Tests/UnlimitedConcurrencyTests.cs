using System.Security.Cryptography;
using Velocity.Core.Models;
using Velocity.Core.Tests.Support;
using Velocity.Data.Repositories;
using Velocity.DownloadEngine;
using Velocity.Services;

namespace Velocity.Core.Tests;

/// <summary>
/// The IDM-style "everything at once" behaviour: with no ceiling configured (the shipped default), a batch of
/// files of very different sizes all transfer simultaneously and each one lands byte-exact.
/// </summary>
public sealed class UnlimitedConcurrencyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    [Fact]
    public async Task EightDownloadsOfDifferentSizesAllProgressAtTheSameTime()
    {
        using var database = new TestDatabase();
        var servers = new List<RangeTestServer>();
        var payloads = new List<byte[]>();
        // A small file next to large ones: the size must not decide who gets to run.
        var megabytes = new[] { 3, 4, 5, 6, 8, 10, 12, 16 };
        try
        {
            for (var index = 0; index < megabytes.Length; index++)
            {
                var payload = Payload(megabytes[index] * 1024 * 1024, index);
                payloads.Add(payload);
                servers.Add(new RangeTestServer(payload, chunkDelayMs: 20));
            }

            // 0 is "no limit": the shipped default, and the setting the user asked for.
            var manager = CreateManager(database);
            var items = new List<DownloadItem>();
            for (var index = 0; index < megabytes.Length; index++)
                items.Add(await manager.StartAsync(new DownloadRequest(
                    $"{servers[index].BaseUrl}/files/mixed{index}.bin", database.Folder, $"mixed{index}.bin", Connections: 4)));

            // The peak is what matters, not one instant: an assertion on a single sample would only be a race
            // between eight transfers that are all intentionally started at the same moment.
            var peak = 0;
            await WaitUntilAsync(() =>
            {
                peak = Math.Max(peak, servers.Count(server => server.InFlight > 0));
                return items.All(item => item.Status == DownloadStatus.Completed);
            }, Timeout);

            Assert.True(peak == servers.Count, $"only {peak} of {servers.Count} files ever transferred at the same time");
            Assert.DoesNotContain(items, item => item.Status == DownloadStatus.Queued);

            for (var index = 0; index < megabytes.Length; index++)
            {
                var saved = await File.ReadAllBytesAsync(items[index].DestinationPath);
                Assert.Equal(payloads[index].Length, saved.Length);
                Assert.Equal(Sha256(payloads[index]), Sha256(saved));
            }

            // Nothing was left half-written, and the history agrees with what is on disk.
            await Task.Delay(300);
            var history = await manager.LoadHistoryAsync();
            Assert.Equal(megabytes.Length, history.Count);
            Assert.All(history, record => Assert.Equal(DownloadStatus.Completed, record.Status));
            foreach (var item in items)
                Assert.False(File.Exists(item.DestinationPath + ".part"), $"{item.FileName}.part was left behind");
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
        Assert.Fail("The condition was not reached before the timeout.");
    }
}
