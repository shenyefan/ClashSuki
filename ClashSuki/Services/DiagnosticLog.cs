using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace ClashSuki.Services;

public static class DiagnosticLog
{
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly ConcurrentDictionary<string, long> ThrottledEntries = new(StringComparer.Ordinal);

    public static string AppLogPath => Path.Combine(AppPaths.LogDirectory, "app.log");
    public static string MihomoLogPath => Path.Combine(AppPaths.LogDirectory, "mihomo.log");

    public static event Action<DiagnosticLogEntry>? AppEntryWritten;

    public static void WriteApp(string source, string message) =>
        Write(AppLogPath, source, "INFO", message, null, AppEntryWritten);

    public static void WriteApp(string source, string level, string message) =>
        Write(AppLogPath, source, level, message, null, AppEntryWritten);

    public static void WriteMihomo(string source, string message) =>
        Write(MihomoLogPath, source, "INFO", message, null);

    public static void WriteMihomo(string source, string level, string message) =>
        Write(MihomoLogPath, source, level, message, null);

    private static void Write(
        string path,
        string source,
        string level,
        string message,
        string? details,
        Action<DiagnosticLogEntry>? entryWritten = null)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            var entry = new DiagnosticLogEntry(
                DateTimeOffset.Now,
                NormalizeLevel(level),
                NormalizeSource(source),
                NormalizeMessage(message),
                NormalizeDetails(details));
            var output = new StringBuilder()
                .Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [").Append(entry.Level)
                .Append("] [").Append(entry.Source)
                .Append("] ").Append(entry.Message)
                .AppendLine();
            if (!string.IsNullOrWhiteSpace(entry.Details))
            {
                output.Append(entry.Details.TrimEnd()).AppendLine();
            }

            lock (Gate)
            {
                ApplyRetention(path);
                File.AppendAllText(path, output.ToString(), Utf8WithoutBom);
            }

            NotifySubscribers(entryWritten, entry);
        }
        catch
        {
            // Diagnostics must never break app flow.
        }
    }

    public static void WriteAppException(
        string source,
        Exception exception,
        string? context = null,
        string level = "ERROR")
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = string.IsNullOrWhiteSpace(context)
            ? NormalizeMessage(exception.Message)
            : NormalizeMessage(context);
        Write(
            AppLogPath,
            source,
            level,
            message,
            exception.ToString(),
            AppEntryWritten);
    }

    public static void WriteAppExceptionThrottled(
        string key,
        string source,
        Exception exception,
        string context,
        TimeSpan? interval = null,
        string level = "WARN")
    {
        var now = Environment.TickCount64;
        var intervalMilliseconds = (long)(interval ?? TimeSpan.FromSeconds(30)).TotalMilliseconds;
        var previous = ThrottledEntries.GetOrAdd(key, long.MinValue);
        if (previous != long.MinValue && now - previous < intervalMilliseconds)
        {
            return;
        }

        ThrottledEntries[key] = now;
        WriteAppException(source, exception, context, level);
    }

    public static string ReadAppLog() => ReadAll(AppLogPath);

    public static string ReadMihomoLog() => ReadAll(MihomoLogPath);

    private static string ReadAll(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (Exception ex)
        {
            return $"读取日志失败：{ex.Message}";
        }
    }

    private static void ApplyRetention(string path)
    {
        var settings = LoadLogSettings();
        var maxBytes = Math.Max(1, settings.MaxLogFileSizeMb) * 1024L * 1024L;
        if (File.Exists(path) && new FileInfo(path).Length > maxBytes)
        {
            var directory = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            var rotated = Path.Combine(directory, $"{name}-{DateTime.Now:yyyyMMddHHmmss}{extension}");
            File.Move(path, rotated, overwrite: true);
        }

        var cutoff = DateTime.Now.AddDays(-Math.Max(1, settings.MaxLogDays));
        foreach (var file in Directory.EnumerateFiles(AppPaths.LogDirectory, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private static AppSettings LoadLogSettings()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(AppPaths.SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static string RunProcess(string fileName, params string[] args)
    {
        var commandLine = CommandLineFormatter.Format(fileName, args);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return $"无法启动进程 {fileName}。";
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(1500))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    return $"命令执行超时且无法终止：{commandLine}；{ex.Message}";
                }

                return $"命令执行超时：{commandLine}";
            }

            var streams = Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(TimeSpan.FromMilliseconds(500))
                .GetAwaiter()
                .GetResult();
            var output = streams[0] + streams[1];
            return $"命令执行完成：{commandLine}；退出代码={process.ExitCode}；输出={output.Trim()}";
        }
        catch (Exception ex)
        {
            return $"命令执行失败：{commandLine}；{ex.Message}";
        }
    }

    private static string NormalizeLevel(string? level)
    {
        var value = string.IsNullOrWhiteSpace(level) ? "INFO" : level.Trim().ToUpperInvariant();
        return value switch
        {
            "TRACE" => "DEBUG",
            "INFORMATION" => "INFO",
            "WARNING" => "WARN",
            "CRITICAL" => "FATAL",
            "DEBUG" or "INFO" or "WARN" or "ERROR" or "FATAL" => value,
            _ => "INFO"
        };
    }

    private static string NormalizeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return LogSources.Application;
        }

        var value = source.Trim();
        if (value.Any(c => c > 127))
        {
            return value;
        }

        var code = value.ToUpperInvariant();
        if (code.StartsWith("SYSTEM-PROXY", StringComparison.Ordinal)) return LogSources.SystemProxy;
        if (code.StartsWith("CORE", StringComparison.Ordinal) || code.StartsWith("MIHOMO", StringComparison.Ordinal)) return LogSources.Core;
        if (code.StartsWith("TUN", StringComparison.Ordinal)) return LogSources.Tun;
        if (code.StartsWith("SERVICE", StringComparison.Ordinal) || code == "SC") return LogSources.Service;
        if (code.StartsWith("PROFILE", StringComparison.Ordinal)) return LogSources.Subscription;
        if (code.StartsWith("OVERRIDE", StringComparison.Ordinal) || code.StartsWith("PATCH", StringComparison.Ordinal)) return LogSources.Override;
        if (code.StartsWith("PROXY", StringComparison.Ordinal)) return LogSources.Proxy;
        if (code.StartsWith("RULE", StringComparison.Ordinal)) return LogSources.Rule;
        if (code.StartsWith("CONNECTION", StringComparison.Ordinal)) return LogSources.Connection;
        if (code.StartsWith("DNS", StringComparison.Ordinal)) return LogSources.Dns;
        if (code.StartsWith("SNIFF", StringComparison.Ordinal)) return LogSources.Sniffer;
        if (code.StartsWith("RESOURCE", StringComparison.Ordinal) || code.StartsWith("GEO", StringComparison.Ordinal)) return LogSources.Resource;
        if (code.StartsWith("SETTING", StringComparison.Ordinal)) return LogSources.Settings;
        if (code.StartsWith("REALTIME", StringComparison.Ordinal)) return LogSources.Realtime;
        if (code.StartsWith("TRAY", StringComparison.Ordinal)) return LogSources.Tray;
        if (code.StartsWith("NETWORK", StringComparison.Ordinal) || code.StartsWith("SSID", StringComparison.Ordinal)) return LogSources.Network;
        if (code.StartsWith("GIST", StringComparison.Ordinal)) return LogSources.Gist;
        if (code.StartsWith("XAML", StringComparison.Ordinal)) return LogSources.UserInterface;
        if (code.StartsWith("REMOTE", StringComparison.Ordinal)) return LogSources.Resource;
        if (code.StartsWith("FIREWALL", StringComparison.Ordinal) || code.StartsWith("UWP", StringComparison.Ordinal)) return LogSources.Tun;
        if (code.StartsWith("SINGLE-INSTANCE", StringComparison.Ordinal)) return LogSources.Application;
        if (code.StartsWith("EXIT", StringComparison.Ordinal)) return "退出";
        if (code.StartsWith("APP", StringComparison.Ordinal) || code.StartsWith("TASK", StringComparison.Ordinal)) return LogSources.Application;
        return value;
    }

    private static string NormalizeMessage(string? message) =>
        (message ?? "").ReplaceLineEndings(" ").Trim();

    private static string NormalizeDetails(string? details) =>
        (details ?? "").Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    private static void NotifySubscribers(
        Action<DiagnosticLogEntry>? subscribers,
        DiagnosticLogEntry entry)
    {
        if (subscribers is null)
        {
            return;
        }

        foreach (Action<DiagnosticLogEntry> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(entry);
            }
            catch
            {
                // 日志观察者异常不能影响文件日志和业务流程。
            }
        }
    }
}

public sealed record DiagnosticLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string Message,
    string Details = "");
