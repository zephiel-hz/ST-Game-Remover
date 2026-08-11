using System.Windows;

namespace SteamPluginManager
{
    public partial class StyledMessageDialog : Window
    {
        public bool IsConfirmed { get; private set; } = false;

        public StyledMessageDialog(string title, string message, bool showCancel = false)
        {
            InitializeComponent();
            DialogTitle.Text = title;
            MessageText.Text = message;
            
            if (showCancel)
            {
                SecondaryButton.Visibility = Visibility.Visible;
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                this.DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.IsConfirmed = false;
            this.DialogResult = false;
            this.Close();
        }

        private void PrimaryButton_Click(object sender, RoutedEventArgs e)
        {
            this.IsConfirmed = true;
            this.DialogResult = true;
            this.Close();
        }

        private void SecondaryButton_Click(object sender, RoutedEventArgs e)
        {
            this.IsConfirmed = false;
            this.DialogResult = false;
            this.Close();
        }
    }
}
