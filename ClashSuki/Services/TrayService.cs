using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using ClashSuki.Stores;
using ClashSuki.ViewModels;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClashSuki.Services;

public sealed class TrayService : IDisposable
{
    private const int SwHide = 0;
    private const int SwShow = 5;

    private readonly Window _window;
    private readonly DashboardViewModel _dashboard;
    private readonly FrameworkElement? _themeRoot;

    private TaskbarIcon? _trayIcon;
    private ToggleMenuFlyoutItem? _systemProxyItem;
    private ToggleMenuFlyoutItem? _tunItem;
    private ToggleMenuFlyoutItem? _ruleModeItem;
    private ToggleMenuFlyoutItem? _globalModeItem;
    private ToggleMenuFlyoutItem? _directModeItem;
    private TrayIconState? _currentIconState;
    private bool _dpiChangedSubscribed;
    private bool _disposed;

    public TrayService(Window window, DashboardViewModel dashboard)
    {
        _window = window;
        _dashboard = dashboard;
        _themeRoot = window.Content as FrameworkElement;
    }

    public bool Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_trayIcon is not null)
        {
            return _trayIcon.IsCreated;
        }

        try
        {
            AppIconProvider.EnsureTrayIconsAvailable();
            var iconState = ResolveIconState();
            var menu = BuildContextMenu();
            _trayIcon = new TaskbarIcon
            {
                ContextFlyout = menu,
                ContextMenuMode = ContextMenuMode.PopupMenu,
                LeftClickCommand = new RelayCommand(ToggleWindow),
                MenuActivation = PopupActivationMode.RightClick,
                NoLeftClickDelay = true,
                RequestedTheme = _themeRoot?.ActualTheme ?? ElementTheme.Default,
                ToolTipText = BuildToolTip()
            };

            ApplyIcon(iconState);
            _trayIcon.TrayIcon.MessageWindow.DpiChanged += TrayIcon_DpiChanged;
            _dpiChangedSubscribed = true;
            _dashboard.Runtime.PropertyChanged += Runtime_PropertyChanged;
            if (_themeRoot is not null)
            {
                _themeRoot.ActualThemeChanged += ThemeRoot_ActualThemeChanged;
            }
            SynchronizeMenuState();

            // ClashSuki must keep its core and controller responsive while hidden.
            _trayIcon.ForceCreate(enablesEfficiencyMode: false);
            if (!_trayIcon.IsCreated)
            {
                throw new InvalidOperationException("Windows 未能创建托盘图标。");
            }

            return true;
        }
        catch (Exception ex)
        {
            Dispose();
            _dashboard.Runtime.Notifications.Error(
                "托盘初始化失败",
                source: LogSources.Tray,
                exception: ex);
            return false;
        }
    }

    private MenuFlyout BuildContextMenu()
    {
        var menu = new MenuFlyout();
        var showWindowItem = CreateMenuItem("显示窗口", "\uE8A7", new RelayCommand(ShowWindow));
        menu.Items.Add(showWindowItem);
        menu.Items.Add(new MenuFlyoutSeparator());

        _systemProxyItem = CreateToggleItem(
            "系统代理",
            "\uE774",
            new AsyncRelayCommand(ToggleSystemProxyAsync));
        _tunItem = CreateToggleItem(
            "虚拟网卡",
            "\uE968",
            new AsyncRelayCommand(ToggleTunAsync));
        menu.Items.Add(_systemProxyItem);
        menu.Items.Add(_tunItem);
        menu.Items.Add(CreateMenuItem(
            "复制代理环境变量",
            "\uE8C8",
            new AsyncRelayCommand(_dashboard.CopyProxyEnvironmentAsync)));
        menu.Items.Add(new MenuFlyoutSeparator());

        var modeMenu = new MenuFlyoutSubItem
        {
            Text = "代理模式",
            Icon = CreateIcon("\uE8D7")
        };
        _ruleModeItem = CreateToggleItem(
            "规则",
            "\uE8FD",
            new AsyncRelayCommand(() => SwitchModeAsync("rule")));
        _globalModeItem = CreateToggleItem(
            "全局",
            "\uE909",
            new AsyncRelayCommand(() => SwitchModeAsync("global")));
        _directModeItem = CreateToggleItem(
            "直连",
            "\uE8AB",
            new AsyncRelayCommand(() => SwitchModeAsync("direct")));
        modeMenu.Items.Add(_ruleModeItem);
        modeMenu.Items.Add(_globalModeItem);
        modeMenu.Items.Add(_directModeItem);
        menu.Items.Add(modeMenu);

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateMenuItem("退出", "\uE8BB", new AsyncRelayCommand(ExitAsync)));
        return menu;
    }

    private static MenuFlyoutItem CreateMenuItem(string text, string glyph, ICommand command)
        => new()
        {
            Text = text,
            Icon = CreateIcon(glyph),
            Command = command
        };

    private static ToggleMenuFlyoutItem CreateToggleItem(string text, string glyph, ICommand command)
        => new()
        {
            Text = text,
            Icon = CreateIcon(glyph),
            Command = command
        };

    private static FontIcon CreateIcon(string glyph)
        => new()
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Glyph = glyph
        };

    private Task ToggleSystemProxyAsync()
        => RunTrayOperationAsync(
            () => _dashboard.SetSystemProxyAsync(!_dashboard.Runtime.IsSystemProxyEnabled));

    private Task ToggleTunAsync()
        => RunTrayOperationAsync(
            () => _dashboard.SetTunAsync(!_dashboard.Runtime.IsTunEnabled));

    private Task SwitchModeAsync(string mode)
        => RunTrayOperationAsync(() => _dashboard.SwitchModeCommand.ExecuteAsync(mode));

    private async Task RunTrayOperationAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            _dashboard.Runtime.Notifications.Error(
                "托盘操作失败",
                source: LogSources.Tray,
                exception: ex);
        }
        finally
        {
            SynchronizeMenuState();
            UpdateIcon();
        }
    }

    private async Task ExitAsync()
    {
        try
        {
            Dispose();
            if (Application.Current is App app)
            {
                await app.ShutdownAsync();
            }
        }
        finally
        {
            Application.Current.Exit();
        }
    }

    private void Runtime_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (
            nameof(RuntimeStore.UploadText) or
            nameof(RuntimeStore.DownloadText) or
            nameof(RuntimeStore.IsSystemProxyEnabled) or
            nameof(RuntimeStore.IsTunEnabled) or
            nameof(RuntimeStore.CurrentMode)))
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (e.PropertyName is nameof(RuntimeStore.UploadText) or nameof(RuntimeStore.DownloadText))
            {
                UpdateToolTip();
                return;
            }

            SynchronizeMenuState();
            if (e.PropertyName is nameof(RuntimeStore.IsSystemProxyEnabled) or nameof(RuntimeStore.IsTunEnabled))
            {
                UpdateIcon();
                UpdateToolTip();
            }
        });
    }

    private void ThemeRoot_ActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.RequestedTheme = sender.ActualTheme;
        }
    }

    private void TrayIcon_DpiChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        RunOnUiThread(() => UpdateIcon(force: true));
    }

    private void SynchronizeMenuState()
    {
        if (_systemProxyItem is null || _tunItem is null)
        {
            return;
        }

        _systemProxyItem.IsChecked = _dashboard.Runtime.IsSystemProxyEnabled;
        _tunItem.IsChecked = _dashboard.Runtime.IsTunEnabled;

        var mode = _dashboard.Runtime.CurrentMode;
        if (_ruleModeItem is not null)
        {
            _ruleModeItem.IsChecked = string.Equals(mode, "rule", StringComparison.OrdinalIgnoreCase);
        }
        if (_globalModeItem is not null)
        {
            _globalModeItem.IsChecked = string.Equals(mode, "global", StringComparison.OrdinalIgnoreCase);
        }
        if (_directModeItem is not null)
        {
            _directModeItem.IsChecked = string.Equals(mode, "direct", StringComparison.OrdinalIgnoreCase);
        }
    }

    private void UpdateIcon(bool force = false)
    {
        if (_trayIcon is null)
        {
            return;
        }

        var iconState = ResolveIconState();
        if (!force && _currentIconState == iconState)
        {
            return;
        }

        try
        {
            ApplyIcon(iconState);
        }
        catch (Exception ex)
        {
            _dashboard.Runtime.Notifications.Error(
                "托盘图标更新失败",
                source: LogSources.Tray,
                exception: ex);
        }
    }

    private void ApplyIcon(TrayIconState state)
    {
        if (_trayIcon is null)
        {
            return;
        }

        var icon = AppIconProvider.CreateTrayIconSource(state).ToIcon();
        try
        {
            // TaskbarIcon owns and disposes the assigned System.Drawing.Icon.
            _trayIcon.Icon = icon;
        }
        catch
        {
            icon.Dispose();
            throw;
        }

        _currentIconState = state;
    }

    private TrayIconState ResolveIconState()
        => (_dashboard.Runtime.IsSystemProxyEnabled, _dashboard.Runtime.IsTunEnabled) switch
        {
            (true, true) => TrayIconState.SystemProxyAndTun,
            (false, true) => TrayIconState.Tun,
            (true, false) => TrayIconState.SystemProxy,
            _ => TrayIconState.Default
        };

    private void UpdateToolTip()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.ToolTipText = BuildToolTip();
        }
    }

    private string BuildToolTip()
    {
        var state = (_dashboard.Runtime.IsSystemProxyEnabled, _dashboard.Runtime.IsTunEnabled) switch
        {
            (true, true) => "系统代理 + 虚拟网卡",
            (false, true) => "虚拟网卡",
            (true, false) => "系统代理",
            _ => "未启用代理"
        };
        return $"ClashSuki · {state}\n↑{_dashboard.Runtime.UploadText}  ↓{_dashboard.Runtime.DownloadText}";
    }

    private void ToggleWindow()
    {
        if (IsVisible())
        {
            HideWindow();
        }
        else
        {
            ShowWindow();
        }
    }

    public void ShowWindow()
    {
        RunOnUiThread(async () =>
        {
            if (_window is MainWindow mainWindow)
            {
                await mainWindow.PresentAsync();
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            NativeShowWindow(hwnd, SwShow);
            SetForegroundWindow(hwnd);
        });
    }

    public void HideWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        NativeShowWindow(hwnd, SwHide);
    }

    public bool IsVisible()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        return IsWindowVisible(hwnd);
    }

    private void RunOnUiThread(Action action)
    {
        if (_window.DispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            _window.DispatcherQueue.TryEnqueue(() => action());
        }
    }

    private void RunOnUiThread(Func<Task> action)
    {
        if (_window.DispatcherQueue.HasThreadAccess)
        {
            _ = action();
        }
        else
        {
            _window.DispatcherQueue.TryEnqueue(async () => await action());
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dashboard.Runtime.PropertyChanged -= Runtime_PropertyChanged;
        if (_themeRoot is not null)
        {
            _themeRoot.ActualThemeChanged -= ThemeRoot_ActualThemeChanged;
        }
        if (_dpiChangedSubscribed && _trayIcon is not null)
        {
            _trayIcon.TrayIcon.MessageWindow.DpiChanged -= TrayIcon_DpiChanged;
            _dpiChangedSubscribed = false;
        }
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

}
