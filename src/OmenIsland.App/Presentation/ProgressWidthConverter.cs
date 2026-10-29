using System.Globalization;
using System.Windows.Data;

namespace OmenIsland.App.Presentation;

public sealed class ProgressWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 ||
            values[0] is not double width ||
            values[1] is not double progress ||
            double.IsNaN(width) ||
            width <= 0)
        {
            return 0d;
        }

        return Math.Clamp(progress, 0, 1) * width;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
