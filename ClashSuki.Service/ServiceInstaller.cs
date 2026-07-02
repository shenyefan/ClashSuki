using System.Diagnostics;
using System.ServiceProcess;
using ClashSuki.ServiceContract;

namespace ClashSuki.Service;

internal static class ServiceInstaller
{
    public const string ServiceName = ServiceProtocol.ServiceName;
    private const string DisplayName = "ClashSuki Service";
    private const string Description = "使用 Windows 服务权限运行 ClashSuki 内核。";

    public static void Install()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定服务程序路径。");

        if (IsInstalled())
        {
            Stop();
            Delete();
        }

        var binPath = $"\"{exePath}\"";
        var create = RunSc(
            "create", ServiceName,
            "binPath=", binPath,
            "start=", "demand",
            "DisplayName=", DisplayName);
        if (create.ExitCode != 0)
        {
            throw new InvalidOperationException(create.Output);
        }

        _ = RunSc("description", ServiceName, Description);
        Start();
    }

    public static void Uninstall()
    {
        if (!IsInstalled()) return;
        Stop();
        Delete();
    }

    public static bool IsInstalled()
    {
        using var c = FindController();
        return c is not null;
    }

    public static void Start()
    {
        using var c = FindController();
        if (c is null)
        {
            throw new InvalidOperationException("ClashSuki 服务尚未安装。");
        }

        if (c.Status is not (ServiceControllerStatus.Running or ServiceControllerStatus.StartPending))
        {
            c.Start();
            try
            {
                c.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            }
            catch (System.ServiceProcess.TimeoutException ex)
            {
                ServiceDiagnostics.Write("启动服务", $"等待服务进入运行状态超时；异常={ex.Message}", "WARN");
            }
        }
    }

    public static void Stop()
    {
        using var c = FindController();
        if (c?.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
        {
            c.Stop();
            try
            {
                c.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
            catch (System.ServiceProcess.TimeoutException ex)
            {
                ServiceDiagnostics.Write("停止服务", $"等待服务停止超时；异常={ex.Message}", "WARN");
            }
        }
    }

    private static void Delete()
    {
        var delete = RunSc("delete", ServiceName);
        if (delete.ExitCode != 0)
        {
            throw new InvalidOperationException(delete.Output);
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsInstalled())
            {
                return;
            }

            Thread.Sleep(100);
        }

        throw new System.TimeoutException("等待 ClashSuki 服务删除超时。");
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
        if (!process.WaitForExit(15_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                ServiceDiagnostics.Write("终止服务控制命令", ex.ToString(), "WARN");
            }

            ServiceDiagnostics.Write("执行服务控制命令", $"sc.exe 执行超时；参数={string.Join(' ', args)}", "ERROR");
            return (-1, "sc.exe 执行超时。");
        }

        var output = Task.WhenAll(stdoutTask, stderrTask)
            .WaitAsync(TimeSpan.FromSeconds(2))
            .GetAwaiter()
            .GetResult();
        var stdout = output[0];
        var stderr = output[1];
        return (process.ExitCode, stdout + stderr);
    }
}
