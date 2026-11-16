using Microsoft.Data.Sqlite;
using Velocity.Core.Interfaces;
using Velocity.Core.Models;
using Velocity.Core.Tests.Support;
using Velocity.Data.Repositories;
using Velocity.Services;

namespace Velocity.Core.Tests;

public sealed class DownloadManagerTests
{
    [Fact]
    public async Task UsesTheFileNameFromTheUrlWhenNoneIsSupplied()
    {
        using var database = new TestDatabase();
        var manager = CreateManager(database, new RecordingEngine());
        var item = await manager.StartAsync(new DownloadRequest("https://example.com/files/quarterly-report.pdf", database.Folder, Connections: 1));

        await WaitUntilAsync(() => item.Status is DownloadStatus.Completed or DownloadStatus.Failed, TimeSpan.FromSeconds(5));

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal("quarterly-report.pdf", item.FileName);
    }

    [Fact]
    public async Task DeletingAQueuedDownloadStopsItFromEverStarting()
    {
        using var database = new TestDatabase();
        var engine = new GatedEngine();
        var settings = CreateSettings(database);
        settings.Current.MaximumActiveDownloads = 1;
        var manager = new DownloadManager(engine, new DownloadRepository(database.Path), settings);

        var first = await manager.StartAsync(new DownloadRequest("https://example.com/first.bin", database.Folder, Connections: 1));
        var second = await manager.StartAsync(new DownloadRequest("https://example.com/second.bin", database.Folder, Connections: 1));
        Assert.Equal(DownloadStatus.Queued, second.Status);

        var error = await manager.DeleteAsync(second, deleteFile: false);
        Assert.Null(error);

        engine.Release();
        await WaitUntilAsync(() => first.Status == DownloadStatus.Completed, TimeSpan.FromSeconds(5));
        await Task.Delay(250);

        Assert.DoesNotContain(engine.RequestedUrls, url => url.EndsWith("second.bin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StopsAfterTheConfiguredNumberOfRetries()
    {
        using var database = new TestDatabase();
        var engine = new FailingEngine();
        var settings = CreateSettings(database);
        settings.Current.RetryLimit = 2;
        var manager = new DownloadManager(engine, new DownloadRepository(database.Path), settings);

        var item = await manager.StartAsync(new DownloadRequest("https://example.com/broken.bin", database.Folder, Connections: 1));
        await WaitUntilAsync(() => item.Status == DownloadStatus.Failed, TimeSpan.FromSeconds(15));

        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.Equal(3, engine.Attempts);
    }

    [Fact]
    public async Task ASelectedDownloadStartsBeforeTheEarlierOnes()
    {
        using var database = new TestDatabase();
        var engine = new BlockingEngine();
        var settings = CreateSettings(database);
        settings.Current.MaximumActiveDownloads = 1;
        var manager = new DownloadManager(engine, new DownloadRepository(database.Path), settings);

        var running = await manager.StartAsync(new DownloadRequest("https://example.com/running.bin", database.Folder, Connections: 1));
        var earlier = await manager.StartAsync(new DownloadRequest("https://example.com/earlier.bin", database.Folder, Connections: 1));
        var later = await manager.StartAsync(new DownloadRequest("https://example.com/later.bin", database.Folder, Connections: 1));
        Assert.Equal(DownloadStatus.Queued, earlier.Status);
        Assert.Equal(DownloadStatus.Queued, later.Status);

        manager.Prioritize([later]);
        engine.Release();
        await WaitUntilAsync(() => earlier.Status == DownloadStatus.Completed, TimeSpan.FromSeconds(10));

        var order = engine.RequestedUrls.Select(url => url.Split('/')[^1]).ToList();
        Assert.Equal(new[] { "running.bin", "later.bin", "earlier.bin" }, order);
    }

    [Fact]
    public async Task PauseAllStopsEveryTransferAndResumeAllContinuesThem()
    {
        using var database = new TestDatabase();
        var engine = new BlockingEngine();
        var settings = CreateSettings(database);
        settings.Current.MaximumActiveDownloads = 0;
        var manager = new DownloadManager(engine, new DownloadRepository(database.Path), settings);

        var first = await manager.StartAsync(new DownloadRequest("https://example.com/one.bin", database.Folder, Connections: 1));
        var second = await manager.StartAsync(new DownloadRequest("https://example.com/two.bin", database.Folder, Connections: 1));
        await WaitUntilAsync(() => engine.RequestedUrls.Count == 2, TimeSpan.FromSeconds(5));

        manager.PauseAll([first, second]);
        await WaitUntilAsync(() => first.Status == DownloadStatus.Paused && second.Status == DownloadStatus.Paused, TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadStatus.Paused, first.Status);
        Assert.Equal(DownloadStatus.Paused, second.Status);

        engine.Release();
        await Task.Delay(200);
        Assert.Equal(DownloadStatus.Paused, first.Status);

        manager.ResumeAll([first, second]);
        await WaitUntilAsync(() => first.Status == DownloadStatus.Completed && second.Status == DownloadStatus.Completed, TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Completed, first.Status);
        Assert.Equal(DownloadStatus.Completed, second.Status);
    }

    [Fact]
    public async Task HeadersAndPrioritySurviveARestart()
    {
        using var database = new TestDatabase();
        var repository = new DownloadRepository(database.Path);
        await repository.AddAsync(new DownloadRecord(
            0, "file.bin", "https://example.com/file.bin", System.IO.Path.Combine(database.Folder, "file.bin"),
            10, 0, DownloadStatus.Queued,
            Headers: new Dictionary<string, string> { ["Cookie"] = "a=1", ["Referer"] = "https://example.com" },
            Priority: 3));

        var record = Assert.Single(await repository.GetRecentAsync());
        Assert.Equal("a=1", record.Headers!["Cookie"]);
        Assert.Equal("https://example.com", record.Headers["Referer"]);
        Assert.Equal(3, record.Priority);
    }

    private static DownloadManager CreateManager(TestDatabase database, IDownloadEngine engine) =>
        new(engine, new DownloadRepository(database.Path), CreateSettings(database));

    private static SettingsService CreateSettings(TestDatabase database)
    {
        var settings = new SettingsService(new SettingsRepository(database.Path));
        settings.LoadAsync().GetAwaiter().GetResult();
        settings.Current.DefaultDownloadDirectory = database.Folder;
        return settings;
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

    private sealed class RecordingEngine : IDownloadEngine
    {
        public Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var name = request.FileName ?? System.IO.Path.GetFileName(new Uri(request.Url).LocalPath);
            var path = System.IO.Path.Combine(request.DestinationDirectory, name);
            File.WriteAllText(path, "payload");
            progress?.Report(new DownloadProgress(7, 7, 0));
            return Task.FromResult(new DownloadResult(path, 7, 7));
        }
    }

    private sealed class GatedEngine : IDownloadEngine
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _requestedUrls = [];
        private int _calls;

        public IReadOnlyList<string> RequestedUrls
        {
            get { lock (_requestedUrls) return _requestedUrls.ToArray(); }
        }

        public void Release() => _gate.TrySetResult();

        public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            lock (_requestedUrls) _requestedUrls.Add(request.Url);
            if (Interlocked.Increment(ref _calls) == 1) await _gate.Task.WaitAsync(cancellationToken);

            var name = request.FileName ?? System.IO.Path.GetFileName(new Uri(request.Url).LocalPath);
            var path = System.IO.Path.Combine(request.DestinationDirectory, name);
            File.WriteAllText(path, "payload");
            return new DownloadResult(path, 7, 7);
        }
    }

    /// <summary>Holds every transfer open until released, so queue order can be observed instead of raced.</summary>
    private sealed class BlockingEngine : IDownloadEngine
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _urls = [];

        public IReadOnlyList<string> RequestedUrls
        {
            get { lock (_urls) return _urls.ToArray(); }
        }

        public void Release() => _open.TrySetResult();

        public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            lock (_urls) _urls.Add(request.Url);
            await _open.Task.WaitAsync(cancellationToken);

            var name = request.FileName ?? System.IO.Path.GetFileName(new Uri(request.Url).LocalPath);
            var path = System.IO.Path.Combine(request.DestinationDirectory, name);
            File.WriteAllText(path, "payload");
            return new DownloadResult(path, 7, 7);
        }
    }

    private sealed class FailingEngine : IDownloadEngine
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attempts);
            throw new HttpRequestException("Simulated failure.");
        }
    }
}
