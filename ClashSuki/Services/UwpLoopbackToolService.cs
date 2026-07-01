using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace ClashSuki.Services;

public static class UwpLoopbackToolService
{
    private const string ToolFileName = "enableLoopback.exe";

    public static async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        var toolPath = ResolveToolPath();
        if (toolPath is null)
        {
            throw new FileNotFoundException("未找到 UWP 工具，请确认 Assets\\UWP\\enableLoopback.exe 已随应用打包。");
        }

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startInfo = new ProcessStartInfo
            {
                FileName = toolPath,
                WorkingDirectory = Path.GetDirectoryName(toolPath) ?? AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas"
            };

            try
            {
                Process.Start(startInfo);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException("已取消打开 UWP 工具。", ex, cancellationToken);
            }
        }, cancellationToken);
    }

    private static string? ResolveToolPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "UWP", ToolFileName),
            Path.Combine(AppContext.BaseDirectory, "UWP", ToolFileName),
            Path.Combine(AppContext.BaseDirectory, ToolFileName)
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
