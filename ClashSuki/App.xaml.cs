using ClashSuki.Services;
using ClashSuki.Stores;
using ClashSuki.Utilities;
using ClashSuki.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClashSuki;

public partial class App : Application
{
    private AppCoordinator? _coordinator;
    private MainWindow? _window;
    private int _xamlErrorNotificationScheduled;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public MainViewModel? ViewModel { get; private set; }
    public static TrayService? TrayService { get; private set; }
    public static MainWindow? CurrentWindow { get; private set; }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        if (Interlocked.Exchange(ref _xamlErrorNotificationScheduled, 1) != 0)
        {
            DiagnosticLog.WriteAppException(LogSources.UserInterface, e.Exception, "界面异常通知已在处理中");
            return;
        }

        var dispatcher = CurrentWindow?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null || !dispatcher.TryEnqueue(() =>
            {
                try
                {
                    ViewModel?.Runtime.Notifications.Error(
                        "界面操作发生异常，本次操作已取消。详细信息已写入程序日志。",
                        "界面异常",
                        LogSources.UserInterface,
                        e.Exception);
                }
                catch (Exception notificationException)
                {
                    DiagnosticLog.WriteAppException("XAML-NOTIFICATION", notificationException);
                }
                finally
                {
                    Interlocked.Exchange(ref _xamlErrorNotificationScheduled, 0);
                }
            }))
        {
            DiagnosticLog.WriteAppException(LogSources.UserInterface, e.Exception, "无法投递界面异常通知");
            Interlocked.Exchange(ref _xamlErrorNotificationScheduled, 0);
        }
    }

    private static void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            DiagnosticLog.WriteAppException("APPDOMAIN-UNHANDLED", exception);
        }
        else
        {
            DiagnosticLog.WriteApp(
                LogSources.Application,
                "ERROR",
                $"应用域发生未知未处理异常：{e.ExceptionObject ?? "无异常信息"}");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.Runtime.Notifications.Error(
                "后台任务发生未处理异常，详细信息已写入程序日志。",
                "后台任务异常",
                LogSources.Application,
                e.Exception);
        }
        else
        {
            DiagnosticLog.WriteAppException(LogSources.Application, e.Exception, "后台任务发生未处理异常");
        }
        e.SetObserved();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        SingleInstanceManager.RegisterActivateHandler(ActivateMainWindow);

        var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        var dispatcher = new UiDispatcher(dispatcherQueue);
        ProxyIconLoader.Initialize(dispatcherQueue);
        var profileService = new ProfileService();
        var runtime = new RuntimeStore();
        var logs = new LogStore();
        var notifications = new AppNotificationService(runtime, dispatcherQueue);
        runtime.AttachNotificationService(notifications);
        _coordinator = new AppCoordinator(
            dispatcher,
            runtime,
            new ProxyStore(),
            new ConnectionStore(),
            new TrafficStatisticsStore(),
            new RuleStore(),
            new ProfileStore(profileService),
            profileService,
            logs);

        ViewModel = new MainViewModel(_coordinator);
        _window = new MainWindow(ViewModel, _coordinator);
        CurrentWindow = _window;

        _ = EnsureWindowAsync(ViewModel);
    }

    private async Task EnsureWindowAsync(MainViewModel viewModel)
    {
        if (_coordinator is null || _window is null)
        {
            return;
        }

        await PrepareStartupStateAsync(viewModel);
        await ApplySavedAppearanceAsync();

        var settings = await AppSettingsService.LoadAsync();
        if (settings.SilentStart)
        {
            await _window.PrepareForSilentStartAsync();
        }
        else
        {
            await _window.ActivateWhenContentReadyAsync();
            await _window.RevealBackdropAsync();
        }

        TrayService = new TrayService(_window, viewModel.Dashboard);
        TrayService.Initialize();

        if (settings.SilentStart)
        {
            TrayService.HideWindow();
        }

        _ = StartCoreAsync(viewModel);
    }

    internal static void ActivateMainWindow()
    {
        if (TrayService is not null)
        {
            TrayService.ShowWindow();
            return;
        }

        if (CurrentWindow is MainWindow window)
        {
            window.DispatcherQueue.TryEnqueue(async () => await window.PresentAsync());
        }
    }

    private async Task PrepareStartupStateAsync(MainViewModel viewModel)
    {
        if (_coordinator is null)
        {
            return;
        }

        try
        {
            await _coordinator.PrepareForWindowAsync();
        }
        catch (Exception ex)
        {
            viewModel.Runtime.Notifications.Error(
                $"启动准备失败：{ex.Message}",
                source: LogSources.Application,
                exception: ex);
        }
    }

    private async Task ApplySavedAppearanceAsync()
    {
        if (_window is null)
        {
            return;
        }

        try
        {
            var settings = await AppSettingsService.LoadAsync();
            _window.ApplyTheme(settings.Theme);
            _window.ApplyBackdrop(settings.Backdrop, shield: true);
        }
        catch (Exception ex)
        {
            ViewModel?.Runtime.Notifications.Warning(
                ex.Message,
                "外观加载失败",
                LogSources.UserInterface,
                ex);
        }
    }

    private static async Task StartCoreAsync(MainViewModel viewModel)
    {
        try
        {
            await viewModel.StartCommand.ExecuteAsync(null);
        }
        catch (OperationCanceledException)
        {
            // Application shutdown cancels startup work and is not an error.
        }
        catch (Exception ex)
        {
            viewModel.Runtime.Notifications.Error(
                $"启动失败：{ex.Message}",
                source: LogSources.Core,
                exception: ex);
        }
    }

    internal async Task ShutdownAsync()
    {
        if (_coordinator is null)
        {
            return;
        }

        await _coordinator.DisposeAsync();
        _coordinator = null;
        CurrentWindow = null;
    }
}
