using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace ClashSuki.Utilities;

public static class FlagEmojiText
{
    private static readonly FontFamily FlagFont =
        new("ms-appx:///Assets/Fonts/TwemojiMozilla.ttf#Twemoji Mozilla");

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(string),
        typeof(FlagEmojiText),
        new PropertyMetadata("", OnTextChanged));

    public static string GetText(DependencyObject element) =>
        (string)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string value) =>
        element.SetValue(TextProperty, value);

    private static void OnTextChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not TextBlock textBlock)
        {
            return;
        }

        BuildInlines(textBlock, args.NewValue as string ?? "");
    }

    private static void BuildInlines(TextBlock textBlock, string text)
    {
        textBlock.Inlines.Clear();
        var plainTextStart = 0;
        var index = 0;

        while (index < text.Length)
        {
            var first = Rune.GetRuneAt(text, index);
            var nextIndex = index + first.Utf16SequenceLength;
            if (IsRegionalIndicator(first) && nextIndex < text.Length)
            {
                var second = Rune.GetRuneAt(text, nextIndex);
                if (IsRegionalIndicator(second))
                {
                    AddPlainRun(textBlock, text[plainTextStart..index]);
                    var flagEnd = nextIndex + second.Utf16SequenceLength;
                    textBlock.Inlines.Add(new Run
                    {
                        Text = text[index..flagEnd],
                        FontFamily = FlagFont
                    });
                    index = flagEnd;
                    plainTextStart = flagEnd;
                    continue;
                }
            }

            index = nextIndex;
        }

        AddPlainRun(textBlock, text[plainTextStart..]);
    }

    private static void AddPlainRun(TextBlock textBlock, string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            textBlock.Inlines.Add(new Run { Text = text });
        }
    }

    private static bool IsRegionalIndicator(Rune rune) =>
        rune.Value is >= 0x1F1E6 and <= 0x1F1FF;
}
