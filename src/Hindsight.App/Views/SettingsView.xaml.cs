using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace Hindsight.App.Views;

public partial class SettingsView : UserControl
{
    public const string WebsiteUrl = "https://hindsight-clips.vercel.app";
    public const string RepoUrl = "https://github.com/efebalci569-dot/hindsight";

    public SettingsView()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Hindsight {version?.ToString(3)} · .NET {Environment.Version.ToString(2)} · MIT lisansı";
    }

    private void OnWebsite(object sender, RoutedEventArgs e) => Open(WebsiteUrl);

    private void OnGitHub(object sender, RoutedEventArgs e) => Open(RepoUrl);

    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
}
