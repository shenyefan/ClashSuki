using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Jint;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ClashSuki.Services;

public sealed record OverrideApplyResult(int EnabledCount, int YamlCount, int ScriptCount);

public sealed class OverrideRuntimeService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly OverrideService _service;

    public OverrideRuntimeService(OverrideService service) => _service = service;

    public async Task<(string Yaml, OverrideApplyResult Result)> BuildAsync(
        string sourceConfigPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceConfigPath))
        {
            throw new FileNotFoundException("当前运行配置不存在，无法应用覆写。", sourceConfigPath);
        }

        var sourceYaml = await File.ReadAllTextAsync(sourceConfigPath, cancellationToken);
        var config = ParseYamlRoot(sourceYaml);
        var overrideConfig = await _service.LoadAsync(cancellationToken);
        var enabledEntries = overrideConfig.Items
            .Where(item => item.Enabled)
            .ToArray();

        var yamlCount = 0;
        var scriptCount = 0;
        foreach (var entry in enabledEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await _service.ReadContentAsync(entry, cancellationToken);
            if (entry.Ext.Equals("js", StringComparison.OrdinalIgnoreCase))
            {
                config = await ApplyJavaScriptAsync(entry, content, config, cancellationToken);
                scriptCount++;
            }
            else
            {
                DeepMerge(config, ParseYamlRoot(content));
                yamlCount++;
            }
        }

        var serializer = new SerializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        return (serializer.Serialize(config), new OverrideApplyResult(enabledEntries.Length, yamlCount, scriptCount));
    }

    private async Task<Dictionary<string, object?>> ApplyJavaScriptAsync(
        OverrideEntry entry,
        string script,
        Dictionary<string, object?> config,
        CancellationToken cancellationToken)
    {
        var logs = new List<string>();
        try
        {
            var engine = new Engine(options =>
            {
                options.Strict();
                options.TimeoutInterval(TimeSpan.FromSeconds(3));
                options.LimitRecursion(128);
                options.MaxStatements(100_000);
            });

            engine.SetValue("console", new ScriptConsole(logs));
            engine.SetValue("__inputConfigJson", JsonSerializer.Serialize(config, JsonOptions));
            engine.Execute(
                """
                const config = JSON.parse(__inputConfigJson);
                """);
            engine.Execute(script);
            engine.Execute(
                """
                if (typeof main !== 'function') {
                    throw new Error('JS 覆写必须导出 main(config) 函数。');
                }
                const __overrideResult = main(config);
                if (__overrideResult === null || typeof __overrideResult !== 'object' || Array.isArray(__overrideResult)) {
                    throw new Error('main(config) 必须返回配置对象。');
                }
                const __overrideResultJson = JSON.stringify(__overrideResult);
                """);

            var resultJson = engine.GetValue("__overrideResultJson").AsString();
            await WriteScriptLogAsync(entry, logs, null, cancellationToken);
            return ParseJsonRoot(resultJson);
        }
        catch (Exception ex)
        {
            await WriteScriptLogAsync(entry, logs, ex, cancellationToken);
            throw new InvalidOperationException($"JS 覆写执行失败，名称: {entry.Name}", ex);
        }
    }

    private async Task WriteScriptLogAsync(
        OverrideEntry entry,
        IReadOnlyList<string> logs,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {entry.Name}");
        foreach (var line in logs)
        {
            builder.AppendLine(line);
        }

        if (exception is not null)
        {
            builder.AppendLine($"ERROR {exception.GetType().Name}: {exception.Message}");
        }

        var path = _service.GetLogPath(entry);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken);
    }

    private static Dictionary<string, object?> ParseYamlRoot(string yaml)
    {
        var deserializer = new DeserializerBuilder().Build();
        var value = string.IsNullOrWhiteSpace(yaml)
            ? new Dictionary<object, object?>()
            : deserializer.Deserialize<object?>(yaml);
        return NormalizeYaml(value) as Dictionary<string, object?>
               ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> ParseJsonRoot(string json)
    {
        using var document = JsonDocument.Parse(json);
        return NormalizeJson(document.RootElement) as Dictionary<string, object?>
               ?? throw new InvalidOperationException("JS 覆写返回值不是有效的配置对象。");
    }

    private static object? NormalizeYaml(object? value)
    {
        return value switch
        {
            IDictionary<object, object> map => map.ToDictionary(
                item => Convert.ToString(item.Key, CultureInfo.InvariantCulture) ?? "",
                item => NormalizeYaml(item.Value),
                StringComparer.OrdinalIgnoreCase),
            IDictionary<string, object?> map => new Dictionary<string, object?>(
                map.Select(item => new KeyValuePair<string, object?>(item.Key, NormalizeYaml(item.Value))),
                StringComparer.OrdinalIgnoreCase),
            IEnumerable list when value is not string => list.Cast<object?>().Select(NormalizeYaml).ToArray(),
            _ => value
        };
    }

    private static object? NormalizeJson(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => NormalizeJson(property.Value),
                StringComparer.OrdinalIgnoreCase),
            JsonValueKind.Array => element.EnumerateArray().Select(NormalizeJson).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => null
        };
    }

    private static void DeepMerge(Dictionary<string, object?> target, Dictionary<string, object?> patch)
    {
        foreach (var (key, value) in patch)
        {
            if (value is Dictionary<string, object?> patchMap &&
                target.TryGetValue(key, out var existing) &&
                existing is Dictionary<string, object?> targetMap)
            {
                DeepMerge(targetMap, patchMap);
                continue;
            }

            target[key] = value;
        }
    }

    private sealed class ScriptConsole
    {
        private readonly List<string> _logs;

        public ScriptConsole(List<string> logs) => _logs = logs;

        public void log(object? value = null) => Add("LOG", value);
        public void info(object? value = null) => Add("INFO", value);
        public void warn(object? value = null) => Add("WARN", value);
        public void error(object? value = null) => Add("ERROR", value);
        public void debug(object? value = null) => Add("DEBUG", value);

        private void Add(string level, object? value) =>
            _logs.Add($"{level} {Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""}");
    }
}
