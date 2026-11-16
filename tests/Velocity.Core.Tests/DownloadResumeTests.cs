using Microsoft.Data.Sqlite;
using Velocity.Core.Models;
using Velocity.Data.Database;
using Velocity.Data.Repositories;
using Velocity.DownloadEngine;
using Velocity.Services;
using Velocity.Core.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Velocity.Core.Tests;

/// <summary>
/// Pause/resume behaviour against a real HTTP server: the bytes on disk after a pause must be the bytes the
/// next request starts from, and no path may mix data from two versions of a file.
/// </summary>
public sealed class DownloadResumeTests
{
    [Fact]
    public async Task PauseKeepsThePartialFileAndResumeAsksForTheRemainingBytes()
    {
        var payload = Payload(4 * 1024 * 1024);
        using var server = new RangeTestServer(payload);
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/files/big.bin", folder.Path, Connections: 1);

        using var pause = new CancellationTokenSource();
        var reports = new List<DownloadProgress>();
        var progress = new InlineProgress(value =>
        {
            lock (reports) reports.Add(value);
            if (value.DownloadedBytes > payload.Length / 3) pause.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.DownloadAsync(request, progress, pause.Token));

        var part = Path.Combine(folder.Path, "big.bin.part");
        Assert.True(File.Exists(part), "pausing must keep the partial file");
        var pausedBytes = new FileInfo(part).Length;
        Assert.InRange(pausedBytes, 1, payload.Length - 1);
        Assert.True(File.Exists(part + ".meta"), "the resume checkpoint must survive a pause");

        var result = await engine.DownloadAsync(request);

        Assert.Equal(payload.Length, result.DownloadedBytes);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Equal($"bytes={pausedBytes}-", server.Requests[^1].Range);
        Assert.False(File.Exists(part));
        Assert.False(File.Exists(part + ".meta"));
        lock (reports) Assert.All(reports, report => Assert.True(report.DownloadedBytes <= payload.Length));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    public async Task ResumingAfterPausingAtAnyProgressPointContinuesFromTheStoredBytes(int percent)
    {
        var payload = Payload(8 * 1024 * 1024);
        using var server = new RangeTestServer(payload, chunkDelayMs: 40);
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/files/large.bin", folder.Path, Connections: 1);
        var threshold = payload.Length * percent / 100L;

        using var pause = new CancellationTokenSource();
        var progress = new InlineProgress(value =>
        {
            if (value.DownloadedBytes >= threshold) pause.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.DownloadAsync(request, progress, pause.Token));

        var part = Path.Combine(folder.Path, "large.bin.part");
        Assert.True(File.Exists(part), "pausing must keep the partial file");
        var pausedBytes = new FileInfo(part).Length;
        Assert.InRange(pausedBytes, threshold, payload.Length - 1);

        var result = await engine.DownloadAsync(request);

        Assert.Equal(payload.Length, result.DownloadedBytes);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Equal($"bytes={pausedBytes}-", server.Requests[^1].Range);
        Assert.False(File.Exists(part + ".meta"));
    }

    [Fact]
    public async Task RestartsSafelyWhenTheServerIgnoresRanges()
    {
        var payload = Payload(1024 * 1024);
        using var server = new RangeTestServer(payload) { HonorRanges = false };
        using var folder = new TemporaryFolder();
        await File.WriteAllBytesAsync(Path.Combine(folder.Path, "file.bin.part"), Payload(200_000));

        var engine = new HttpDownloadEngine(new HttpClient());
        var result = await engine.DownloadAsync(new DownloadRequest($"{server.BaseUrl}/file.bin", folder.Path, Connections: 1));

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.False(result.ResumeSupported);
    }

    [Fact]
    public async Task FinishesWithoutTransferringWhenThePartialFileIsAlreadyComplete()
    {
        var payload = Payload(512 * 1024);
        using var server = new RangeTestServer(payload) { RejectRanges = true };
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/done.bin", folder.Path, FileName: "done.bin", Connections: 1);

        // First run writes the checkpoint, then the file is completed behind our back.
        await File.WriteAllBytesAsync(Path.Combine(folder.Path, "done.bin.part"), payload);
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "done.bin.part.meta"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Version = 1,
                Url = request.Url,
                FinalUrl = (string?)null,
                FileName = "done.bin",
                TotalBytes = payload.Length,
                DownloadedBytes = payload.Length,
                ETag = (string?)null,
                LastModified = (string?)null,
                RangeSupported = true,
                Segments = 1,
                Written = new[] { payload.Length }
            }));

        var result = await engine.DownloadAsync(request);

        Assert.Equal(payload.Length, result.DownloadedBytes);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task RestartsWhenTheRemoteFileChangedWhilePaused()
    {
        var original = Payload(3 * 1024 * 1024);
        var replacement = PayloadReversed(3 * 1024 * 1024);
        using var server = new RangeTestServer(original);
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/changed.bin", folder.Path, Connections: 1);

        using var pause = new CancellationTokenSource();
        var progress = new InlineProgress(value => { if (value.DownloadedBytes > original.Length / 3) pause.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.DownloadAsync(request, progress, pause.Token));

        server.ReplacePayload(replacement);
        var result = await engine.DownloadAsync(request);

        Assert.Contains(server.Requests, request => request.IfRange is not null);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(result.FilePath));
        Assert.False(File.Exists(Path.Combine(folder.Path, "changed.bin.part")));
    }

    [Fact]
    public async Task RestartsWhenTheServerRejectsAnUntrustedRange()
    {
        var payload = Payload(1024 * 1024);
        using var server = new RangeTestServer(payload) { RejectRanges = true };
        using var folder = new TemporaryFolder();
        await File.WriteAllBytesAsync(Path.Combine(folder.Path, "stale.bin.part"), Payload(5000));

        var engine = new HttpDownloadEngine(new HttpClient());
        var result = await engine.DownloadAsync(new DownloadRequest($"{server.BaseUrl}/stale.bin", folder.Path, Connections: 1));

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.True(server.Requests.Count >= 2, "the engine must retry from zero after a 416");
        Assert.Equal("bytes=5000-", server.Requests[0].Range);
        Assert.Null(server.Requests[^1].Range);
    }

    [Fact]
    public async Task SegmentedDownloadsResumeEachConnectionFromItsCheckpoint()
    {
        var payload = Payload(6 * 1024 * 1024);
        using var server = new RangeTestServer(payload, chunkDelayMs: 15);
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/multi.bin", folder.Path, Connections: 4);

        using var pause = new CancellationTokenSource();
        var progress = new InlineProgress(value => { if (value.DownloadedBytes > payload.Length / 3) pause.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.DownloadAsync(request, progress, pause.Token));

        var part = Path.Combine(folder.Path, "multi.bin.part");
        Assert.True(File.Exists(part + ".meta"), "a paused multi-connection transfer must keep its checkpoint");

        var result = await engine.DownloadAsync(new DownloadRequest($"{server.BaseUrl}/multi.bin", folder.Path, Connections: 4));

        Assert.Equal(payload.Length, result.DownloadedBytes);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Contains(server.Requests.Skip(1), request => request.Range is not null && !request.Range.StartsWith("bytes=0-", StringComparison.Ordinal));
        Assert.False(File.Exists(part));
    }

    [Fact]
    public async Task UsesTheServerFileNameWhenTheCallerSuppliedNone()
    {
        var payload = Payload(300 * 1024);
        using var server = new RangeTestServer(payload) { DispositionName = "quarterly-report-final.pdf" };
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());

        var result = await engine.DownloadAsync(new DownloadRequest($"{server.BaseUrl}/download?id=42", folder.Path, Connections: 1));

        Assert.Equal("quarterly-report-final.pdf", Path.GetFileName(result.FilePath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
    }

    [Fact]
    public async Task SegmentedAttemptFallsBackWhenTheServerCannotDoRanges()
    {
        var payload = Payload(2 * 1024 * 1024);
        using var server = new RangeTestServer(payload) { HonorRanges = false };
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());

        var result = await engine.DownloadAsync(new DownloadRequest($"{server.BaseUrl}/plain.bin", folder.Path, Connections: 4));

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.False(result.ResumeSupported);
    }

    [Fact]
    public async Task PausedDownloadSurvivesAManagerRestartAndResumesFromTheStoredBytes()
    {
        var payload = Payload(5 * 1024 * 1024);
        using var server = new RangeTestServer(payload, chunkDelayMs: 20);
        using var database = new TestDatabase();
        var settings = new SettingsService(new SettingsRepository(database.Path));
        await settings.LoadAsync();
        settings.Current.DefaultDownloadDirectory = database.Folder;
        settings.Current.DefaultConnectionCount = 1;

        var url = $"{server.BaseUrl}/restart.bin";
        var first = new DownloadManager(new HttpDownloadEngine(new HttpClient()), new DownloadRepository(database.Path), settings);
        var item = await first.StartAsync(new DownloadRequest(url, database.Folder, Connections: 1));

        await WaitUntilAsync(() => item.DownloadedBytes > payload.Length / 3, TimeSpan.FromSeconds(10));
        first.Pause(item);
        await WaitUntilAsync(() => item.Status == DownloadStatus.Paused, TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Paused, item.Status);

        var part = item.DestinationPath + ".part";
        var pausedBytes = new FileInfo(part).Length;
        Assert.True(pausedBytes > 0);

        // A second manager with a fresh engine models closing and reopening Velocity.
        SqliteConnection.ClearAllPools();
        var second = new DownloadManager(new HttpDownloadEngine(new HttpClient()), new DownloadRepository(database.Path), settings);
        var record = (await second.LoadHistoryAsync()).Single(value => value.Url == url);
        var restored = second.CreateHistoryItem(record);

        Assert.Equal(DownloadStatus.Paused, restored.Status);
        Assert.Equal(pausedBytes, restored.DownloadedBytes);

        second.Resume(restored);
        await WaitUntilAsync(() => restored.Status is DownloadStatus.Completed or DownloadStatus.Failed, TimeSpan.FromSeconds(30));

        Assert.Equal(DownloadStatus.Completed, restored.Status);
        Assert.Equal(payload, await File.ReadAllBytesAsync(restored.DestinationPath));
        Assert.Equal($"bytes={pausedBytes}-", server.Requests[^1].Range);
        Assert.False(File.Exists(part));
    }

    /* ─────────────────────────── helpers ─────────────────────────── */

    private static byte[] Payload(int size)
    {
        var data = new byte[size];
        for (var index = 0; index < size; index++) data[index] = (byte)((index * 31 + 7) % 251);
        return data;
    }

    private static byte[] PayloadReversed(int size)
    {
        var data = Payload(size);
        Array.Reverse(data);
        return data;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("velocity-resume-db-");

        public TestDatabase()
        {
            Path = System.IO.Path.Combine(_directory.FullName, "test.db");
            new DatabaseInitializer(NullLogger<DatabaseInitializer>.Instance).InitializeAsync(Path).GetAwaiter().GetResult();
        }

        public string Path { get; }
        public string Folder => _directory.FullName;

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { _directory.Delete(true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
