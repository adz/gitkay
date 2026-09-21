using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace GitKay.UI;

public class ObjectConverters {
    public static readonly IValueConverter IsNotNull = new FuncValueConverter<object?, bool>(x => x != null);
}

/// <summary>The triangle beside a folder: pointing down when it is open, right when it is folded away.</summary>
public sealed class FolderGlyphConverter : IValueConverter {
    public static readonly FolderGlyphConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "▾" : "▸";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public class LaneMarginConverter : IValueConverter {
    public static readonly LaneMarginConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
        if (value is int lane) {
            return new Thickness(lane * 15, 0, 0, 0);
        }
        return new Thickness(0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
