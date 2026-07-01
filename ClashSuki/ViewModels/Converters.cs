using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ClashSuki.Utilities;
using System.Collections;

namespace ClashSuki.ViewModels;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is not Visibility.Visible;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is not true;
}

public sealed class CollectionEmptyToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isEmpty = value switch
        {
            null => true,
            ICollection collection => collection.Count == 0,
            IEnumerable enumerable => !enumerable.Cast<object>().Any(),
            _ => false
        };

        if (Invert)
        {
            isEmpty = !isEmpty;
        }

        return isEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class NumberZeroToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isZero = value switch
        {
            int i => i == 0,
            long l => l == 0,
            double d => Math.Abs(d) < double.Epsilon,
            _ => true
        };

        if (Invert)
        {
            isZero = !isZero;
        }

        return isZero ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isNull = value is null;
        if (Invert)
        {
            isNull = !isNull;
        }

        return isNull ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class ProcessIconElementConverter : IValueConverter
{
    private const double IconSize = 20;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is Uri uri)
        {
            return new BitmapIcon
            {
                UriSource = uri,
                ShowAsMonochrome = false,
                Width = IconSize,
                Height = IconSize
            };
        }

        return new FontIcon
        {
            Glyph = "\uE968",
            FontSize = 14,
            Width = IconSize,
            Height = IconSize,
            Opacity = 0.35
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class DelayBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Formatters.DelayBrush(value as int?);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class LogLevelBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var level = (value as string)?.Trim().ToUpperInvariant();
        var color = level switch
        {
            "ERROR" => Windows.UI.Color.FromArgb(255, 232, 17, 35),
            "WARN" or "WARNING" => Windows.UI.Color.FromArgb(255, 255, 185, 0),
            "INFO" => Windows.UI.Color.FromArgb(255, 0, 120, 212),
            "DEBUG" => Windows.UI.Color.FromArgb(255, 107, 105, 214),
            _ => Windows.UI.Color.FromArgb(255, 128, 128, 128)
        };

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
