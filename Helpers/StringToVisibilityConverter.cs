using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SteamPluginManager.Helpers
{
    public class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool hasText = value is string str && !string.IsNullOrWhiteSpace(str);
            if (string.Equals(parameter?.ToString(), "Invert", StringComparison.OrdinalIgnoreCase))
            {
                hasText = !hasText;
            }

            return hasText ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}