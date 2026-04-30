using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace GitKay.UI;

public class ObjectConverters
{
    public static readonly IValueConverter IsNotNull = new FuncValueConverter<object?, bool>(x => x != null);
}

public class LaneMarginConverter : IValueConverter
{
    public static readonly LaneMarginConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int lane)
        {
            return new Thickness(lane * 15, 0, 0, 0);
        }
        return new Thickness(0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class LaneConverter : IValueConverter
{
    public static readonly LaneConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return null; // Just a placeholder
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
