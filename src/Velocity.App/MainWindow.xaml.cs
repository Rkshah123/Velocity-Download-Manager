using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Velocity.Core.Models;
using Velocity.Services;

namespace Velocity.App;

public partial class MainWindow : Window
{
    private readonly DownloadManager _downloadManager;
    private readonly SettingsService _settings;
    private readonly SchedulerService _scheduler;
    private readonly DispatcherTimer _browserInboxTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _trayStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _clipboardTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly DispatcherTimer _copyIconTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private System.Windows.Shapes.Path? _copiedIcon;
    private Geometry? _copiedIconData;
    private readonly HashSet<string> _offeredClipboard = [];
    private string? _lastClipboardText;
    private ListCollectionView? _view;

    public ObservableCollection<DownloadItem> Downloads { get; } = [];
    public ObservableCollection<LibraryFilter> Filters { get; } = [];

    public MainWindow(DownloadManager downloadManager, SettingsService settings, SchedulerService scheduler)
    {
        InitializeComponent();
        _downloadManager = downloadManager;
        _settings = settings;
        _scheduler = scheduler;
        DataContext = this;

        BuildFilters();
        FilterList.ItemsSource = Filters;

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(Downloads);
        _view.Filter = MatchesView;
        _view.SortDescriptions.Add(new SortDescription(nameof(DownloadItem.Id), ListSortDirection.Descending));
        DownloadsGrid.ItemsSource = _view;

        FilterList.SelectedIndex = 0;
        Downloads.CollectionChanged += (_, _) => OnLibraryChanged();
        _downloadManager.ItemRegistered += OnItemRegistered;
        _browserInboxTimer.Tick += async (_, _) => await ProcessBrowserInboxAsync();
        _trayStatusTimer.Tick += (_, _) => UpdateTrayStatus();
        _clipboardTimer.Tick += (_, _) => OfferClipboardLink();
        _copyIconTimer.Tick += (_, _) => RestoreCopyIcon();
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        Closed += (_, _) => Detach();
    }

