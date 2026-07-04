using CommunityToolkit.WinUI.Behaviors;
using ClashSuki.Helpers;
using ClashSuki.Services;
using ClashSuki.ViewModels;
using ClashSuki.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace ClashSuki
{
    public sealed partial class MainWindow : Window
    {
        private const int MinWindowWidth = 1024;
        private const int MinWindowHeight = 720;
        private const int WmGetMinMaxInfo = 0x0024;

        private readonly AppCoordinator _coordinator;
        private readonly TaskCompletionSource _contentLoadedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SubclassProc _windowSubclassProc;
        private readonly Brush? _defaultRootBackground;
        private IntPtr _hwnd;
        private bool _isClosing;
        private bool _isContentShown;
        private bool _isBackdropShieldVisible;
        private string? _appliedBackdrop;

        private readonly Dictionary<string, Type> _pages = new()
        {
            ["Dashboard"] = typeof(DashboardPage),
            ["Proxies"] = typeof(ProxiesPage),
            ["Connections"] = typeof(ConnectionsPage),
            ["Profiles"] = typeof(ProfilesPage),
            ["Rules"] = typeof(RulesPage),
            ["Override"] = typeof(OverridePage),
            ["Resources"] = typeof(ResourcesPage),
            ["Core"] = typeof(CorePage),
            ["Logs"] = typeof(LogsPage),
            ["Dns"] = typeof(DnsPage),
            ["Tun"] = typeof(TunPage),
            ["Sniffer"] = typeof(SnifferPage),
            ["SysProxy"] = typeof(SysProxyPage),
            ["Settings"] = typeof(SettingsPage)
        };

        public MainWindow(MainViewModel viewModel, AppCoordinator coordinator)
        {
            ViewModel = viewModel;
            _coordinator = coordinator;
            _windowSubclassProc = WindowSubclassProc;
            InitializeComponent();
            _defaultRootBackground = RootGrid.Background;
            RootGrid.DataContext = ViewModel;
            SetWindowProperties();
            rootFrame.CacheSize = _pages.Count;
            RootGrid.ActualThemeChanged += (_, _) => TitleBarHelper.ApplySystemThemeToCaptionButtons(this, RootGrid.ActualTheme);
            AppWindow.Closing += AppWindow_Closing;
            ViewModel.Runtime.PropertyChanged += Runtime_PropertyChanged;
            rootFrame.Navigate(typeof(DashboardPage));
            EnsureNavigationSelection("Dashboard");
        }

        public MainViewModel ViewModel { get; }

        public async Task ActivateWhenContentReadyAsync()
        {
            if (Content is FrameworkElement { IsLoaded: false })
            {
                await Task.WhenAny(_contentLoadedCompletion.Task, Task.Delay(250));
            }

            Activate();
            await ShowContentAsync();
        }

        public async Task PresentAsync()
        {
            await ActivateWhenContentReadyAsync();
            await RevealBackdropAsync();
        }

        public async Task PrepareForSilentStartAsync()
        {
            if (Content is FrameworkElement { IsLoaded: false })
            {
                await Task.WhenAny(_contentLoadedCompletion.Task, Task.Delay(250));
            }

            HideBackdropShieldImmediately();
        }

        private async Task ShowContentAsync()
        {
            if (_isContentShown)
            {
                return;
            }

            _isContentShown = true;
            await Task.Yield();

            var storyboard = new Storyboard();
            var fadeIn = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(160),
                EnableDependentAnimation = true
            };

            Storyboard.SetTarget(fadeIn, RootGrid);
            Storyboard.SetTargetProperty(fadeIn, "Opacity");
            storyboard.Children.Add(fadeIn);

            var completion = new TaskCompletionSource();
            storyboard.Completed += (_, _) => completion.TrySetResult();
            storyboard.Begin();
            await completion.Task;
        }

        public void ApplyTheme(string? theme)
        {
            var requested = theme?.ToLowerInvariant() switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };

            if (RootGrid.RequestedTheme == requested)
            {
                return;
            }

            RootGrid.RequestedTheme = requested;
            TitleBarHelper.ApplySystemThemeToCaptionButtons(this, RootGrid.ActualTheme);
        }

        public void ApplyBackdrop(string? backdrop, bool shield = false)
        {
            var normalized = backdrop?.ToLowerInvariant() switch
            {
                "acrylic" => "acrylic",
                "none" => "none",
                _ => "mica"
            };

            if (string.Equals(_appliedBackdrop, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _appliedBackdrop = normalized;
            var useMaterial = normalized is not "none";

            if (useMaterial && shield)
            {
                ShowBackdropShield();
            }

            SystemBackdrop = normalized switch
            {
                "none" => null,
                "acrylic" => new DesktopAcrylicBackdrop(),
                _ => new MicaBackdrop()
            };

            RootGrid.Background = useMaterial
                ? new SolidColorBrush(Colors.Transparent)
                : _defaultRootBackground;

            if (!useMaterial && shield)
            {
                HideBackdropShieldImmediately();
            }
        }

        public async Task RevealBackdropAsync()
        {
            if (!_isBackdropShieldVisible)
            {
                return;
            }

            await Task.Delay(80);
            await FadeBackdropShieldToAsync(0, 180);
            BackdropShield.Visibility = Visibility.Collapsed;
            _isBackdropShieldVisible = false;
        }

        private void ShowBackdropShield()
        {
            BackdropShield.Opacity = 1;
            BackdropShield.Visibility = Visibility.Visible;
            _isBackdropShieldVisible = true;
        }

        private void HideBackdropShieldImmediately()
        {
            BackdropShield.Opacity = 0;
            BackdropShield.Visibility = Visibility.Collapsed;
            _isBackdropShieldVisible = false;
        }

        private async Task FadeBackdropShieldToAsync(double opacity, int milliseconds)
        {
            var storyboard = new Storyboard();
            var animation = new DoubleAnimation
            {
                To = opacity,
                Duration = TimeSpan.FromMilliseconds(milliseconds),
                EnableDependentAnimation = true
            };

            Storyboard.SetTarget(animation, BackdropShield);
            Storyboard.SetTargetProperty(animation, "Opacity");
            storyboard.Children.Add(animation);

            var completion = new TaskCompletionSource();
            storyboard.Completed += (_, _) => completion.TrySetResult();
            storyboard.Begin();
            await completion.Task;
        }

        private void SetWindowProperties()
        {
            Title = "ClashSuki";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(titleBar);
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            titleBar.IconSource = AppIconProvider.CreateTitleBarIcon();
            SetWindowIcon();
            SetWindowSubclass(_hwnd, _windowSubclassProc, 1, IntPtr.Zero);
            EnsureMinimumWindowSize();
        }

        private void SetWindowIcon()
        {
            try
            {
                AppIconProvider.ApplyWindowIcon(AppWindow);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAppException(
                    LogSources.UserInterface,
                    ex,
                    "设置窗口图标失败",
                    "WARN");
            }
        }

        private void EnsureMinimumWindowSize()
        {
            var minSize = GetScaledMinimumSize();
            if (AppWindow.Size.Width < minSize.Width || AppWindow.Size.Height < minSize.Height)
            {
                AppWindow.Resize(new SizeInt32(
                    Math.Max(AppWindow.Size.Width, minSize.Width),
                    Math.Max(AppWindow.Size.Height, minSize.Height)));
            }
        }

        private SizeInt32 GetScaledMinimumSize()
        {
            var dpi = _hwnd == IntPtr.Zero ? 96u : GetDpiForWindow(_hwnd);
            var scale = dpi / 96.0;
            return new SizeInt32(
                (int)Math.Ceiling(MinWindowWidth * scale),
                (int)Math.Ceiling(MinWindowHeight * scale));
        }

        private IntPtr WindowSubclassProc(
            IntPtr hWnd,
            uint message,
            IntPtr wParam,
            IntPtr lParam,
            nuint subclassId,
            IntPtr refData)
        {
            if (message == WmGetMinMaxInfo)
            {
                var minMaxInfo = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                var minSize = GetScaledMinimumSize();
                minMaxInfo.ptMinTrackSize.X = minSize.Width;
                minMaxInfo.ptMinTrackSize.Y = minSize.Height;
                Marshal.StructureToPtr(minMaxInfo, lParam, true);
                return IntPtr.Zero;
            }

            return DefSubclassProc(hWnd, message, wParam, lParam);
        }

        private void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            TitleBarHelper.ApplySystemThemeToCaptionButtons(this, RootGrid.ActualTheme);
            _contentLoadedCompletion.TrySetResult();
        }

        private void TitleBar_BackRequested(TitleBar sender, object args)
        {
            if (rootFrame.CanGoBack)
            {
                rootFrame.GoBack();
            }
        }

        private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
        {
            NavigationViewControl.IsPaneOpen = !NavigationViewControl.IsPaneOpen;
        }

        private void OnPaneDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
        {
            titleBar.IsPaneToggleButtonVisible = sender.PaneDisplayMode != NavigationViewPaneDisplayMode.Top;
        }

        private void OnNavigationViewSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItemContainer?.Tag is not string pageKey ||
                !_pages.TryGetValue(pageKey, out var pageType) ||
                rootFrame.CurrentSourcePageType == pageType)
            {
                return;
            }

            rootFrame.Navigate(pageType);
        }

        private void EnsureNavigationSelection(string pageKey)
        {
            foreach (var item in NavigationViewControl.MenuItems.OfType<NavigationViewItem>()
                         .Concat(NavigationViewControl.FooterMenuItems.OfType<NavigationViewItem>()))
            {
                if (item.Tag as string == pageKey)
                {
                    NavigationViewControl.SelectedItem = item;
                    item.IsSelected = true;
                    return;
                }
            }
        }

        private void Runtime_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(Stores.RuntimeStore.NotificationId) ||
                string.IsNullOrWhiteSpace(ViewModel.Runtime.NotificationMessage))
            {
                return;
            }

            try
            {
                NotificationQueue.Show(new Notification
                {
                    Title = ViewModel.Runtime.NotificationTitle,
                    Message = ViewModel.Runtime.NotificationMessage,
                    Severity = ViewModel.Runtime.NotificationSeverity,
                    Duration = TimeSpan.FromSeconds(3)
                });
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAppExceptionThrottled(
                    "global-notification-display",
                    LogSources.UserInterface,
                    ex,
                    "显示全局提示失败",
                    level: "WARN");
            }
        }

        private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_isClosing)
            {
                return;
            }

            var trayService = App.TrayService;
            if (trayService is not null)
            {
                var settings = await AppSettingsService.LoadAsync();
                if (settings.CloseToTray)
                {
                    args.Cancel = true;
                    trayService.HideWindow();
                    return;
                }
            }

            args.Cancel = true;
            _isClosing = true;
            try
            {
                trayService?.Dispose();
                await _coordinator.DisposeAsync();
            }
            finally
            {
                Close();
            }
        }

        private delegate IntPtr SubclassProc(
            IntPtr hWnd,
            uint message,
            IntPtr wParam,
            IntPtr lParam,
            nuint subclassId,
            IntPtr refData);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(
            IntPtr hWnd,
            SubclassProc pfnSubclass,
            nuint uIdSubclass,
            IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern IntPtr DefSubclassProc(
            IntPtr hWnd,
            uint uMsg,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);
    }
}
