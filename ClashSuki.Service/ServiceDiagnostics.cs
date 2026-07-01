using System.Text;

namespace ClashSuki.Service;

/// <summary>
/// 提权安装/卸载阶段的诊断日志。写入 %ProgramData%\ClashSuki，
/// 该路径对管理员可写且固定，便于在提权进程崩溃后回看真实异常。
/// </summary>
internal static class ServiceDiagnostics
{
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ClashSuki",
        "service-install.log");

    public static void Write(string operation, string message, string level = "INFO")
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var normalizedLevel = level.Trim().ToUpperInvariant() switch
            {
                "WARNING" => "WARN",
                "CRITICAL" => "FATAL",
                "DEBUG" or "INFO" or "WARN" or "ERROR" or "FATAL" => level.Trim().ToUpperInvariant(),
                _ => "INFO"
            };
            var normalizedOperation = operation.ReplaceLineEndings(" ").Trim();
            var normalizedMessage = message.ReplaceLineEndings(" ").Trim();
            var line =
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{normalizedLevel}] [服务] {normalizedOperation}：{normalizedMessage}{Environment.NewLine}";
            File.AppendAllText(LogPath, line, Encoding.UTF8);
        }
        catch
        {
            // 诊断日志为尽力而为，不能因写日志失败再次抛出。
        }
    }
}
