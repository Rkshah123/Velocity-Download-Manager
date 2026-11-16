using System.Net;
using Velocity.Core.Models;
using Velocity.DownloadEngine;

namespace Velocity.Core.Tests;

public sealed class DownloadValidationTests
{
    [Theory]
    [InlineData("https://example.com/file.pdf")]
    [InlineData("http://localhost/archive.zip")]
    public async Task AcceptsHttpAndHttpsUrls(string url)
    {
        using var client = new HttpClient(new RejectingHandler());
        var engine = new HttpDownloadEngine(client);
        await Assert.ThrowsAsync<HttpRequestException>(() => engine.DownloadAsync(new DownloadRequest(url, Path.GetTempPath(), Connections: 1)));
    }

    [Theory]
    [InlineData("ftp://example.com/file.zip")]
    [InlineData("not-a-url")]
    public async Task RejectsUnsupportedUrls(string url)
    {
        using var client = new HttpClient();
        var engine = new HttpDownloadEngine(client);
        await Assert.ThrowsAsync<ArgumentException>(() => engine.DownloadAsync(new DownloadRequest(url, Path.GetTempPath())));
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
