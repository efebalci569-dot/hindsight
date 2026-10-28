using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Hindsight.App.ViewModels;

namespace Hindsight.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly Storyboard _pulse;
    private bool _pulsing;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _pulse = (Storyboard)Resources["Pulse"];
        vm.PropertyChanged += OnVmChanged;
        StateChanged += (_, _) =>
        {
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        };
        SourceInitialized += (_, _) => ApplyWindowsChrome();
        Loaded += (_, _) => UpdateIndicator();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsBuffering) or nameof(MainViewModel.IsManualRecording) or nameof(MainViewModel.HasError) or nameof(MainViewModel.BufferFill))
        {
            UpdateIndicator();
        }
    }

    private void UpdateIndicator()
    {
        var live = _vm.IsBuffering || _vm.IsManualRecording;
        StatusDot.Fill = (Brush)FindResource(_vm.HasError ? "ErrorBrush" : live ? "AccentBrush" : "DimBrush");
        FillBar.Width = 186 * (_vm.IsManualRecording ? 1 : _vm.BufferFill);
        if (live && !_pulsing)
        {
            _pulse.Begin(this, true);
            _pulsing = true;
        }
        else if (!live && _pulsing)
        {
            _pulse.Stop(this);
            PulseRing.Opacity = 0;
            _pulsing = false;
        }
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

    protected override void OnClosed(EventArgs e)
    {
        _vm.PropertyChanged -= OnVmChanged;
        _pulse.Stop(this);
        base.OnClosed(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
