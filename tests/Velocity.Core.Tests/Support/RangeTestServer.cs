using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Velocity.Core.Tests.Support;

/// <summary>A request the test server answered, so a test can assert what the engine actually asked for.</summary>
internal sealed record ServedRequest(string Path, string? Range, string? IfRange);

/// <summary>
/// Minimal HTTP/1.1 server with byte-range behaviour, used to exercise pause/resume against the real engine:
/// honours ranges, ignores them (200), rejects them (416), or changes the file between requests.
/// </summary>
internal sealed class RangeTestServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<ServedRequest> _requests = [];
    private readonly object _gate = new();
    private byte[] _payload;
    private int _version = 1;

    public RangeTestServer(byte[] payload, int chunkDelayMs = 25)
    {
        _payload = payload;
        ChunkDelayMs = chunkDelayMs;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }
    public int ChunkDelayMs { get; set; }
    public bool HonorRanges { get; set; } = true;
    public bool HonorIfRange { get; set; } = true;
    public bool RejectRanges { get; set; }
    public string? DispositionName { get; set; }

    /// <summary>Advertise <c>Accept-Ranges: bytes</c> even while ignoring range requests (a lying server).</summary>
    public bool AdvertiseRanges { get; set; } = true;

    /// <summary>Send an ETag on every response.</summary>
    public bool SendETag { get; set; } = true;

    /// <summary>Send an ETag on 206 responses. Some servers only send it on the first, full response.</summary>
    public bool SendETagOnPartial { get; set; } = true;

    /// <summary>Send Last-Modified on every response.</summary>
    public bool SendLastModified { get; set; } = true;

    /// <summary>Send the ETag in its weak form (<c>W/"v1"</c>), which is not a valid <c>If-Range</c> validator.</summary>
    public bool WeakETag { get; set; }

    /// <summary>Refuse the one-byte <c>bytes=0-0</c> probe, while still honouring every real range.</summary>
    public bool RejectProbe { get; set; }

    /// <summary>Change the Last-Modified date together with the payload version.</summary>
    public bool VersionedLastModified { get; set; }

    /// <summary>Drop the connection after this many body bytes, to simulate a network interruption.</summary>
    public long? DropAfterBytes { get; set; }

    /// <summary>Answer this request path with a 302 to <see cref="RedirectTarget"/>.</summary>
    public string? RedirectPath { get; set; }

    public string? RedirectTarget { get; set; }

    /// <summary>How many requests are being served right now, so a test can prove real parallelism.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    private int _inFlight;
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public string ETag => $"\"v{_version}\"";

    public IReadOnlyList<ServedRequest> Requests
    {
        get { lock (_gate) return _requests.ToArray(); }
    }

    public void ReplacePayload(byte[] payload)
    {
        lock (_gate)
        {
            _payload = payload;
            _version++;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream);
                if (request is null) return;

                if (RedirectPath is not null && RedirectTarget is not null &&
                    string.Equals(request.Value.Path, RedirectPath, StringComparison.Ordinal))
                {
                    var location = $"http://127.0.0.1:{Port}{RedirectTarget}";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                    return;
                }

                byte[] payload;
                int version;
                lock (_gate)
                {
                    payload = _payload;
                    version = _version;
                }

                var (path, range, ifRange) = request.Value;
                lock (_gate) _requests.Add(new ServedRequest(path, range, ifRange));

                var total = payload.Length;
                var status = 200;
                var start = 0;
                var end = total - 1;

                var strongEtag = $"\"v{version}\"";
                if (range is not null)
                {
                    // A weak validator must not satisfy If-Range (RFC 9110 13.1.5): the server ignores the range and
                    // answers with the whole file. Last-Modified is accepted, like a real server would.
                    var weak = ifRange?.StartsWith("W/", StringComparison.OrdinalIgnoreCase) == true;
                    var matchesValidator = !HonorIfRange || ifRange is null ||
                        (!weak && (string.Equals(ifRange, strongEtag, StringComparison.Ordinal) ||
                                   string.Equals(ifRange, LastModifiedFor(version).ToString("R"), StringComparison.Ordinal)));
                    if (RejectRanges || (RejectProbe && range == "bytes=0-0"))
                    {
                        status = 416;
                    }
                    else if (HonorRanges && matchesValidator)
                    {
                        var match = Regex.Match(range, @"bytes=(\d+)-(\d*)");
                        if (match.Success)
                        {
                            status = 206;
                            start = int.Parse(match.Groups[1].Value);
                            end = match.Groups[2].Value.Length > 0 ? int.Parse(match.Groups[2].Value) : total - 1;
                            if (start >= total) { status = 416; }
                            else end = Math.Min(end, total - 1);
                        }
                    }
                }

                var headers = new StringBuilder();
                headers.Append(status switch { 206 => "HTTP/1.1 206 Partial Content\r\n", 416 => "HTTP/1.1 416 Range Not Satisfiable\r\n", _ => "HTTP/1.1 200 OK\r\n" });
                if (SendETag && (status != 206 || SendETagOnPartial)) headers.Append(WeakETag ? $"ETag: W/{strongEtag}\r\n" : $"ETag: {strongEtag}\r\n");
                if (SendLastModified)
                {
                    // Built from a real date so the weekday is valid; .NET rejects RFC1123 dates whose weekday is wrong.
                    headers.Append($"Last-Modified: {LastModifiedFor(version).ToString("R")}\r\n");
                }
                headers.Append("Content-Type: application/octet-stream\r\n");
                headers.Append("Connection: close\r\n");
                if (HonorRanges && AdvertiseRanges) headers.Append("Accept-Ranges: bytes\r\n");
                if (DispositionName is not null) headers.Append($"Content-Disposition: attachment; filename=\"{DispositionName}\"\r\n");
                if (status == 206) headers.Append($"Content-Range: bytes {start}-{end}/{total}\r\n");
                if (status == 416) headers.Append($"Content-Range: bytes */{total}\r\n");

                var bodyLength = status switch { 206 => end - start + 1, 200 => total, _ => 0 };
                headers.Append($"Content-Length: {bodyLength}\r\n\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), _stop.Token);

                const int chunk = 64 * 1024;
                var bodyWritten = 0L;
                for (var offset = start; status != 416 && offset <= end; offset += chunk)
                {
                    var count = Math.Min(chunk, end - offset + 1);
                    if (DropAfterBytes is long drop && bodyWritten + count > drop)
                    {
                        // Cut the connection mid-body: the client must keep what it already received.
                        try { await stream.WriteAsync(payload.AsMemory(offset, (int)Math.Max(0, drop - bodyWritten)), _stop.Token); }
                        catch (Exception) { }
                        return;
                    }
                    await stream.WriteAsync(payload.AsMemory(offset, count), _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                    bodyWritten += count;
                    if (ChunkDelayMs > 0) await Task.Delay(ChunkDelayMs, _stop.Token);
                }
            }
            catch (Exception)
            {
                // The client cancelling mid-body is a normal part of a pause test.
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private DateTimeOffset LastModifiedFor(int version) =>
        new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero).AddDays(VersionedLastModified ? version - 1 : 0);

    private static async Task<(string Path, string? Range, string? IfRange)?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new List<byte>(1024);
        var one = new byte[1];
        while (buffer.Count < 16 * 1024)
        {
            var read = await stream.ReadAsync(one);
            if (read == 0) return null;
            buffer.Add(one[0]);
            if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n') break;
        }

        var text = Encoding.ASCII.GetString(buffer.ToArray());
        var lines = text.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        string? range = null;
        string? ifRange = null;
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (name.Equals("Range", StringComparison.OrdinalIgnoreCase)) range = value;
            if (name.Equals("If-Range", StringComparison.OrdinalIgnoreCase)) ifRange = value;
        }

        return (requestLine[1], range, ifRange);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        _stop.Dispose();
    }
}
