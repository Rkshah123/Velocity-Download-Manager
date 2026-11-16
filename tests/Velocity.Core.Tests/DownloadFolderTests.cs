using Velocity.Core.Models;
using Velocity.Core.Tests.Support;
using Velocity.Data.Repositories;
using Velocity.DownloadEngine;
using Velocity.Services;

namespace Velocity.Core.Tests;

/// <summary>
/// Velocity must not build a folder tree around the user's downloads. A finished file lands in the folder the
/// download was pointed at and nothing else appears there — no "Videos", "Archives", "Documents" or "Other".
/// </summary>
public sealed class DownloadFolderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData("episode.mkv")]
    [InlineData("manual.pdf")]
    [InlineData("bundle.zip")]
    [InlineData("unknown-file")]
    [InlineData("setup.exe")]
    public async Task AFinishedDownloadCreatesNoCategoryFolders(string fileName)
    {
        using var database = new TestDatabase();
        // Its own empty folder, so any directory in it can only have been created by Velocity.
        var folder = Directory.CreateDirectory(Path.Combine(database.Folder, "chosen")).FullName;
        using var server = new RangeTestServer(new byte[512 * 1024], chunkDelayMs: 0);

        var manager = CreateManager(database);
        var item = await manager.StartAsync(new DownloadRequest($"{server.BaseUrl}/files/{Uri.EscapeDataString(fileName)}", folder, fileName, Connections: 2));

        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline && item.Status is DownloadStatus.Queued or DownloadStatus.Resuming or DownloadStatus.Downloading)
            await Task.Delay(50);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(folder, Path.GetDirectoryName(item.DestinationPath));
        Assert.True(File.Exists(item.DestinationPath), item.DestinationPath);

        var folders = Directory.GetDirectories(folder).Select(Path.GetFileName).ToArray();
        Assert.True(folders.Length == 0, $"Velocity created {string.Join(", ", folders)} next to the download");

        // The chosen folder holds the file and nothing else; a category folder anywhere under it would fail here.
        var entries = Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).ToArray();
        Assert.Single(entries);
    }

    private static DownloadManager CreateManager(TestDatabase database)
    {
        var settings = new SettingsService(new SettingsRepository(database.Path));
        settings.LoadAsync().GetAwaiter().GetResult();
        settings.Current.DefaultDownloadDirectory = database.Folder;
        settings.Current.DefaultConnectionCount = 2;
        settings.Current.MaximumActiveDownloads = 0;
        settings.Current.RetryLimit = 0;
        var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        return new DownloadManager(new HttpDownloadEngine(client), new DownloadRepository(database.Path), settings);
    }
}
