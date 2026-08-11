using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SteamPluginManager
{
    public partial class App : Application
    {
        public static event EventHandler? LanguageChanged;
        private Mutex? _singleInstanceMutex;
        private Views.SplashWindow? _splashWindow;

        public static void RaiseLanguageChanged()
        {
            try
            {
                LanguageChanged?.Invoke(Application.Current, EventArgs.Empty);
            }
            catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _singleInstanceMutex = new Mutex(true, "Local\\HZLuaManagerSingleInstance", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("HZ Lua Manager is already running.", "Already Running", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            try
            {
                ApplySavedTheme();
                ApplySavedLanguage();
            }
            catch (Exception ex)
            {
                try { Logger.Log($"App startup failed to apply resources: {ex.Message}"); } catch { }
            }

            _splashWindow = new Views.SplashWindow();
            _splashWindow.Show();

            var startupLicenseCheckTask = Views.DashboardView.CheckDeviceTokenAsync();

            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await Task.WhenAll(Task.Delay(1200), startupLicenseCheckTask);

                    bool isLicenseVerified = await startupLicenseCheckTask;
                    var shell = new Views.MainShell(isLicenseVerified);
                    shell.Show();
                    MainWindow = shell;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (isLicenseVerified)
                            {
                                await UnlockerRegistryHelper.EnsureOpenSteamToolInstalledAsync();
                            }
                        }
                        catch (Exception ex)
                        {
                            try { Logger.Log($"App startup failed to ensure unlocker install: {ex.Message}"); } catch { }
                        }
                    });

                    if (_splashWindow != null)
                    {
                        await _splashWindow.FadeOutAsync();
                        _splashWindow.Close();
                        _splashWindow = null;
                    }
                }
                catch (Exception ex)
                {
                    try { Logger.Log($"App startup failed to open main shell: {ex.Message}"); } catch { }
                    _splashWindow?.Close();
                    _splashWindow = null;
                    Shutdown();
                }
            }));
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
                _singleInstanceMutex?.Dispose();
            }
            catch { }

            base.OnExit(e);
        }

        private void ApplySavedTheme()
        {
            try
            {
                var saved = ThemePreferences.GetSavedThemePreference();
                bool useLight = saved ?? false;
                var app = Application.Current;
                ResourceDictionary? existing = null;
                foreach (var dict in app.Resources.MergedDictionaries)
                {
                    if (dict.Source?.OriginalString?.Contains("Themes/") == true)
                    {
                        existing = dict;
                        break;
                    }
                }
                if (existing != null) app.Resources.MergedDictionaries.Remove(existing);

                string themeFile = useLight ? "Themes/Light.xaml" : "Themes/Dark.xaml";
                var themeDict = new ResourceDictionary { Source = new Uri(themeFile, UriKind.Relative) };
                app.Resources.MergedDictionaries.Add(themeDict);
            }
            catch (Exception ex)
            {
                try { Logger.Log($"Failed to apply saved theme: {ex.Message}"); } catch { }
            }
        }

        private void ApplySavedLanguage()
        {
            try
            {
                string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string appFolder = Path.Combine(appDataPath, "SteamPluginManager");
                string configFile = Path.Combine(appFolder, "language.txt");

                string lang = "en";
                if (File.Exists(configFile))
                {
                    var txt = File.ReadAllText(configFile).Trim();
                    if (txt == "zh") lang = "zh";
                }

                var app = Application.Current;
                ResourceDictionary? existing = null;
                foreach (var dict in app.Resources.MergedDictionaries)
                {
                    if (dict.Source?.OriginalString?.StartsWith("Strings/") == true)
                    {
                        existing = dict; break;
                    }
                }
                if (existing != null) app.Resources.MergedDictionaries.Remove(existing);

                string langFile = lang == "zh" ? "Strings/zh.xaml" : "Strings/en.xaml";
                var langDict = new ResourceDictionary { Source = new Uri(langFile, UriKind.Relative) };
                app.Resources.MergedDictionaries.Add(langDict);
            }
            catch (Exception ex)
            {
                try { Logger.Log($"Failed to apply saved language: {ex.Message}"); } catch { }
            }
        }
    }
}

