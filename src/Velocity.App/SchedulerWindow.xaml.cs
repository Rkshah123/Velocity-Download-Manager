using System.Globalization;
using System.Windows;
using Velocity.Core.Models;
using Velocity.Services;

namespace Velocity.App;

public partial class SchedulerWindow : Window
{
    private readonly SchedulerService _scheduler;
    private readonly SettingsService _settings;

    public SchedulerWindow(SchedulerService scheduler, SettingsService settings)
    {
        InitializeComponent();
        _scheduler = scheduler;
        _settings = settings;
        DateBox.Text = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        UrlBox.Focus();
    }

    private void TodayClick(object sender, RoutedEventArgs e) =>
        DateBox.Text = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void TomorrowClick(object sender, RoutedEventArgs e) =>
        DateBox.Text = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void ScheduleClick(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            ShowError("Enter a valid HTTP or HTTPS link.");
            return;
        }

        var dateText = DateBox.Text.Trim();
        if (!DateTime.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            && !DateTime.TryParse(dateText, CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
        {
            ShowError("Enter the start date as YYYY-MM-DD, for example 2026-11-07.");
            return;
        }

        if ((!TimeSpan.TryParseExact(TimeBox.Text.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out var time)
             && !TimeSpan.TryParse(TimeBox.Text.Trim(), CultureInfo.InvariantCulture, out time))
            || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
        {
            ShowError("Enter the start time as HH:mm, for example 22:30.");
            return;
        }

        var startAt = new DateTimeOffset(date.Date.Add(time));
        if (startAt <= DateTimeOffset.Now)
        {
            ShowError("Choose a start time in the future.");
            return;
        }

        var request = new DownloadRequest(
            uri.ToString(),
            _settings.Current.DefaultDownloadDirectory,
            Connections: _settings.Current.DefaultConnectionCount);

        _scheduler.Schedule(request, startAt);
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorBox.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
