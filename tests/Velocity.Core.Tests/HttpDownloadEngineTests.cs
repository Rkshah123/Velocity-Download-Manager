using System.Net;
using System.Net.Sockets;
using System.Text;
using Velocity.Core.Models;
using Velocity.DownloadEngine;

namespace Velocity.Core.Tests;

public sealed class HttpDownloadEngineTests
{
    [Fact]
    public async Task DownloadsResponseToDestinationAndReportsProgress()
    {
        var payload = Encoding.UTF8.GetBytes("Velocity integration download\n");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buffer = new byte[2048];
            _ = await stream.ReadAsync(buffer);
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(payload);
        });

        var destination = Directory.CreateTempSubdirectory("velocity-test-");
        try
        {
            using var httpClient = new HttpClient();
            var engine = new HttpDownloadEngine(httpClient);
            var progressValues = new List<DownloadProgress>();
            var result = await engine.DownloadAsync(new DownloadRequest($"http://127.0.0.1:{port}/sample.bin", destination.FullName, Connections: 1), new Progress<DownloadProgress>(progressValues.Add));

            Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
            Assert.Equal(payload.Length, result.DownloadedBytes);
            Assert.NotEmpty(progressValues);
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            destination.Delete(true);
            listener.Stop();
        }
    }
}
