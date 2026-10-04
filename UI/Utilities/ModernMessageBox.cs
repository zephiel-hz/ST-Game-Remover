using System;
using System.Linq;
using System.Windows;
using SteamPluginManager.Views;

namespace SteamPluginManager
{
    public static class ModernMessageBox
    {
        public static MessageBoxResult Show(
            string messageBoxText,
            string caption = "Information",
            MessageBoxButton button = MessageBoxButton.OK,
            MessageBoxImage icon = MessageBoxImage.None,
            MessageBoxResult defaultResult = MessageBoxResult.None,
            Window? owner = null)
        {
            if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
            {
                return Application.Current.Dispatcher.Invoke(() => Show(messageBoxText, caption, button, icon, defaultResult, owner));
            }

            try
            {
                var dialog = new ModernDialogWindow();
                dialog.Configure(messageBoxText, caption, button, icon, defaultResult);

                Window? targetOwner = owner 
                    ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible && w != dialog)
                    ?? WindowNavigator.GetMainShell()
                    ?? Application.Current?.MainWindow;

                if (targetOwner != null && targetOwner.IsVisible && targetOwner != dialog)
                {
                    dialog.Owner = targetOwner;
                    dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }
                else
                {
                    dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }

                dialog.ShowDialog();
                return dialog.Result;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ModernMessageBox] Error displaying modern dialog: {ex.Message}. Falling back to system MessageBox.");
                return MessageBox.Show(messageBoxText, caption, button, icon, defaultResult);
            }
        }

        public static MessageBoxResult Show(string messageBoxText) =>
            Show(messageBoxText, "Information", MessageBoxButton.OK, MessageBoxImage.None);

        public static MessageBoxResult Show(string messageBoxText, string caption) =>
            Show(messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None);

        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button) =>
            Show(messageBoxText, caption, button, MessageBoxImage.None);

        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) =>
            Show(messageBoxText, caption, button, icon, MessageBoxResult.None);

        public static MessageBoxResult Show(Window owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) =>
            Show(messageBoxText, caption, button, icon, MessageBoxResult.None, owner);

        public static bool ShowConfirm(string message, string title = "Confirm", bool isDestructive = false, Window? owner = null) =>
            Show(message, title, MessageBoxButton.YesNo, isDestructive ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.None, owner) == MessageBoxResult.Yes;

        public static void ShowInformation(string message, string title = "Information", Window? owner = null) =>
            Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.None, owner);

        public static void ShowWarning(string message, string title = "Warning", Window? owner = null) =>
            Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.None, owner);

        public static void ShowError(string message, string title = "Error", Window? owner = null) =>
            Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.None, owner);
    }
}
