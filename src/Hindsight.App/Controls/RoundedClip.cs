using System.Windows;
using System.Windows.Media;

namespace Hindsight.App.Controls;

/// <summary>
/// Clips an element to a rounded rectangle (WPF's ClipToBounds is square, so overflowing artwork would
/// poke out of rounded cards). Usage: c:RoundedClip.Radius="22".
/// </summary>
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0.0, OnRadiusChanged));

    public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);

    public static void SetRadius(DependencyObject d, double value) => d.SetValue(RadiusProperty, value);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.SizeChanged -= Update;
        element.SizeChanged += Update;
        Apply(element);
    }

    private static void Update(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement element)
    {
        var radius = GetRadius(element);
        var clip = new RectangleGeometry(new Rect(element.RenderSize), radius, radius);
        clip.Freeze();
        element.Clip = clip;
    }
}
