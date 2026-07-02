namespace ClashSuki.Utilities;

public static class ConfigTextCodec
{
    public static IReadOnlyList<string> ParseLines(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static string FormatLines(IEnumerable<string>? values) =>
        values is null
            ? string.Empty
            : string.Join(Environment.NewLine, Normalize(values));

    public static IReadOnlyList<string> ParseValues(string? value, params char[] separators)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var effectiveSeparators = separators
            .Append('\n')
            .Distinct()
            .ToArray();
        return value
            .ReplaceLineEndings("\n")
            .Split(effectiveSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static string FormatValues(IEnumerable<string>? values, char separator = ',') =>
        values is null
            ? string.Empty
            : string.Join(separator, Normalize(values));

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseMapping(string? value)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ParseLines(value))
        {
            var separatorIndex = line.IndexOfAny(['=', ':']);
            if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
            {
                throw new FormatException($"“{line}”不是有效的键值项，应使用“名称=值”。");
            }

            var key = line[..separatorIndex].Trim();
            var values = ParseValues(line[(separatorIndex + 1)..], ',');
            if (values.Count == 0)
            {
                throw new FormatException($"“{key}”至少需要一个值。");
            }

            result[key] = values;
        }

        return result;
    }

    public static string FormatMapping(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? values)
    {
        if (values is null || values.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            values.Select(pair => $"{pair.Key}={FormatValues(pair.Value)}"));
    }

    private static IEnumerable<string> Normalize(IEnumerable<string> values) =>
        values
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!);
}
