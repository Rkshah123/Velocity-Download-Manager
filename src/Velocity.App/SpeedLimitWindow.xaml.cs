using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Velocity.Core.Models;
using Velocity.Services;

namespace Velocity.App;

/// <summary>
/// IDM-style per-download speed ceiling, entered in MB/s because that is the unit a person actually thinks in.
/// The value applies to this file only; removing it restores full speed immediately, even when an app-wide
/// ceiling is configured.
/// </summary>
public partial class SpeedLimitWindow : Window
{
    private readonly DownloadItem _item;
    private readonly int _appDefaultKilobytes;

    public SpeedLimitWindow(DownloadItem item, int appDefaultKilobytesPerSecond)
    {
        InitializeComponent();
        _item = item;
        _appDefaultKilobytes = Math.Max(0, appDefaultKilobytesPerSecond);

        FileText.Text = item.FileName;
        LimitBox.Text = FormatMegabytes(item.SpeedLimitKilobytesPerSecond);
        HintText.Text = _appDefaultKilobytes > 0
            ? $"No limit means full speed for this file, even though the app-wide limit is {FormatMegabytes(_appDefaultKilobytes)} MB/s. A value above zero limits only this file."
            : "No limit means full speed. A value above zero limits only this file and leaves the others at full speed.";
        LimitBox.Focus();
        LimitBox.SelectAll();
    }

    /// <summary>The ceiling in KB/s, which is what the download manager and the engine work with.</summary>
    public int KilobytesPerSecond { get; private set; }

    private static string FormatMegabytes(int kilobytes) =>
        kilobytes <= 0 ? "0" : (kilobytes / 1024d).ToString("0.##", CultureInfo.CurrentCulture);

    private static bool TryParseMegabytes(string? text, out double megabytes) =>
        double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out megabytes) ||
        double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out megabytes);

    private void PresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string preset }) LimitBox.Text = preset;
        ErrorPanel.Visibility = Visibility.Collapsed;
    }

    private void NoLimitClick(object sender, RoutedEventArgs e)
    {
        LimitBox.Text = "0";
        ErrorPanel.Visibility = Visibility.Collapsed;
    }

    private void ApplyClick(object sender, RoutedEventArgs e)
    {
        // 100 MB/s is the same ceiling the settings screen allows, expressed in one place.
        if (!TryParseMegabytes(LimitBox.Text, out var megabytes) || megabytes < 0 || megabytes > 100)
        {
            ErrorText.Text = "Enter a speed in MB/s between 0 and 100. Use a dot for fractions, for example 1.5.";
            ErrorPanel.Visibility = Visibility.Visible;
            LimitBox.Focus();
            LimitBox.SelectAll();
            return;
        }

        KilobytesPerSecond = (int)Math.Round(megabytes * 1024);
        DialogResult = true;
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
