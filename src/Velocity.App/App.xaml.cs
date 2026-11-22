using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Velocity.Data.Database;
using Velocity.Services;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using CreateParams = System.Windows.Forms.CreateParams;
using Message = System.Windows.Forms.Message;
using MouseButtons = System.Windows.Forms.MouseButtons;
using NativeWindow = System.Windows.Forms.NativeWindow;
using ToolTipIcon = System.Windows.Forms.ToolTipIcon;
using WinIcon = System.Drawing.Icon;
using Bitmap = System.Drawing.Bitmap;
using Color = System.Drawing.Color;
using Graphics = System.Drawing.Graphics;
using Pen = System.Drawing.Pen;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Rectangle = System.Drawing.Rectangle;
using SmoothingMode = System.Drawing.Drawing2D.SmoothingMode;

namespace Velocity.App;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;
    private NotifyIcon? _trayIcon;
    private TaskbarWatcher? _taskbar;
    private WinIcon? _baseTrayIcon;
    private readonly Dictionary<int, WinIcon> _progressIcons = [];
    private int _trayIconPercent = -1;
    private DownloadManager? _downloadManager;
    private SettingsService? _settings;
    private DownloadCoordinator? _coordinator;
    private bool _mainWindowClosed;
    private bool _exiting;

    public App()
    {
        // The library window and a download's window are independent: closing one must never shut down the
        // application while the other is open or a transfer is still running. The exit decision is made below.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(ClearInputFocusOnBackgroundClick), true);

        try
        {
            await StartAsync();
        }
        catch (Exception exception)
        {
            try
            {
                VelocityDialog.Show(null, "Velocity could not start", "The application stopped before the main window opened.",
                    VelocityDialogKind.Danger, detail: exception.Message);
            }
            catch (Exception)
            {
                // If even the themed window cannot load, a plain box is still better than a silent exit.
                MessageBox.Show($"Velocity could not start.\n\n{exception.Message}", "Velocity", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            Shutdown(1);
        }
    }

    private async Task StartAsync()
    {
        var databasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velocity", "velocity.db");
        _services = ApplicationBootstrapper.BuildServiceProvider(databasePath);
        await _services.GetRequiredService<DatabaseInitializer>().InitializeAsync(databasePath);

        var settings = _services.GetRequiredService<SettingsService>();
        await settings.LoadAsync();
        ThemeManager.Apply(settings.Current.ThemeName);
        BrowserSettingsWriter.Write(settings);

        // A real installation keeps itself ready: the browser's native host stays registered against an
        // executable that exists, and Velocity starts with Windows so a download never waits for a manual launch.
        StartupIntegration.EnsureNativeHost();
        StartupIntegration.ApplyStartWithWindows(settings.Current.StartWithWindows);

        _settings = settings;
        _downloadManager = _services.GetRequiredService<DownloadManager>();
        // The capture listener and every download window belong to the application, not to the library window:
        // a transfer that is still running when the library is closed keeps its window and keeps going.
        _coordinator = new DownloadCoordinator(_downloadManager, settings);
        _coordinator.Start();

        MainWindow = CreateMainWindow();
        // A capture handed over by the browser is why Velocity was started. The transfer's window is what the user
        // asked for, so the library opens minimized in the background instead of covering the page they are on;
        // its taskbar button or the tray icon brings it forward only when they ask for it.
        if (DownloadCoordinator.HasPendingCapture())
        {
            MainWindow.ShowActivated = false;
            MainWindow.WindowState = WindowState.Minimized;
        }
        MainWindow.Show();
        InitializeTrayIcon();
        // A fresh hand-off is processed at once instead of waiting for the first two-second tick.
        await _coordinator.ProcessInboxAsync();
    }

    private MainWindow CreateMainWindow()
    {
        var window = new MainWindow(
            _downloadManager!,
            _settings!,
            _services!.GetRequiredService<SchedulerService>(),
            _coordinator!);

        window.Closed += (_, _) =>
        {
            _mainWindowClosed = true;
            NotifyWindowClosed(window);
        };

        return window;
    }

    /// <summary>
    /// Decides what a closing window means for the process. A window that is still open, or a transfer that is
    /// still running, keeps Velocity alive; only the last window of an idle application ends it. A download that
    /// is running when the library is closed therefore continues in the background until it finishes.
    /// </summary>
    internal void NotifyWindowClosed(Window closing)
    {
        if (Windows.OfType<Window>().Any(window => !ReferenceEquals(window, closing) && window.IsVisible)) return;

        // A running transfer, or one that is paused and waiting for the user, keeps Velocity alive in the
        // notification area. Only "Force quit" from the tray menu ends the process.
        if (_downloadManager?.HasUnfinishedDownloads == true)
        {
            ShowNotification("Velocity is still running", "Your downloads are kept in the notification area. Click the tray icon to open Velocity again.");
            return;
        }

        Shutdown();
    }

    /// <summary>
    /// True while the process must not exit — either the tray menu's force quit was chosen, or a window is closing
    /// while downloads are still unfinished.
    /// </summary>
    internal bool IsExiting => _exiting;

    /// <summary>
    /// The library window's X button must never throw away unfinished work. While any download is running, queued,
    /// paused or failed the window is hidden into the notification area instead of closing, and the tray icon brings
    /// it back with its rows exactly as they were.
    /// </summary>
    internal bool HideMainWindowInsteadOfClosing(Window window)
    {
        if (_exiting || !ReferenceEquals(window, MainWindow)) return false;
        if (_downloadManager?.HasUnfinishedDownloads != true) return false;

        ShowNotification("Velocity is still running", "Your downloads stay in the notification area. Click the tray icon to open Velocity again.");
        return true;
    }

    /// <summary>Ends the process even with transfers running, exactly like the tray menu's force quit.</summary>
    internal void ForceQuit()
    {
        _exiting = true;
        Shutdown();
    }

    private static void ClearInputFocusOnBackgroundClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        for (DependencyObject? current = e.OriginalSource as DependencyObject; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : null)
        {
            if (current is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.ComboBox or System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.DatePicker) return;
            if (current is Window) break;
        }

        Keyboard.ClearFocus();
    }

    /// <summary>Shows a non-blocking tray notification instead of interrupting the user with a dialog.</summary>
    public void ShowNotification(string title, string message)
    {
        if (_trayIcon is null) return;
        _trayIcon.BalloonTipTitle = title;
        _trayIcon.BalloonTipText = message;
        _trayIcon.BalloonTipIcon = ToolTipIcon.Info;
        _trayIcon.ShowBalloonTip(5000);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _coordinator?.Stop();
        _services?.Dispose();
        _taskbar?.DestroyHandle();
        _trayIcon?.Dispose();
        foreach (var icon in _progressIcons.Values) icon.Dispose();
        _progressIcons.Clear();
        base.OnExit(e);
    }

    private void InitializeTrayIcon()
    {
        _baseTrayIcon = LoadTrayIcon();
        _trayIcon = new NotifyIcon { Icon = _baseTrayIcon, Text = "Velocity", Visible = true };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Velocity", null, (_, _) => ShowMainWindow())!.Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Force quit Velocity", null, (_, _) => ForceQuit());
        _trayIcon.ContextMenuStrip = menu;
        // One click is enough, exactly like IDM: the window comes back on the first left click instead of
        // waiting for a double click. A second click of a double click simply re-activates the same window.
        _trayIcon.MouseClick += (_, click) =>
        {
            if (click.Button == MouseButtons.Left) ShowMainWindow();
        };
        _trayIcon.BalloonTipClicked += (_, _) => ShowMainWindow();

        // Explorer rebuilds the notification area when it restarts; without this the icon is dropped for good.
        _taskbar = new TaskbarWatcher(() =>
        {
            if (_trayIcon is null) return;
            _trayIcon.Visible = false;
            _trayIcon.Visible = true;
        });
    }

    /// <summary>
    /// The application's own icon, not a system placeholder: the one embedded in the executable when it can be
    /// read, otherwise the copy embedded in these assemblies, so the notification area always shows Velocity.
    /// </summary>
    private static WinIcon LoadTrayIcon()
    {
        if (Environment.ProcessPath is { Length: > 0 } executable)
        {
            try
            {
                if (WinIcon.ExtractAssociatedIcon(executable) is { } fromExecutable) return fromExecutable;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // A network path or a locked file: the embedded copy below is the fallback.
            }
        }

        try
        {
            if (GetResourceStream(new Uri("pack://application:,,,/icon.ico")) is { } resource)
                return new WinIcon(resource.Stream, new System.Drawing.Size(32, 32));
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException or UriFormatException)
        {
            // Never let the tray icon stop the application from starting.
        }

        return WinIcon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty) ?? System.Drawing.SystemIcons.Application;
    }

    /// <summary>
    /// Reflects the overall transfer state on the tray icon itself: a ring for the combined progress and a
    /// tooltip with the count, the speed and the percentage. Idle restores the plain brand icon.
    /// </summary>
    public void UpdateTrayStatus(int downloading, double percent, string detail)
    {
        if (_trayIcon is null || _baseTrayIcon is null) return;

        var text = downloading <= 0 || string.IsNullOrWhiteSpace(detail) ? "Velocity" : $"Velocity — {detail}";
        // NotifyIcon refuses a tooltip longer than 63 characters.
        _trayIcon.Text = text.Length <= 63 ? text : text[..62] + "…";

        // The icon is redrawn only when the rounded progress changes, so a one-second tick cannot churn handles.
        var bucket = downloading <= 0 ? 0 : Math.Clamp((int)Math.Round(percent / 5) * 5, 5, 100);
        if (bucket == _trayIconPercent) return;
        _trayIconPercent = bucket;
        _trayIcon.Icon = IconFor(bucket);
    }

    /// <summary>The brand icon with a progress ring, cached per five percent so each handle is created once.</summary>
    private WinIcon IconFor(int percent)
    {
        if (_baseTrayIcon is null || percent <= 0) return _baseTrayIcon ?? System.Drawing.SystemIcons.Application;
        if (_progressIcons.TryGetValue(percent, out var cached)) return cached;

        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.DrawIcon(_baseTrayIcon, new Rectangle(0, 0, 32, 32));
            using var track = new Pen(Color.FromArgb(150, 15, 19, 28), 3);
            using var fill = new Pen(Color.FromArgb(255, 91, 140, 255), 3);
            graphics.DrawEllipse(track, 2, 2, 28, 28);
            graphics.DrawArc(fill, 2, 2, 28, 28, -90, 3.6f * percent);
        }

        var handle = bitmap.GetHicon();
        var icon = (WinIcon)WinIcon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        _progressIcons[percent] = icon;
        return icon;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private void ShowMainWindow()
    {
        // Closing the library window during a download leaves the application running, and the tray icon must be
        // able to bring it back: a closed Window cannot be shown again, so it is created anew and fills itself
        // from the downloads the manager still owns.
        if (_mainWindowClosed || MainWindow is null)
        {
            _mainWindowClosed = false;
            MainWindow = CreateMainWindow();
            MainWindow.Show();
            MainWindow.Activate();
            return;
        }

        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    /// <summary>
    /// Hears the shell's "TaskbarCreated" broadcast. A NotifyIcon cannot re-register itself after Explorer
    /// restarts, so the icon has to be shown again from a window that is alive at that moment.
    /// </summary>
    private sealed class TaskbarWatcher : NativeWindow
    {
        private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
        private readonly Action _onTaskbarCreated;

        public TaskbarWatcher(Action onTaskbarCreated)
        {
            _onTaskbarCreated = onTaskbarCreated;
            CreateHandle(new CreateParams { Caption = "Velocity.TrayIconWatcher" });
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == TaskbarCreated) _onTaskbarCreated();
            base.WndProc(ref message);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string message);
    }
}
