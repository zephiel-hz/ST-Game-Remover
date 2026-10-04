using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SteamPluginManager.Models;
using SteamPluginManager.Services.API;
using SteamPluginManager.Services.Profile;
using SteamPluginManager.UI.Dialogs;

namespace SteamPluginManager.Views
{
    public partial class CustomizeProfileModalView : UserControl
    {
        private UserProfile? _editingProfile;
        private Action? _onSavedCallback;
        private byte[]? _pendingCroppedBytes;
        private bool _photoRemoved;
        private bool _isMandatorySetup;

        public bool IsOpen { get; private set; }

        public CustomizeProfileModalView()
        {
            InitializeComponent();
            KeyDown += CustomizeProfileModalView_KeyDown;
        }

        private void CustomizeProfileModalView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (!_isMandatorySetup)
                {
                    HideModal();
                }
                e.Handled = true;
            }
        }

        public void ShowModal(UserProfile? profile = null, Action? onSaved = null, bool isMandatorySetup = false)
        {
            Dispatcher.Invoke(async () =>
            {
                IsOpen = true;
                _isMandatorySetup = isMandatorySetup;
                _onSavedCallback = onSaved;
                _pendingCroppedBytes = null;
                _photoRemoved = false;

                if (_isMandatorySetup)
                {
                    if (CloseButton != null) CloseButton.Visibility = Visibility.Collapsed;
                    if (CancelButton != null) CancelButton.Visibility = Visibility.Collapsed;
                    if (ModalTitleText != null) ModalTitleText.Text = "Set Your Display Name";
                    if (ModalSubtitleText != null) ModalSubtitleText.Text = "A display name is required before you can use the application and community features.";
                }
                else
                {
                    if (CloseButton != null) CloseButton.Visibility = Visibility.Visible;
                    if (CancelButton != null) CancelButton.Visibility = Visibility.Visible;
                    if (ModalTitleText != null) ModalTitleText.Text = "Customize Profile";
                    if (ModalSubtitleText != null) ModalSubtitleText.Text = "Update your public display name and account bio.";
                }

                if (profile == null)
                {
                    try
                    {
                        profile = await DashboardView.LoadUserProfileForEditingAsync();
                    }
                    catch { }
                }

                _editingProfile = profile ?? new UserProfile();

                // Populate fields
                DisplayNameTextBox.Text = _editingProfile.DisplayName ?? string.Empty;
                BioTextBox.Text = _editingProfile.Bio ?? string.Empty;

                // Update Device ID preview
                string displayDeviceId = _editingProfile.DeviceId;
                if (string.IsNullOrWhiteSpace(displayDeviceId))
                {
                    displayDeviceId = "UNKNOWN";
                }
                else if (displayDeviceId.Length > 16)
                {
                    displayDeviceId = displayDeviceId.Substring(0, 14) + "...";
                }
                DeviceIdPreviewText.Text = $"Device: {displayDeviceId}";

                // Load existing avatar if any
                if (_editingProfile.Id.HasValue && !string.IsNullOrWhiteSpace(_editingProfile.AvatarUrl))
                {
                    var cachedImg = await ProfilePictureCacheService.GetAvatarAsync(_editingProfile.Id.Value, _editingProfile.AvatarUrl);
                    if (cachedImg != null)
                    {
                        AvatarPreviewImage.Source = cachedImg;
                        AvatarPreviewImage.Visibility = Visibility.Visible;
                        AvatarPreviewInitial.Visibility = Visibility.Collapsed;
                        RemovePhotoButton.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        AvatarPreviewImage.Source = null;
                        AvatarPreviewImage.Visibility = Visibility.Collapsed;
                        AvatarPreviewInitial.Visibility = Visibility.Visible;
                        RemovePhotoButton.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    AvatarPreviewImage.Source = null;
                    AvatarPreviewImage.Visibility = Visibility.Collapsed;
                    AvatarPreviewInitial.Visibility = Visibility.Visible;
                    RemovePhotoButton.Visibility = Visibility.Collapsed;
                }

                UpdateLivePreview();
                HideValidation();

                SaveButton.IsEnabled = true;
                SaveButton.Content = "Save Changes";

                // Host inside MainShell's global centered overlay
                var mainShell = WindowNavigator.GetMainShell();
                if (mainShell != null)
                {
                    mainShell.ShowGlobalModal(this);
                }
                else
                {
                    this.Visibility = Visibility.Visible;
                }

                // Focus on DisplayName
                DisplayNameTextBox.Focus();
                DisplayNameTextBox.SelectAll();
            });
        }

        public void HideModal()
        {
            Dispatcher.Invoke(() =>
            {
                IsOpen = false;
                _pendingCroppedBytes = null;
                _photoRemoved = false;
                HideValidation();

                var mainShell = WindowNavigator.GetMainShell();
                if (mainShell != null)
                {
                    mainShell.HideGlobalModal();
                }
                else
                {
                    this.Visibility = Visibility.Collapsed;
                }
            });
        }

        private void UpdateLivePreview()
        {
            var name = DisplayNameTextBox?.Text?.Trim() ?? string.Empty;
            var bio = BioTextBox?.Text?.Trim() ?? string.Empty;

            // Update Avatar Name & Initial
            if (string.IsNullOrWhiteSpace(name))
            {
                if (AvatarPreviewName != null) AvatarPreviewName.Text = "User";
                if (AvatarPreviewInitial != null) AvatarPreviewInitial.Text = "U";
            }
            else
            {
                if (AvatarPreviewName != null) AvatarPreviewName.Text = name;
                if (AvatarPreviewInitial != null) AvatarPreviewInitial.Text = name[0].ToString().ToUpperInvariant();
            }

            // Update Avatar Bio
            if (AvatarPreviewBio != null)
            {
                if (string.IsNullOrWhiteSpace(bio))
                {
                    AvatarPreviewBio.Text = "No bio provided.";
                    AvatarPreviewBio.FontStyle = FontStyles.Italic;
                }
                else
                {
                    AvatarPreviewBio.Text = bio;
                    AvatarPreviewBio.FontStyle = FontStyles.Normal;
                }
            }

            // Update Character Counts
            if (NameCharCountText != null)
            {
                NameCharCountText.Text = $"{DisplayNameTextBox?.Text?.Length ?? 0}/30";
            }

            if (BioCharCountText != null)
            {
                BioCharCountText.Text = $"{BioTextBox?.Text?.Length ?? 0}/180";
            }
        }

        private void ChangePhoto_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Title = "Select New Profile Picture",
                    Filter = "Image Files (*.jpg;*.jpeg;*.png;*.webp;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.bmp|All Files (*.*)|*.*",
                    Multiselect = false
                };

                var parentWindow = Window.GetWindow(this);
                if (openFileDialog.ShowDialog(parentWindow) == true)
                {
                    string selectedPath = openFileDialog.FileName;
                    var croppedBytes = ImageCropperDialog.ShowAndCrop(parentWindow, selectedPath);
                    if (croppedBytes != null && croppedBytes.Length > 0)
                    {
                        _pendingCroppedBytes = croppedBytes;
                        _photoRemoved = false;

                        // Display new cropped preview
                        var previewBmp = ProfilePictureCacheService.LoadBitmapFromBytes(croppedBytes);
                        if (previewBmp != null)
                        {
                            AvatarPreviewImage.Source = previewBmp;
                            AvatarPreviewImage.Visibility = Visibility.Visible;
                            AvatarPreviewInitial.Visibility = Visibility.Collapsed;
                            RemovePhotoButton.Visibility = Visibility.Visible;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[CustomizeProfileModal] Error picking photo: {ex.Message}");
                ShowValidation($"Failed to select photo: {ex.Message}");
            }
        }

        private void RemovePhoto_Click(object sender, RoutedEventArgs e)
        {
            _pendingCroppedBytes = null;
            _photoRemoved = true;

            AvatarPreviewImage.Source = null;
            AvatarPreviewImage.Visibility = Visibility.Collapsed;
            AvatarPreviewInitial.Visibility = Visibility.Visible;
            RemovePhotoButton.Visibility = Visibility.Collapsed;
        }

        private void DisplayNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (DisplayNamePlaceholder != null)
            {
                DisplayNamePlaceholder.Visibility = string.IsNullOrEmpty(DisplayNameTextBox.Text)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            UpdateLivePreview();
        }

        private void DisplayNameTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SaveButton_Click(sender, e);
                e.Handled = true;
            }
        }

        private void BioTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (BioPlaceholder != null)
            {
                BioPlaceholder.Visibility = string.IsNullOrEmpty(BioTextBox.Text)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            UpdateLivePreview();
        }

        private void ShowValidation(string message)
        {
            if (ValidationBanner != null && ValidationText != null)
            {
                ValidationText.Text = message;
                ValidationBanner.Visibility = Visibility.Visible;
            }
        }

        private void HideValidation()
        {
            if (ValidationBanner != null)
            {
                ValidationBanner.Visibility = Visibility.Collapsed;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            HideModal();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            HideModal();
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var newName = DisplayNameTextBox.Text?.Trim() ?? string.Empty;
                var newBio = BioTextBox.Text?.Trim() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(newName))
                {
                    ShowValidation("Display Name cannot be empty.");
                    DisplayNameTextBox.Focus();
                    return;
                }

                HideValidation();
                SaveButton.IsEnabled = false;
                SaveButton.Content = "Saving...";

                if (_editingProfile == null)
                {
                    _editingProfile = new UserProfile();
                }

                _editingProfile.DisplayName = newName;
                _editingProfile.Bio = newBio;

                // 1. If photo was removed
                if (_photoRemoved)
                {
                    if (_editingProfile.Id.HasValue)
                    {
                        await ProfilePictureCacheService.DeleteAvatarAsync(_editingProfile.Id.Value);
                    }
                    _editingProfile.AvatarUrl = null;
                    _editingProfile.AvatarImage = null;
                }

                // 2. Ensure profile exists in Supabase to acquire ID if brand new
                if (!_editingProfile.Id.HasValue)
                {
                    var (initSuccess, initErr) = await DashboardView.SaveUserProfileAsync(_editingProfile);
                    if (!initSuccess)
                    {
                        ShowValidation(string.IsNullOrWhiteSpace(initErr) ? "Failed to initialize profile. Please try again." : initErr);
                        SaveButton.IsEnabled = true;
                        SaveButton.Content = "Save Changes";
                        return;
                    }
                }

                // 3. If there is a new cropped photo to upload to B2, upload FIRST before publishing DB record
                if (_pendingCroppedBytes != null && _pendingCroppedBytes.Length > 0 && _editingProfile.Id.HasValue)
                {
                    SaveButton.Content = "Uploading photo...";
                    int uid = _editingProfile.Id.Value;
                    bool uploadSuccess = await ProfilePictureCacheService.SaveAndUploadAvatarAsync(uid, _pendingCroppedBytes);
                    
                    if (uploadSuccess)
                    {
                        _editingProfile.AvatarUrl = $"pfp/{uid}.jpg";
                        _editingProfile.AvatarImage = AvatarPreviewImage.Source;
                    }
                    else
                    {
                        Logger.Log($"[CustomizeProfileModal] Avatar upload to B2 failed for user {uid}");
                    }
                }

                // 4. Save profile to Supabase (now containing the updated avatar_url)
                var (success, errorMessage) = await DashboardView.SaveUserProfileAsync(_editingProfile);
                if (!success)
                {
                    ShowValidation(string.IsNullOrWhiteSpace(errorMessage) ? "Failed to save profile. Please try again." : errorMessage);
                    SaveButton.IsEnabled = true;
                    SaveButton.Content = "Save Changes";
                    return;
                }

                // 5. Broadcast profile update over realtime websocket so other users see new avatar and name immediately
                _ = CommunityChatRealtimeClient.BroadcastProfileUpdatedAsync(_editingProfile);

                // 4. Update UI across the application
                var shell = WindowNavigator.GetMainShell();
                if (shell != null)
                {
                    shell.SetHeaderProfile(newName, _editingProfile.AvatarImage);
                    shell.GlobalSidebar?.SetProfile(newName, string.Empty, _editingProfile.AvatarImage);
                }

                _onSavedCallback?.Invoke();
                HideModal();
            }
            catch (Exception ex)
            {
                ShowValidation($"Error saving profile: {ex.Message}");
                SaveButton.IsEnabled = true;
                SaveButton.Content = "Save Changes";
            }
        }
    }
}
