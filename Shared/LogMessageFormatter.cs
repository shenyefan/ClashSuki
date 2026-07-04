#nullable enable

using System;
using System.Text;

namespace ClashSuki.Shared;

public static class LogMessageFormatter
{
    private static readonly char[] TrailingPunctuation = ['。', '．', '.', '；', ';', '，', ','];

    public static string Normalize(string? message)
    {
        var value = (message ?? "")
            .ReplaceLineEndings(" ")
            .Trim();
        if (value.Length == 0)
        {
            return "";
        }

        value = NormalizeStructuredFields(value)
            .Replace("；", "，", StringComparison.Ordinal)
            .Replace("。", "，", StringComparison.Ordinal)
            .Replace("．", "，", StringComparison.Ordinal);
        return value.TrimEnd().TrimEnd(TrailingPunctuation);
    }

    private static string NormalizeStructuredFields(string value)
    {
        var builder = new StringBuilder(value.Length);
        var tokenStart = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current == '=' && ShouldReplaceEquals(value, tokenStart, index))
            {
                builder.Append(": ");
                while (index + 1 < value.Length && value[index + 1] == ' ')
                {
                    index++;
                }

                continue;
            }

            builder.Append(current);
            if (char.IsWhiteSpace(current) || current is '，' or '；' or ',' or ';')
            {
                tokenStart = index + 1;
            }
        }

        return builder.ToString();
    }

    private static bool ShouldReplaceEquals(string value, int tokenStart, int equalsIndex)
    {
        if (equalsIndex == 0 ||
            value[equalsIndex - 1] is '=' or '?' or '&' ||
            equalsIndex + 1 < value.Length && value[equalsIndex + 1] == '=')
        {
            return false;
        }

        var token = value[tokenStart..equalsIndex].Trim();
        return token.Length > 0 &&
               !token.StartsWith("-", StringComparison.Ordinal) &&
               !token.Contains("://", StringComparison.Ordinal) &&
               !token.Contains('?') &&
               !token.Contains('&');
    }
}
