using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Velocity.Core.Interfaces;
using Velocity.Core.Models;

namespace Velocity.DownloadEngine;

/// <summary>
/// HTTP engine with real pause/resume.
///
/// Bytes are always written to <c>&lt;file&gt;.part</c> and described by <c>&lt;file&gt;.part.meta</c>, so a pause,
/// a crash or an application restart continues from the last byte the disk actually received:
///  * <c>206 Partial Content</c> — validated against the requested offset, then appended. This single request is
///    also the validation that the link still works and that the server can continue from the saved byte, so a
///    resume costs no extra round trip.
///  * <c>200 OK</c>, an unexpected <c>206</c>, <c>416</c>, or an expired/forbidden/missing link — the resume cannot
///    continue. <see cref="DownloadLinkExpiredException"/> is raised and the partial file is kept untouched:
///    restarting or truncating it here would silently throw away what the user already downloaded.
///  * <c>ETag</c> / <c>Last-Modified</c> are sent back as <c>If-Range</c>.
///
/// Segmented downloads keep a per-segment byte count in the same sidecar, so pausing a multi-connection
/// transfer no longer throws the partial file away. A segmented resume starts its range requests immediately from
/// the checkpoint instead of probing the server first.
/// </summary>
public sealed class HttpDownloadEngine(HttpClient httpClient) : IDownloadEngine
{
    private const int BufferSize = 128 * 1024;
    private const int MaxSegments = 16;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("Only HTTP and HTTPS URLs are supported.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.DestinationDirectory)) throw new ArgumentException("Choose a destination folder for this download.", nameof(request));

        var directory = Path.GetFullPath(request.DestinationDirectory);
        Directory.CreateDirectory(directory);
        var mirrors = MirrorUris(request);

        var requestedName = SanitizeFileName(request.FileName);

        // The ceiling is an object, not a number: the manager changes it while the transfer runs, and the engine
        // reads it on every buffer. That is what makes "remove the limit" take effect instantly, with no pause.
        var liveLimit = request.LiveLimit ?? new SpeedLimit(request.SpeedLimitBytesPerSecond);

        // A playlist is one video, not a file: it is joined from its segments instead of being saved as text.
        if (HlsPlaylistDownloader.IsPlaylist(uri))
            return await new HlsPlaylistDownloader(httpClient).DownloadAsync(uri, directory, requestedName, liveLimit, progress, cancellationToken);
        var state = FindState(directory, request.Url, requestedName);
        var name = requestedName ?? state?.FileName;
        var partialLength = 0L;
        if (name is not null)
        {
            var existing = Path.Combine(directory, name) + ".part";
            if (File.Exists(existing)) partialLength = new FileInfo(existing).Length;
        }

        // A speed ceiling paces the combined throughput of every connection instead of forcing a single
        // stream, so a limited download still uses all of its connections and can always resume segmented.
        var limiter = request.LiveLimit is not null || liveLimit.Enabled ? new RateLimiter(liveLimit) : null;

        // A paused multi-connection transfer resumes segment by segment from the sidecar. The file is
        // preallocated to its full size, so the checkpoint (not the file length) says how much is real.
        if (state is { Segments: > 1, TotalBytes: > 0 } && state.DownloadedBytes > 0 && state.DownloadedBytes < state.TotalBytes &&
            File.Exists(Path.Combine(directory, state.FileName) + ".part"))
        {
            var resumed = await SegmentedAsync(uri, directory, state, null, 0, limiter, progress, cancellationToken, mirrors, request.Headers);
            if (resumed is not null) return resumed;
        }

        // Fresh transfers may use several connections when the server supports ranges.
        if (partialLength == 0 && state is null && request.Connections > 1)
        {
            var segmented = await SegmentedAsync(uri, directory, null, requestedName, request.Connections, limiter, progress, cancellationToken, mirrors, request.Headers);
            if (segmented is not null) return segmented;
        }

        return await SingleAsync(uri, directory, name, state, limiter, progress, cancellationToken, request.Headers);
    }

    /* ─────────────────────────── single connection (always resumable) ─────────────────────────── */

