using System;
using System.Windows;
using SteamPluginManager.Views;

namespace SteamPluginManager
{
    public static class WindowNavigator
    {
        // When navigation requires token verification, this property holds the target view identifier.
        public static string? NextViewAfterVerify { get; set; }

        public static string CurrentHZManifestDetailSource { get; private set; } = "HZManifest";

        public static void NavigateToOnlineFix()
        {
            if (_mainShellInstance != null)
            {
                _mainShellInstance.NavigateToOnlineFix();
            }
        }
        private static MainWindow? _mainWindowInstance;
        private static Dashboard? _dashboardInstance;
        private static MainShell? _mainShellInstance;

        // New single-window navigation methods
        public static void RegisterMainShell(MainShell shell)
        {
            _mainShellInstance = shell;
        }

        public static MainShell? GetMainShell()
        {
            return _mainShellInstance;
        }

        public static void NavigateToDashboard()
        {
            _mainShellInstance?.NavigateToDashboard();
        }

        public static void NavigateToGameLibrary()
        {
            _mainShellInstance?.NavigateToGameLibrary();
            
            // Update MainWindow title
            if (_mainWindowInstance != null)
            {
                _mainWindowInstance.UpdateWindowTitle("Game Library");
            }
        }

        public static void NavigateToSettings()
        {
            _mainShellInstance?.NavigateToSettings();
        }

        public static void NavigateToSaweria()
        {
            _mainShellInstance?.NavigateToSaweria();
        }

        public static void NavigateToHZManifest()
        {
            NavigateToHZManifest(forceRefresh: true);
        }

        public static void NavigateToHZManifest(bool forceRefresh)
        {
            if (_mainShellInstance != null)
            {
                _mainShellInstance.NavigateToHZManifest(forceRefresh);
            }
        }

        public static void NavigateToHZManifestFromDetail()
        {
            NavigateToHZManifest(forceRefresh: false);
        }

        public static void NavigateToVerifyToken()
        {
            if (_mainShellInstance != null)
            {
                _mainShellInstance.TransitionToView(new VerifyTokenView());
            }
        }

        public static void NavigateToHZManifestDetail(ManifestFile file, string source = "HZManifest")
        {
            CurrentHZManifestDetailSource = source;

            if (_mainShellInstance != null)
            {
                var detailView = new HZManifestDetailView();
                detailView.SetFile(file);
                _mainShellInstance.TransitionToView(detailView);
            }
        }

        public static void NavigateBackFromHZManifestDetail()
        {
            if (string.Equals(CurrentHZManifestDetailSource, "Dashboard", StringComparison.OrdinalIgnoreCase))
            {
                NavigateToDashboard();
                return;
            }

            NavigateToHZManifest(forceRefresh: false);
        }

        public static void NavigateToGameBypass()
        {
            if (_mainShellInstance != null)
            {
                _mainShellInstance.NavigateToGameBypass();
            }
        }

        public static void RestartSteam()
        {
            _mainShellInstance?.RestartSteam();
        }

        public static void CheckForUpdates()
        {
            _mainShellInstance?.CheckForUpdates();
        }

        public static void NavigateToAbout()
        {
            _mainShellInstance?.ShowAboutDialog();
        }

        public static void NavigateToGameBypassDetail(BypassFile file)
        {
            if (_mainShellInstance != null)
            {
                var detailView = new GameBypassDetailView();
                detailView.SetFile(file);
                _mainShellInstance.TransitionToView(detailView);
            }
        }

        public static void NavigateToOnlineFixDetail(OnlineFixFile file)
        {
            if (_mainShellInstance != null)
            {
                var detailView = new OnlineFixDetailView();
                detailView.SetFile(file);
                _mainShellInstance.TransitionToView(detailView);
            }
        }

        /// <summary>
        /// Buka Dashboard dan tutup MainWindow jika ada (Legacy support)
        /// </summary>
        public static void OpenDashboard()
        {
            if (_mainShellInstance != null)
            {
                NavigateToDashboard();
                return;
            }

            if (_dashboardInstance == null || !_dashboardInstance.IsVisible)
            {
                _dashboardInstance = new Dashboard();
                _dashboardInstance.Show();
            }
            else
            {
                _dashboardInstance.Activate();
                _dashboardInstance.Focus();
            }

            // Tutup MainWindow jika ada
            if (_mainWindowInstance != null && _mainWindowInstance.IsVisible)
            {
                _mainWindowInstance.Hide();
            }
        }

        /// <summary>
        /// Buka MainWindow dan tutup Dashboard jika ada (Legacy support)
        /// </summary>
        public static void OpenMainWindow()
        {
            if (_mainShellInstance != null)
            {
                NavigateToGameLibrary();
                return;
            }

            if (_mainWindowInstance == null || !_mainWindowInstance.IsVisible)
            {
                _mainWindowInstance = new MainWindow();
                _mainWindowInstance.Show();
            }
            else
            {
                _mainWindowInstance.Activate();
                _mainWindowInstance.Focus();
            }

            // Tutup Dashboard jika ada
            if (_dashboardInstance != null && _dashboardInstance.IsVisible)
            {
                _dashboardInstance.Hide();
            }
        }

        /// <summary>
        /// Toggle antara Dashboard dan MainWindow (Legacy support)
        /// </summary>
        public static void ToggleWindow()
        {
            if (_mainShellInstance != null)
            {
                // Will add toggle logic if needed
                return;
            }

            if (_dashboardInstance != null && _dashboardInstance.IsVisible)
            {
                OpenMainWindow();
            }
            else
            {
                OpenDashboard();
            }
        }

        public static void RegisterMainWindow(MainWindow window)
        {
            _mainWindowInstance = window;
        }

        public static void RefreshGameList()
        {
            if (_mainWindowInstance != null)
            {
                _mainWindowInstance.RefreshGameList();
            }
        }

        public static void RefreshGameLibraryView()
        {
            if (_mainShellInstance != null)
            {
                _mainShellInstance.RefreshGameLibrary();
            }
        }

        public static void RegisterDashboard(Dashboard window)
        {
            _dashboardInstance = window;
        }

        public static Views.DashboardView GetDashboardView()
        {
            if (_dashboardInstance != null && _dashboardInstance.Content is Views.DashboardView dashboardView)
            {
                return dashboardView;
            }
            return null;
        }
    }
}
