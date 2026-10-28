using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Hindsight.Core;

namespace Hindsight.App.Views;

/// <summary>
/// A small "clip saved" card in the bottom-right corner of the screen the game is on. It never takes focus
/// and lets clicks pass through, so it cannot interrupt a game. One instance is reused for every notice.
/// (Games in exclusive full screen hide every other window; the confirmation sound covers that case.)
/// </summary>
public partial class ToastWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;
    private static readonly TimeSpan Visible = TimeSpan.FromSeconds(3.4);
    private readonly DispatcherTimer _hide = new() { Interval = Visible };

    public ToastWindow()
    {
        InitializeComponent();
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExTransparent | WsExToolWindow | WsExNoActivate);
        };
        SizeChanged += (_, _) => Place();
    }

    public void Show(Notice notice)
    {
        TitleText.Text = notice.Title;
        MessageText.Text = notice.Message;
        MessageText.Visibility = string.IsNullOrWhiteSpace(notice.Message) ? Visibility.Collapsed : Visibility.Visible;
        (IconGlyph.Text, IconCircle.Background) = notice.Kind switch
        {
            NoticeKind.Success => ("", (Brush)FindResource("AccentGradient")),
            NoticeKind.Warning => ("", new SolidColorBrush(Color.FromRgb(0xB4, 0x83, 0x14))),
            NoticeKind.Error => ("", new SolidColorBrush(Color.FromRgb(0xB9, 0x3B, 0x3B))),
            _ => ("", new SolidColorBrush(Color.FromRgb(0x2F, 0x5E, 0xA8))),
        };

        if (!IsVisible)
        {
            base.Show();
        }

        Place();
        BringToTop();
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        Progress.BeginAnimation(WidthProperty, new DoubleAnimation(Math.Max(0, ActualWidth - 56), 0, Visible));
        _hide.Stop();
        _hide.Start();
    }

    /// <summary>Bottom-right of the work area of the monitor the foreground window (the game) is on.</summary>
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        var monitor = MonitorFromWindow(GetForegroundWindow(), 2 /* nearest */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) && PresentationSource.FromVisual(this) is { CompositionTarget: { } target })
        {
            var toDip = target.TransformFromDevice;
            var topLeft = toDip.Transform(new Point(info.Work.Left, info.Work.Top));
            var bottomRight = toDip.Transform(new Point(info.Work.Right, info.Work.Bottom));
            area = new Rect(topLeft, bottomRight);
        }

        Left = area.Right - ActualWidth - 6;
        Top = area.Bottom - ActualHeight - 6;
    }

    /// <summary>Re-asserts "topmost": borderless full-screen games put themselves above earlier topmost windows.</summary>
    private void BringToTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040); // NOSIZE|NOMOVE|NOACTIVATE|SHOWWINDOW
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
