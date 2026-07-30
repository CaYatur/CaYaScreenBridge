using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CaYaScreenBridge.Windows.Ui;

/// <summary>Green when the engine is healthy, amber when it is standing down.</summary>
public sealed class HealthBrushConverter : IValueConverter
{
    private static readonly Brush Healthy = Create(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush Idle = Create(Color.FromRgb(0xF5, 0x9E, 0x0B));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Healthy : Idle;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static Brush Create(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

public sealed class OnOffConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Loc.Get("common.on") : Loc.Get("common.off");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Bridges an enum property to a <c>SelectedIndex</c>, keeping the XAML free of converters per enum.</summary>
public sealed class EnumIndexConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? 0 : System.Convert.ToInt32(value, CultureInfo.InvariantCulture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int index = value is null ? 0 : System.Convert.ToInt32(value, CultureInfo.InvariantCulture);

        Type enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (!enumType.IsEnum)
        {
            return index;
        }

        Array values = Enum.GetValues(enumType);
        return index >= 0 && index < values.Length ? values.GetValue(index)! : values.GetValue(0)!;
    }
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
