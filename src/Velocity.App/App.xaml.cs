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

        MainWindow = new MainWindow(
            _services.GetRequiredService<DownloadManager>(),
            settings,
            _services.GetRequiredService<SchedulerService>());
        MainWindow.Show();
        InitializeTrayIcon();
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
        menu.Items.Add("Open Velocity", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
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
        if (MainWindow is null) return;
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
