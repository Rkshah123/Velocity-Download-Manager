namespace Velocity.Core.Models;

public sealed class AppSettings
{
    public string DefaultDownloadDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>How many transfers may run at the same time. <c>0</c> means no limit at all.</summary>
    public int MaximumActiveDownloads { get; set; }
    public int DefaultConnectionCount { get; set; } = 4;
    public int RetryLimit { get; set; } = 3;
    public bool DarkTheme { get; set; } = true;
    /// <summary>App-wide ceiling for each transfer in KB/s. <c>0</c> means no limit.</summary>
    public int GlobalSpeedLimitKilobytesPerSecond { get; set; }
    public bool CompletionNotifications { get; set; } = true;

    /// <summary>Offer a link that appears on the clipboard, the way IDM does. On by default and switchable in Settings.</summary>
    public bool MonitorClipboard { get; set; } = true;

    /// <summary>Start Velocity with Windows, so the browser integration never has to wait for a manual launch.</summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>
    /// Continue transfers that were running when Velocity last closed. Off by default: starting the app must not
    /// create files or folders in the download directory unless the user actually asks for a download.
    /// </summary>
    public bool ResumeOnStartup { get; set; }
    public string ThemeName { get; set; } = "Midnight";
}
