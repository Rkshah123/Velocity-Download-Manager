using System.Net;
using System.Net.Http.Headers;
using Velocity.Core.Models;
using Velocity.Core.Tests.Support;
using Velocity.DownloadEngine;

namespace Velocity.Core.Tests;

/// <summary>
/// Authenticated downloads: the headers a user supplies belong on every request the engine makes, because a
/// host that accepts one and refuses the next would fail a transfer that looked fine at the start.
/// </summary>
public sealed class RequestHeaderTests
{
    [Fact]
    public async Task HeadersReachTheProbeAndEverySegment()
    {
        using var folder = new TemporaryFolder();
        var payload = Payload(6 * 1024 * 1024, 51);
        var handler = new RecordingHandler(payload);
        var engine = new HttpDownloadEngine(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });

        await engine.DownloadAsync(new DownloadRequest(
            "http://127.0.0.1:9/protected.bin", folder.Path, "protected.bin", Connections: 4,
            Headers: new Dictionary<string, string>
            {
                ["Referer"] = "https://example.com/watch",
                ["Cookie"] = "session=abc123",
                ["X-Api-Key"] = "key-42"
            }));

        Assert.True(handler.Requests.Count >= 5, $"expected a probe and four segments, saw {handler.Requests.Count}");
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("session=abc123", Value(request, "Cookie"));
            Assert.Equal("https://example.com/watch", Value(request, "Referer"));
            Assert.Equal("key-42", Value(request, "X-Api-Key"));
        });
    }

    [Fact]
    public async Task HeadersAreNotAllowedToBreakRangeHandling()
    {
        using var folder = new TemporaryFolder();
        var payload = Payload(2 * 1024 * 1024, 52);
        var handler = new RecordingHandler(payload);
        var engine = new HttpDownloadEngine(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });

        await engine.DownloadAsync(new DownloadRequest(
            "http://127.0.0.1:9/guarded.bin", folder.Path, "guarded.bin", Connections: 1,
            Headers: new Dictionary<string, string> { ["Range"] = "bytes=999999-", ["Host"] = "elsewhere" }));

        // The engine still owns the range, and a bogus Host never reaches the wire.
        Assert.All(handler.Requests, request =>
        {
            Assert.Null(Value(request, "Host"));
            if (request.Headers.Range is { } range)
            {
                Assert.NotEqual(999999, range.Ranges.FirstOrDefault()?.From);
            }
        });
    }

    private static string? Value(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static byte[] Payload(int size, int seed)
    {
        var payload = new byte[size];
        new Random(seed).NextBytes(payload);
        return payload;
    }

    /// <summary>Serves a fixed payload with ranges, remembering every request it was asked to answer.</summary>
    private sealed class RecordingHandler(byte[] payload) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (request.Headers.Range?.Ranges.FirstOrDefault() is { From: long from } range)
            {
                var to = range.To ?? payload.Length - 1;
                var slice = payload[(int)from..(int)(to + 1)];
                var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(slice) };
                partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, payload.Length);
                partial.Content.Headers.ContentLength = slice.Length;
                partial.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return Task.FromResult(partial);
            }

            var full = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            full.Content.Headers.ContentLength = payload.Length;
            full.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            full.Headers.AcceptRanges.Add("bytes");
            return Task.FromResult(full);
        }
    }
}