    private async Task<DownloadResult> SingleAsync(Uri uri, string directory, string? knownName, DownloadCheckpoint? state, RateLimiter? limiter, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? headers = null)
    {
        var name = knownName ?? GetFileName(uri);
        var destination = Path.Combine(directory, name);
        var partial = destination + ".part";
        var metaPath = partial + ".meta";
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        while (true)
        {
            using var guard = new StallGuard(cancellationToken, StallTimeout);
            using var message = new HttpRequestMessage(HttpMethod.Get, uri);
            ApplyHeaders(message, headers);
            if (offset > 0)
            {
                message.Headers.Range = new RangeHeaderValue(offset, null);
                if (RangeValidator(state) is { } validator) message.Headers.TryAddWithoutValidation("If-Range", validator);
            }

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, guard.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("The server did not respond in time. The partial file was kept so the download can be resumed.");
            }

            using (response)
            {
                // The server names the file when the caller did not; honour Content-Disposition.
                if (offset == 0 && state is null && knownName is null)
                {
                    var dispositionName = SanitizeFileName(FileNameFromDisposition(response));
                    if (dispositionName is not null && !string.Equals(dispositionName, name, StringComparison.Ordinal))
                    {
                        name = dispositionName;
                        destination = Path.Combine(directory, name);
                        partial = destination + ".part";
                        metaPath = partial + ".meta";
                    }
                }

                var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? uri.ToString();
                var etag = response.Headers.ETag?.ToString();
                var lastModified = response.Content.Headers.LastModified?.ToString("R");
                var rangeSupported = response.Headers.AcceptRanges.Any(value => string.Equals(value, "bytes", StringComparison.OrdinalIgnoreCase));

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // Only a single-stream partial can be trusted as complete. A preallocated multi-connection
                    // file of the full length may still contain holes, so it must never take this path.
                    var completeTotal = response.Content.Headers.ContentRange?.Length;
                    if (offset > 0 && state is { Segments: <= 1 } && completeTotal is long remoteTotal && remoteTotal == offset && File.Exists(partial))
                    {
                        var completeLength = new FileInfo(partial).Length;
                        File.Move(partial, destination, true);
                        TryDeleteCheckpoint(metaPath);
                        progress?.Report(new DownloadProgress(completeLength, remoteTotal, 0, destination));
                        return new DownloadResult(destination, completeLength, remoteTotal, finalUrl, etag, lastModified, true);
                    }

                    // The bytes on disk are not what this link serves any more. Deleting the partial here is what
                    // used to restart a download behind the user's back; it stays on disk until they ask again.
                    throw new DownloadLinkExpiredException("The server rejected the resume request (HTTP 416), so the file it has no longer matches the saved bytes.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    // A link that now answers 401/403/404/410 is expired, signed out or gone. A 5xx or a timeout is
                    // a server problem, which keeps the normal retry path and its short backoff.
                    if (offset > 0 && IsLinkGone(response.StatusCode))
                        throw new DownloadLinkExpiredException($"The server answered HTTP {(int)response.StatusCode} ({response.StatusCode}) for the saved link.");

                    throw new HttpRequestException($"The server returned {(int)response.StatusCode} ({response.StatusCode}).", null, response.StatusCode);
                }

                var contentRange = response.Content.Headers.ContentRange;
                if (response.StatusCode == HttpStatusCode.PartialContent && (contentRange is null || contentRange.From != offset))
                {
                    // A partial response that cannot be placed at the requested offset would leave a hole or write
                    // duplicate bytes, so the resume stops here and the saved data stays untouched.
                    throw new DownloadLinkExpiredException("The server answered with a byte range that does not start at the saved position.");
                }

                if (response.StatusCode == HttpStatusCode.PartialContent && ValidatorChanged(state, etag, lastModified))
                {
                    // A different ETag/Last-Modified means the bytes on disk are from another version of the file:
                    // appending would mix two builds into one silently corrupt file.
                    throw new DownloadLinkExpiredException("The file on the server changed since this download was paused.");
                }

                var contentLength = response.Content.Headers.ContentLength;
                if (response.StatusCode == HttpStatusCode.OK && offset > 0)
                {
                    // The server ignored the range, so it will not continue from the saved byte — either it cannot
                    // resume or the link now points at a different resource. Re-downloading from zero here is
                    // exactly the silent restart that must never happen; the partial file is kept instead.
                    throw new DownloadLinkExpiredException("The server sent the whole file instead of the part after the saved position, so this link cannot resume.");
                }

                var total = contentRange?.Length ?? contentLength;
                if (total is long expected) EnsureFreeSpace(destination, Math.Max(0, expected - offset));

                DownloadCheckpoint.Write(metaPath, new DownloadCheckpoint(1, uri.ToString(), finalUrl, name, total ?? 0, offset, etag, lastModified, rangeSupported, 1, [offset]));
                progress?.Report(new DownloadProgress(offset, total, 0, destination));

                long downloaded;
                await using (var input = await response.Content.ReadAsStreamAsync(guard.Token))
                await using (var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[BufferSize];
                    downloaded = offset;
                    var clock = Stopwatch.StartNew();
                    var reportAt = Stopwatch.StartNew();
                    var persistAt = Stopwatch.StartNew();
                    try
                    {
                        while (true)
                        {
                            var read = await input.ReadAsync(buffer, guard.Token);
                            if (read == 0) break;
                            guard.Touch();
                            await output.WriteAsync(buffer.AsMemory(0, read), guard.Token);
                            downloaded += read;

                            // A deliberately slow transfer is not a stalled one: the guard is touched between slices.
                            if (limiter is not null) await limiter.WaitAsync(read, guard.Token, guard.Touch);

                            if (reportAt.ElapsedMilliseconds >= 100)
                            {
                                reportAt.Restart();
                                progress?.Report(new DownloadProgress(downloaded, total, (downloaded - offset) / Math.Max(clock.Elapsed.TotalSeconds, .001), destination));
                            }

                            if (persistAt.ElapsedMilliseconds >= 1500)
                            {
                                persistAt.Restart();
                                await output.FlushAsync(guard.Token);
                                DownloadCheckpoint.Write(metaPath, new DownloadCheckpoint(1, uri.ToString(), finalUrl, name, total ?? 0, downloaded, etag, lastModified, rangeSupported, 1, [downloaded]));
                            }
                        }

                        await output.FlushAsync(guard.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        await FlushQuietlyAsync(output);
                        DownloadCheckpoint.Write(metaPath, new DownloadCheckpoint(1, uri.ToString(), finalUrl, name, total ?? 0, downloaded, etag, lastModified, rangeSupported, 1, [downloaded]));
                        throw new IOException("The server stopped sending data. The partial file was kept so the download can be resumed.");
                    }
                    catch (OperationCanceledException)
                    {
                        await FlushQuietlyAsync(output);
                        DownloadCheckpoint.Write(metaPath, new DownloadCheckpoint(1, uri.ToString(), finalUrl, name, total ?? 0, downloaded, etag, lastModified, rangeSupported, 1, [downloaded]));
                        throw;
                    }
                    catch (Exception exception) when (exception is IOException or HttpRequestException)
                    {
                        await FlushQuietlyAsync(output);
                        DownloadCheckpoint.Write(metaPath, new DownloadCheckpoint(1, uri.ToString(), finalUrl, name, total ?? 0, downloaded, etag, lastModified, rangeSupported, 1, [downloaded]));
                        throw;
                    }
                }

                if (total is long expectedTotal && downloaded != expectedTotal)
                {
                    if (downloaded < expectedTotal)
                        throw new IOException($"The download ended early ({downloaded} of {expectedTotal} bytes). Resume it to continue from where it stopped.");

                    TryDelete(partial);
                    TryDeleteCheckpoint(metaPath);
                    throw new IOException("The server sent more data than it announced. The partial file was discarded so the download can start clean.");
                }

                progress?.Report(new DownloadProgress(downloaded, total ?? downloaded, 0, destination));
                File.Move(partial, destination, true);
                TryDeleteCheckpoint(metaPath);
                return new DownloadResult(destination, downloaded, total ?? downloaded, finalUrl, etag, lastModified, rangeSupported);
            }
        }
    }

