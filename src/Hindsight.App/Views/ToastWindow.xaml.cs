using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Hindsight.Core;

namespace Hindsight.App.Views;

/// <summary>
/// A small "clip saved" card in the bottom-right corner. It never takes focus and lets clicks pass
/// through, so it cannot interrupt a game. One instance is reused for every notice.
/// </summary>
public partial class ToastWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(3.2) };

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
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        _hide.Stop();
        _hide.Start();
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 8;
        Top = area.Bottom - ActualHeight - 8;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
