using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ClashSuki.PrivilegedOperations;
using ClashSuki.ServiceContract;
using Windows.Management.Deployment;

namespace ClashSuki.Repair;

internal static class Program
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClashSuki",
        "logs",
        "repair.log");

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (PortableServiceInstaller.IsInstallCommand(args))
            {
                return PortableServiceInstaller.Run(args);
            }

            if (PortableServiceUninstaller.IsUninstallCommand(args))
            {
                return PortableServiceUninstaller.Run(args);
            }

            if (LoopbackExemptionRepair.IsCommand(args))
            {
                return await LoopbackExemptionRepair.RunAsync(args);
            }

            var options = RepairOptions.Parse(args);
            await WaitForProcessExitAsync(options.WaitProcessId, TimeSpan.FromMinutes(2));
            await RegisterPackageAsync(options.PackageFullName);
            StartApplication(options.AppUserModelId);
            WriteLog("INFO", "应用包重新注册完成，已重新启动 ClashSuki");
            return 0;
        }
        catch (Exception ex)
        {
            WriteLog("ERROR", "应用包修复失败", ex.ToString());
            return 1;
        }
    }

    private static async Task WaitForProcessExitAsync(int processId, TimeSpan timeout)
    {
        Process? process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        using (var timeoutSource = new CancellationTokenSource(timeout))
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
    }

    private static async Task RegisterPackageAsync(string packageFullName)
    {
        var packageManager = new PackageManager();
        var result = await packageManager
            .RegisterPackageByFullNameAsync(
                packageFullName,
                Array.Empty<string>(),
                DeploymentOptions.ForceApplicationShutdown);

        if (!result.IsRegistered)
        {
            var detail = string.IsNullOrWhiteSpace(result.ErrorText)
                ? result.ExtendedErrorCode?.Message
                : result.ErrorText;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? "Windows 未能重新注册应用包。"
                    : $"Windows 未能重新注册应用包：{detail}",
                result.ExtendedErrorCode);
        }
    }

    private static void StartApplication(string appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            throw new ArgumentException("应用用户模型 ID 为空。", nameof(appUserModelId));
        }

        object? activationManagerObject = null;
        try
        {
            activationManagerObject = new ApplicationActivationManager();
            var activationManager = (IApplicationActivationManager)activationManagerObject;
            var result = activationManager.ActivateApplication(
                appUserModelId,
                arguments: null,
                ActivateOptions.NoErrorUi,
                out _);
            Marshal.ThrowExceptionForHR(result);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"应用包已修复，但 Windows 无法激活应用“{appUserModelId}”。",
                ex);
        }
        finally
        {
            if (activationManagerObject is not null && Marshal.IsComObject(activationManagerObject))
            {
                _ = Marshal.FinalReleaseComObject(activationManagerObject);
            }
        }
    }

    [Flags]
    private enum ActivateOptions : uint
    {
        None = 0,
        DesignMode = 0x1,
        NoErrorUi = 0x2,
        NoSplashScreen = 0x4
    }

    [ComImport]
    [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            ActivateOptions options,
            out uint processId);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private sealed class ApplicationActivationManager
    {
    }

    internal static void WriteLog(string level, string message, string? details = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(
                LogPath,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] [Repair] {message.ReplaceLineEndings(" ").Trim()}{Environment.NewLine}");
            if (!string.IsNullOrWhiteSpace(details))
            {
                File.AppendAllText(LogPath, details.TrimEnd() + Environment.NewLine);
            }
        }
        catch
        {
            // Repair must not fail only because diagnostics cannot be persisted.
        }
    }

    private sealed record RepairOptions(
        int WaitProcessId,
        string PackageFullName,
        string AppUserModelId)
    {
        public static RepairOptions Parse(IReadOnlyList<string> args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < args.Count; index += 2)
            {
                if (index + 1 >= args.Count || !args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("修复进程参数无效。");
                }

                values[args[index]] = args[index + 1];
            }

            if (!values.TryGetValue("--wait-pid", out var processIdText) ||
                !int.TryParse(processIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) ||
                processId <= 0 ||
                !values.TryGetValue("--package-full-name", out var packageFullName) ||
                string.IsNullOrWhiteSpace(packageFullName) ||
                !values.TryGetValue("--app-user-model-id", out var appUserModelId) ||
                string.IsNullOrWhiteSpace(appUserModelId))
            {
                throw new ArgumentException("修复进程缺少必要参数。");
            }

            return new RepairOptions(processId, packageFullName, appUserModelId);
        }
    }
}
