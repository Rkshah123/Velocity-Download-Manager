using System.Security.Cryptography;
using Velocity.Core.Models;
using Velocity.Core.Tests.Support;
using Velocity.DownloadEngine;

namespace Velocity.Core.Tests;

/// <summary>
/// The server-behaviour matrix: resume must continue from the exact stored byte when the response allows it,
/// and must restart safely (never append) when it does not.
/// </summary>
public sealed class ResumeCompatibilityTests
{
    [Fact]
    public async Task ContinuesWhenTheServerOnlySendsTheETagOnFullResponses()
    {
        var payload = Payload(4 * 1024 * 1024);
        using var server = new RangeTestServer(payload);
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/files/etag.bin", folder.Path, "etag.bin", Connections: 1);

        var paused = await PauseAtAsync(engine, request, payload.Length / 3);
        Assert.True(paused > 0);

        // Some servers only attach the validator to the first, complete response.
        server.SendETagOnPartial = false;

        var result = await engine.DownloadAsync(request);

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Equal($"bytes={paused}-", server.Requests[^1].Range);
    }

    [Fact]
    public async Task ContinuesWhenTheServerOnlySendsLastModified()
    {
        var payload = Payload(4 * 1024 * 1024);
        using var server = new RangeTestServer(payload) { SendETag = false, VersionedLastModified = true };
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/files/lastmodified.bin", folder.Path, "lastmodified.bin", Connections: 1);

        var paused = await PauseAtAsync(engine, request, payload.Length / 3);
        Assert.True(paused > 0);

        var result = await engine.DownloadAsync(request);

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Equal($"bytes={paused}-", server.Requests[^1].Range);
        Assert.Contains(server.Requests, request => request.IfRange is not null);
    }

    [Fact]
    public async Task RestartsWhenLastModifiedChangesWhilePaused()
    {
        var payload = Payload(3 * 1024 * 1024);
        using var server = new RangeTestServer(payload) { SendETag = false, VersionedLastModified = true };
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/files/changed-date.bin", folder.Path, "changed-date.bin", Connections: 1);

        await PauseAtAsync(engine, request, payload.Length / 3);

        var replacement = Payload(3 * 1024 * 1024);
        server.ReplacePayload(replacement);

        var result = await engine.DownloadAsync(request);

        Assert.Equal(replacement, await File.ReadAllBytesAsync(result.FilePath));
        Assert.False(File.Exists(Path.Combine(folder.Path, "changed-date.bin.part")));
    }

    [Fact]
    public async Task ResumesWhenTheServerSendsNoValidatorsAtAll()
    {
        var payload = Payload(4 * 1024 * 1024);
        using var server = new RangeTestServer(payload) { SendETag = false, SendLastModified = false };
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/files/anonymous.bin", folder.Path, "anonymous.bin", Connections: 1);

        var paused = await PauseAtAsync(engine, request, payload.Length / 3);
        var result = await engine.DownloadAsync(request);

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Equal($"bytes={paused}-", server.Requests[^1].Range);
        Assert.DoesNotContain(server.Requests.Skip(1), request => request.IfRange is not null);
    }

    [Fact]
    public async Task RestartsFromZeroWhenTheServerAdvertisesRangesButIgnoresThem()
    {
        var payload = Payload(3 * 1024 * 1024);
        using var server = new RangeTestServer(payload) { HonorRanges = false, AdvertiseRanges = true };
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/files/liar.bin", folder.Path, "liar.bin", Connections: 1);

        var paused = await PauseAtAsync(engine, request, payload.Length / 3);
        Assert.True(paused > 0);

        var result = await engine.DownloadAsync(request);

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Contains(server.Requests.Skip(1), request => request.Range is not null);
        Assert.Equal(payload.Length, new FileInfo(result.FilePath).Length);
    }

    [Fact]
    public async Task ResumesWhenTheServerSupportsRangesButDoesNotAdvertiseThem()
    {
        var payload = Payload(4 * 1024 * 1024);
        using var server = new RangeTestServer(payload) { AdvertiseRanges = false };
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/files/hidden.bin", folder.Path, "hidden.bin", Connections: 1);

        var paused = await PauseAtAsync(engine, request, payload.Length / 3);
        var result = await engine.DownloadAsync(request);

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Equal($"bytes={paused}-", server.Requests[^1].Range);
    }

