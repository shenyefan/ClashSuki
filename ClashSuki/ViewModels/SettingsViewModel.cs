using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClashSuki.Services;
using ClashSuki.Utilities;
using Microsoft.UI.Xaml;
using System.Diagnostics;

namespace ClashSuki.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppCoordinator _coordinator;
    private readonly GistSyncService _gistSync = new();

    [ObservableProperty] private AppSettings settings = new();
    [ObservableProperty] private bool isLoading = true;
    [ObservableProperty] private int themeIndex;
    [ObservableProperty] private int backdropIndex;
    [ObservableProperty] private int priorityIndex = 2;
    [ObservableProperty] private int envTypeIndex;
    [ObservableProperty] private double subscriptionTimeout;
    [ObservableProperty] private double delayTestConcurrency;
    [ObservableProperty] private double delayTestTimeout;
    [ObservableProperty] private string pauseSsidText = "";

    public SettingsViewModel(AppCoordinator coordinator)
    {
        _coordinator = coordinator;
        Runtime = coordinator.Runtime;
    }

    public Stores.RuntimeStore Runtime { get; }

    public async Task LoadAsync()
    {
        Settings = await AppSettingsService.LoadAsync();
        Settings.AutoRun = await WindowsAutoRunService.IsEnabledAsync();
        ThemeIndex = ThemeToIndex(Settings.Theme);
        BackdropIndex = BackdropToIndex(Settings.Backdrop);
        PriorityIndex = PriorityToIndex(Settings.MihomoCpuPriority);
        EnvTypeIndex = EnvTypeToIndex(Settings.EnvType);
        SubscriptionTimeout = Settings.SubscriptionTimeout;
        DelayTestConcurrency = Settings.DelayTestConcurrency;
        DelayTestTimeout = Settings.DelayTestTimeout;
        PauseSsidText = ConfigTextCodec.FormatLines(Settings.PauseSsids);
        IsLoading = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var previous = await AppSettingsService.LoadAsync();
        var newTheme = IndexToTheme(ThemeIndex);
        var newBackdrop = IndexToBackdrop(BackdropIndex);
        var themeChanged = !string.Equals(Settings.Theme, newTheme, StringComparison.OrdinalIgnoreCase);
        var backdropChanged = !string.Equals(Settings.Backdrop, newBackdrop, StringComparison.OrdinalIgnoreCase);

        Settings.Theme = newTheme;
        Settings.Backdrop = newBackdrop;
        Settings.MihomoCpuPriority = IndexToPriority(PriorityIndex);
        Settings.EnvType = IndexToEnvType(EnvTypeIndex);
        Settings.SubscriptionTimeout = NormalizeInt(SubscriptionTimeout, 1, 600, 30);
        Settings.DelayTestConcurrency = NormalizeInt(DelayTestConcurrency, 1, 100, 10);
        Settings.DelayTestTimeout = NormalizeInt(DelayTestTimeout, 1000, 60000, 5000);
        Settings.PauseSsids = ConfigTextCodec.ParseLines(PauseSsidText).ToList();
        if (string.IsNullOrWhiteSpace(Settings.DelayTestUrl))
        {
            Settings.DelayTestUrl = "https://www.gstatic.com/generate_204";
        }

        try
        {
            await AppSettingsService.SaveAsync(Settings);
            await WindowsAutoRunService.SetEnabledAsync(Settings.AutoRun);
            await _coordinator.ApplySavedSettingsSideEffectsAsync();

            if (themeChanged)
            {
                App.CurrentWindow?.ApplyTheme(Settings.Theme);
            }

            if (backdropChanged)
            {
                App.CurrentWindow?.ApplyBackdrop(Settings.Backdrop);
            }

            Runtime.Notifications.Success("设置已保存。", source: LogSources.Settings);
        }
        catch (Exception ex)
        {
            try
            {
                await AppSettingsService.SaveAsync(previous);
                await WindowsAutoRunService.SetEnabledAsync(previous.AutoRun);
            }
            catch (Exception rollbackEx)
            {
                DiagnosticLog.WriteAppException("SETTINGS-ROLLBACK", rollbackEx);
            }

            Settings = previous;
            SyncEditorState(previous);
            App.CurrentWindow?.ApplyTheme(previous.Theme);
            App.CurrentWindow?.ApplyBackdrop(previous.Backdrop);
            try
            {
                await _coordinator.ApplySavedSettingsSideEffectsAsync();
            }
            catch (Exception rollbackEx)
            {
                DiagnosticLog.WriteAppException("SETTINGS-SIDE-EFFECT-ROLLBACK", rollbackEx);
            }

            Runtime.Notifications.Error(
                $"设置保存失败：{ex.Message}",
                source: LogSources.Settings,
                exception: ex);
        }
    }

    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        Settings = new AppSettings();
        SyncEditorState(Settings);
        await SaveAsync();
    }

    [RelayCommand]
    private Task CreateBackupAsync() =>
        ExecuteAsync(async () =>
        {
            var path = await BackupService.CreateBackupAsync();
            Runtime.Notifications.Success(
                $"备份已创建：{Path.GetFileName(path)}",
                source: LogSources.Settings);
        }, "创建备份", LogSources.Settings);

    [RelayCommand]
    private Task RestoreLatestBackupAsync() =>
        ExecuteAsync(async () =>
        {
            await BackupService.RestoreLatestAsync();
            await LoadAsync();
            await ApplyRestoredRuntimeStateAsync();
            Runtime.Notifications.Success(
                "已恢复最新备份，建议重启应用。",
                source: LogSources.Settings);
        }, "恢复备份", LogSources.Settings);

    [RelayCommand]
    private void OpenBackupDirectory() =>
        Execute(() =>
        {
            Directory.CreateDirectory(BackupService.BackupDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = BackupService.BackupDirectory,
                UseShellExecute = true
            });
            Runtime.Notifications.Info(
                "备份目录已打开。",
                source: LogSources.Settings,
                writeLog: false);
        }, "打开备份目录", LogSources.Settings);

    [RelayCommand]
    private Task ResetSoftwareAsync() =>
        ExecuteAsync(async () =>
        {
            await BackupService.CreateBackupAsync();
            await AppSettingsService.SaveAsync(new AppSettings());
            await WindowsAutoRunService.SetEnabledAsync(false);
            AppSettingsService.InvalidateCache();
            await LoadAsync();
            await ApplyRestoredRuntimeStateAsync();
            Runtime.Notifications.Success(
                "软件设置已重置，数据已自动备份。",
                source: LogSources.Settings);
        }, "重置软件", LogSources.Settings);

    private async Task ApplyRestoredRuntimeStateAsync()
    {
        await WindowsAutoRunService.SetEnabledAsync(Settings.AutoRun);
        App.CurrentWindow?.ApplyTheme(Settings.Theme);
        App.CurrentWindow?.ApplyBackdrop(Settings.Backdrop);
        await _coordinator.ReloadRestoredStateAsync();
    }

    [RelayCommand]
    private Task SyncGistAsync() =>
        ExecuteAsync(async () =>
        {
            var gistId = await _gistSync.SyncRuntimeConfigAsync(Settings, CancellationToken.None);
            Settings.GistId = gistId;
            await AppSettingsService.SaveAsync(Settings);
            Runtime.Notifications.Success(
                "运行时配置已同步到 Gist。",
                source: LogSources.Gist);
        }, "同步运行时配置到 Gist", LogSources.Gist);

    [RelayCommand]
    private Task GenerateAgeKeyAsync() =>
        ExecuteAsync(async () =>
        {
            var keyPair = await _gistSync.GenerateAgeKeyPairAsync(CancellationToken.None);
            Settings.GistAgeSecretKey = keyPair.SecretKey;
            Settings.GistAgeRecipient = keyPair.Recipient;
            OnPropertyChanged(nameof(Settings));
            Runtime.Notifications.Success("Age 密钥对已生成。", source: LogSources.Gist);
        }, "生成 Age 密钥对", LogSources.Gist);

    [RelayCommand]
    private async Task ExitAppAsync()
    {
        if (Application.Current is App app)
        {
            await app.ShutdownAsync();
        }

        Application.Current.Exit();
    }

    [RelayCommand]
    public async Task SetThemeAsync(string? theme)
    {
        if (string.IsNullOrWhiteSpace(theme)) return;
        if (string.Equals(Settings.Theme, theme, StringComparison.OrdinalIgnoreCase))
        {
            ThemeIndex = ThemeToIndex(theme);
            return;
        }

        var previous = Settings.Theme;
        try
        {
            ThemeIndex = ThemeToIndex(theme);
            await SaveAsync();
        }
        catch
        {
            Settings.Theme = previous;
            ThemeIndex = ThemeToIndex(previous);
            OnPropertyChanged(nameof(Settings));
            throw;
        }
    }

    public async Task SetBackdropAsync(string? backdrop)
    {
        if (string.IsNullOrWhiteSpace(backdrop)) return;
        if (string.Equals(Settings.Backdrop, backdrop, StringComparison.OrdinalIgnoreCase))
        {
            BackdropIndex = BackdropToIndex(backdrop);
            return;
        }

        var previous = Settings.Backdrop;
        try
        {
            BackdropIndex = BackdropToIndex(backdrop);
            await SaveAsync();
        }
        catch
        {
            Settings.Backdrop = previous;
            BackdropIndex = BackdropToIndex(previous);
            OnPropertyChanged(nameof(Settings));
            throw;
        }
    }

    [RelayCommand]
    private async Task ToggleCloseToTrayAsync() => await SetCloseToTrayAsync(!Settings.CloseToTray);

    public async Task SetCloseToTrayAsync(bool value)
    {
        if (Settings.CloseToTray == value) return;
        var previous = Settings.CloseToTray;
        try
        {
            Settings.CloseToTray = value;
            await SaveAsync();
        }
        catch
        {
            Settings.CloseToTray = previous;
            OnPropertyChanged(nameof(Settings));
            throw;
        }
    }

    public async Task SetAutoCloseConnectionAsync(bool value)
    {
        if (Settings.AutoCloseConnection == value) return;
        var previous = Settings.AutoCloseConnection;
        try
        {
            Settings.AutoCloseConnection = value;
            await SaveAsync();
        }
        catch
        {
            Settings.AutoCloseConnection = previous;
            OnPropertyChanged(nameof(Settings));
            throw;
        }
    }

    private static int ThemeToIndex(string? theme) => theme?.ToLowerInvariant() switch
    {
        "light" => 1,
        "dark" => 2,
        _ => 0
    };

    private static int BackdropToIndex(string? backdrop) => backdrop?.ToLowerInvariant() switch
    {
        "acrylic" => 1,
        "none" => 2,
        _ => 0
    };

    private static string IndexToTheme(int index) => index switch
    {
        1 => "light",
        2 => "dark",
        _ => "system"
    };

    private static string IndexToBackdrop(int index) => index switch
    {
        1 => "acrylic",
        2 => "none",
        _ => "mica"
    };

    private static int PriorityToIndex(string? priority) => priority?.ToLowerInvariant() switch
    {
        "idle" => 0,
        "below_normal" => 1,
        "above_normal" => 3,
        "high" => 4,
        "real_time" => 5,
        _ => 2
    };

    private static string IndexToPriority(int index) => index switch
    {
        0 => "idle",
        1 => "below_normal",
        3 => "above_normal",
        4 => "high",
        5 => "real_time",
        _ => "normal"
    };

    private static int EnvTypeToIndex(string? type) => type?.ToLowerInvariant() switch
    {
        "cmd" => 1,
        "bash" => 2,
        "fish" => 3,
        "nushell" => 4,
        _ => 0
    };

    private static string IndexToEnvType(int index) => index switch
    {
        1 => "cmd",
        2 => "bash",
        3 => "fish",
        4 => "nushell",
        _ => "powershell"
    };

    private async Task ExecuteAsync(Func<Task> operation, string action, string source)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(ex.Message, $"{action}失败", source, ex);
        }
    }

    private void Execute(Action operation, string action, string source)
    {
        try
        {
            operation();
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(ex.Message, $"{action}失败", source, ex);
        }
    }

    private static int NormalizeInt(double value, int min, int max, int fallback)
    {
        if (!double.IsFinite(value))
        {
            return fallback;
        }

        return Math.Clamp((int)Math.Round(value), min, max);
    }

    private void SyncEditorState(AppSettings value)
    {
        ThemeIndex = ThemeToIndex(value.Theme);
        BackdropIndex = BackdropToIndex(value.Backdrop);
        PriorityIndex = PriorityToIndex(value.MihomoCpuPriority);
        EnvTypeIndex = EnvTypeToIndex(value.EnvType);
        SubscriptionTimeout = value.SubscriptionTimeout;
        DelayTestConcurrency = value.DelayTestConcurrency;
        DelayTestTimeout = value.DelayTestTimeout;
        PauseSsidText = ConfigTextCodec.FormatLines(value.PauseSsids);
    }
}
