using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Velocity.Core.Models;
using Velocity.Services;

namespace Velocity.App;

/// <summary>
/// Owns the parts of Velocity that have to outlive the library window: the window opened for each transfer and the
/// listener that turns an accepted browser capture into a download. Every download window is a top-level window
/// with no owner, so the two windows are independent — closing or minimizing one never touches the other, and a
/// transfer keeps running when one of them is gone.
/// </summary>
public sealed class DownloadCoordinator
{
    private readonly DownloadManager _manager;
    private readonly SettingsService _settings;
    private readonly Dictionary<DownloadItem, DownloadWindow> _windows = [];
    private readonly DispatcherTimer _inboxTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public DownloadCoordinator(DownloadManager manager, SettingsService settings)
    {
        _manager = manager;
        _settings = settings;
        _inboxTimer.Tick += async (_, _) => await ProcessInboxAsync();
    }

    /// <summary>Raised after a capture started, so a visible library can select the row it just gained.</summary>
    public event EventHandler<DownloadItem>? CaptureStarted;

    public void Start() => _inboxTimer.Start();

    public void Stop() => _inboxTimer.Stop();

    /// <summary>
    /// True when the browser handed a download over before Velocity started — the native host writes the entry and
    /// then launches the app. Startup uses it to keep the library window out of the way of the page.
    /// </summary>
    public static bool HasPendingCapture()
    {
        var inbox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velocity", "browser-inbox.jsonl");
        try
        {
            return File.Exists(inbox) && new FileInfo(inbox).Length > 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Shows the transfer's own window, or brings back the one already open for it. A second window for the same
    /// transfer would only duplicate its live values.
    /// </summary>
    public void Open(DownloadItem item)
    {
        if (_windows.TryGetValue(item, out var existing))
        {
            existing.Show();
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        // A transfer that was resumed from the library shows its real state straight away; the window reads the item
        // live, so nothing has to be refreshed by hand here.

        var window = new DownloadWindow(item, _manager, _settings);
        _windows[item] = window;
        window.Closed += (_, _) =>
        {
            _windows.Remove(item);
            // Only this window is gone. Whether the application also has a reason to stay alive is decided
            // from the windows and transfers that remain, never from this close.
            (Application.Current as App)?.NotifyWindowClosed(window);
        };
        window.Show();
    }

    /// <summary>Closes the window of a download that no longer exists. The transfer itself is not touched.</summary>
    public void Close(DownloadItem item)
    {
        if (_windows.Remove(item, out var window)) window.Close();
    }

    /// <summary>
    /// Turns the native host's queue into downloads. A page's offer card is the only thing that asks, so an
    /// accepted entry starts the transfer and opens its own window; a deferred entry is queued without a window.
    /// The library window is never raised here — the window that appears is the transfer's own.
    /// </summary>
    public async Task ProcessInboxAsync()
    {
        var inbox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velocity", "browser-inbox.jsonl");
        if (!File.Exists(inbox)) return;

        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(inbox);
            File.Delete(inbox);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return; // Leave the inbox for the next tick rather than processing it twice.
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                // Older hosts could prefix the first line with a byte-order mark.
                using var json = JsonDocument.Parse(line.TrimStart('\uFEFF'));
                if (!json.RootElement.TryGetProperty("url", out var url)) continue;
                var requestUrl = url.GetString();
                if (string.IsNullOrWhiteSpace(requestUrl)) continue;
                if (_manager.Items.Any(existing => existing.Url == requestUrl && existing.Status is DownloadStatus.Queued or DownloadStatus.Resuming or DownloadStatus.Downloading or DownloadStatus.Paused)) continue;

                var fileName = json.RootElement.TryGetProperty("filename", out var fileNameElement) ? fileNameElement.GetString() : null;
                // The page the request came from. A video host (or a signed link) answers only when the
                // request looks like the player made it, so the browser's Referer is carried through.
                Dictionary<string, string>? headers = null;
                if (json.RootElement.TryGetProperty("referer", out var refererElement) && refererElement.ValueKind == JsonValueKind.String)
                {
                    var referer = refererElement.GetString();
                    if (!string.IsNullOrWhiteSpace(referer)) headers = new Dictionary<string, string> { ["Referer"] = referer };
                }

                var request = new DownloadRequest(requestUrl, _settings.Current.DefaultDownloadDirectory, fileName, _settings.Current.DefaultConnectionCount, Headers: headers);
                var deferred = json.RootElement.TryGetProperty("action", out var action) && string.Equals(action.GetString(), "later", StringComparison.OrdinalIgnoreCase);

                if (deferred)
                {
                    await _manager.QueueAsync(request);
                    continue;
                }

                // The user already said yes on the page, so the only window that opens is this transfer's.
                var item = await _manager.StartAsync(request);
                Open(item);
                CaptureStarted?.Invoke(this, item);
            }
            catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException or ArgumentException)
            {
                // A malformed browser entry must never stop the listener.
            }
        }
    }
}
