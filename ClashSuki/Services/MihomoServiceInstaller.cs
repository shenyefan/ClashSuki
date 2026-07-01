using System.Diagnostics;
using System.ServiceProcess;

namespace ClashSuki.Services;

public static class MihomoServiceInstaller
{
    public const string ServiceName = "ClashSukiService";

    public static bool IsInstalled()
    {
        using var controller = FindController();
        return controller is not null;
    }

    public static bool IsRunning()
    {
        using var controller = FindController();
        return controller?.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
    }

    public static void Install()
    {
        var exePath = ResolveServiceExecutablePath();
        DiagnosticLog.WriteApp("SERVICE-INSTALL", $"开始安装服务；程序路径={exePath}");

        if (IsInstalled())
        {
            DiagnosticLog.WriteApp("SERVICE-INSTALL", "检测到已安装的服务，将先卸载旧服务。");
            Uninstall();
        }

        var binPath = $"\"{exePath}\"";
        var create = RunSc(
            "create",
            ServiceName,
            "binPath=",
            binPath,
            "start=",
            "demand",
            "DisplayName=",
            "ClashSuki Service");
        if (create.ExitCode != 0)
        {
            throw new InvalidOperationException(create.Output);
        }

        _ = RunSc("description", ServiceName, "使用 Windows 服务权限运行 ClashSuki 内核。");
        Start();
    }

    public static void Start()
    {
        using var controller = FindController();
        if (controller is null) return;

        if (controller.Status != ServiceControllerStatus.Running &&
            controller.Status != ServiceControllerStatus.StartPending)
        {
            controller.Start();
            try
            {
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                DiagnosticLog.WriteApp("SERVICE", "WARN", "等待服务启动超时，服务可能仍在启动。");
            }
        }
    }

    public static void Restart()
    {
        Stop();
        Start();
    }

    public static void Uninstall()
    {
        if (!IsInstalled())
        {
            DiagnosticLog.WriteApp("SERVICE-UNINSTALL", "服务尚未安装，无需卸载。");
            return;
        }

        Stop();
        var delete = RunSc("delete", ServiceName);
        if (delete.ExitCode != 0)
        {
            throw new InvalidOperationException(delete.Output);
        }
    }

    public static void Stop()
    {
        using var controller = FindController();
        if (controller is null) return;

        if (controller.Status == ServiceControllerStatus.Running ||
            controller.Status == ServiceControllerStatus.StartPending)
        {
            try
            {
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAppException("SERVICE", ex, "停止服务失败");
                throw;
            }
        }
    }

    private static ServiceController? FindController()
    {
        foreach (var sc in ServiceController.GetServices())
        {
            if (string.Equals(sc.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                return sc;
            }
            sc.Dispose();
        }
        return null;
    }

    private static string ResolveServiceExecutablePath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "ClashSuki.Service.exe"),
            Path.Combine(baseDirectory, "AppX", "ClashSuki.Service.exe")
        };

        var serviceExe = candidates.FirstOrDefault(File.Exists);
        if (serviceExe is not null)
        {
            return serviceExe;
        }

        throw new FileNotFoundException(
            "找不到 ClashSuki.Service.exe，请重新生成 WinUI 3 项目并确认服务程序已复制到输出目录。",
            Path.Combine(baseDirectory, "ClashSuki.Service.exe"));
    }

    private static (int ExitCode, string Output) RunSc(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "sc.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动 sc.exe。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            TryKill(process);
            var timeoutOutput = $"命令执行超时：{CommandLineFormatter.Format("sc.exe", args)}";
            DiagnosticLog.WriteApp("SC", "WARN", timeoutOutput);
            return (-1, timeoutOutput);
        }

        var streams = Task.WhenAll(stdoutTask, stderrTask)
            .WaitAsync(TimeSpan.FromSeconds(1))
            .GetAwaiter()
            .GetResult();
        var stdout = streams[0];
        var stderr = streams[1];
        var output = stdout + stderr;
        DiagnosticLog.WriteApp(
            "SC",
            $"命令={CommandLineFormatter.Format("sc.exe", args)}；退出代码={process.ExitCode}；输出={output.Trim()}");
        return (process.ExitCode, output);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException("SC", ex, "终止已超时的 sc.exe 进程失败");
        }
    }
}
