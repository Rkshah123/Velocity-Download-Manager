using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using Velocity.Services;

namespace Velocity.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly string _originalTheme;
    private string _pendingTheme;
    private bool _committed;

    public SettingsWindow(SettingsService settings)
    {
        InitializeComponent();
        _settings = settings;

        FolderBox.Text = settings.Current.DefaultDownloadDirectory;
        MaxBox.Text = settings.Current.MaximumActiveDownloads.ToString();
        ConnectionsBox.Text = settings.Current.DefaultConnectionCount.ToString();
        RetriesBox.Text = settings.Current.RetryLimit.ToString();
        SpeedBox.Text = (settings.Current.GlobalSpeedLimitKilobytesPerSecond / 1024d).ToString("0.##", CultureInfo.CurrentCulture);
        NotificationsBox.IsChecked = settings.Current.CompletionNotifications;
        ClipboardBox.IsChecked = settings.Current.MonitorClipboard;
        StartupBox.IsChecked = settings.Current.StartWithWindows;
        ResumeBox.IsChecked = settings.Current.ResumeOnStartup;

        _originalTheme = ThemeManager.ThemeNames.FirstOrDefault(name =>
            string.Equals(name, settings.Current.ThemeName, StringComparison.OrdinalIgnoreCase)) ?? ThemeManager.DefaultTheme;
        _pendingTheme = _originalTheme;
        CardFor(_originalTheme).IsChecked = true;
    }

    private RadioButton CardFor(string theme) => theme switch
    {
        "Ember" => EmberCard,
        "Light" => LightCard,
        _ => MidnightCard
    };

    private void ThemeCardChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Content: string theme }) return;
        _pendingTheme = theme;
        // Preview immediately in every open window; Save commits, Cancel restores.
        ThemeManager.Apply(theme);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // A live preview that is not saved must not outlive the dialog.
        if (!_committed) ThemeManager.Apply(_originalTheme);
        base.OnClosing(e);
    }

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = FolderBox.Text };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) FolderBox.Text = dialog.SelectedPath;
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FolderBox.Text))
        {
            ShowError("Choose a default download folder.", FolderBox);
            return;
        }

        if (!int.TryParse(MaxBox.Text, out var max) || max is < 0 or > 64)
        {
            ShowError("Active downloads must be between 0 and 64 (0 = no limit).", MaxBox);
            return;
        }

        if (!int.TryParse(ConnectionsBox.Text, out var connections) || connections is < 1 or > 16)
        {
            ShowError("Connections per download must be between 1 and 16.", ConnectionsBox);
            return;
        }

        if (!int.TryParse(RetriesBox.Text, out var retries) || retries is < 0 or > 20)
        {
            ShowError("Automatic retries must be between 0 and 20.", RetriesBox);
            return;
        }

        // The box is in MB/s (the unit people use); the app stores KB/s, so 1.5 MB/s becomes 1536 KB/s exactly.
        if (!TryParseMegabytes(SpeedBox.Text, out var speedMegabytes) || speedMegabytes is < 0 or > 100)
        {
            ShowError("The speed limit must be between 0 and 100 MB/s. Use a dot for fractions, for example 2.5.", SpeedBox);
            return;
        }

        var speed = (int)Math.Round(speedMegabytes * 1024);

        _settings.Current.DefaultDownloadDirectory = FolderBox.Text.Trim();
        _settings.Current.MaximumActiveDownloads = max;
        _settings.Current.DefaultConnectionCount = connections;
        _settings.Current.RetryLimit = retries;
        _settings.Current.GlobalSpeedLimitKilobytesPerSecond = speed;
        _settings.Current.CompletionNotifications = NotificationsBox.IsChecked == true;
        _settings.Current.MonitorClipboard = ClipboardBox.IsChecked == true;
        _settings.Current.StartWithWindows = StartupBox.IsChecked == true;
        _settings.Current.ResumeOnStartup = ResumeBox.IsChecked == true;
        // The Run entry is a Windows setting, not a Velocity one, so it is written as soon as it is chosen.
        StartupIntegration.ApplyStartWithWindows(_settings.Current.StartWithWindows);
        _settings.Current.ThemeName = _pendingTheme;
        _committed = true;
        DialogResult = true;
    }

    private static bool TryParseMegabytes(string? text, out double megabytes) =>
        double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out megabytes) ||
        double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out megabytes);

    private void ShowError(string message, TextBox focus)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        focus.Focus();
        focus.SelectAll();
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
