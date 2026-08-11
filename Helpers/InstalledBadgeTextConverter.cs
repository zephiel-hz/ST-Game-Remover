using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;

namespace SteamPluginManager.Helpers
{
    public class InstalledBadgeTextConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (values.Length < 2) return string.Empty;

                bool isInstalled = values[0] is bool installed && installed;
                string? installationPath = values[1] as string;

                if (!isInstalled)
                    return string.Empty;

                var installedLabel = Application.Current?.TryFindResource("GameLibrary.Installed") as string ?? "Installed";
                var driveLetter = GetDriveLetter(installationPath);

                return string.IsNullOrWhiteSpace(driveLetter)
                    ? installedLabel
                    : $"{installedLabel} {driveLetter}";
            }
            catch
            {
                return Application.Current?.TryFindResource("GameLibrary.Installed") as string ?? "Installed";
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }

        private static string GetDriveLetter(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                var driveInfo = new DriveInfo(path);
                return driveInfo.Name.TrimEnd('\\');
            }
            catch
            {
                if (path.Length >= 2 && path[1] == ':')
                    return path.Substring(0, 2).ToUpperInvariant();
                return string.Empty;
            }
        }
    }
}
