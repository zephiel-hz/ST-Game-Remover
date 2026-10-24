using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace SteamPluginManager.Services.SystemTray
{
    public class SystemTrayService : IDisposable
    {
        #region Native Win32 API Definitions

        private const uint WM_USER = 0x0400;
        private const uint WM_TRAYICON = WM_USER + 1024;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint NIN_BALLOONUSERCLICK = WM_USER + 5;

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;

        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;
        private const uint NIF_INFO = 0x00000010;

        private const uint NIIF_INFO = 0x00000001;

        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint LR_DEFAULTSIZE = 0x00000040;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern uint ExtractIconEx(string szFileName, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

        #endregion

        private readonly Window _ownerWindow;
        private readonly Action _onRestoreAction;
        private readonly Action _onOpenSettingsAction;
        private readonly Action _onExitAction;
        private readonly Action? _onNotificationClickAction;

        private IntPtr _hwnd = IntPtr.Zero;
        private HwndSource? _hwndSource;
        private IntPtr _currentIconHandle = IntPtr.Zero;
        private uint _taskbarCreatedMsg = 0;
        private bool _isIconAdded = false;
        private ContextMenu? _trayContextMenu;

        public bool IsIconVisible => _isIconAdded;

        public SystemTrayService(
            Window ownerWindow,
            Action onRestoreAction,
            Action onOpenSettingsAction,
            Action onExitAction,
            Action? onNotificationClickAction = null)
        {
            _ownerWindow = ownerWindow ?? throw new ArgumentNullException(nameof(ownerWindow));
            _onRestoreAction = onRestoreAction ?? throw new ArgumentNullException(nameof(onRestoreAction));
            _onOpenSettingsAction = onOpenSettingsAction ?? throw new ArgumentNullException(nameof(onOpenSettingsAction));
            _onExitAction = onExitAction ?? throw new ArgumentNullException(nameof(onExitAction));
            _onNotificationClickAction = onNotificationClickAction;
        }

        public void Initialize()
        {
            try
            {
                var helper = new WindowInteropHelper(_ownerWindow);
                _hwnd = helper.Handle;

                if (_hwnd == IntPtr.Zero)
                {
                    Logger.Log("[SystemTrayService] Window handle is not ready yet. Postponing initialization until SourceInitialized.");
                    _ownerWindow.SourceInitialized += OwnerWindow_SourceInitialized;
                    return;
                }

                AttachHwndHook();
                CreateContextMenu();
                AddTrayIcon();

                App.ThemeChanged += App_ThemeChanged;
            }
            catch (Exception ex)
            {
                Logger.Log($"[SystemTrayService] Initialize error: {ex.Message}");
            }
        }

        private void OwnerWindow_SourceInitialized(object? sender, EventArgs e)
        {
            _ownerWindow.SourceInitialized -= OwnerWindow_SourceInitialized;
            var helper = new WindowInteropHelper(_ownerWindow);
            _hwnd = helper.Handle;

            AttachHwndHook();
            CreateContextMenu();
            AddTrayIcon();

            App.ThemeChanged += App_ThemeChanged;
        }

        private void AttachHwndHook()
        {
            if (_hwndSource != null || _hwnd == IntPtr.Zero) return;

            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(HwndMessageHook);
            _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
        }

        private void App_ThemeChanged(object? sender, EventArgs e)
        {
            try
            {
                _ownerWindow.Dispatcher.BeginInvoke(new Action(UpdateTrayIcon));
            }
            catch { }
        }

        private void AddTrayIcon()
        {
            if (_hwnd == IntPtr.Zero) return;

            try
            {
                LoadCurrentIcon();

                var nid = CreateNotifyIconData();
                nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
                nid.uCallbackMessage = WM_TRAYICON;
                nid.hIcon = _currentIconHandle;
                nid.szTip = "HZ Lua Manager";

                _isIconAdded = Shell_NotifyIcon(NIM_ADD, ref nid);
                Logger.Log($"[SystemTrayService] AddTrayIcon result: {_isIconAdded}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[SystemTrayService] AddTrayIcon error: {ex.Message}");
            }
        }

        public void UpdateTrayIcon()
        {
            if (!_isIconAdded || _hwnd == IntPtr.Zero) return;

            try
            {
                LoadCurrentIcon();

                var nid = CreateNotifyIconData();
                nid.uFlags = NIF_ICON | NIF_TIP;
                nid.hIcon = _currentIconHandle;
                nid.szTip = "HZ Lua Manager";

                Shell_NotifyIcon(NIM_MODIFY, ref nid);
            }
            catch (Exception ex)
            {
                Logger.Log($"[SystemTrayService] UpdateTrayIcon error: {ex.Message}");
            }
        }

        public void RemoveTrayIcon()
        {
            if (!_isIconAdded || _hwnd == IntPtr.Zero) return;

            try
            {
                var nid = CreateNotifyIconData();
                Shell_NotifyIcon(NIM_DELETE, ref nid);
                _isIconAdded = false;
                Logger.Log("[SystemTrayService] RemoveTrayIcon completed");
            }
            catch (Exception ex)
            {
                Logger.Log($"[SystemTrayService] RemoveTrayIcon error: {ex.Message}");
            }
            finally
            {
                ReleaseCurrentIcon();
            }
        }

        public void ShowNotification(string title, string message)
        {
            if (!_isIconAdded || _hwnd == IntPtr.Zero) return;

            try
            {
                var nid = CreateNotifyIconData();
                nid.uFlags = NIF_INFO | NIF_MESSAGE;
                nid.uCallbackMessage = WM_TRAYICON;

                string safeTitle = title ?? "HZ Lua Manager";
                if (safeTitle.Length > 63) safeTitle = safeTitle.Substring(0, 60) + "...";

                string safeMessage = message ?? string.Empty;
                if (safeMessage.Length > 255) safeMessage = safeMessage.Substring(0, 252) + "...";

                nid.szInfoTitle = safeTitle;
                nid.szInfo = safeMessage;
                nid.dwInfoFlags = NIIF_INFO;

                Shell_NotifyIcon(NIM_MODIFY, ref nid);
            }
            catch (Exception ex)
            {
                Logger.Log($"[SystemTrayService] ShowNotification error: {ex.Message}");
            }
        }

        private NOTIFYICONDATA CreateNotifyIconData()
        {
            return new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1001,
                szTip = string.Empty,
                szInfo = string.Empty,
                szInfoTitle = string.Empty
            };
        }

        private void LoadCurrentIcon()
        {
            ReleaseCurrentIcon();

            try
            {
                string iconPath = App.GetThemeIconPath();
                if (!File.Exists(iconPath))
                {
                    iconPath = Path.Combine(AppContext.BaseDirectory, "Config", "SGR.ico");
                }

                if (File.Exists(iconPath))
                {
                    _currentIconHandle = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
                }

                if (_currentIconHandle == IntPtr.Zero)
                {
                    // Fallback to application primary icon via Win32 ExtractIconEx
                    var proc = System.Diagnostics.Process.GetCurrentProcess();
                    string? mainModule = proc.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(mainModule) && File.Exists(mainModule))
                    {
                        var smallIcons = new IntPtr[1];
                        uint count = ExtractIconEx(mainModule, 0, null, smallIcons, 1);
                        if (count > 0 && smallIcons[0] != IntPtr.Zero)
                        {
                            _currentIconHandle = smallIcons[0];
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SystemTrayService] LoadCurrentIcon error: {ex.Message}");
            }
        }

        private void ReleaseCurrentIcon()
        {
            if (_currentIconHandle != IntPtr.Zero)
            {
                try
                {
                    DestroyIcon(_currentIconHandle);
                }
                catch { }
                _currentIconHandle = IntPtr.Zero;
            }
        }

        private void CreateContextMenu()
        {
            var menu = new ContextMenu
            {
                Placement = PlacementMode.MousePoint,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };

            // Custom ContextMenu Template (Dark background, rounded corners, subtle border, shadow)
            var contextMenuTemplate = new ControlTemplate(typeof(ContextMenu));
            var menuBorderFactory = new FrameworkElementFactory(typeof(Border));
            menuBorderFactory.SetValue(Border.BackgroundProperty, (Brush)Application.Current.FindResource("CardBackgroundBrush"));
            menuBorderFactory.SetValue(Border.BorderBrushProperty, (Brush)Application.Current.FindResource("BorderBrush"));
            menuBorderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            menuBorderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            menuBorderFactory.SetValue(Border.PaddingProperty, new Thickness(4, 5, 4, 5));
            menuBorderFactory.SetValue(Border.SnapsToDevicePixelsProperty, true);

            var shadowEffect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 20,
                ShadowDepth = 6,
                Opacity = 0.55
            };
            menuBorderFactory.SetValue(UIElement.EffectProperty, shadowEffect);

            var itemsPresenter = new FrameworkElementFactory(typeof(ItemsPresenter));
            itemsPresenter.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
            menuBorderFactory.AppendChild(itemsPresenter);
            contextMenuTemplate.VisualTree = menuBorderFactory;
            menu.Template = contextMenuTemplate;

            // Base MenuItem Style (No icon gutter, smooth rounded hover)
            var menuItemStyle = new Style(typeof(MenuItem));
            menuItemStyle.Setters.Add(new Setter(MenuItem.ForegroundProperty, (Brush)Application.Current.FindResource("ForegroundBrush")));
            menuItemStyle.Setters.Add(new Setter(MenuItem.CursorProperty, Cursors.Hand));
            menuItemStyle.Setters.Add(new Setter(MenuItem.FontSizeProperty, 12.0));

            var menuItemTemplate = new ControlTemplate(typeof(MenuItem));
            var itemBorderFactory = new FrameworkElementFactory(typeof(Border));
            itemBorderFactory.Name = "ItemBorder";
            itemBorderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            itemBorderFactory.SetValue(Border.PaddingProperty, new Thickness(14, 7, 16, 7));
            itemBorderFactory.SetValue(Border.MarginProperty, new Thickness(2, 1, 2, 1));
            itemBorderFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);

            var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
            contentPresenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            itemBorderFactory.AppendChild(contentPresenter);

            menuItemTemplate.VisualTree = itemBorderFactory;

            var highlightTrigger = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            highlightTrigger.Setters.Add(new Setter(Border.BackgroundProperty, (Brush)Application.Current.FindResource("HoverBrush"), "ItemBorder"));
            menuItemTemplate.Triggers.Add(highlightTrigger);

            menuItemStyle.Setters.Add(new Setter(MenuItem.TemplateProperty, menuItemTemplate));

            // Header Style (Non-clickable, accent colored)
            var headerStyle = new Style(typeof(MenuItem), menuItemStyle);
            headerStyle.Setters.Add(new Setter(MenuItem.CursorProperty, Cursors.Arrow));
            headerStyle.Setters.Add(new Setter(MenuItem.ForegroundProperty, (Brush)Application.Current.FindResource("AccentBrush")));

            var headerTemplate = new ControlTemplate(typeof(MenuItem));
            var headerBorderFactory = new FrameworkElementFactory(typeof(Border));
            headerBorderFactory.SetValue(Border.PaddingProperty, new Thickness(14, 7, 16, 5));
            headerBorderFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);

            var headerContentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
            headerContentPresenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            headerContentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            headerBorderFactory.AppendChild(headerContentPresenter);
            headerTemplate.VisualTree = headerBorderFactory;
            headerStyle.Setters.Add(new Setter(MenuItem.TemplateProperty, headerTemplate));

            // Exit MenuItem Style (Red text, danger hover)
            var exitItemStyle = new Style(typeof(MenuItem), menuItemStyle);
            exitItemStyle.Setters.Add(new Setter(MenuItem.ForegroundProperty, (Brush)Application.Current.FindResource("DangerButtonBrush")));

            // Separator Style
            var sepStyle = new Style(typeof(Separator));
            var sepTemplate = new ControlTemplate(typeof(Separator));
            var sepBorderFactory = new FrameworkElementFactory(typeof(Border));
            sepBorderFactory.SetValue(Border.HeightProperty, 1.0);
            sepBorderFactory.SetValue(Border.BackgroundProperty, (Brush)Application.Current.FindResource("BorderBrush"));
            sepBorderFactory.SetValue(Border.OpacityProperty, 0.45);
            sepBorderFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 4, 6, 4));
            sepTemplate.VisualTree = sepBorderFactory;
            sepStyle.Setters.Add(new Setter(Separator.TemplateProperty, sepTemplate));

            // Title header
            var titleHeader = new MenuItem
            {
                Header = "HZ Lua Manager",
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                IsEnabled = false,
                Style = headerStyle
            };
            menu.Items.Add(titleHeader);
            menu.Items.Add(new Separator { Style = sepStyle });

            // Restore / Open item
            var openItem = new MenuItem
            {
                Header = "Open HZ Lua Manager",
                FontWeight = FontWeights.SemiBold,
                Style = menuItemStyle
            };
            openItem.Click += (s, e) => _onRestoreAction?.Invoke();
            menu.Items.Add(openItem);

            // Settings item
            var settingsItem = new MenuItem
            {
                Header = "Settings",
                Style = menuItemStyle
            };
            settingsItem.Click += (s, e) => _onOpenSettingsAction?.Invoke();
            menu.Items.Add(settingsItem);

            menu.Items.Add(new Separator { Style = sepStyle });

            // Exit item
            var exitItem = new MenuItem
            {
                Header = "Exit Application",
                Style = exitItemStyle
            };
            exitItem.Click += (s, e) => _onExitAction?.Invoke();
            menu.Items.Add(exitItem);

            _trayContextMenu = menu;
        }

        private IntPtr HwndMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == (int)WM_TRAYICON)
            {
                uint mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);

                if (mouseMsg == WM_LBUTTONUP || mouseMsg == WM_LBUTTONDBLCLK)
                {
                    _ownerWindow.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _onRestoreAction?.Invoke();
                    }));
                    handled = true;
                }
                else if (mouseMsg == WM_RBUTTONUP)
                {
                    _ownerWindow.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ShowContextMenu();
                    }));
                    handled = true;
                }
                else if (mouseMsg == NIN_BALLOONUSERCLICK)
                {
                    _ownerWindow.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _onNotificationClickAction?.Invoke();
                    }));
                    handled = true;
                }
            }
            else if (_taskbarCreatedMsg != 0 && (uint)msg == _taskbarCreatedMsg)
            {
                // Windows Explorer restarted; re-add the tray icon
                Logger.Log("[SystemTrayService] TaskbarCreated message received. Re-adding tray icon.");
                _ownerWindow.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _isIconAdded = false;
                    AddTrayIcon();
                }));
            }

            return IntPtr.Zero;
        }

        private void ShowContextMenu()
        {
            try
            {
                if (_trayContextMenu == null)
                {
                    CreateContextMenu();
                }

                if (_hwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(_hwnd);
                }

                if (_trayContextMenu != null)
                {
                    _trayContextMenu.IsOpen = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SystemTrayService] ShowContextMenu error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            App.ThemeChanged -= App_ThemeChanged;

            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(HwndMessageHook);
                _hwndSource = null;
            }

            RemoveTrayIcon();
        }
    }
}
