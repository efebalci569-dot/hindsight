using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Hindsight.App.ViewModels;

namespace Hindsight.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        StateChanged += (_, _) =>
        {
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        };
        SourceInitialized += (_, _) => ApplyWindowsChrome();
    }

    /// <summary>Rounded corners and a dark frame on Windows 11.</summary>
    private void ApplyWindowsChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        var dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
