using System.Globalization;
using System.Windows.Data;

namespace OmenIsland.App.Presentation;

/// <summary>True when the bound value's text matches ConverterParameter; used by segmented buttons.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
