using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SteamPluginManager
{
    public partial class WelcomeScreen : Window
    {
        public WelcomeScreen()
        {
            InitializeComponent();
            var icon = new BitmapImage();
            icon.BeginInit();
            icon.UriSource = new Uri(App.GetThemeIconPath(), UriKind.Absolute);
            icon.CacheOption = BitmapCacheOption.OnLoad;
            icon.EndInit();
            icon.Freeze();
            WelcomeLogoImage.Source = icon;
            Icon = BitmapFrame.Create(icon);
            CenterWindow();
            SetupEventHandlers();
            DisplayVersion();
            StartAnimations();
        }
        
        private void CenterWindow()
        {
            // Center the window on the screen
            this.WindowStartupLocation = WindowStartupLocation.Manual;
            this.Left = (SystemParameters.PrimaryScreenWidth - this.Width) / 2;
            this.Top = (SystemParameters.PrimaryScreenHeight - this.Height) / 2;
        }
        
        private void SetupEventHandlers()
        {
            // Add hover effects for buttons
            GetStartedButton.MouseEnter += (s, e) => 
            {
                GetStartedButton.RenderTransform = new ScaleTransform(1.05, 1.05);
                GetStartedButton.RenderTransformOrigin = new Point(0.5, 0.5);
            };
            
            GetStartedButton.MouseLeave += (s, e) => 
            {
                GetStartedButton.RenderTransform = new ScaleTransform(1.0, 1.0);
            };
            
            CloseButton.MouseEnter += (s, e) => 
            {
                CloseButton.Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
            };
            
            CloseButton.MouseLeave += (s, e) => 
            {
                CloseButton.Background = Brushes.Transparent;
            };
        }
        
        private void DisplayVersion()
        {
            try
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                if (version != null)
                {
                    VersionText.Text = $"v{version.Major}.{version.Minor}.{version.Build}";
                }
                else
                {
                    VersionText.Text = "v2.2.2";
                }
            }
            catch
            {
                VersionText.Text = "v2.2.2";
            }
        }
        
        private void StartAnimations()
        {
            // Start fade in animation for the main container
            var fadeIn = (Storyboard)FindResource("FadeInAnimation");
            fadeIn.Begin();
            
            // Gentle stagger for CTA and checkbox
            ApplySlideIn(GetStartedButton, 0.1);
            ApplySlideIn(DontShowAgainCheckBox, 0.2);
        }
        
        private void ApplySlideIn(FrameworkElement element, double delay)
        {
            if (element == null) return;
            element.Opacity = 0;
            element.RenderTransform = new TranslateTransform(0, 12);
            var sb = new Storyboard();
            var move = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, BeginTime = TimeSpan.FromSeconds(delay) };
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { BeginTime = TimeSpan.FromSeconds(delay) };
            Storyboard.SetTarget(move, element);
            Storyboard.SetTargetProperty(move, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
            Storyboard.SetTarget(fade, element);
            Storyboard.SetTargetProperty(fade, new PropertyPath("Opacity"));
            sb.Children.Add(move);
            sb.Children.Add(fade);
            sb.Begin();
        }
        
        private System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
        {
            if (depObj != null)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(depObj, i);
                    if (child != null && child is T)
                    {
                        yield return (T)child;
                    }

                    foreach (T childOfChild in FindVisualChildren<T>(child))
                    {
                        yield return childOfChild;
                    }
                }
            }
        }
        
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // Close Welcome Screen and shutdown application
            Application.Current.Shutdown();
        }
        
        private void GetStartedButton_Click(object sender, RoutedEventArgs e)
        {
            // Save preference if checkbox is checked
            if (DontShowAgainCheckBox.IsChecked == true)
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\STGameRemover"))
                    {
                        key?.SetValue("ShowWelcomeScreen", false, RegistryValueKind.DWord);
                    }
                }
                catch (Exception ex)
                {
                    // Log error but don't crash
                    System.Diagnostics.Debug.WriteLine($"Failed to save welcome screen preference: {ex.Message}");
                }
            }
            
            // Close the welcome screen
            Close();
        }
        
        public static bool ShouldShowWelcome()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\STGameRemover"))
                {
                    if (key != null)
                    {
                        object value = key.GetValue("ShowWelcomeScreen");
                        if (value is int intValue)
                        {
                            return intValue != 0; // 0 = false, 1 = true
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read welcome screen preference: {ex.Message}");
            }
            
            // Default to showing welcome screen
            return true;
        }
        
        public static void ResetWelcomePreference()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\STGameRemover"))
                {
                    key?.SetValue("ShowWelcomeScreen", true, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to reset welcome screen preference: {ex.Message}");
            }
        }

        // Window Controls
        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximizeRestore();
        }

        private void ToggleMaximizeRestore()
        {
            if (this.WindowState == WindowState.Maximized)
                this.WindowState = WindowState.Normal;
            else
                this.WindowState = WindowState.Maximized;
        }

        private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}