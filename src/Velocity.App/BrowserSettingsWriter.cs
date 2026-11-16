using System.IO;
using System.Text.Json;
using Velocity.Services;

namespace Velocity.App;

/// <summary>
/// Publishes the folder and theme the browser extension should show. The extension reads this through the
/// native messaging host, so it never needs to know where the database lives.
/// </summary>
internal static class BrowserSettingsWriter
{
    public static void Write(SettingsService settings)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velocity");
            Directory.CreateDirectory(directory);

            var payload = new
            {
                destination = settings.Current.DefaultDownloadDirectory,
                theme = settings.Current.ThemeName,
                appPath = Environment.ProcessPath,
                appVersion = typeof(BrowserSettingsWriter).Assembly.GetName().Version?.ToString(3),
                updatedUtc = DateTimeOffset.UtcNow
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(directory, "browser-settings.json"), json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The extension falls back to the host's defaults when this file cannot be written.
        }
    }
}