    private bool _trayHintShown;

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;
        Hide();
        if (_trayHintShown) return;
        _trayHintShown = true;
        (Application.Current as App)?.ShowNotification("Velocity is still running", "Downloads continue in the background. Use the tray icon to reopen Velocity.");
    }

    private void BuildFilters()
    {
        Geometry Icon(string key) => TryFindResource(key) as Geometry ?? Geometry.Empty;
        Filters.Add(new LibraryFilter("All downloads", Icon("IconLibrary"), _ => true));
        Filters.Add(new LibraryFilter("Downloading", Icon("IconDownload"), item => item.Status == DownloadStatus.Downloading));
        Filters.Add(new LibraryFilter("Queued", Icon("IconClock"), item => item.Status is DownloadStatus.Queued or DownloadStatus.Resuming));
        Filters.Add(new LibraryFilter("Paused", Icon("IconPause"), item => item.Status == DownloadStatus.Paused));
        Filters.Add(new LibraryFilter("Completed", Icon("IconCheckCircle"), item => item.Status == DownloadStatus.Completed));
        Filters.Add(new LibraryFilter("Failed", Icon("IconAlert"), item => item.Status == DownloadStatus.Failed));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var restored = new List<DownloadItem>();
        try
        {
            foreach (var record in await _downloadManager.LoadHistoryAsync())
            {
                var item = _downloadManager.CreateHistoryItem(record);
                AddItem(item);
                restored.Add(item);
            }
        }
        catch (Exception exception)
        {
            ShowDialog("History could not be loaded", "Your previous downloads are safe on disk, but this list could not be read.",
                VelocityDialogKind.Warning, detail: exception.Message);
        }

        // Anything that was still transferring when the app closed stays paused unless the user asked Velocity to
        // continue automatically. Opening the app must never start transferring (or create files) on its own.
        if (_settings.Current.ResumeOnStartup) _downloadManager.ResumeInterrupted(restored);

        // A cancelled or failed transfer leaves a partial and its checkpoint behind. No row owns those any more,
        // so they are removed instead of piling up in the download folder every time Velocity starts.
        _downloadManager.CleanupAbandonedPartials(restored);

        OnLibraryChanged();
        _browserInboxTimer.Start();
        _trayStatusTimer.Start();
        _clipboardTimer.Start();
        await ProcessBrowserInboxAsync();
    }

    private void Detach()
    {
        _browserInboxTimer.Stop();
        _trayStatusTimer.Stop();
        _clipboardTimer.Stop();
        RestoreCopyIcon();
        _downloadManager.ItemRegistered -= OnItemRegistered;
    }

    private void OnItemRegistered(object? sender, DownloadItem item)
    {
        if (Dispatcher.CheckAccess()) AddItem(item);
        else Dispatcher.BeginInvoke(new Action(() => AddItem(item)));
    }

    private void AddItem(DownloadItem item)
    {
        if (Downloads.Contains(item)) return;
        item.PropertyChanged += OnDownloadPropertyChanged;
        Downloads.Add(item);
    }

    private void OnDownloadPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DownloadItem item) return;

        if (e.PropertyName == nameof(DownloadItem.Status))
        {
            Dispatcher.Invoke(() =>
            {
                _view?.Refresh();
                OnLibraryChanged();
                if (_settings.Current.CompletionNotifications && item.Status is DownloadStatus.Completed or DownloadStatus.Failed)
                    Notify(item);
            });
        }
        else if (e.PropertyName == nameof(DownloadItem.BytesPerSecond))
        {
            // Coalesce live speed updates into a single background pass.
            Dispatcher.BeginInvoke(new Action(UpdateStats), DispatcherPriority.Background);
        }
    }

    private void Notify(DownloadItem item)
    {
        if (Application.Current is not App app) return;
        if (item.Status == DownloadStatus.Completed)
            app.ShowNotification("Download complete", item.FileName);
        else
            app.ShowNotification("Download failed", string.IsNullOrWhiteSpace(item.Error) ? item.FileName : $"{item.FileName} — {item.Error}");
    }

    private async Task ProcessBrowserInboxAsync()
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
                if (Downloads.Any(existing => existing.Url == requestUrl && existing.Status is DownloadStatus.Queued or DownloadStatus.Resuming or DownloadStatus.Downloading or DownloadStatus.Paused)) continue;

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

                if (deferred) await _downloadManager.QueueAsync(request);
                else await _downloadManager.StartAsync(request);
            }
            catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException or ArgumentException)
            {
                // A malformed browser entry must never stop the listener.
            }
        }

        OnLibraryChanged();
    }

    /// <summary>
    /// Puts Velocity in front of whatever the user is doing before it shows a download dialog. A prompt must never
    /// open behind another window or next to a minimized taskbar button, so the window is restored, raised and
    /// focused; the dialog itself is centered on the screen by <see cref="AddDownloadWindow"/>.
    /// </summary>
    private void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private async void AddDownloadClick(object sender, RoutedEventArgs e)
    {
        BringToFront();
        var dialog = new AddDownloadWindow(_settings.Current.DefaultDownloadDirectory) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Requests.Count == 0) return;

        var added = 0;
        var skipped = 0;
        var failures = new List<string>();

        foreach (var request in dialog.Requests)
        {
            // The same link must not be queued twice, whether it is already running or inside this batch.
            if (Downloads.Any(item => item.Url == request.Url && item.Status is DownloadStatus.Queued or DownloadStatus.Resuming or DownloadStatus.Downloading or DownloadStatus.Paused))
            {
                skipped++;
                continue;
            }

            try
            {
                await _downloadManager.StartAsync(request with { Connections = _settings.Current.DefaultConnectionCount });
                added++;
            }
            catch (Exception exception)
            {
                failures.Add($"{request.Url} — {exception.Message}");
            }
        }

        if (failures.Count > 0)
        {
            ShowDialog(added == 0 ? "Download could not start" : "Some links could not be added",
                added == 0 ? "Velocity could not add this transfer." : $"{added} added, {failures.Count} failed.",
                VelocityDialogKind.Danger, detail: string.Join(Environment.NewLine, failures));
        }
        else if (added == 0 && skipped > 0)
        {
            ShowDialog("Already in the list", "Those links are already being downloaded or queued, so Velocity will not add them twice.",
                VelocityDialogKind.Information);
        }
        else if (added > 1)
        {
            (Application.Current as App)?.ShowNotification("Downloads added", $"{added} downloads started.");
        }
    }

    /// <summary>
    /// IDM-style clipboard monitor: a copied link is offered once and never added behind the user's back. It
    /// only runs while the window is visible, so a Velocity parked in the tray never throws a dialog at the
    /// desktop, and it ignores links that are already on the list.
    /// </summary>
    private void OfferClipboardLink()
    {
        if (!_settings.Current.MonitorClipboard || !IsVisible || WindowState == WindowState.Minimized) return;

        string text;
        try
        {
            if (!Clipboard.ContainsText()) return;
            text = Clipboard.GetText().Trim();
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            // Another process owns the clipboard for a moment; the next tick will see the link.
            return;
        }

        if (text.Length is 0 or > 2048 || string.Equals(text, _lastClipboardText, StringComparison.Ordinal)) return;
        _lastClipboardText = text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        if (_offeredClipboard.Count > 200) _offeredClipboard.Clear();
        if (!_offeredClipboard.Add(uri.ToString())) return;
        if (Downloads.Any(item => string.Equals(item.Url, uri.ToString(), StringComparison.OrdinalIgnoreCase))) return;

        var accepted = VelocityDialog.Show(this, "Download this link?", "Velocity found a link on your clipboard.",
            VelocityDialogKind.Information, "Download", "Not now", detail: uri.ToString(), caption: "CLIPBOARD");
        if (accepted) _ = StartClipboardDownloadAsync(uri);
    }

    private async Task StartClipboardDownloadAsync(Uri uri)
    {
        try
        {
            await _downloadManager.StartAsync(new DownloadRequest(
                uri.ToString(), _settings.Current.DefaultDownloadDirectory, Connections: _settings.Current.DefaultConnectionCount));
        }
        catch (Exception exception)
        {
            ShowDialog("Download could not start", "Velocity could not add the link from the clipboard.",
                VelocityDialogKind.Danger, detail: exception.Message);
        }
    }

    /// <summary>Keeps the tray icon honest: an arc for the combined progress and a tooltip with count and speed.</summary>
    private void UpdateTrayStatus()
    {
        if (Application.Current is not App app) return;

        var running = Downloads.Where(item => item.Status == DownloadStatus.Downloading).ToList();
        if (running.Count == 0)
        {
            app.UpdateTrayStatus(0, 0, string.Empty);
            return;
        }

        var speed = running.Sum(item => item.BytesPerSecond);
        var known = running.All(item => item.TotalBytes is > 0);
        var percent = known
            ? running.Sum(item => (double)item.DownloadedBytes) / running.Sum(item => item.TotalBytes!.Value) * 100
            : 0;
        var detail = $"{running.Count} downloading · {FormatSpeed(speed)}" + (known ? $" · {percent:0}%" : string.Empty);
        app.UpdateTrayStatus(running.Count, percent, detail);
    }

    private static string FormatSpeed(double bytesPerSecond) =>
        bytesPerSecond >= 1024 * 1024 ? $"{bytesPerSecond / (1024 * 1024):0.0} MB/s"
            : bytesPerSecond >= 1024 ? $"{bytesPerSecond / 1024:0} KB/s"
            : $"{bytesPerSecond:0} B/s";

    private void PauseAllClick(object sender, RoutedEventArgs e) => _downloadManager.PauseAll(Downloads);

    private void ResumeAllClick(object sender, RoutedEventArgs e) => _downloadManager.ResumeAll(Downloads);

    private void PrioritizeSelectedClick(object sender, RoutedEventArgs e)
    {
        var selected = DownloadsGrid.SelectedItems.OfType<DownloadItem>().ToList();
        if (selected.Count > 0) _downloadManager.Prioritize(selected);
    }

    private static DownloadItem? ItemFrom(object sender) => (sender as FrameworkElement)?.Tag as DownloadItem;

    private void PauseClick(object sender, RoutedEventArgs e) { if (ItemFrom(sender) is { } item) _downloadManager.Pause(item); }
    private void ResumeClick(object sender, RoutedEventArgs e) { if (ItemFrom(sender) is { } item) _downloadManager.Resume(item); }
    private void RetryClick(object sender, RoutedEventArgs e) { if (ItemFrom(sender) is { } item) _downloadManager.Retry(item); }

    /// <summary>
    /// Copies the address the file was downloaded from — the original link, never the local path, so it can be
    /// pasted into a browser, a message or another download manager.
    /// </summary>
    private void CopyLinkClick(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item) return;

        try
        {
            Clipboard.SetText(item.Url);
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            ShowDialog("Link could not be copied", "Windows did not give Velocity the clipboard just now. Try again in a moment.",
                VelocityDialogKind.Warning, detail: item.Url);
            return;
        }

        // The row icon turns into a checkmark for a moment, so a copy that did work is visibly confirmed.
        if (sender is Button { Content: System.Windows.Shapes.Path icon }) ShowCopiedIcon(icon);
    }

    /// <summary>Swaps the double-square icon for a checkmark, then puts the original back after a moment.</summary>
    private void ShowCopiedIcon(System.Windows.Shapes.Path icon)
    {
        RestoreCopyIcon();
        _copiedIcon = icon;
        _copiedIconData = icon.Data;
        icon.Data = (Geometry)FindResource("IconCheck");
        _copyIconTimer.Start();
    }

    private void RestoreCopyIcon()
    {
        _copyIconTimer.Stop();
        if (_copiedIcon is not null && _copiedIconData is not null) _copiedIcon.Data = _copiedIconData;
        _copiedIcon = null;
        _copiedIconData = null;
    }

    private void OpenFileClick(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item) return;
        if (!File.Exists(item.DestinationPath))
        {
            ShowDialog("File not found", "This download is no longer where Velocity saved it. It may have been moved, renamed or deleted.",
                VelocityDialogKind.Warning, detail: item.DestinationPath);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(item.DestinationPath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowDialog("File could not be opened", "Windows refused to open this file.",
                VelocityDialogKind.Danger, detail: exception.Message);
        }
    }

    private void OpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item) return;
        var directory = Directory.Exists(item.DestinationDirectory)
            ? item.DestinationDirectory
            : Path.GetDirectoryName(item.DestinationPath);

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            ShowDialog("Folder not found", "The destination folder no longer exists.", VelocityDialogKind.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowDialog("Folder could not be opened", "Windows refused to open this folder.",
                VelocityDialogKind.Danger, detail: exception.Message);
        }
    }

    private async void DeleteItemClick(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is { } item) await ConfirmAndDeleteAsync([item]);
    }

    private async void DeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        var selected = DownloadsGrid.SelectedItems.OfType<DownloadItem>().ToList();
        await ConfirmAndDeleteAsync(selected);
    }

    private async Task ConfirmAndDeleteAsync(IReadOnlyList<DownloadItem> items)
    {
        if (items.Count == 0) return;

        // A delete on a live transfer must never be a single careless click: it would silently throw away
        // everything downloaded so far.
        var active = items.Where(item => item.Status is DownloadStatus.Downloading or DownloadStatus.Queued or DownloadStatus.Resuming).ToList();
        string message;
        string? detail = null;
        if (items.Count == 1 && active.Count == 1)
        {
            var item = items[0];
            message = $"'{item.FileName}' is still downloading. Removing it stops the transfer and discards the part that is already on disk.";
            detail = $"{item.StatusText} · {item.DownloadedSize} of {item.TotalSize} ({item.PercentText})";
        }
        else if (items.Count > 1 && active.Count > 0)
        {
            message = $"{active.Count} of these {items.Count} downloads are still transferring. Removing them stops those transfers, deletes their partial files and removes every entry from the list.";
            detail = string.Join("\n", active.Select(item => $"• {item.FileName} — {item.StatusText} · {item.PercentText}"));
        }
        else
        {
            message = items.Count == 1
                ? $"Remove '{items[0].FileName}' from Velocity?"
                : $"Remove these {items.Count} downloads from Velocity?";
        }

        var dialog = new DeleteConfirmWindow(message, deleteFileDefault: true, isActive: active.Count > 0, detail: detail) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        string? error = null;
        foreach (var item in items)
        {
            try
            {
                error ??= await _downloadManager.DeleteAsync(item, dialog.DeleteFile);
            }
            catch (Exception exception)
            {
                error ??= $"'{item.FileName}' could not be removed: {exception.Message}";
            }

            item.PropertyChanged -= OnDownloadPropertyChanged;
            Downloads.Remove(item);
        }

        DownloadsGrid.SelectedItems.Clear();
        OnLibraryChanged();

        if (error is not null)
            ShowDialog("Download could not be removed", "The entry was removed, but part of it could not be cleaned up.",
                VelocityDialogKind.Warning, detail: error);
    }

    private void DownloadsSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SetSelectionActionsEnabled(DownloadsGrid.SelectedItems.Count > 0);

    /// <summary>The selection-only actions follow the grid in one place, so they can never disagree.</summary>
    private void SetSelectionActionsEnabled(bool enabled)
    {
        DeleteButton.IsEnabled = enabled;
        PrioritizeButton.IsEnabled = enabled;
    }

    private void DownloadsGridPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<ListViewItem>(e.OriginalSource as DependencyObject) is null) DownloadsGrid.SelectedItems.Clear();
    }

    private static T? FindVisualParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = element is Visual visual ? VisualTreeHelper.GetParent(visual) : null;
        }
        return null;
    }

    private bool MatchesView(object candidate)
    {
        if (candidate is not DownloadItem item) return false;
        if (FilterList.SelectedItem is LibraryFilter filter && !filter.Match(item)) return false;

        var query = SearchTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(query)) return true;

        return item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Url.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.DestinationDirectory.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void FilterSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilterList.SelectedItem is LibraryFilter filter) PageTitle.Text = filter.Name;
        _view?.Refresh();
        UpdateEmptyStates();
    }

    private void SearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _view?.Refresh();
        UpdateEmptyStates();
    }

    private void SearchGotFocus(object sender, RoutedEventArgs e) => SearchPlaceholder.Visibility = Visibility.Collapsed;
    private void SearchLostFocus(object sender, RoutedEventArgs e) =>
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    private void ClearFiltersClick(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Clear();
        FilterList.SelectedIndex = 0;
    }

    private void OnLibraryChanged()
    {
        foreach (var filter in Filters) filter.Count = Downloads.Count(filter.Match);
        UpdateStats();
        UpdateEmptyStates();
    }

    private void UpdateStats()
    {
        var active = Downloads.Count(item => item.Status == DownloadStatus.Downloading);
        var queued = Downloads.Count(item => item.Status is DownloadStatus.Queued or DownloadStatus.Resuming);
        var completed = Downloads.Count(item => item.Status == DownloadStatus.Completed);
        long totalBytes = 0;
        double bytesPerSecond = 0;
        foreach (var item in Downloads)
        {
            totalBytes += item.DownloadedBytes;
            if (item.Status == DownloadStatus.Downloading) bytesPerSecond += item.BytesPerSecond;
        }

        ActiveStatText.Text = active.ToString();
        QueuedStatText.Text = queued.ToString();
        CompletedStatText.Text = completed.ToString();
        VolumeStatText.Text = ByteSize.Format(totalBytes);

        var speed = $"{ByteSize.Format((long)bytesPerSecond)}/s";
        PageSubtitle.Text = active > 0 ? $"{active} downloading · {speed}" : "Manage and monitor every transfer";
        SidebarSummary.Text = active > 0 ? $"{speed} across {active} transfer{(active == 1 ? "" : "s")}" : "No transfers running";
        StatusBarText.Text = active > 0 ? $"Downloading {active} file{(active == 1 ? "" : "s")} · {speed}" : "Ready";
    }

    private void UpdateEmptyStates()
    {
        var hasDownloads = Downloads.Count > 0;
        var visible = _view?.Count ?? Downloads.Count;

        EmptyState.Visibility = hasDownloads ? Visibility.Collapsed : Visibility.Visible;
        NoResultsState.Visibility = hasDownloads && visible == 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadsGrid.Visibility = visible > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsText.Text = hasDownloads ? $"{visible} of {Downloads.Count}" : string.Empty;
        SetSelectionActionsEnabled(DownloadsGrid.SelectedItems.Count > 0);
    }

    private void WindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.N)
        {
            AddDownloadClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.F)
        {
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Keyboard.FocusedElement is not TextBoxBase and not ComboBox)
        {
            DeleteSelectedClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private async void SettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        ThemeManager.Apply(_settings.Current.ThemeName);
        BrowserSettingsWriter.Write(_settings);
        try
        {
            await _settings.SaveAsync();
        }
        catch (Exception exception)
        {
            ShowDialog("Settings could not be saved", "The new settings are in effect for this session but could not be written to disk.",
                VelocityDialogKind.Danger, detail: exception.Message);
        }

        _downloadManager.RefreshQueue();
        // A new app-wide ceiling applies to the transfers already running too, not only to the next ones.
        _downloadManager.RefreshSpeeds(Downloads);
        OnLibraryChanged();
    }

    private void SpeedLimitClick(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item) return;
        var dialog = new SpeedLimitWindow(item, _settings.Current.GlobalSpeedLimitKilobytesPerSecond) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _downloadManager.SetSpeedLimit(item, dialog.KilobytesPerSecond);
    }

    private void ShowDialog(string heading, string message, VelocityDialogKind kind, string? detail = null) =>
        VelocityDialog.Show(this, heading, message, kind, detail: detail);

    private void SchedulerClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SchedulerWindow(_scheduler, _settings) { Owner = this };
        dialog.ShowDialog();
    }

    private void AboutClick(object sender, RoutedEventArgs e)
    {
        var dialog = new AboutWindow { Owner = this };
        dialog.ShowDialog();
    }
}
