using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Velocity.Core.Models;

namespace Velocity.DownloadEngine;

/// <summary>
/// IDM-style streaming support for HLS: a playlist describes one video made of many short segments, so it is
/// downloaded as one file instead of offering a stray <c>.ts</c> fragment as if it were the video.
///
/// The playlist is read first, the best variant is chosen from a master playlist, and the media playlist's
/// segments are fetched in order and joined. Playlists that cannot be represented as one finished file are
/// refused with a message that says why: a live stream (no end), or a stream the server protects with
/// SAMPLE-AES/DRM. AES-128 segment encryption is decrypted, because it is not DRM, only transport protection.
/// </summary>
internal sealed class HlsPlaylistDownloader(HttpClient httpClient)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly Regex AttributePattern = new("([A-Z0-9-]+)=(\"[^\"]*\"|[^,]*)", RegexOptions.Compiled);

    /// <summary>True when the request is aimed at an HLS playlist rather than at a file.</summary>
    public static bool IsPlaylist(Uri uri) =>
        uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
        uri.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);

    public async Task<DownloadResult> DownloadAsync(Uri playlistUri, string directory, string? requestedName, SpeedLimit? liveLimit, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        // The same live ceiling the rest of the engine uses: a stream honours a limit set or removed halfway
        // through without the download being restarted.
        var limiter = liveLimit is null ? null : new RateLimiter(liveLimit);
        var (text, finalUri) = await ReadTextAsync(playlistUri, cancellationToken);
        if (!text.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal))
            throw new ArgumentException("The link is not an HLS playlist, so there is no video to join from it.");

        var variants = ParseMaster(text, finalUri);
        var bandwidth = 0L;
        if (variants.Count > 0)
        {
            // The highest bandwidth variant is the best copy the server offers, which is what the user wants
            // when they click a stream that the player is showing at its best quality.
            var best = variants.OrderByDescending(variant => variant.Bandwidth).First();
            bandwidth = best.Bandwidth;
            (text, finalUri) = await ReadTextAsync(best.Uri, cancellationToken);
            if (!text.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal))
                throw new InvalidOperationException("The stream's variant playlist could not be read.");
        }

        var media = ParseMedia(text, finalUri, bandwidth);
        if (media is { Live: true })
            throw new InvalidOperationException("This is a live stream: it has no complete end yet, so it cannot be saved as one finished video.");
        if (media.Segments.Count == 0)
            throw new InvalidOperationException("The stream playlist listed no segments to download.");

        var name = OutputName(requestedName, playlistUri, media.FrameBoundary);
        var destination = Path.Combine(directory, name);
        var partial = destination + ".part";

        // Segments are joined exactly once, so an older partial is not appended to: the result would be two
        // copies of the video in one file. It is replaced from the first segment.
        TryDelete(partial);
        TryDelete(destination + ".part.meta");
        TryDelete(destination + ".part.meta.prev");

        var clock = Stopwatch.StartNew();
        long written = 0;
        var estimatedTotal = media.EstimatedBytes;
        var lastReport = TimeSpan.Zero;
        var keyCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        try
        {
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                if (media.Initialization is { } initialization)
                {
                    var init = await ReadBytesAsync(initialization, cancellationToken);
                    await file.WriteAsync(init, cancellationToken);
                    written += init.Length;
                    if (limiter is not null) await limiter.WaitAsync(init.Length, cancellationToken);
                }

                foreach (var segment in media.Segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytes = await ReadBytesAsync(segment.Uri, cancellationToken);
                    if (segment.Key is { } key)
                        bytes = Decryption.Decrypt(bytes, await KeyAsync(key, keyCache, cancellationToken), segment.Iv ?? DefaultIv(segment.Sequence));
                    await file.WriteAsync(bytes, cancellationToken);
                    written += bytes.Length;

                    // A ceiling is honoured over the whole joined file, not per segment, and it is read live.
                    if (limiter is not null) await limiter.WaitAsync(bytes.Length, cancellationToken);

                    if (progress is not null && (clock.Elapsed - lastReport).TotalMilliseconds >= 250)
                    {
                        lastReport = clock.Elapsed;
                        progress.Report(new DownloadProgress(written, estimatedTotal, written / Math.Max(0.01, clock.Elapsed.TotalSeconds), destination));
                    }
                }
            }

            if (estimatedTotal is null) estimatedTotal = written;
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(partial, destination);
            progress?.Report(new DownloadProgress(written, estimatedTotal, written / Math.Max(0.01, clock.Elapsed.TotalSeconds), destination));
            // The estimate kept the progress bar moving; the finished file is the only trustworthy total.
            return new DownloadResult(destination, written, written, finalUri.ToString(), null, null, false);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /* ─────────────────────────── playlists ─────────────────────────── */

    private static List<Variant> ParseMaster(string text, Uri baseUri)
    {
        var variants = new List<Variant>();
        var bandwidth = 0L;
        // Only the URI that follows a #EXT-X-STREAM-INF line is a variant. In a plain media playlist the bare
        // lines are segments, and treating one of them as a variant would hand a single fragment to the user.
        var awaitingUri = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                var attributes = ParseAttributes(line["#EXT-X-STREAM-INF:".Length..]);
                bandwidth = attributes.TryGetValue("BANDWIDTH", out var value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0;
                attributes.TryGetValue("AVERAGE-BANDWIDTH", out var average);
                if (long.TryParse(average, NumberStyles.Integer, CultureInfo.InvariantCulture, out var averageValue) && averageValue > 0) bandwidth = averageValue;
                awaitingUri = true;
                continue;
            }
            if (line.StartsWith('#')) continue;
            if (!awaitingUri) continue;
            awaitingUri = false;
            if (Uri.TryCreate(baseUri, line, out var uri)) variants.Add(new Variant(uri, bandwidth));
        }
        return variants;
    }

    private static MediaPlaylist ParseMedia(string text, Uri baseUri, long bandwidth)
    {
        var segments = new List<Segment>();
        Uri? initialization = null;
        KeyInfo? key = null;
        var sequence = 0L;
        double duration = 0;
        var live = true;
        var frameBoundary = false;
        var pending = 0.0;
        var ordinal = 0L;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence);
                continue;
            }
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var attributes = ParseAttributes(line["#EXT-X-MAP:".Length..]);
                if (attributes.TryGetValue("URI", out var map) && Uri.TryCreate(baseUri, map, out var mapUri))
                {
                    initialization = mapUri;
                    frameBoundary = true;
                }
                continue;
            }
            if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                var attributes = ParseAttributes(line["#EXT-X-KEY:".Length..]);
                var method = attributes.GetValueOrDefault("METHOD", "NONE").ToUpperInvariant();
                if (method is "NONE") key = null;
                else if (method is "AES-128")
                {
                    var keyUri = attributes.TryGetValue("URI", out var uriValue) && Uri.TryCreate(baseUri, uriValue, out var resolved) ? resolved : null;
                    key = keyUri is null ? null : new KeyInfo(keyUri, ParseIv(attributes.GetValueOrDefault("IV")));
                }
                else throw new InvalidOperationException($"This stream is protected with {method}, which cannot be downloaded as a normal file.");
                continue;
            }
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var value = line["#EXTINF:".Length..].Split(',')[0];
                pending = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
                continue;
            }
            if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal)) { live = false; continue; }
            if (line.StartsWith('#')) continue;

            if (Uri.TryCreate(baseUri, line, out var segmentUri))
            {
                duration += pending;
                segments.Add(new Segment(segmentUri, key, sequence + ordinal, pending, key?.Iv));
                ordinal++;
                pending = 0;
                if (segmentUri.AbsolutePath.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) ||
                    segmentUri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) frameBoundary = true;
            }
        }

        // Bandwidth is bits per second, so a known duration turns into a size estimate the list can show.
        long? estimate = bandwidth > 0 && duration > 0 ? (long)(bandwidth * duration / 8) : null;
        return new MediaPlaylist(segments, initialization, live, estimate, frameBoundary);
    }

    private static Dictionary<string, string> ParseAttributes(string value)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributePattern.Matches(value))
        {
            var text = match.Groups[2].Value;
            attributes[match.Groups[1].Value] = text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
        }
        return attributes;
    }

    private static byte[]? ParseIv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (hex.Length % 2 != 0) hex = "0" + hex;
        try { return Convert.FromHexString(hex); } catch (FormatException) { return null; }
    }

    private static byte[] DefaultIv(long sequence)
    {
        var iv = new byte[16];
        for (var index = 0; index < 8; index++) iv[15 - index] = (byte)(sequence >> (index * 8));
        return iv;
    }

    private static string OutputName(string? requestedName, Uri playlistUri, bool frameBoundary)
    {
        var extension = frameBoundary ? ".mp4" : ".ts";
        var baseName = string.IsNullOrWhiteSpace(requestedName)
            ? Path.GetFileNameWithoutExtension(playlistUri.AbsolutePath)
            : Path.GetFileNameWithoutExtension(Path.GetFileName(requestedName));
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "video";
        // A ".m3u8" or ".m3u" name is the playlist, not the video, so it is always replaced.
        baseName = Path.GetFileNameWithoutExtension(baseName);
        return baseName + extension;
    }

    /* ─────────────────────────── network ─────────────────────────── */

    private async Task<(string Text, Uri FinalUri)> ReadTextAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseContentRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The stream playlist answered HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        return (text, response.RequestMessage?.RequestUri ?? uri);
    }

    private async Task<byte[]> ReadBytesAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseContentRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"A stream segment answered HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        return await response.Content.ReadAsByteArrayAsync(timeout.Token);
    }

    private async Task<byte[]> KeyAsync(KeyInfo key, Dictionary<string, byte[]> cache, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(key.Uri.ToString(), out var known)) return known;
        var bytes = await ReadBytesAsync(key.Uri, cancellationToken);
        if (bytes.Length != 16) throw new InvalidOperationException("The stream's AES key is not 16 bytes, so the segments cannot be decrypted.");
        cache[key.Uri.ToString()] = bytes;
        return bytes;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* replaced on demand */ }
    }

    /* ─────────────────────────── types ─────────────────────────── */

    private sealed record Variant(Uri Uri, long Bandwidth);

    private sealed record KeyInfo(Uri Uri, byte[]? Iv);

    private sealed record Segment(Uri Uri, KeyInfo? Key, long Sequence, double Duration, byte[]? Iv);

    private sealed record MediaPlaylist(List<Segment> Segments, Uri? Initialization, bool Live, long? EstimatedBytes, bool FrameBoundary);

    private static class Decryption
    {
        public static byte[] Decrypt(byte[] data, byte[] key, byte[] iv)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv.Length == 16 ? iv : iv.Take(16).Concat(new byte[16]).Take(16).ToArray();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            var plain = decryptor.TransformFinalBlock(data, 0, data.Length);
            return plain;
        }
    }
}
