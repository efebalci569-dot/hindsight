using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Hindsight.App.Controls;

/// <summary>true → Visible. ConverterParameter "invert" flips it. Also treats non-empty strings / non-null objects / non-zero numbers as true.</summary>
public sealed class VisibleWhen : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var truthy = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            double d => d > 0,
            _ => true,
        };
        if (parameter is "invert")
        {
            truthy = !truthy;
        }

        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>value == parameter (as string) → true; used for the sidebar radio buttons.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter! : Binding.DoNothing;
}

/// <summary>0..1 → a width in pixels relative to ConverterParameter (total width).</summary>
public sealed class FractionToWidth : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d && double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var total)
            ? Math.Clamp(d, 0, 1) * total
            : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InvertBool : IValueConverter
{
    public static readonly InvertBool Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// Binds a RadioButton to one value of an enum/int property: IsChecked = (value == parameter); checking it writes
/// the parameter back, converted to the property's type. Usage: IsChecked="{Binding Fps, Converter={StaticResource Is}, ConverterParameter=60}".
/// </summary>
public sealed class IsValue : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null)
        {
            return Binding.DoNothing;
        }

        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return type.IsEnum
            ? Enum.Parse(type, parameter.ToString()!, ignoreCase: true)
            : System.Convert.ChangeType(parameter, type, CultureInfo.InvariantCulture);
    }
}

/// <summary>Visible when value == parameter (used for settings sections).</summary>
public sealed class VisibleIfEquals : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
