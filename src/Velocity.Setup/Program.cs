using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Velocity.Setup;

/// <summary>
/// Velocity's per-user installer.
///
/// A double-click extracts the published application to <c>%LOCALAPPDATA%\Programs\Velocity</c>, creates the Start
/// Menu and desktop shortcuts, and registers it in "Apps &amp; features" so it can be removed like any other
/// desktop program. It never needs administrator rights and never writes to a system-wide location.
///
///     Velocity-Setup.exe                 install, then launch Velocity
///     Velocity-Setup.exe /S              install silently, no launch
///     Velocity-Setup.exe /desktop=false  install without a desktop shortcut
///     Velocity-Setup.exe /uninstall      remove the program (downloads, history and settings are kept)
/// </summary>
internal static class Program
{
    private const string AppName = "Velocity";
    private const string Version = "1.0.0";
    private const string Publisher = "Velocity Project";
    private const string Executable = "Velocity.App.exe";
    private const string PayloadName = "Velocity.Setup.payload.zip";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Velocity";

    [STAThread]
    private static int Main(string[] args)
    {
        var silent = args.Any(value => value.Equals("/S", StringComparison.OrdinalIgnoreCase) || value.Equals("/silent", StringComparison.OrdinalIgnoreCase));
        var uninstall = args.Any(value => value.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));
        var desktop = !args.Any(value => value.Equals("/desktop=false", StringComparison.OrdinalIgnoreCase));

        try
        {
            return uninstall ? Remove(silent) : Install(silent, desktop);
        }
        catch (Exception exception)
        {
            Report(silent, $"Velocity could not be {(uninstall ? "removed" : "installed")}.\r\n\r\n{exception.Message}", "Velocity");
            return 1;
        }
    }

    private static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    private static int Install(bool silent, bool desktop)
    {
        var target = InstallDirectory;
        // Velocity starts with Windows, so it is usually running when someone repairs or updates it; a running
        // executable cannot be replaced, which used to fail the whole installation halfway through.
        CloseRunningApp();
        Directory.CreateDirectory(target);

        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadName)
                     ?? throw new InvalidOperationException("The installation payload is missing. Build the installer with artifacts/build-installer.ps1.");
        using (stream)
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            foreach (var entry in archive.Entries)
            {
                var destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
                if (!destination.StartsWith(target, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The installation payload contains an invalid path.");

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        entry.ExtractToFile(destination, overwrite: true);
                        break;
                    }
                    catch (Exception exception) when (exception is IOException && attempt < 10)
                    {
                        // A process that was just closed can hold its file for another moment.
                        Thread.Sleep(300);
                    }
                }
            }
        }

        var executable = Path.Combine(target, Executable);
        if (!File.Exists(executable)) throw new InvalidOperationException("The installed files do not contain Velocity.App.exe.");

        // Start Menu always, desktop unless it was declined.
        CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), $"{AppName}.lnk"), executable);
        if (desktop) CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk"), executable);

        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            if (key is not null)
            {
                key.SetValue("DisplayName", $"{AppName} {Version}");
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", Publisher);
                key.SetValue("InstallLocation", target);
                key.SetValue("DisplayIcon", executable);
                key.SetValue("UninstallString", $"\"{Environment.ProcessPath}\" /uninstall");
                key.SetValue("QuietUninstallString", $"\"{Environment.ProcessPath}\" /uninstall /S");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", EstimatedSizeMegabytes(target), RegistryValueKind.DWord);
            }
        }

        if (!silent)
        {
            try
            {
                Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = target });
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Installed anyway; the Start Menu entry starts it.
            }

            Report(silent: false,
                $"{AppName} {Version} is installed.\r\n\r\n{target}\r\n\r\nStart it from the Start Menu, or let the browser extension send downloads to it.",
                $"{AppName} Setup");
        }

        return 0;
    }

    private static void Trace(string message)
    {
        // A silent uninstall has no window, so it leaves a small trace to make a failure diagnosable.
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "velocity-setup.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void CloseRunningApp()
    {
        foreach (var process in Process.GetProcessesByName("Velocity.App"))
        {
            try
            {
                Trace($"closing running app (pid {process.Id})");
                process.CloseMainWindow();
                if (!process.WaitForExit(3000)) process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // An already-exited process needs nothing.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static int Remove(bool silent)
    {
        Trace($"uninstall start (pid {Environment.ProcessId})");
        CloseRunningApp();

        Trace("processes handled");
        foreach (var shortcut in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), $"{AppName}.lnk"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk")
                 })
        {
            try { if (File.Exists(shortcut)) File.Delete(shortcut); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        Trace("shortcuts handled");

        var target = InstallDirectory;
        for (var attempt = 0; Directory.Exists(target) && attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A just-closed executable can hold its own file for a moment; retry below.
                Thread.Sleep(300);
            }
        }

        Trace("folder handled");
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException) { }

        Trace("uninstall key handled");

        // The start-with-Windows entry points at the program that was just removed.
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run?.GetValue("Velocity") is not null) run.DeleteValue("Velocity", throwOnMissingValue: false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException) { }

        // The download history, settings and finished files are the user's and are deliberately kept.
        Report(silent, $"{AppName} has been removed.\r\n\r\nYour downloads, history and settings in %LOCALAPPDATA%\\Velocity were kept.",
            $"{AppName} Setup");
        return 0;
    }

    private static void CreateShortcut(string path, string executable)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null) return;
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = executable;
        link.WorkingDirectory = Path.GetDirectoryName(executable);
        link.IconLocation = executable;
        link.Description = "Velocity download manager";
        link.Save();
    }

    private static int EstimatedSizeMegabytes(string directory)
    {
        try
        {
            var bytes = new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
            return (int)Math.Clamp(bytes / (1024 * 1024), 0, int.MaxValue);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void Report(bool silent, string message, string caption)
    {
        if (silent) return;
        MessageBox(IntPtr.Zero, message, caption, 0x40 /* MB_ICONINFORMATION */);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
