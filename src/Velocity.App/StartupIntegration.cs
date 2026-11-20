using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Velocity.App;

/// <summary>
/// The Windows-side arrangements Velocity makes for itself, so the user never has to run a script or keep a
/// server alive: starting with Windows, and the browser's native-messaging registration.
///
/// Both are local by construction — a registry value and a JSON file that names an executable. There is no
/// port, no loopback address and no network lookup anywhere in this path, which is what keeps the browser
/// integration working while a VPN is connected, disconnected or switching servers.
/// </summary>
internal static class StartupIntegration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Velocity";
    private const string HostName = "com.velocity.download_manager";

    /// <summary>
    /// The extension's id comes from the public key carried in its manifest, so it is the same on every machine and
    /// after any reinstall or folder change — unlike an unpacked extension's path-derived id, which is what made the
    /// helper stop matching the moment the project folder moved. It is registered whether or not anything else is
    /// known yet, so the first start of the app already authorises the extension.
    /// </summary>
    private const string ExtensionId = "jnfpmdnbiomjnelmlaohkpifaacandjg";

    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velocity");

    private static readonly string HostDirectory = Path.Combine(DataDirectory, "native-host");
    private static readonly string HostManifest = Path.Combine(HostDirectory, HostName + ".json");
    private static readonly string BrowserSettings = Path.Combine(DataDirectory, "browser-settings.json");

    /// <summary>Adds or removes the per-user Run entry. A missing or unwritable key is not an error worth stopping for.</summary>
    public static void ApplyStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return;

            if (!enabled)
            {
                if (key.GetValue(RunValueName) is not null) key.DeleteValue(RunValueName, throwOnMissingValue: false);
                return;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return;

            var wanted = $"\"{executable}\"";
            if (!string.Equals(key.GetValue(RunValueName) as string, wanted, StringComparison.OrdinalIgnoreCase))
                key.SetValue(RunValueName, wanted, RegistryValueKind.String);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // A locked-down profile simply starts Velocity by hand; nothing else depends on this.
        }
    }

    /// <summary>
    /// Keeps the native-messaging manifest pointing at an executable that actually exists, and registers it for
    /// every Chromium browser installed for this user. Origins already granted are preserved, so a working
    /// installation is repaired rather than replaced.
    /// </summary>
    public static void EnsureNativeHost()
    {
        try
        {
            var existing = ReadManifest();
            var hostPath = ResolveHostExecutable(existing);
            if (hostPath is null) return;   // never point a browser at an executable that is not there

            // The shipped id is always authorised, so registration can never depend on a settings file that a fresh
            // profile, a move or a reinstall has not written yet.
            var origins = ReadOrigins(existing);
            AddOrigin(origins, ExtensionId);
            AddOrigin(origins, ReadExtensionId());

            Directory.CreateDirectory(HostDirectory);
            var manifest = new JsonObject
            {
                ["name"] = HostName,
                ["description"] = "Velocity native messaging host",
                ["path"] = hostPath,
                ["type"] = "stdio"
            };
            var array = new JsonArray();
            foreach (var origin in origins) array.Add(origin);
            manifest["allowed_origins"] = array;

            var json = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            if (!File.Exists(HostManifest) || !string.Equals(File.ReadAllText(HostManifest), json, StringComparison.Ordinal))
                File.WriteAllText(HostManifest, json);

            foreach (var vendor in new[]
                     {
                         @"Software\Google\Chrome\NativeMessagingHosts",
                         @"Software\Microsoft\Edge\NativeMessagingHosts",
                         @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
                         @"Software\Vivaldi\NativeMessagingHosts",
                         @"Software\Chromium\NativeMessagingHosts"
                     })
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"{vendor}\{HostName}");
                if (key is null) continue;
                if (!string.Equals(key.GetValue(null) as string, HostManifest, StringComparison.OrdinalIgnoreCase))
                    key.SetValue(null, HostManifest, RegistryValueKind.String);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException
                                              or IOException or JsonException or InvalidOperationException)
        {
            // Registration is a convenience that can be repeated by the extension's own script if needed.
        }
    }

    private static void AddOrigin(List<string> origins, string? extensionId)
    {
        var id = extensionId?.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(id) || !id.All(character => character is >= 'a' and <= 'p')) return;
        var origin = $"chrome-extension://{id}/";
        if (!origins.Contains(origin, StringComparer.OrdinalIgnoreCase)) origins.Add(origin);
    }

    private static JsonObject? ReadManifest()
    {
        try
        {
            if (!File.Exists(HostManifest)) return null;
            return JsonNode.Parse(File.ReadAllText(HostManifest)) as JsonObject;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private static List<string> ReadOrigins(JsonObject? manifest)
    {
        var origins = new List<string>();
        if (manifest?["allowed_origins"] is not JsonArray array) return origins;
        foreach (var node in array)
        {
            var value = node?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(value) && !origins.Contains(value, StringComparer.OrdinalIgnoreCase))
                origins.Add(value);
        }
        return origins;
    }

    private static string? ReadExtensionId()
    {
        try
        {
            if (!File.Exists(BrowserSettings)) return null;
            var settings = JsonNode.Parse(File.ReadAllText(BrowserSettings)) as JsonObject;
            return settings?["extensionId"]?.GetValue<string>();
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ResolveHostExecutable(JsonObject? manifest)
    {
        var candidates = new List<string>();

        // An installation that already works keeps the path it was registered with.
        var configured = manifest?["path"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);

        var baseDirectory = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDirectory, "Velocity.BrowserHost.exe"));
        candidates.Add(Path.Combine(baseDirectory, "host", "Velocity.BrowserHost.exe"));
        candidates.Add(Path.Combine(DataDirectory, "app", "Velocity.BrowserHost.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Velocity", "Velocity.BrowserHost.exe"));

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
            {
                // A malformed path is skipped like a missing one.
            }
        }
        return null;
    }
}
