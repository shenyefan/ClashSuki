using Windows.ApplicationModel;
using Microsoft.Win32;

namespace ClashSuki.Services;

public static class WindowsAutoRunService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClashSuki";
    private const string StartupTaskId = "ClashSukiStartup";

    public static void ReconcilePackageRegistration()
    {
        if (!PackageIdentityService.IsPackaged)
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException(
                LogSources.Settings,
                ex,
                "清理旧版开机自启注册表项失败",
                "WARN");
        }
    }

    public static async Task<bool> IsEnabledAsync()
    {
        if (PackageIdentityService.IsPackaged)
        {
            ReconcilePackageRegistration();
            var task = await StartupTask.GetAsync(StartupTaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string value &&
               value.Contains(Environment.ProcessPath ?? AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task SetEnabledAsync(bool enabled)
    {
        if (PackageIdentityService.IsPackaged)
        {
            ReconcilePackageRegistration();
            var task = await StartupTask.GetAsync(StartupTaskId);
            if (!enabled)
            {
                task.Disable();
                return;
            }

            var state = await task.RequestEnableAsync();
            if (state is not StartupTaskState.Enabled and not StartupTaskState.EnabledByPolicy)
            {
                throw new InvalidOperationException(state == StartupTaskState.DisabledByUser
                    ? "开机自启已被系统禁用，请在 Windows 设置的“启动应用”中启用 ClashSuki。"
                    : $"无法启用开机自启，系统状态：{state}。");
            }

            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ClashSuki.exe");
        key.SetValue(ValueName, $"\"{exe}\"");
    }
}
