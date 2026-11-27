using System.Globalization;
using System.Windows.Data;

namespace VolumeMixer.Converters;

public sealed class SliderFillWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4
            || values[0] is not double value
            || values[1] is not double min
            || values[2] is not double max
            || values[3] is not double totalWidth)
        {
            return 0.0;
        }

        if (max <= min || totalWidth <= 0) return 0.0;

        double ratio = Math.Clamp((value - min) / (max - min), 0.0, 1.0);
        return ratio * totalWidth;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
