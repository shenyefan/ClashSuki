using System.Globalization;
using System.Text;

namespace ClashSuki.Utilities;

public static class EmojiText
{
    public static string GetLeadingEmoji(string? value)
    {
        var text = value?.TrimStart();
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var textElement = StringInfo.GetNextTextElement(text);
        return IsEmoji(textElement) ? textElement : "";
    }

    public static string RemoveLeadingEmoji(string? value)
    {
        var text = value?.TrimStart() ?? "";
        var emoji = GetLeadingEmoji(text);
        return string.IsNullOrEmpty(emoji)
            ? value ?? ""
            : text[emoji.Length..].TrimStart();
    }

    private static bool IsEmoji(string textElement)
    {
        var runes = textElement.EnumerateRunes().ToArray();
        if (runes.Length == 0)
        {
            return false;
        }

        if (runes.Any(rune => rune.Value is 0xFE0F or 0x20E3))
        {
            return true;
        }

        return runes[0].Value switch
        {
            >= 0x1F000 and <= 0x1FAFF => true,
            >= 0x2600 and <= 0x27BF => true,
            >= 0x2300 and <= 0x23FF => true,
            >= 0x2B00 and <= 0x2BFF => true,
            0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139 or
                0x3030 or 0x303D or 0x3297 or 0x3299 => true,
            _ => false
        };
    }
}
