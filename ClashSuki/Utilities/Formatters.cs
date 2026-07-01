namespace ClashSuki.Utilities;

using Microsoft.UI.Xaml.Media;

public static class Formatters
{
    public static string FormatSpeed(long value) => $"{FormatBytes(value)}/s";

    public static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = Math.Max(0, (double)value);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{size:0} {units[unit]}" : $"{size:0.##} {units[unit]}";
    }

    public static string Delay(int? delay) => delay switch
    {
        null => "--",
        <= 0 => "超时",
        _ => $"{delay} ms"
    };

    /// <summary>Party 节点延迟按钮文案：未测显示「测速」，超时 timeout，否则仅数字。</summary>
    public static string DelayButton(int? delay) => delay switch
    {
        null => "测速",
        <= 0 => "超时",
        _ => $"{delay}"
    };

    public static Brush DelayBrush(int? delay) => delay switch
    {
        null => DelayBrushes.Primary,
        <= 0 => DelayBrushes.Danger,
        < 500 => DelayBrushes.Success,
        _ => DelayBrushes.Warning
    };
}

public static class DelayBrushes
{
    public static readonly SolidColorBrush Primary = Create(0, 120, 212);
    public static readonly SolidColorBrush Success = Create(16, 124, 16);
    public static readonly SolidColorBrush Warning = Create(255, 140, 0);
    public static readonly SolidColorBrush Danger = Create(232, 17, 35);
    public static readonly SolidColorBrush Transparent = Create(0, 0, 0, 0);
    public static readonly SolidColorBrush SelectedBackground = Create(0, 120, 212, 24);
    public static readonly SolidColorBrush FixedBackground = Create(255, 140, 0, 24);
    public static readonly SolidColorBrush NeutralBackground = Create(128, 128, 128, 16);

    private static SolidColorBrush Create(byte r, byte g, byte b, byte a = 255) =>
        new(Windows.UI.Color.FromArgb(a, r, g, b));
}
