using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace SteamPluginManager.Views
{
    public partial class SaweriaView : UserControl
    {
        private const string SaweriaUrl = "https://saweria.co/zortyyy";

        public SaweriaView()
        {
            InitializeComponent();
            Loaded += SaweriaView_Loaded;
        }

        private async void SaweriaView_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= SaweriaView_Loaded;

            try
            {
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SteamPluginManager",
                    "WebView2");
                Directory.CreateDirectory(userDataFolder);

                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataFolder);
                await SaweriaBrowser.EnsureCoreWebView2Async(environment);
                SaweriaBrowser.NavigationStarting += SaweriaBrowser_NavigationStarting;
                SaweriaBrowser.NavigationCompleted += SaweriaBrowser_NavigationCompleted;
                SaweriaBrowser.CoreWebView2.HistoryChanged += CoreWebView2_HistoryChanged;
                SaweriaBrowser.Source = new Uri(SaweriaUrl);
            }
            catch (Exception ex)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                MessageBox.Show(
                    $"WebView2 Runtime is required to display Saweria inside the app.\n\n{ex.Message}",
                    "Saweria",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void SaweriaBrowser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            LoadingOverlay.Visibility = Visibility.Visible;
        }

        private void SaweriaBrowser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            UpdateNavigationButtons();
        }

        private void CoreWebView2_HistoryChanged(object? sender, object e)
        {
            Dispatcher.BeginInvoke(UpdateNavigationButtons);
        }

        private void UpdateNavigationButtons()
        {
            if (SaweriaBrowser.CoreWebView2 is null)
                return;

            BackButton.IsEnabled = SaweriaBrowser.CoreWebView2.CanGoBack;
            ForwardButton.IsEnabled = SaweriaBrowser.CoreWebView2.CanGoForward;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (SaweriaBrowser.CoreWebView2?.CanGoBack == true)
                SaweriaBrowser.CoreWebView2.GoBack();
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            if (SaweriaBrowser.CoreWebView2?.CanGoForward == true)
                SaweriaBrowser.CoreWebView2.GoForward();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            SaweriaBrowser.CoreWebView2?.Reload();
        }
    }
}
