using System;
using System.Globalization;
using System.Windows.Data;

namespace SampleApp;

/// <summary>A converter from the project. The DataTemplate preview cannot load it, so it leaves it out.</summary>
public class UpperCaseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString()?.ToUpperInvariant() ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => value;
}
