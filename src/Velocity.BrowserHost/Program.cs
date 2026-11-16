using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/*
 * Velocity native messaging host.
 *
 * The browser extension talks to this small helper instead of touching the app's database. It answers
 * three requests:
 *   {type:"settings"}                     → destination folder, theme and whether the app is running
 *   {type:"download", url, filename, action} → queues the download for the app and starts it if needed
 *   {type:"ping"}                         → liveness check used by the options page
 *
 * Nothing here downloads anything itself: it writes one JSON line to browser-inbox.jsonl, which the
 * running app consumes, and starts the app when it is not running.
 *
 * Every response is assembled with JsonObject rather than a reflection-based serializer, so the release build
 * can be trimmed to a few megabytes without silently dropping properties.
 */

const int MaxMessageBytes = 1024 * 1024;
const string HostVersion = "2.1.0";
const string AppProcessName = "Velocity.App";

var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velocity");
var settingsPath = Path.Combine(dataDirectory, "browser-settings.json");
var inboxPath = Path.Combine(dataDirectory, "browser-inbox.jsonl");

using var input = Console.OpenStandardInput();
using var output = Console.OpenStandardOutput();

while (true)
{
    var lengthBytes = new byte[4];
    var read = await ReadExactlyAsync(input, lengthBytes);
    if (read != 4) break;
    var length = BitConverter.ToInt32(lengthBytes, 0);
    if (length <= 0 || length > MaxMessageBytes) break;

    var payload = new byte[length];
    if (await ReadExactlyAsync(input, payload) != length) break;

    byte[] response;
    try
    {
        using var json = JsonDocument.Parse(payload);
        var answer = await HandleAsync(json.RootElement);
        response = Encoding.UTF8.GetBytes(answer.ToJsonString());
    }
    catch (Exception exception)
    {
        response = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["ok"] = false,
            ["error"] = exception.Message
        }.ToJsonString());
    }

    await WriteMessageAsync(output, response);
}

async Task<JsonObject> HandleAsync(JsonElement request)
{
    var type = request.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
        ? typeElement.GetString()
        : request.TryGetProperty("url", out _) ? "download" : "";

    switch (type)
    {
        case "settings":
            var settings = ReadSettings();
            return new JsonObject
            {
                ["ok"] = true,
                ["settings"] = new JsonObject
                {
                    ["destination"] = settings.Destination,
                    ["theme"] = settings.Theme,
                    ["running"] = IsAppRunning(),
                    ["appPath"] = settings.AppPath,
                    ["version"] = HostVersion
                }
            };

        case "ping":
            return new JsonObject { ["ok"] = true, ["running"] = IsAppRunning(), ["version"] = HostVersion };

        case "download":
            return await QueueDownloadAsync(request);

        default:
            return new JsonObject { ["ok"] = false, ["error"] = $"Unknown request type '{type}'." };
    }
}