    /* ─────────────────────────── multiple connections (pause safe) ─────────────────────────── */

    /// <summary>
    /// Downloads several byte ranges at once. <paramref name="resume"/> carries the previous per-segment byte
    /// counts and its own length/validator, so a resume starts its ranges immediately instead of probing first;
    /// <paramref name="requestedName"/> is only used for a fresh transfer. Returns null when the server cannot
    /// honour ranges, so the caller falls back to a single resumable connection.
    /// </summary>
    private async Task<DownloadResult?> SegmentedAsync(Uri uri, string directory, DownloadCheckpoint? resume, string? requestedName, int connectionCount, RateLimiter? limiter, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken, IReadOnlyList<Uri>? mirrors = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        // A resume already knows the address, length and validator from its checkpoint, so asking the server for
        // them again only adds a round trip before the transfer can continue. The per-segment Range requests that
        // follow are the real validation: each one either returns the exact bytes the checkpoint expects or the
        // resume fails with the partial file untouched.
        var resuming = resume is { TotalBytes: > 0 };
        ProbeResult probe;
        if (resuming)
        {
            probe = FromCheckpoint(resume!, uri);
        }
        else
        {
            try
            {
                probe = await ProbeAsync(uri, headers, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                return null;
            }
        }

        if (!probe.RangeSupported || probe.Total is not long total || total <= 0) return null;

        // Mirrors join the transfer only after they prove they hold exactly this file (same length, same validator).
        var endpoints = await VerifiedEndpointsAsync(uri, mirrors, total, probe.ETag, probe.LastModified, headers, cancellationToken);

        var name = resume?.FileName ?? requestedName ?? probe.DispositionName ?? GetFileName(uri);
        var destination = Path.Combine(directory, name);
        var partial = destination + ".part";
        var metaPath = partial + ".meta";

        if (resume is not null && (!SameResource(resume, probe) || resume.TotalBytes != total))
        {
            // The remote file changed (or the metadata disagrees): never combine bytes from two versions, and never
            // delete what is already on disk either. The user decides whether to download it again.
            throw new DownloadLinkExpiredException("The file on the server changed since this download was paused.");
        }

        var segments = resume is { Segments: > 1 } ? Math.Min(resume.Segments, MaxSegments) : Math.Clamp(connectionCount, 2, MaxSegments);
        segments = (int)Math.Min(segments, total);
        // A verified mirror that never receives a segment would only cost its probe, so each one gets a share.
        if (resume is null) segments = (int)Math.Min(Math.Max(segments, endpoints.Count), total);
        var partSize = (total + segments - 1) / segments;

        var written = new long[segments];
        for (var index = 0; index < segments; index++)
        {
            var size = SegmentSize(index, partSize, total);
            written[index] = resume is not null && index < resume.Written.Length ? Math.Clamp(resume.Written[index], 0, size) : 0;
        }

        EnsureFreeSpace(destination, Math.Max(0, total - written.Sum()));

        if (resume is null || !File.Exists(partial))
        {
            TryDelete(partial);
            await using var created = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            created.SetLength(total);
        }

        var aggregate = new long[] { written.Sum() };
        var startBytes = aggregate[0];
        var clock = Stopwatch.StartNew();
        var reportAt = Stopwatch.StartNew();
        var persistAt = Stopwatch.StartNew();
        var noRanges = new bool[1];
        var reportGate = new object();
        var finalUrl = probe.FinalUrl;
        var validator = RangeValidator(probe.ETag, probe.LastModified);
        var initial = startBytes;

        DownloadCheckpoint Snapshot(long downloaded) =>
            new(1, uri.ToString(), finalUrl, name, total, downloaded, probe.ETag, probe.LastModified, true, segments, written.ToArray());

        DownloadCheckpoint.Write(metaPath, Snapshot(initial));
        progress?.Report(new DownloadProgress(initial, total, 0, destination, written.ToArray()));

        using var guard = new StallGuard(cancellationToken, StallTimeout);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);

        void Report()
        {
            lock (reportGate)
            {
                if (reportAt.ElapsedMilliseconds < 100) return;
                reportAt.Restart();
                var value = Interlocked.Read(ref aggregate[0]);
                progress?.Report(new DownloadProgress(value, total, Math.Max(0, value - startBytes) / Math.Max(clock.Elapsed.TotalSeconds, .001), destination, written.ToArray()));
                if (persistAt.ElapsedMilliseconds < 1500) return;
                persistAt.Restart();
                DownloadCheckpoint.Write(metaPath, Snapshot(value));
            }
        }

        async Task DownloadSegmentAsync(int index)
        {
            var segmentStart = index * partSize;
            var size = SegmentSize(index, partSize, total);
            var end = segmentStart + size - 1;
            var from = segmentStart + written[index];

            // Segment N is fetched from mirror N % count: every verified host serves part of the same file.
            var endpoint = endpoints[index % endpoints.Count];
            using var message = new HttpRequestMessage(HttpMethod.Get, endpoint);
            ApplyHeaders(message, headers);
            message.Headers.Range = new RangeHeaderValue(from, end);
            if (validator is { Length: > 0 }) message.Headers.TryAddWithoutValidation("If-Range", validator);

            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, runCts.Token);
            var range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent ||
                range?.From != from ||
                (range.To is long to && to != end) ||
                (range.Length is long length && length != total))
            {
                if (resuming)
                {
                    // The link cannot serve the exact bytes this transfer still needs. Restarting the whole file
                    // here (as a fresh transfer would) would discard the paused progress, so it is refused.
                    throw new DownloadLinkExpiredException($"The server refused the byte range this download needs (HTTP {(int)response.StatusCode}).");
                }

                noRanges[0] = true;
                throw new RangeUnsupportedException();
            }

