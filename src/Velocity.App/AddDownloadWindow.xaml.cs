using System.Windows;
using System.Windows.Controls;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using Velocity.Core.Models;

namespace Velocity.App;

public partial class AddDownloadWindow : Window
{
    /// <summary>Every link the user entered, in the order they appear.</summary>
    public List<DownloadRequest> Requests { get; } = [];

    public AddDownloadWindow(string defaultFolder)
    {
        InitializeComponent();

        // Compact by design, but never taller than the screen: past that the content inside scrolls instead
        // of pushing the Start button below the taskbar.
        MaxHeight = System.Windows.Forms.Screen.PrimaryScreen is { } screen
            ? screen.WorkingArea.Height * 0.8
            : SystemParameters.WorkArea.Height * 0.8;

        FolderTextBox.Text = defaultFolder;
        UrlTextBox.Focus();
    }

    private void StartClick(object sender, RoutedEventArgs e)
    {
        var folder = FolderTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            ShowError("Choose a destination folder.", FolderTextBox);
            return;
        }

        var urls = new List<Uri>();
        foreach (var line in Lines(UrlTextBox.Text))
        {
            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                ShowError($"'{line}' is not a valid HTTP or HTTPS link.", UrlTextBox);
                return;
            }
            // Pasting the same list twice must not queue the same file twice.
            if (!urls.Any(existing => existing == uri)) urls.Add(uri);
        }

        if (urls.Count == 0)
        {
            ShowError("Paste at least one link.", UrlTextBox);
            return;
        }

        var mirrors = new List<string>();
        foreach (var line in Lines(MirrorsTextBox.Text))
        {
            if (!Uri.TryCreate(line, UriKind.Absolute, out var mirror) || mirror.Scheme is not ("http" or "https"))
            {
                ShowError($"'{line}' is not a valid mirror link.", MirrorsTextBox);
                return;
            }
            var value = mirror.ToString();
            if (!mirrors.Contains(value) && !urls.Any(url => url.ToString() == value)) mirrors.Add(value);
        }

        if (mirrors.Count > 0 && urls.Count > 1)
        {
            ShowError("Mirrors belong to one file. Enter them with a single link.", MirrorsTextBox);
            return;
        }

        // Referer, cookies and arbitrary headers travel with the request itself, so a protected link keeps
        // working through a resume instead of failing once the first response is gone.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var referer = RefererTextBox.Text.Trim();
        if (referer.Length > 0)
        {
            if (!Uri.TryCreate(referer, UriKind.Absolute, out _))
            {
                ShowError("The referer must be a full address, like https://example.com/watch.", RefererTextBox);
                return;
            }
            headers["Referer"] = referer;
        }

        var cookies = CookiesTextBox.Text.Trim();
        if (cookies.Length > 0) headers["Cookie"] = cookies;

        // Headers are split on newlines only: a header value is allowed to contain spaces.
        foreach (var line in HeaderLines(HeadersTextBox.Text))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0 || separator == line.Length - 1)
            {
                ShowError($"'{line}' is not a header. Write it as Name: value.", HeadersTextBox);
                return;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (name.Length == 0 || value.Length == 0)
            {
                ShowError($"'{line}' is not a header. Write it as Name: value.", HeadersTextBox);
                return;
            }

            headers[name] = value;
        }

        var fileName = string.IsNullOrWhiteSpace(FileNameTextBox.Text) ? null : FileNameTextBox.Text.Trim();
        Requests.Clear();
        foreach (var uri in urls)
        {
            Requests.Add(new DownloadRequest(
                uri.ToString(),
                folder,
                // One name cannot describe several files, so it is only applied when there is one link.
                urls.Count == 1 ? fileName : null,
                Mirrors: mirrors.Count > 0 ? mirrors : null,
                Headers: headers.Count > 0 ? headers : null));
        }

        DialogResult = true;
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> HeaderLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = FolderTextBox.Text };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            FolderTextBox.Text = dialog.SelectedPath;
            ErrorPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowError(string message, TextBox focus)
    {
        ErrorTextBlock.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        focus.Focus();
        focus.SelectAll();
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
