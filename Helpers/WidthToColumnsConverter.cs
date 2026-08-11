using System;
using System.Globalization;
using System.Windows.Data;

namespace SteamPluginManager.Helpers
{
    public class WidthToColumnsConverter : IValueConverter
    {
        // ConverterParameter should be the desired minimum column width (double)
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double actualWidth)
            {
                double minColWidth = 300.0;
                if (parameter != null && double.TryParse(parameter.ToString(), out var p))
                    minColWidth = p;

                if (minColWidth <= 0)
                    minColWidth = 300.0;

                int cols = Math.Max(1, (int)(actualWidth / minColWidth));
                return cols;
            }
            return 1;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
