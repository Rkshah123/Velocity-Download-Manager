using Velocity.Core.Models;
using Velocity.Data.Repositories;

namespace Velocity.Services;

public sealed class SettingsService(SettingsRepository repository)
{
    public AppSettings Current { get; } = new();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var values = await repository.LoadAsync(cancellationToken);
        if (values.TryGetValue(nameof(AppSettings.DefaultDownloadDirectory), out var folder)) Current.DefaultDownloadDirectory = folder;
        if (values.TryGetValue(nameof(AppSettings.MaximumActiveDownloads), out var max) && int.TryParse(max, out var maxValue)) Current.MaximumActiveDownloads = Math.Clamp(maxValue, 0, 64);
        if (values.TryGetValue(nameof(AppSettings.DefaultConnectionCount), out var connections) && int.TryParse(connections, out var connectionValue)) Current.DefaultConnectionCount = Math.Clamp(connectionValue, 1, 16);
        if (values.TryGetValue(nameof(AppSettings.RetryLimit), out var retries) && int.TryParse(retries, out var retryValue)) Current.RetryLimit = Math.Clamp(retryValue, 0, 20);
        if (values.TryGetValue(nameof(AppSettings.DarkTheme), out var dark) && bool.TryParse(dark, out var darkValue)) Current.DarkTheme = darkValue;
        if (values.TryGetValue(nameof(AppSettings.GlobalSpeedLimitKilobytesPerSecond), out var speed) && int.TryParse(speed, out var speedValue)) Current.GlobalSpeedLimitKilobytesPerSecond = Math.Clamp(speedValue, 0, 102400);
        if (values.TryGetValue(nameof(AppSettings.CompletionNotifications), out var notifications) && bool.TryParse(notifications, out var notificationValue)) Current.CompletionNotifications = notificationValue;
        if (values.TryGetValue(nameof(AppSettings.MonitorClipboard), out var clipboard) && bool.TryParse(clipboard, out var clipboardValue)) Current.MonitorClipboard = clipboardValue;
        if (values.TryGetValue(nameof(AppSettings.StartWithWindows), out var startup) && bool.TryParse(startup, out var startupValue)) Current.StartWithWindows = startupValue;
        if (values.TryGetValue(nameof(AppSettings.ResumeOnStartup), out var resume) && bool.TryParse(resume, out var resumeValue)) Current.ResumeOnStartup = resumeValue;
        if (values.TryGetValue(nameof(AppSettings.ThemeName), out var theme) && theme is "Midnight" or "Ember" or "Light") Current.ThemeName = theme;
    }

    public Task SaveAsync(CancellationToken cancellationToken = default) => repository.SaveAsync(new Dictionary<string, string>
    {
        [nameof(AppSettings.DefaultDownloadDirectory)] = Current.DefaultDownloadDirectory,
        [nameof(AppSettings.MaximumActiveDownloads)] = Current.MaximumActiveDownloads.ToString(),
        [nameof(AppSettings.DefaultConnectionCount)] = Current.DefaultConnectionCount.ToString(),
        [nameof(AppSettings.RetryLimit)] = Current.RetryLimit.ToString(),
        [nameof(AppSettings.DarkTheme)] = Current.DarkTheme.ToString(),
        [nameof(AppSettings.GlobalSpeedLimitKilobytesPerSecond)] = Current.GlobalSpeedLimitKilobytesPerSecond.ToString(),
        [nameof(AppSettings.CompletionNotifications)] = Current.CompletionNotifications.ToString(),
        [nameof(AppSettings.MonitorClipboard)] = Current.MonitorClipboard.ToString(),
        [nameof(AppSettings.StartWithWindows)] = Current.StartWithWindows.ToString(),
        [nameof(AppSettings.ResumeOnStartup)] = Current.ResumeOnStartup.ToString(),
        [nameof(AppSettings.ThemeName)] = Current.ThemeName
    }, cancellationToken);
}