            await using var input = await response.Content.ReadAsStreamAsync(runCts.Token);
            using var handle = File.OpenHandle(partial, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous);
            var buffer = new byte[BufferSize];
            var position = from;
            while (position <= end)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(BufferSize, end - position + 1)), runCts.Token);
                if (read == 0) throw new IOException($"The connection for bytes {from}-{end} ended early.");
                guard.Touch();
                RandomAccess.Write(handle, buffer.AsSpan(0, read), position);
                position += read;
                written[index] += read;
                Interlocked.Add(ref aggregate[0], read);
                if (limiter is not null) await limiter.WaitAsync(read, runCts.Token, guard.Touch);
                Report();
            }
        }

        var tasks = Enumerable.Range(0, segments)
            .Where(index => written[index] < SegmentSize(index, partSize, total))
            .Select(DownloadSegmentAsync)
            .ToArray();

        Exception? failure = null;
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            await runCts.CancelAsync();
            try { await Task.WhenAll(tasks); }
            catch (Exception) { /* the first failure is the one that matters */ }
        }

        DownloadCheckpoint.Write(metaPath, Snapshot(Interlocked.Read(ref aggregate[0])));

        if (noRanges[0])
        {
            // A connection was refused its range: restart on a single stream rather than corrupt the file.
            TryDelete(partial);
            TryDeleteCheckpoint(metaPath);
            return null;
        }

        if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException("The download was paused.", cancellationToken);

        if (failure is OperationCanceledException)
            throw new IOException("The server stopped sending data. The partial file was kept so the download can be resumed.");

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();

        var downloadedTotal = Interlocked.Read(ref aggregate[0]);
        if (downloadedTotal != total || new FileInfo(partial).Length != total)
            throw new IOException($"The segmented download stopped at {downloadedTotal} of {total} bytes. Resume it to continue.");

        progress?.Report(new DownloadProgress(total, total, 0, destination));
        File.Move(partial, destination, true);
        TryDeleteCheckpoint(metaPath);
        return new DownloadResult(destination, total, total, finalUrl, probe.ETag, probe.LastModified, true);
    }

    /* ─────────────────────────── mirrors ─────────────────────────── */

    /// <summary>The mirrors the caller supplied, as absolute http(s) addresses with no duplicates.</summary>
    private static List<Uri> MirrorUris(DownloadRequest request)
    {
        var urls = new List<Uri>();
        foreach (var value in request.Mirrors ?? [])
        {
            if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var mirror) || mirror.Scheme is not ("http" or "https")) continue;
            if (!urls.Contains(mirror)) urls.Add(mirror);
        }
        return urls;
    }

    /// <summary>
    /// The primary address plus every mirror that provably serves the same file. A mirror whose length or
    /// validator disagrees is dropped: joining bytes from two builds would produce a file that opens fine and
    /// is silently corrupt, which is worse than downloading from one host.
    /// </summary>
    private async Task<List<Uri>> VerifiedEndpointsAsync(Uri primary, IReadOnlyList<Uri>? mirrors, long total, string? etag, string? lastModified, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        var endpoints = new List<Uri> { primary };
        if (mirrors is not { Count: > 0 }) return endpoints;

        // Every mirror is checked at the same time: one slow mirror must not delay the resume it was meant to help.
        var candidates = mirrors.Where(mirror => mirror != primary).ToList();
        var checks = candidates.Select(async mirror =>
        {
            try
            {
                var probe = await ProbeAsync(mirror, headers, cancellationToken);
                if (!probe.RangeSupported || probe.Total != total) return null;
                // The mirror must present the same validator the primary did; an absent one proves nothing.
                if (!string.IsNullOrEmpty(etag))
                {
                    if (string.IsNullOrEmpty(probe.ETag) || !string.Equals(etag, probe.ETag, StringComparison.Ordinal)) return null;
                }
                else if (!string.IsNullOrEmpty(lastModified))
                {
                    if (string.IsNullOrEmpty(probe.LastModified) || !string.Equals(lastModified, probe.LastModified, StringComparison.Ordinal)) return null;
                }
                return mirror;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                // A mirror that cannot answer is simply not used.
                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Its probe timed out; the transfer itself is still fine without it.
                return null;
            }
        });

        foreach (var verified in await Task.WhenAll(checks))
            if (verified is not null) endpoints.Add(verified);
        return endpoints;
    }

    /* ─────────────────────────── request headers ─────────────────────────── */

    /// <summary>
    /// Sends the caller's own headers — a <c>Referer</c>, a session <c>Cookie</c>, an <c>Authorization</c> token
    /// or anything else a host checks. Every request gets them: the capability probe, each segment and each
    /// resume attempt, because a host that refused one of those would fail the download after it looked fine.
    /// </summary>
    private static void ApplyHeaders(HttpRequestMessage message, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is not { Count: > 0 }) return;
        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(value)) continue;
            // The engine owns these; letting a value through would corrupt the transfer rather than authenticate it.
            if (name.Equals("Range", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("If-Range", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                continue;

            message.Headers.TryAddWithoutValidation(name, value);
        }
    }

    /* ─────────────────────────── response helpers ─────────────────────────── */

    private async Task<ProbeResult> ProbeAsync(Uri uri, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        using var guard = new StallGuard(cancellationToken, StallTimeout);
        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyHeaders(message, headers);
        message.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, guard.Token);
        var total = response.StatusCode == HttpStatusCode.PartialContent ? response.Content.Headers.ContentRange?.Length : null;
        var probe = new ProbeResult(
            response.StatusCode == HttpStatusCode.PartialContent && total is > 0,
            total,
            SanitizeFileName(FileNameFromDisposition(response)),
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified?.ToString("R"),
            response.RequestMessage?.RequestUri?.ToString() ?? uri.ToString());
        response.Content.Dispose();
        return probe;
    }

    /// <summary>
    /// True only when the response proves the remote resource is a different version. Validators are compared
    /// like with like: many servers send an ETag on the first full response but only Last-Modified on later
    /// partial responses, which says nothing about the file having changed. When one side is missing, the
    /// server's own <c>If-Range</c> decision (it returned 206 rather than 200) is the authority.
    /// </summary>
    private static bool ValidatorChanged(DownloadCheckpoint? state, string? etag, string? lastModified)
    {
        if (state is null) return false;
        if (etag is not null && state.ETag is { Length: > 0 } storedEtag) return !string.Equals(etag, storedEtag, StringComparison.Ordinal);
        if (lastModified is not null && state.LastModified is { Length: > 0 } storedDate) return !string.Equals(lastModified, storedDate, StringComparison.Ordinal);
        return false;
    }

    /// <summary>
    /// True for the answers that mean this address no longer serves the file: signed-out, forbidden, removed, or
    /// pointed at something else. Anything else (a 5xx, a gateway timeout, a throttled 429) is a server problem
    /// worth retrying, not an expired link.
    /// </summary>
    private static bool IsLinkGone(HttpStatusCode status) =>
        status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound or HttpStatusCode.Gone;

    private static ProbeResult FromCheckpoint(DownloadCheckpoint resume, Uri uri) =>
        new(true, resume.TotalBytes, resume.FileName, resume.ETag, resume.LastModified, resume.FinalUrl ?? uri.ToString());

    /// <summary>
    /// The validator to send as <c>If-Range</c>. A weak ETag is not a valid If-Range validator, so a conforming
    /// server ignores the range and answers with the whole file, which would restart the download. A strong ETag is
    /// preferred, then Last-Modified, and when neither exists no header is sent at all.
    /// </summary>
    private static string? RangeValidator(DownloadCheckpoint? state) => RangeValidator(state?.ETag, state?.LastModified);

    private static string? RangeValidator(string? etag, string? lastModified)
    {
        if (!string.IsNullOrWhiteSpace(etag) && !etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) return etag;
        if (!string.IsNullOrWhiteSpace(lastModified)) return lastModified;
        return null;
    }

    private static bool SameResource(DownloadCheckpoint state, ProbeResult probe)
    {
        var stored = state.Validator;
        var current = probe.ETag ?? probe.LastModified;
        if (stored is null || current is null) return true;
        return string.Equals(stored, current, StringComparison.Ordinal);
    }

    private static long SegmentSize(int index, long partSize, long total) => Math.Clamp(total - index * partSize, 0, partSize);

    /* ─────────────────────────── partial state ─────────────────────────── */

    private static DownloadCheckpoint? FindState(string directory, string url, string? requestedName)
    {
        if (requestedName is not null)
        {
            var direct = DownloadCheckpoint.Read(Path.Combine(directory, requestedName) + ".part.meta");
            if (direct is not null && SameUrl(direct.Url, url)) return direct;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.part.meta"))
            {
                var state = DownloadCheckpoint.Read(file);
                if (state is not null && SameUrl(state.Url, url)) return state;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A read-only or busy folder simply means no resume state is available.
        }

        return null;
    }

    private static bool SameUrl(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);

    /* ─────────────────────────── names ─────────────────────────── */

    private static string? FileNameFromDisposition(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentDisposition is not { } disposition) return null;
        var name = disposition.FileNameStar ?? disposition.FileName;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim().Trim('"');
    }

    private static string GetFileName(Uri uri)
    {
        string pathName;
        try { pathName = Uri.UnescapeDataString(Path.GetFileName(uri.LocalPath)); }
        catch (UriFormatException) { pathName = Path.GetFileName(uri.LocalPath); }
        return SanitizeFileName(pathName) ?? "download";
    }

    private static string? SanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = string.Join('_', value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned is "." or "..") return null;

        // Windows refuses these device names regardless of extension.
        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(stem[3])))
        {
            cleaned = "_" + cleaned;
        }

        return cleaned;
    }

    private static void EnsureFreeSpace(string destination, long needed)
    {
        if (needed <= 0) return;
        try
        {
            var root = Path.GetPathRoot(destination);
            if (string.IsNullOrWhiteSpace(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return;
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return;
            if (drive.AvailableFreeSpace < needed + 16L * 1024 * 1024)
                throw new IOException("There is not enough free disk space to safely continue this download.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            if (exception is IOException && exception.Message.StartsWith("There is not enough", StringComparison.Ordinal)) throw;
            // Unknown or network volumes: skip the drive-space pre-check rather than failing the download.
        }
    }

    private static async Task FlushQuietlyAsync(FileStream stream)
    {
        try { await stream.FlushAsync(CancellationToken.None); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Removes every copy of a resume checkpoint (live, backup and half-written temporary).</summary>
    private static void TryDeleteCheckpoint(string metaPath)
    {
        TryDelete(metaPath);
        TryDelete(metaPath + ".prev");
        TryDelete(metaPath + ".tmp");
    }

    /* ─────────────────────────── state records ─────────────────────────── */

    private sealed record ProbeResult(
        bool RangeSupported,
        long? Total,
        string? DispositionName,
        string? ETag,
        string? LastModified,
        string FinalUrl);

    private sealed class RangeUnsupportedException : Exception
    {
    }

    /// <summary>Cancels the transfer when the server goes silent, independently of the user's pause.</summary>
    private sealed class StallGuard : IDisposable
    {
        private readonly CancellationTokenSource _linked;
        private readonly Timer _timer;
        private readonly TimeSpan _timeout;
        private readonly CancellationToken _caller;

        public StallGuard(CancellationToken caller, TimeSpan timeout)
        {
            _caller = caller;
            _timeout = timeout;
            _linked = CancellationTokenSource.CreateLinkedTokenSource(caller);
            _timer = new Timer(_ =>
            {
                try { _linked.Cancel(); }
                catch (ObjectDisposedException) { }
            }, null, timeout, Timeout.InfiniteTimeSpan);
        }

        public CancellationToken Token => _linked.Token;
        public bool CallerCancelled => _caller.IsCancellationRequested;

        public void Touch()
        {
            try { _timer.Change(_timeout, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _timer.Dispose();
            _linked.Dispose();
        }
    }
}
