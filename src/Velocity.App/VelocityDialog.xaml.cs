using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Velocity.App;

/// <summary>The visual weight of an in-app dialog. It drives the icon and its colours, nothing else.</summary>
public enum VelocityDialogKind
{
    Information,
    Warning,
    Danger
}

/// <summary>
/// The in-app replacement for <see cref="MessageBox"/>. A system message box ignores the Velocity theme, so it
/// looked like a foreign window; this one is built from the same brushes, type and buttons as every other
/// window, which also means it follows a live theme switch (every brush is a dynamic resource).
/// </summary>
public partial class VelocityDialog : Window
{
    private VelocityDialog(string heading, string message, string? detail, VelocityDialogKind kind, string okText, string? cancelText)
    {
        InitializeComponent();

        Title = heading;
        HeadingText.Text = heading;
        MessageText.Text = message;
        OkButton.Content = okText;

        if (cancelText is null)
        {
            CancelButton.Visibility = Visibility.Collapsed;
            OkButton.Margin = new Thickness(0);
        }
        else
        {
            CancelButton.Content = cancelText;
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            DetailText.Text = detail;
            DetailPanel.Visibility = Visibility.Visible;
        }

        var (background, foreground, icon) = kind switch
        {
            VelocityDialogKind.Warning => ("WarningSoftBrush", "WarningBrush", "IconAlert"),
            VelocityDialogKind.Danger => ("DangerSoftBrush", "DangerBrush", "IconAlert"),
            _ => ("AccentSoftBrush", "AccentBrush", "IconInfo")
        };

        IconTile.SetResourceReference(Border.BackgroundProperty, background);
        IconPath.SetResourceReference(Shape.StrokeProperty, foreground);
        if (TryFindResource(icon) is Geometry geometry) IconPath.Data = geometry;
    }

    /// <summary>
    /// Shows a themed dialog. Returns true for the primary action, false for Cancel or a closed window.
    /// </summary>
    public static bool Show(
        Window? owner,
        string heading,
        string message,
        VelocityDialogKind kind = VelocityDialogKind.Information,
        string okText = "OK",
        string? cancelText = null,
        string? detail = null,
        string? caption = null)
    {
        var dialog = new VelocityDialog(heading, message, detail, kind, okText, cancelText);
        if (!string.IsNullOrWhiteSpace(caption))
        {
            dialog.CaptionText.Text = caption;
            dialog.CaptionText.Visibility = Visibility.Visible;
        }

        if (owner is not null && owner.IsVisible)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true;
    }

    private void OkClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
