using Velocity.Core.Models;
using Velocity.Core.Tests.Support;
using Velocity.DownloadEngine;

namespace Velocity.Core.Tests;

/// <summary>
/// The promise of a pause is that resuming continues from the byte on disk, and that a host which cannot continue
/// from it is refused instead of starting the file over. These are the server behaviours that hid a restart from
/// zero: a host that ignores ranges, one that sends only a weak ETag (so <c>If-Range</c> is ignored and the whole
/// file is sent), and one that refuses the resume probe while still honouring real ranges.
/// </summary>
public sealed class ResumeRobustnessTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task AResumeIsRefusedWhenTheServerIgnoresRanges()
    {
        var payload = Payload(6 * 1024 * 1024, 51);
        using var server = new RangeTestServer(payload, chunkDelayMs: 10) { HonorRanges = false };
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/files/no-ranges.bin", folder.Path, FileName: "no-ranges.bin", Connections: 1);

        var paused = await PauseAsync(engine, request, payload.Length / 3, folder.Path);
        Assert.True(paused > 0);

        // The host only offers the whole file, so it cannot continue this download. The saved bytes stay on disk
        // until the user asks for the file again; nothing is silently restarted or truncated.
        await Assert.ThrowsAsync<DownloadLinkExpiredException>(() => engine.DownloadAsync(request));

        var partial = Path.Combine(folder.Path, "no-ranges.bin.part");
        Assert.Equal(paused, new FileInfo(partial).Length);
        Assert.False(File.Exists(Path.Combine(folder.Path, "no-ranges.bin")));
    }

    [Fact]
    public async Task AWeakETagDoesNotRestartASegmentedResume()
    {
        var payload = Payload(6 * 1024 * 1024, 52);
        // A weak ETag is not a valid If-Range validator, and without Last-Modified there is nothing else to send.
        using var server = new RangeTestServer(payload, chunkDelayMs: 10) { WeakETag = true, SendLastModified = false };
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/files/weak-etag.bin", folder.Path, FileName: "weak-etag.bin", Connections: 4);

        var paused = await PauseAsync(engine, request, payload.Length / 4, folder.Path);
        Assert.True(paused > 0);
        var requestsBeforeResume = server.Requests.Count;

        var reports = new List<long>();
        var result = await engine.DownloadAsync(request, new InlineProgress(value =>
        {
            lock (reports) reports.Add(value.DownloadedBytes);
        }));

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
        lock (reports)
        {
            Assert.NotEmpty(reports);
            Assert.All(reports, bytes => Assert.True(bytes >= paused,
                $"the resumed transfer reported {bytes} bytes after pausing at {paused}: it started over"));
        }

        // A weak ETag is unusable for If-Range, but it must not cost the multi-connection transfer either: the
        // first run keeps all of its connections and the resume keeps them too.
        static List<string> Ranges(IEnumerable<ServedRequest> requests) => requests
            .Where(request => request.Range is not null && request.Range != "bytes=0-0")
            .Select(request => request.Range!)
            .ToList();

        Assert.True(Ranges(server.Requests.Take(requestsBeforeResume)).Distinct().Count() >= 2,
            "the transfer did not use several connections, so the If-Range validator was refused");
        var resumedRanges = Ranges(server.Requests.Skip(requestsBeforeResume));
        Assert.True(resumedRanges.Distinct().Count() >= 2, "the resume stopped using its connections");
        Assert.DoesNotContain(resumedRanges, range => range.StartsWith("bytes=0-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedProbeDoesNotRestartASegmentedResume()
    {
        var payload = Payload(6 * 1024 * 1024, 53);
        using var server = new RangeTestServer(payload, chunkDelayMs: 10);
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/files/refused-probe.bin", folder.Path, FileName: "refused-probe.bin", Connections: 4);

        // The one-byte probe starts failing only after the transfer is under way, exactly like a host whose range
        // support is flaky: the checkpoint still knows everything a resume needs.
        using var pause = new CancellationTokenSource();
        var progress = new InlineProgress(value =>
        {
            if (value.DownloadedBytes < payload.Length / 4) return;
            server.RejectProbe = true;
            pause.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.DownloadAsync(request, progress, pause.Token));
        var paused = DownloadedAsync(Path.Combine(folder.Path, "refused-probe.bin.part"));
        Assert.True(paused > 0);

        var reports = new List<long>();
        var result = await engine.DownloadAsync(request, new InlineProgress(value =>
        {
            lock (reports) reports.Add(value.DownloadedBytes);
        }));

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.True(result.ResumeSupported);
        lock (reports)
        {
            Assert.NotEmpty(reports);
            Assert.All(reports, bytes => Assert.True(bytes >= paused,
                $"the resumed transfer reported {bytes} bytes after pausing at {paused}: it started over"));
        }
    }

    /// <summary>Pauses a transfer once it passes <paramref name="threshold"/> and returns the bytes the disk kept.</summary>
    private static async Task<long> PauseAsync(HttpDownloadEngine engine, DownloadRequest request, long threshold, string folder)
    {
        using var pause = new CancellationTokenSource();
        var progress = new InlineProgress(value =>
        {
            if (value.DownloadedBytes >= threshold) pause.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.DownloadAsync(request, progress, pause.Token));
        var partial = Path.Combine(folder, request.FileName!) + ".part";
        Assert.True(File.Exists(partial), "pausing must keep the partial file");
        return DownloadedAsync(partial);
    }

    /// <summary>
    /// The bytes really written. A multi-connection partial is preallocated, so its length is the whole file and the
    /// checkpoint is the only trustworthy count.
    /// </summary>
    private static long DownloadedAsync(string partial)
    {
        var checkpoint = DownloadCheckpoint.Read(partial + ".meta");
        return checkpoint?.DownloadedBytes ?? new FileInfo(partial).Length;
    }

    private static byte[] Payload(int size, int seed)
    {
        var payload = new byte[size];
        new Random(seed).NextBytes(payload);
        return payload;
    }
}
