using System.Windows;
using System.Windows.Media;

namespace Hindsight.App.Controls;

/// <summary>
/// The replay buffer drawn as a ring of 2-second segments (the same picture as on the website):
/// filled segments are footage that a clip would contain, the white dot is where ffmpeg is writing now.
/// Rendered once per change (about once a second), no animation timers.
/// </summary>
public sealed class BufferRing : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = Register(nameof(Segments), 15);
    public static readonly DependencyProperty FillProperty = Register(nameof(Fill), 0.0);
    public static readonly DependencyProperty HeadProperty = Register(nameof(Head), 0);
    public static readonly DependencyProperty IsLiveProperty = Register(nameof(IsLive), false);
    public static readonly DependencyProperty StrokeWidthProperty = Register(nameof(StrokeWidth), 10.0);

    private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x30)));
    private static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0x10, 0x10, 0x16)));

    public int Segments { get => (int)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }

    /// <summary>0..1 share of the ring that holds footage.</summary>
    public double Fill { get => (double)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    /// <summary>Index of the segment being written.</summary>
    public int Head { get => (int)GetValue(HeadProperty); set => SetValue(HeadProperty, value); }

    public bool IsLive { get => (bool)GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }

    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(BufferRing), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var count = Math.Max(3, Segments);
        var stroke = StrokeWidth;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var outer = (size / 2) - 1;
        var inner = outer - stroke;
        var gap = Math.Min(4.0, 360.0 / count * 0.18);

        // Accent gradient laid over the whole ring, so each segment picks up its own part of it.
        var accent = new LinearGradientBrush(Color.FromRgb(0xFF, 0x9F, 0x43), Color.FromRgb(0xFF, 0x4D, 0x5E),
            new Point(center.X - outer, center.Y - outer), new Point(center.X + outer, center.Y + outer))
        { MappingMode = BrushMappingMode.Absolute };
        accent.Freeze();

        dc.DrawEllipse(null, new Pen(TrackBrush, stroke + 6), center, outer - (stroke / 2), outer - (stroke / 2));

        var filled = IsLive ? (int)Math.Round(Math.Clamp(Fill, 0, 1) * count) : 0;
        var head = ((Head % count) + count) % count;
        for (var i = 0; i < count; i++)
        {
            var start = (360.0 / count * i) + (gap / 2);
            var end = (360.0 / count * (i + 1)) - (gap / 2);
            var geometry = Segment(center, outer, inner, start, end);
            var age = (head - i + count) % count; // 0 = being written now
            if (age < Math.Max(1, filled) && IsLive)
            {
                dc.PushOpacity(age == 0 ? 1 : Math.Max(0.28, 0.95 - (age * 0.045)));
                dc.DrawGeometry(accent, null, geometry);
                dc.Pop();
            }
            else
            {
                dc.DrawGeometry(DimBrush, null, geometry);
            }
        }

        if (IsLive)
        {
            var angle = (360.0 / count * (head + 0.5)) * Math.PI / 180;
            var mid = outer - (stroke / 2);
            var dot = new Point(center.X + (mid * Math.Sin(angle)), center.Y - (mid * Math.Cos(angle)));
            dc.DrawEllipse(Brushes.White, null, dot, stroke * 0.32, stroke * 0.32);
        }
    }

    private static StreamGeometry Segment(Point c, double outer, double inner, double startDeg, double endDeg)
    {
        Point At(double r, double deg)
        {
            var a = deg * Math.PI / 180;
            return new Point(c.X + (r * Math.Sin(a)), c.Y - (r * Math.Cos(a)));
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(At(outer, startDeg), true, true);
            ctx.ArcTo(At(outer, endDeg), new Size(outer, outer), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(At(inner, endDeg), true, true);
            ctx.ArcTo(At(inner, startDeg), new Size(inner, inner), 0, false, SweepDirection.Counterclockwise, true, true);
        }

        geometry.Freeze();
        return geometry;
    }
}
