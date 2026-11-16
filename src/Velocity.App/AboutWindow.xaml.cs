using System.Reflection;
using System.Windows;

namespace Velocity.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        VersionText.Text = version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
