using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Velocity.Core.Models;
using Velocity.Services;

namespace Velocity.App;

/// <summary>
/// The window XDM opens beside a transfer: ring, name, bytes, speed and time left, the three actions, and one row
/// per connection. It exists in two sizes — the big window and the small one "Less" switches to — and every value
/// is read from the live <see cref="DownloadItem"/>, so it follows the transfer without owning any of it.
/// </summary>
public partial class DownloadWindow : Window
{
    private const double ExpandedWidth = 960;
    private const double ExpandedHeight = 640;
    private const double CompactWidth = 440;
    private const double CompactHeight = 392;

    private readonly DownloadItem _item;
    private readonly DownloadManager _manager;
    private readonly SettingsService _settings;
    private readonly ObservableCollection<ConnectionRow> _rows = [];

    private bool _compact;
    private bool _closedOnCompletion;

    public DownloadWindow(DownloadItem item, DownloadManager manager, SettingsService settings)
    {
        InitializeComponent();
        _item = item;
        _manager = manager;
        _settings = settings;

        ConnectionsList.ItemsSource = _rows;
        ConnectionsListC.ItemsSource = _rows;

        item.PropertyChanged += OnItemChanged;
        Closed += (_, _) => item.PropertyChanged -= OnItemChanged;
        Loaded += (_, _) => Refresh();

        Refresh();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Progress reports arrive on the UI thread, but a status change after a cancel can land elsewhere.
        if (Dispatcher.CheckAccess()) { Refresh(); LeaveOnCompletion(); }
        else Dispatcher.BeginInvoke(new Action(() => { Refresh(); LeaveOnCompletion(); }));
    }

    /// <summary>
    /// A finished transfer has nothing left to show, so its window closes itself instead of staying on screen at
    /// 100%. The close is queued at background priority so the final 100% frame paints first, and it runs once.
    /// </summary>
    private void LeaveOnCompletion()
    {
        if (_closedOnCompletion || _item.Status != DownloadStatus.Completed) return;
        _closedOnCompletion = true;
        Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Background);
    }

    private void Refresh()
    {
        var percentText = _item.TotalBytes is > 0 ? $"{_item.Progress:0}%" : "—";
        var sizeText = _item.TotalBytes is > 0 ? $"{_item.DownloadedSize} / {_item.TotalSize}" : _item.TotalSize;

        CaptionText.Text = $"[ {percentText} ] {_item.FileName}";
        Title = $"[ {percentText} ] {_item.FileName}";

        Ring.Value = _item.Progress;
        RingC.Value = _item.Progress;
        RingText.Text = percentText;
        RingTextC.Text = percentText;
        Bar.Value = _item.Progress;
        BarC.Value = _item.Progress;

        NameText.Text = _item.FileName;
        NameTextC.Text = _item.FileName;
        var state = _item.LinkExpired ? "Link expired — the partial file was kept" : _item.StatusText;
        StateText.Text = state;
        StateTextC.Text = state;

        DownloadedText.Text = $"Downloaded {percentText}";
        DownloadedTextC.Text = $"Downloaded {percentText}";
        SizeText.Text = sizeText;
        SizeTextC.Text = sizeText;

        SpeedText.Text = $"Speed {(_item.BytesPerSecond > 0 ? _item.Speed : "—")}";
        SpeedTextC.Text = SpeedText.Text;
        var remaining = _item.TimeRemaining;
        LeftText.Text = remaining;
        LeftTextC.Text = remaining;

        var label = _item.CanPause ? "Pause" : _item.CanResume ? "Resume" : _item.CanDownloadAgain ? "Download again" : _item.CanRetry ? "Retry" : "Pause";
        PauseButton.Content = label;
        PauseButtonC.Content = label;
        PauseButton.IsEnabled = _item.ShowStatusAction;
        PauseButtonC.IsEnabled = _item.ShowStatusAction;

        RefreshConnections();
    }

    /// <summary>
    /// One row per connection, like the table under XDM's buttons. The engine reports the bytes each segment has
    /// written; a single-stream transfer simply has one row.
    /// </summary>
    private void RefreshConnections()
    {
        var written = _item.ConnectionBytes;
        var count = written?.Count ?? 1;
        if (count != _rows.Count) BuildRows(count);

        var total = _item.TotalBytes ?? 0;
        var part = count > 0 && total > 0 ? (total + count - 1) / count : 0;

        for (var index = 0; index < _rows.Count; index++)
        {
            var bytes = written is not null && index < written.Count
                ? written[index]
                : index == 0 ? _item.DownloadedBytes : 0;
            var size = part > 0 ? Math.Clamp(total - index * part, 0, part) : 0;

            var status = _item.Status switch
            {
                DownloadStatus.Completed => "Completed",
                DownloadStatus.Failed => size > 0 && bytes >= size ? "Completed" : "Failed",
                DownloadStatus.Cancelled => "Stopped",
                DownloadStatus.Paused => "Paused",
                DownloadStatus.Queued or DownloadStatus.Resuming => "Waiting",
                _ => size > 0 && bytes >= size ? "Completed" : "Downloading"
            };

            _rows[index].Update(status, ByteSize.Format(bytes));
        }
    }

    private void BuildRows(int count)
    {
        _rows.Clear();
        for (var index = 1; index <= count; index++) _rows.Add(new ConnectionRow(index));
    }

    private void ToggleLayoutClick(object sender, RoutedEventArgs e)
    {
        _compact = !_compact;
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;

        ExpandedLayout.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        CompactLayout.Visibility = _compact ? Visibility.Visible : Visibility.Collapsed;
        MaximizeButton.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;

        MinWidth = _compact ? 380 : 560;
        MinHeight = _compact ? 320 : 400;
        Width = _compact ? CompactWidth : ExpandedWidth;
        Height = _compact ? CompactHeight : ExpandedHeight;
    }

    private void HideClick(object sender, RoutedEventArgs e) => Hide();

    private void PauseResumeClick(object sender, RoutedEventArgs e)
    {
        if (_item.CanPause) _manager.Pause(_item);
        else if (_item.CanResume) _manager.Resume(_item);
        else if (_item.CanDownloadAgain) _manager.DownloadAgain(_item);
        else if (_item.CanRetry) _manager.Retry(_item);
    }

    private void SpeedLimitClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SpeedLimitWindow(_item, _settings.Current.GlobalSpeedLimitKilobytesPerSecond) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _manager.SetSpeedLimit(_item, dialog.KilobytesPerSecond);
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeIcon.Data = (System.Windows.Media.Geometry)FindResource(
            WindowState == WindowState.Maximized ? "IconRestoreShape" : "IconMaximizeShape");
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>A single row of the connection table; only the values that changed are pushed to the UI.</summary>
    public sealed class ConnectionRow(int index) : INotifyPropertyChanged
    {
        private string _status = "Waiting";
        private string _downloaded = "0 B";

        public int Index { get; } = index;
        public string Number => Index.ToString();

        public string Status
        {
            get => _status;
            private set => Set(ref _status, value);
        }

        public string Downloaded
        {
            get => _downloaded;
            private set => Set(ref _downloaded, value);
        }

        internal void Update(string status, string downloaded)
        {
            Status = status;
            Downloaded = downloaded;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set(ref string field, string value, [CallerMemberName] string? propertyName = null)
        {
            if (string.Equals(field, value, StringComparison.Ordinal)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
