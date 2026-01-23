using Microsoft.Win32;
using System;
using System.Windows;

namespace SteamPluginManager
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                // Inisialisasi cache (akan dibuat di folder aplikasi)
                CacheManager.Initialize();
                ApplyTheme();
                this.DispatcherUnhandledException += (s, ev) =>
                {
                    try
                    {
                        SteamPluginManager.Logger.Log($"Unhandled exception: {ev.Exception}");
                    }
                    catch { }
                    MessageBox.Show($"Unhandled error: {ev.Exception}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    ev.Handled = true;
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize application.\n{ex}", "Initialization Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, ev) =>
            {
                if (ev.Category == Microsoft.Win32.UserPreferenceCategory.General)
                {
                    try
                    {
                        ApplyTheme();
                    }
                    catch { /* biarkan saja, jangan crash */ }
                }
            };

            // Window utama akan dibuat secara otomatis oleh WPF
        }

        private void ApplyTheme()
        {
            bool isLight = true;
            
            // Coba ambil preferensi tema yang tersimpan dulu
            var savedTheme = ThemePreferences.GetSavedThemePreference();
            if (savedTheme.HasValue)
            {
                isLight = savedTheme.Value;
            }
            else
            {
                // Jika tidak ada preferensi tersimpan, gunakan tema sistem Windows
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(
                        "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
                    if (key != null)
                    {
                        object value = key.GetValue("AppsUseLightTheme");
                        if (value is int intValue)
                            isLight = intValue > 0;
                    }
                }
                catch { /* kalau gagal baca registry, pakai default Light */ }
            }

            var dicts = Resources.MergedDictionaries;
            try
            {
                string themeFile = isLight ? "Themes/Light.xaml" : "Themes/Dark.xaml";
                var themeDict = new ResourceDictionary { Source = new Uri(themeFile, UriKind.Relative) };

                if (dicts.Count > 0)
                {
                    if (dicts[0].Source != null && dicts[0].Source.OriginalString.StartsWith("Themes/"))
                    {
                        dicts[0] = themeDict;
                    }
                    else
                    {
                        dicts.Insert(0, themeDict);
                    }
                }
                else
                {
                    dicts.Add(themeDict);
                }

                // Ensure strings dictionary exists at index 1 (default to English at app start)
                if (dicts.Count < 2 || dicts[1].Source == null || !dicts[1].Source.OriginalString.StartsWith("Strings/"))
                {
                    dicts.Insert(dicts.Count >= 1 ? 1 : dicts.Count, new ResourceDictionary { Source = new Uri("Strings/en.xaml", UriKind.Relative) });
                }
            }
            catch
            {
                // fallback kalau file theme tidak ada
                dicts.Clear();
                dicts.Add(new ResourceDictionary
                {
                    ["BackgroundBrush"] = SystemColors.WindowBrush,
                    ["ForegroundBrush"] = SystemColors.ControlTextBrush
                });
            }
        }
    }
}