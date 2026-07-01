using CommunityToolkit.Mvvm.ComponentModel;
using ClashSuki.Services;
using ClashSuki.Utilities;
using ClashSuki.ViewModels;

namespace ClashSuki.Stores;

public sealed partial class LogStore : ObservableObject
{
    public BoundedObservableCollection<LogItemViewModel> AppItems { get; } = new(1000);
    public BoundedObservableCollection<LogItemViewModel> MihomoItems { get; } = new(1000);

    [ObservableProperty] private bool isPaused;

    public void AddApp(string level, string message, string source = LogSources.Application)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var normalizedLevel = NormalizeLevel(level);
        DiagnosticLog.WriteApp(NormalizeSource(source), normalizedLevel, NormalizeMessage(message));
    }

    public void AddPersistedApp(DiagnosticLogEntry entry)
    {
        if (IsPaused)
        {
            return;
        }

        AppItems.AddNewest(new LogItemViewModel
        {
            Source = entry.Source,
            Level = entry.Level,
            Timestamp = entry.Timestamp,
            Message = entry.Message,
            Details = entry.Details
        });
    }

    public void AddMihomo(string level, string message, string source = "MIHOMO")
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var parsed = ParseMihomoLog(NormalizeMessage(message));
        var normalizedMessage = parsed.Message;
        var normalizedLevel = NormalizeLevel(parsed.Level ?? level);
        DiagnosticLog.WriteMihomo(NormalizeSource(source), normalizedLevel, normalizedMessage);
        if (IsPaused)
        {
            return;
        }

        MihomoItems.AddNewest(new LogItemViewModel
        {
            Source = NormalizeSource(source),
            Level = normalizedLevel,
            Timestamp = DateTimeOffset.Now,
            Message = normalizedMessage,
            Details = ""
        });
    }

    public void AddMihomoBatch(IReadOnlyList<(string Level, string Message)> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var items = new List<LogItemViewModel>(entries.Count);
        foreach (var (level, message) in entries)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            var parsed = ParseMihomoLog(NormalizeMessage(message));
            var normalizedMessage = parsed.Message;
            var normalizedLevel = NormalizeLevel(parsed.Level ?? level);
            DiagnosticLog.WriteMihomo("MIHOMO", normalizedLevel, normalizedMessage);
            if (!IsPaused)
            {
                items.Add(new LogItemViewModel
                {
                    Source = "内核",
                    Level = normalizedLevel,
                    Timestamp = DateTimeOffset.Now,
                    Message = normalizedMessage,
                    Details = ""
                });
            }
        }

        MihomoItems.AddRangeNewest(items);
    }

    public void Clear()
    {
        AppItems.Clear();
        MihomoItems.Clear();
    }

    private static string NormalizeLevel(string? level)
    {
        var value = (level ?? "INFO").Trim().ToUpperInvariant();
        return value switch
        {
            "WARNING" => "WARN",
            "TRACE" => "DEBUG",
            "CORE" => "INFO",
            "" => "INFO",
            _ => value
        };
    }

    private static string NormalizeSource(string? source)
    {
        var value = (source ?? "APP").Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(value) ? "APP" : value;
    }

    private static string NormalizeMessage(string message) =>
        message.ReplaceLineEndings(" ").Trim();

    private static (string? Level, string Message) ParseMihomoLog(string message)
    {
        var level = TryReadLogfmtValue(message, "level");
        var parsedMessage = TryReadLogfmtValue(message, "msg");
        if (!string.IsNullOrWhiteSpace(parsedMessage))
        {
            return (level, parsedMessage);
        }

        return (level ?? ExtractBracketLevel(message), message);
    }

    private static string? TryReadLogfmtValue(string text, string key)
    {
        var prefix = $"{key}=";
        var index = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = index + prefix.Length;
        if (start >= text.Length)
        {
            return "";
        }

        if (text[start] != '"')
        {
            var end = text.IndexOf(' ', start);
            return end < 0 ? text[start..].Trim() : text[start..end].Trim();
        }

        var builder = new System.Text.StringBuilder();
        var escaping = false;
        for (var i = start + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (escaping)
            {
                builder.Append(c switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => c
                });
                escaping = false;
                continue;
            }

            if (c == '\\')
            {
                escaping = true;
                continue;
            }

            if (c == '"')
            {
                return builder.ToString().Trim();
            }

            builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    private static string? ExtractBracketLevel(string message)
    {
        var lower = message.ToLowerInvariant();
        foreach (var level in new[] { "debug", "info", "warning", "warn", "error" })
        {
            if (lower.Contains($"[{level}]", StringComparison.Ordinal))
            {
                return level;
            }
        }

        return null;
    }
}
