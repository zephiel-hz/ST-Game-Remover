using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SteamPluginManager;

namespace SteamPluginManager.Views
{
    public partial class Sidebar : UserControl
    {
        private bool _isSyncingUnlockerToggle;

        public Sidebar()
        {
            InitializeComponent();
            Loaded += Sidebar_Loaded;
        }

        private void Sidebar_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= Sidebar_Loaded;
            LoadUnlockerToggleState();
        }

        private void Expander_Expanded(object sender, RoutedEventArgs e)
        {
            if (sender is not Expander expanded)
                return;

            if (MainMenuExpander is not null && !ReferenceEquals(expanded, MainMenuExpander))
                MainMenuExpander.IsExpanded = false;
            if (ToolsExpander is not null && !ReferenceEquals(expanded, ToolsExpander))
                ToolsExpander.IsExpanded = false;
            if (SettingsExpander is not null && !ReferenceEquals(expanded, SettingsExpander))
                SettingsExpander.IsExpanded = false;
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            // Navigate via WindowNavigator so sidebar can be used anywhere
            WindowNavigator.NavigateToSettings();
        }

        private void Dev_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.NavigateToDev();
        }

        public void SetAdminVisibility(bool isAdmin)
        {
            Dispatcher.Invoke(() =>
            {
                if (DevButton != null)
                {
                    DevButton.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
                }
            });
        }

        private void Library_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.NavigateToGameLibrary();
        }

        private void Tools_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.NavigateToTools();
        }

        private void HZManifest_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.NavigateToHZManifest();
        }

        private void Dashboard_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.NavigateToDashboard();
        }

        private void GameBypass_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.NavigateToGameBypass();
        }

        private void OnlineFix_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.NavigateToOnlineFix();
        }

        private void RestartSteam_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.RestartSteam();
        }

        private void UnlockerToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (_isSyncingUnlockerToggle) return;

            try
            {
                bool isEnabled = UnlockerToggle?.IsChecked ?? false;
                UnlockerRegistryHelper.SetUnlockerEnabled(isEnabled);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to update Unlocker toggle: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LoadUnlockerToggleState()
        {
            SetUnlockerToggleState(UnlockerRegistryHelper.IsUnlockerEnabled());
        }

        public void SetUnlockerToggleState(bool isEnabled)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => SetUnlockerToggleState(isEnabled)));
                return;
            }

            try
            {
                if (UnlockerToggle != null && UnlockerToggle.IsChecked != isEnabled)
                {
                    _isSyncingUnlockerToggle = true;
                    try
                    {
                        UnlockerToggle.IsChecked = isEnabled;
                    }
                    finally
                    {
                        _isSyncingUnlockerToggle = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[Sidebar] Failed to set unlocker toggle state: {ex.Message}");
            }
        }

        private void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.CheckForUpdates();
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.NavigateToAbout();
        }

        private void Saweria_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.NavigateToSaweria();
        }

        private async void ProfileNameText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            await DashboardView.OpenProfileEditorAsync();
        }

        // Allow other views to update sidebar content
        public void SetProfile(string displayName, string status, System.Windows.Media.ImageSource? avatarImage = null)
        {
            Dispatcher.Invoke(() =>
            {
                if (!string.IsNullOrWhiteSpace(displayName))
                    ProfileNameText.Text = displayName;
                if (ProfileStatusText != null)
                {
                    ProfileStatusText.Text = status ?? string.Empty;
                    ProfileStatusText.Visibility = string.IsNullOrWhiteSpace(status) ? Visibility.Collapsed : Visibility.Visible;
                }
            });
        }

        public void SetTip(string tip)
        {
            // Tip area removed from sidebar UI.
        }

        // Set the visual active menu by key. This only updates visuals.
        public void SetActiveMenu(string key)
        {
            Dispatcher.Invoke(() =>
            {
                // Clear all
                void Clear(Button? b)
                {
                    if (b == null) return;
                    b.ClearValue(BackgroundProperty);
                    b.ClearValue(BorderBrushProperty);
                    b.ClearValue(BorderThicknessProperty);
                }

                Clear(DashboardButton);
                Clear(LibraryButton);
                Clear(ToolsButton);
                Clear(HZManifestButton);
                Clear(GameBypassButton);
                Clear(OnlineFixButton);
                Clear(RestartSteamButton);
                Clear(SettingsButton);
                Clear(DevButton);
                Clear(CheckUpdatesButton);
                Clear(AboutButton);
                Clear(SaweriaButton);
                if (SaweriaCard != null)
                {
                    SaweriaCard.SetResourceReference(BackgroundProperty, "CardBackgroundBrush");
                    SaweriaCard.SetResourceReference(BorderBrushProperty, "BorderBrush");
                    SaweriaCard.BorderThickness = new Thickness(1);
                }

                Button? select = key?.ToLowerInvariant() switch
                {
                    "dashboard" => DashboardButton,
                    "library" => LibraryButton,
                    "tools" => ToolsButton,
                    "hzmanifest" => HZManifestButton,
                    "gamebypass" => GameBypassButton,
                    "onlinefix" => OnlineFixButton,
                    "restartsteam" => RestartSteamButton,
                    "settings" => SettingsButton,
                    "dev" => DevButton,
                    "checkupdates" => CheckUpdatesButton,
                    "about" => AboutButton,
                    "saweria" => SaweriaButton,
                    _ => null
                };

                if (select != null)
                {
                    // Apply selected visuals using dynamic resources defined in themes
                    select.SetResourceReference(BackgroundProperty, "SelectedBrush");
                    select.SetResourceReference(BorderBrushProperty, "AccentBrush");
                    select.BorderThickness = new Thickness(1);
                }
            });
        }
    }
}