    [Fact]
    public async Task CompletesWithoutTransferringWhenAWholePartialIsRejectedWith416()
    {
        var payload = Payload(2 * 1024 * 1024);
        using var server = new RangeTestServer(payload) { RejectRanges = true };
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/files/whole.bin", folder.Path, "whole.bin", Connections: 1);

        // A pause mid-download leaves a checkpoint; pretend the transfer had reached the end.
        var paused = await PauseAtAsync(engine, request, payload.Length / 2);
        Assert.True(paused > 0);
        await File.WriteAllBytesAsync(Path.Combine(folder.Path, "whole.bin.part"), payload);

        var result = await engine.DownloadAsync(request);

        Assert.Equal(payload.Length, result.DownloadedBytes);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
    }

    [Fact]
    public async Task KeepsTheBytesThatArrivedBeforeTheConnectionDropped()
    {
        var payload = Payload(3 * 1024 * 1024);
        using var server = new RangeTestServer(payload) { DropAfterBytes = 300 * 1024 };
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/files/dropped.bin", folder.Path, "dropped.bin", Connections: 1);

        await Assert.ThrowsAnyAsync<Exception>(() => engine.DownloadAsync(request));

        var partial = Path.Combine(folder.Path, "dropped.bin.part");
        Assert.True(File.Exists(partial), "an interrupted connection must keep the partial file");
        var kept = new FileInfo(partial).Length;
        Assert.InRange(kept, 64 * 1024, payload.Length - 1);

        server.DropAfterBytes = null;
        var result = await engine.DownloadAsync(request);

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Equal($"bytes={kept}-", server.Requests[^1].Range);
    }

    [Fact]
    public async Task ResumesThroughARedirect()
    {
        var payload = Payload(4 * 1024 * 1024);
        using var server = new RangeTestServer(payload);
        server.RedirectPath = "/redirect";
        server.RedirectTarget = "/files/target.bin";
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/redirect", folder.Path, "redirected.bin", Connections: 1);

        var paused = await PauseAtAsync(engine, request, payload.Length / 3);
        var result = await engine.DownloadAsync(request);

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Equal($"bytes={paused}-", server.Requests[^1].Range);
        Assert.EndsWith("/files/target.bin", result.FinalUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SegmentedResumeAfterAPauseIsByteExact()
    {
        var payload = Payload(6 * 1024 * 1024, 7);
        using var server = new RangeTestServer(payload, chunkDelayMs: 20);
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/files/segmented.bin", folder.Path, "segmented.bin", Connections: 4);

        var paused = await PauseAtAsync(engine, request, payload.Length / 3);
        var partial = Path.Combine(folder.Path, "segmented.bin.part");
        Assert.True(File.Exists(partial), "a paused multi-connection download must keep its partial file");

        var result = await engine.DownloadAsync(request);

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Contains(server.Requests.Skip(1), request => request.Range is not null);
        Assert.True(paused >= 0);
    }

    [Fact]
    public async Task SegmentedResumeRecoversWhenTheServerStopsSendingTheETagOnPartialResponses()
    {
        var payload = Payload(6 * 1024 * 1024, 8);
        using var server = new RangeTestServer(payload, chunkDelayMs: 20);
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/files/segmented-etag.bin", folder.Path, "segmented-etag.bin", Connections: 4);

        await PauseAtAsync(engine, request, payload.Length / 3);
        server.SendETagOnPartial = false;

        var result = await engine.DownloadAsync(request);

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
    }

    [Fact]
    public async Task AChangedFileIsNeverAppendedToASegmentedPartial()
    {
        var payload = Payload(6 * 1024 * 1024, 9);
        using var server = new RangeTestServer(payload, chunkDelayMs: 20);
        using var folder = new TemporaryFolder();
        var engine = new HttpDownloadEngine(new HttpClient());
        var request = new DownloadRequest($"{server.BaseUrl}/files/segmented-changed.bin", folder.Path, "segmented-changed.bin", Connections: 4);

        await PauseAtAsync(engine, request, payload.Length / 3);

        var replacement = Payload(6 * 1024 * 1024, 10);
        server.ReplacePayload(replacement);

        var result = await engine.DownloadAsync(request);

        Assert.Equal(replacement, await File.ReadAllBytesAsync(result.FilePath));
    }

    private static async Task<long> PauseAtAsync(HttpDownloadEngine engine, DownloadRequest request, long threshold)
    {
        using var pause = new CancellationTokenSource();
        var progress = new InlineProgress(value =>
        {
            if (value.DownloadedBytes >= threshold) pause.Cancel();
        });

        try
        {
            await engine.DownloadAsync(request, progress, pause.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected: the caller paused the transfer.
        }

        var partial = Path.Combine(request.DestinationDirectory, (request.FileName ?? "download") + ".part");
        return File.Exists(partial) ? new FileInfo(partial).Length : 0;
    }

    private static byte[] Payload(int size, int seed = 1)
    {
        var payload = new byte[size];
        new Random(seed).NextBytes(payload);
        return payload;
    }
}
