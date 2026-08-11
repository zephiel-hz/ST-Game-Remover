using System.Windows;

namespace SteamPluginManager
{
    /// <summary>
    /// Interaction logic for DeleteChoiceDialog.xaml
    /// </summary>
    public partial class DeleteChoiceDialog : Window
    {
        public enum DeleteAction
        {
            Cancel,
            ManifestAndLua,
            Uninstall,
            Both
        }

        public DeleteAction SelectedAction { get; private set; } = DeleteAction.Cancel;
        private readonly int _selectedCount;

        public DeleteChoiceDialog(int selectedCount)
        {
            InitializeComponent();
            _selectedCount = selectedCount;
            // Use resource strings when available (supports localization resource dictionaries)
            var title = TryFindResource("DeleteDialog.Title") as string;
            if (!string.IsNullOrEmpty(title)) Title = title;
            var template = TryFindResource("DeleteDialog.Message") as string ?? "{0} item(s) selected. Choose what to do:";
            MessageText.Text = string.Format(template, _selectedCount);
        }

        private void ManifestAndLua_Click(object sender, RoutedEventArgs e)
        {
            var template = TryFindResource("DeleteDialog.Confirm.ManifestAndLua") as string ?? "Delete manifest and Lua files for {0} item(s)? This will not trigger Steam uninstall.";
            var confirmTitle = TryFindResource("Dialog.Confirm") as string ?? "Confirm";
            var msg = string.Format(template, _selectedCount);
            if (MessageBox.Show(this, msg, confirmTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            SelectedAction = DeleteAction.ManifestAndLua;
            DialogResult = true;
            Close();
        }

        private void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            var template = TryFindResource("DeleteDialog.Confirm.Uninstall") as string ?? "Trigger uninstall for {0} item(s)? This will not delete manifest or Lua files.";
            var confirmTitle = TryFindResource("Dialog.Confirm") as string ?? "Confirm";
            var msg = string.Format(template, _selectedCount);
            if (MessageBox.Show(this, msg, confirmTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            SelectedAction = DeleteAction.Uninstall;
            DialogResult = true;
            Close();
        }

        private void Both_Click(object sender, RoutedEventArgs e)
        {
            var template = TryFindResource("DeleteDialog.Confirm.Both") as string ?? "Delete manifest and Lua files and trigger uninstall for {0} item(s)? This will remove files and start the uninstall process.";
            var confirmTitle = TryFindResource("Dialog.Confirm") as string ?? "Confirm";
            var msg = string.Format(template, _selectedCount);
            if (MessageBox.Show(this, msg, confirmTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            SelectedAction = DeleteAction.Both;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = DeleteAction.Cancel;
            DialogResult = false;
            Close();
        }

        // Window Controls
        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximizeRestore();
            }
            else
            {
                try { DragMove(); }
                catch { }
            }
        }

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

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}