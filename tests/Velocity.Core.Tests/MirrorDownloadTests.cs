using System.Security.Cryptography;
using Velocity.Core.Models;
using Velocity.Core.Tests.Support;
using Velocity.DownloadEngine;

namespace Velocity.Core.Tests;

/// <summary>
/// Multi-source downloads: a mirror only ever receives a byte range after it proves it holds the same file, so
/// combining hosts can make a transfer faster without silently producing a corrupt result.
/// </summary>
public sealed class MirrorDownloadTests
{
    [Fact]
    public async Task EveryVerifiedMirrorServesPartOfTheSameFile()
    {
        using var folder = new TemporaryFolder();
        var payload = Payload(6 * 1024 * 1024, 91);
        using var primary = new RangeTestServer(payload, chunkDelayMs: 12);
        using var first = new RangeTestServer(payload, chunkDelayMs: 12);
        using var second = new RangeTestServer(payload, chunkDelayMs: 12);

        var engine = new HttpDownloadEngine(new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan });
        var result = await engine.DownloadAsync(new DownloadRequest(
            $"{primary.BaseUrl}/files/mirrored.bin", folder.Path, "mirrored.bin", Connections: 4,
            Mirrors: [$"{first.BaseUrl}/files/mirrored.bin", $"{second.BaseUrl}/files/mirrored.bin"]));

        Assert.Equal(Sha256(payload), Sha256(await File.ReadAllBytesAsync(result.FilePath)));

        // A mirror is only useful if it actually transfers bytes, not just answers the probe.
        static bool Served(ServedRequest request) => request.Range is not null && request.Range != "bytes=0-0";
        Assert.Contains(first.Requests, Served);
        Assert.Contains(second.Requests, Served);
    }

    [Fact]
    public async Task AMirrorWithADifferentFileIsNeverUsed()
    {
        using var folder = new TemporaryFolder();
        var payload = Payload(4 * 1024 * 1024, 92);
        var different = Payload(3 * 1024 * 1024, 93);
        using var primary = new RangeTestServer(payload, chunkDelayMs: 10);
        using var impostor = new RangeTestServer(different, chunkDelayMs: 10);

        var engine = new HttpDownloadEngine(new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan });
        var result = await engine.DownloadAsync(new DownloadRequest(
            $"{primary.BaseUrl}/files/strict.bin", folder.Path, "strict.bin", Connections: 4,
            Mirrors: [$"{impostor.BaseUrl}/files/strict.bin"]));

        Assert.Equal(Sha256(payload), Sha256(await File.ReadAllBytesAsync(result.FilePath)));

        // The probe is allowed; a byte range from the impostor is not.
        Assert.DoesNotContain(impostor.Requests, request => request.Range is not null && request.Range != "bytes=0-0");
    }

    private static byte[] Payload(int size, int seed)
    {
        var payload = new byte[size];
        new Random(seed).NextBytes(payload);
        return payload;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
