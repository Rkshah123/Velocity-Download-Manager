using System.Windows;
using System.Windows.Controls;

namespace Velocity.App;

public partial class DeleteConfirmWindow : Window
{
    public DeleteConfirmWindow(string message, bool deleteFileDefault = true, bool isActive = false, string? detail = null)
    {
        InitializeComponent();
        MessageText.Text = message;
        DeleteFileBox.IsChecked = deleteFileDefault;

        if (isActive)
        {
            HeadingText.Text = "Download still running";
            CaptionText.Text = "This transfer is active right now. Confirm only if you want it to stop.";
            IconTile.SetResourceReference(Border.BackgroundProperty, "WarningSoftBrush");
            IconPath.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "WarningBrush");
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            DetailText.Text = detail;
            DetailPanel.Visibility = Visibility.Visible;
        }
    }

    public bool DeleteFile => DeleteFileBox.IsChecked == true;

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void DeleteClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
