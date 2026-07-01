using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace ClashSuki.Services;

public static class WindowsFirewallService
{
    public static async Task SetupMihomoRulesAsync(CancellationToken cancellationToken = default)
    {
        var rules = ResolveRules();
        if (rules.Count == 0)
        {
            throw new InvalidOperationException("未找到可写入防火墙规则的程序路径。");
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"ClashSuki-Firewall-{Guid.NewGuid():N}.cmd");
        try
        {
            await File.WriteAllTextAsync(scriptPath, BuildScript(rules), cancellationToken);
            await RunElevatedAsync(scriptPath, cancellationToken);
        }
        finally
        {
            try
            {
                if (File.Exists(scriptPath))
                {
                    File.Delete(scriptPath);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAppException(
                    LogSources.Network,
                    ex,
                    $"删除防火墙配置临时脚本失败；路径={scriptPath}",
                    "WARN");
            }
        }
    }

    private static List<(string Name, string Program)> ResolveRules()
    {
        var appPath = Environment.ProcessPath;
        var candidates = new[]
        {
            ("mihomo", AppPaths.ManagedCorePath),
            ("mihomo-alpha", Path.Combine(AppPaths.CoreDirectory, "mihomo-alpha.exe")),
            ("ClashSuki", appPath ?? "")
        };

        return candidates
            .Where(item => !string.IsNullOrWhiteSpace(item.Item2) && File.Exists(item.Item2))
            .Select(item => (item.Item1, item.Item2))
            .ToList();
    }

    private static string BuildScript(IEnumerable<(string Name, string Program)> rules)
    {
        var lines = new List<string> { "@echo off" };
        foreach (var (name, program) in rules)
        {
            var escapedName = name.Replace("\"", "\\\"", StringComparison.Ordinal);
            var escapedProgram = program.Replace("\"", "\\\"", StringComparison.Ordinal);
            lines.Add($"netsh advfirewall firewall delete rule name=\"{escapedName}\" >nul 2>nul");
            lines.Add($"netsh advfirewall firewall add rule name=\"{escapedName}\" dir=in action=allow program=\"{escapedProgram}\" enable=yes profile=any");
            lines.Add("if errorlevel 1 exit /b 1");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static async Task RunElevatedAsync(string scriptPath, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startInfo = new ProcessStartInfo
            {
                FileName = scriptPath,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            try
            {
                using var process = Process.Start(startInfo)
                                  ?? throw new InvalidOperationException("无法启动防火墙配置进程。");
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"防火墙规则写入失败，退出代码：{process.ExitCode}。");
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException("已取消防火墙规则配置。", ex, cancellationToken);
            }
        }, cancellationToken);
    }
}
