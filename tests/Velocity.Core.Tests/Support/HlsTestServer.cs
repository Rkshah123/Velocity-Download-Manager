using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Velocity.Core.Tests.Support;

/// <summary>
/// Serves the pieces of an HLS stream: a master playlist, variant playlists, segments and an AES-128 key, so
/// a test can prove that what the user gets is the joined video and not one stray segment.
/// </summary>
internal sealed class HlsTestServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, (string Type, byte[] Body)> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _requests = [];
    private readonly object _gate = new();

    public HlsTestServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public IReadOnlyList<string> Requests
    {
        get { lock (_gate) return _requests.ToArray(); }
    }

    public HlsTestServer AddText(string path, string text, string type = "application/vnd.apple.mpegurl")
    {
        _routes[path] = (type, Encoding.UTF8.GetBytes(text));
        return this;
    }

    public HlsTestServer AddBytes(string path, byte[] bytes, string type = "video/mp2t")
    {
        _routes[path] = (type, bytes);
        return this;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) { return; }

            try
            {
                var path = context.Request.Url?.AbsolutePath ?? "/";
                lock (_gate) _requests.Add(path);

                if (_routes.TryGetValue(path, out var route))
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = route.Type;
                    context.Response.ContentLength64 = route.Body.Length;
                    await context.Response.OutputStream.WriteAsync(route.Body, _stop.Token);
                }
                else
                {
                    context.Response.StatusCode = 404;
                }
                context.Response.Close();
            }
            catch (Exception)
            {
                try { context.Response.Abort(); } catch { /* the client gave up first */ }
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { /* already closed */ }
        _listener.Close();
        _stop.Dispose();
    }
}
