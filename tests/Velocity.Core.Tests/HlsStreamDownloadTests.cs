using System.Security.Cryptography;
using System.Text;
using Velocity.Core.Models;
using Velocity.Core.Tests.Support;
using Velocity.DownloadEngine;

namespace Velocity.Core.Tests;

/// <summary>
/// A stream is offered to the user as one video. These tests prove what Velocity does with the playlist: it
/// joins the best variant's segments into a single file, decrypts AES-128 transport encryption, and refuses
/// a live stream instead of saving a fragment of it.
/// </summary>
public sealed class HlsStreamDownloadTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task TheBestVariantIsJoinedIntoOneVideoFile()
    {
        using var folder = new TemporaryFolder();
        using var server = new HlsTestServer();
        var first = Payload(96 * 1024, 1);
        var second = Payload(64 * 1024, 2);
        var third = Payload(80 * 1024, 3);

        server
            .AddText("/master.m3u8", """
                #EXTM3U
                #EXT-X-STREAM-INF:BANDWIDTH=400000,RESOLUTION=640x360
                low.m3u8
                #EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=1280x720
                high.m3u8
                """)
            .AddText("/low.m3u8", """
                #EXTM3U
                #EXT-X-TARGETDURATION:4
                #EXTINF:4.0,
                low/1.ts
                #EXT-X-ENDLIST
                """)
            .AddBytes("/low/1.ts", Payload(32 * 1024, 9))
            .AddText("/high.m3u8", """
                #EXTM3U
                #EXT-X-TARGETDURATION:4
                #EXT-X-MEDIA-SEQUENCE:7
                #EXTINF:4.0,
                high/1.ts
                #EXTINF:4.0,
                high/2.ts
                #EXTINF:4.0,
                high/3.ts
                #EXT-X-ENDLIST
                """)
            .AddBytes("/high/1.ts", first)
            .AddBytes("/high/2.ts", second)
            .AddBytes("/high/3.ts", third);

        var engine = new HttpDownloadEngine(new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan });
        var result = await engine.DownloadAsync(new DownloadRequest($"{server.BaseUrl}/master.m3u8", folder.Path, "movie.ts", Connections: 4));

        var saved = await File.ReadAllBytesAsync(result.FilePath);
        Assert.Equal(first.Concat(second).Concat(third).ToArray(), saved);
        Assert.Equal(saved.Length, result.DownloadedBytes);

        // The playlist itself is never what gets saved, and no single segment is passed off as the video.
        Assert.DoesNotContain("#EXTM3U", Encoding.UTF8.GetString(saved));
        Assert.True(saved.Length > first.Length, "only one segment reached the file");

        // The best variant was chosen: the low-quality playlist was never fetched.
        Assert.Contains("/high.m3u8", server.Requests);
        Assert.DoesNotContain("/low.m3u8", server.Requests);
        Assert.EndsWith(".ts", result.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnAesEncryptedStreamIsDecryptedWhileItIsJoined()
    {
        using var folder = new TemporaryFolder();
        using var server = new HlsTestServer();
        var key = Payload(16, 42);
        var first = Payload(48 * 1024, 4);
        var second = Payload(40 * 1024, 5);

        server
            .AddBytes("/key.bin", key, "application/octet-stream")
            .AddText("/enc.m3u8", """
                #EXTM3U
                #EXT-X-TARGETDURATION:4
                #EXT-X-KEY:METHOD=AES-128,URI="key.bin"
                #EXTINF:4.0,
                enc/1.ts
                #EXTINF:4.0,
                enc/2.ts
                #EXT-X-ENDLIST
                """)
            .AddBytes("/enc/1.ts", Encrypt(first, key, Iv(0)))
            .AddBytes("/enc/2.ts", Encrypt(second, key, Iv(1)));

        var engine = new HttpDownloadEngine(new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan });
        var result = await engine.DownloadAsync(new DownloadRequest($"{server.BaseUrl}/enc.m3u8", folder.Path, "protected.ts", Connections: 1), cancellationToken: CancellationToken.None);

        Assert.Equal(first.Concat(second).ToArray(), await File.ReadAllBytesAsync(result.FilePath));
        Assert.False(File.Exists(result.FilePath + ".part"), "the partial file must be gone once the video is complete");
    }

    [Fact]
    public async Task AStreamWithNoEndIsRefusedInsteadOfSavedAsAFragment()
    {
        using var folder = new TemporaryFolder();
        using var server = new HlsTestServer();
        server
            .AddText("/live.m3u8", """
                #EXTM3U
                #EXT-X-TARGETDURATION:4
                #EXTINF:4.0,
                live/1.ts
                """)
            .AddBytes("/live/1.ts", Payload(8 * 1024, 6));

        var engine = new HttpDownloadEngine(new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan });
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.DownloadAsync(new DownloadRequest($"{server.BaseUrl}/live.m3u8", folder.Path, "live.ts", Connections: 1), cancellationToken: CancellationToken.None).WaitAsync(Timeout));

        Assert.Contains("live stream", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task AStreamProtectedWithDrmIsReportedInsteadOfDownloadedAsGarbage()
    {
        using var folder = new TemporaryFolder();
        using var server = new HlsTestServer();
        server
            .AddText("/drm.m3u8", """
                #EXTM3U
                #EXT-X-TARGETDURATION:4
                #EXT-X-KEY:METHOD=SAMPLE-AES,URI="skd://key"
                #EXTINF:4.0,
                drm/1.ts
                #EXT-X-ENDLIST
                """)
            .AddBytes("/drm/1.ts", Payload(8 * 1024, 7));

        var engine = new HttpDownloadEngine(new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan });
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.DownloadAsync(new DownloadRequest($"{server.BaseUrl}/drm.m3u8", folder.Path, "drm.ts", Connections: 1), cancellationToken: CancellationToken.None).WaitAsync(Timeout));

        Assert.Contains("SAMPLE-AES", exception.Message);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    private static byte[] Iv(long sequence)
    {
        var iv = new byte[16];
        for (var index = 0; index < 8; index++) iv[15 - index] = (byte)(sequence >> (index * 8));
        return iv;
    }

    private static byte[] Encrypt(byte[] plain, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plain, 0, plain.Length);
    }

    private static byte[] Payload(int size, int seed)
    {
        var payload = new byte[size];
        new Random(seed).NextBytes(payload);
        return payload;
    }
}