async Task<JsonObject> QueueDownloadAsync(JsonElement request)
{
    if (!request.TryGetProperty("url", out var urlElement) || !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        throw new InvalidDataException("A valid HTTP or HTTPS url is required.");

    var filename = request.TryGetProperty("filename", out var filenameElement) && filenameElement.ValueKind == JsonValueKind.String
        ? filenameElement.GetString()
        : null;
    if (filename is { Length: > 260 }) filename = filename[..260];

    var action = request.TryGetProperty("action", out var actionElement) && actionElement.ValueKind == JsonValueKind.String
        ? actionElement.GetString()
        : "start";
    if (action is not ("start" or "later" or "download")) action = "start";

    // The page that asked for the file. A stream host (and plenty of ordinary ones) answers only when the
    // request looks like it came from the player, so the page address travels with the download.
    var referer = request.TryGetProperty("referer", out var refererElement) && refererElement.ValueKind == JsonValueKind.String
        ? refererElement.GetString()
        : null;
    if (!string.IsNullOrWhiteSpace(referer) &&
        (!Uri.TryCreate(referer, UriKind.Absolute, out var refererUri) || refererUri.Scheme is not ("http" or "https")))
        referer = null;

    var settings = ReadSettings();
    Directory.CreateDirectory(dataDirectory);

    var entry = new JsonObject
    {
        ["url"] = uri.ToString(),
        ["filename"] = filename,
        ["action"] = action == "later" ? "later" : "download",
        ["referer"] = referer,
        ["receivedUtc"] = DateTimeOffset.UtcNow.ToString("O")
    };

    await AppendWithRetryAsync(inboxPath, entry.ToJsonString() + Environment.NewLine);

    var launched = false;
    if (!IsAppRunning())
    {
        var appPath = ResolveAppPath(settings.AppPath);
        if (appPath is not null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(appPath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(appPath) ?? dataDirectory
                });
                launched = true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = $"The download was queued, but Velocity could not be started ({exception.Message}). Open Velocity and it will pick the download up."
                };
            }
        }
    }

    return new JsonObject
    {
        ["ok"] = true,
        ["launched"] = launched,
        ["destination"] = settings.Destination,
        ["running"] = launched || IsAppRunning()
    };
}

HostSettings ReadSettings()
{
    var fallback = new HostSettings(
        Destination: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        Theme: "Midnight",
        AppPath: ResolveAppPath(null));

    try
    {
        if (File.Exists(settingsPath))
        {
            var stored = JsonNode.Parse(File.ReadAllText(settingsPath));
            if (stored is JsonObject obj)
            {
                return new HostSettings(
                    Destination: ReadString(obj, "destination") ?? fallback.Destination,
                    Theme: ReadString(obj, "theme") is "Midnight" or "Ember" or "Light" ? ReadString(obj, "theme")! : fallback.Theme,
                    AppPath: ReadString(obj, "appPath") ?? fallback.AppPath);
            }
        }
    }
    catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
    {
        // A damaged settings file must never stop a download; the app rewrites it on its next start.
    }

    WriteSettings(fallback);
    return fallback;
}

void WriteSettings(HostSettings settings)
{
    try
    {
        Directory.CreateDirectory(dataDirectory);
        var json = new JsonObject
        {
            ["destination"] = settings.Destination,
            ["theme"] = settings.Theme,
            ["appPath"] = settings.AppPath,
            ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O")
        };
        File.WriteAllText(settingsPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        // The extension falls back to its own defaults.
    }
}

static string? ReadString(JsonObject obj, string name) =>
    obj.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
        ? text
        : null;

static bool IsAppRunning()
{
    try
    {
        var processes = Process.GetProcessesByName(AppProcessName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }
    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        return false;
    }
}

static string? ResolveAppPath(string? configured)
{
    if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "Velocity.App.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velocity", "app", "Velocity.App.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Velocity", "Velocity.App.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Velocity", "Velocity.App.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Velocity", "Velocity.App.exe")
    };

    foreach (var candidate in candidates)
        if (File.Exists(candidate)) return candidate;
    return null;
}

static async Task AppendWithRetryAsync(string path, string content)
{
    // One JSON object per line, never with a byte-order mark: the reader parses each line on its own.
    var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    for (var attempt = 0; ; attempt++)
    {
        try
        {
            await File.AppendAllTextAsync(path, content, encoding);
            return;
        }
        catch (IOException) when (attempt < 4)
        {
            await Task.Delay(40);
        }
    }
}

static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var count = await stream.ReadAsync(buffer.AsMemory(offset));
        if (count == 0) return offset;
        offset += count;
    }
    return offset;
}

static async Task WriteMessageAsync(Stream output, byte[] payload)
{
    await output.WriteAsync(BitConverter.GetBytes(payload.Length));
    await output.WriteAsync(payload);
    await output.FlushAsync();
}

internal sealed record HostSettings(string Destination, string Theme, string? AppPath);
