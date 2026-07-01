using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace ClashSuki.Helpers;

internal static class TitleBarHelper
{
    public static void ApplySystemThemeToCaptionButtons(Window window, ElementTheme currentTheme)
    {
        if (window.AppWindow is null)
        {
            return;
        }

        var foregroundColor = currentTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
        window.AppWindow.TitleBar.ButtonForegroundColor = foregroundColor;
        window.AppWindow.TitleBar.ButtonHoverForegroundColor = foregroundColor;

        var hoverBackgroundColor = currentTheme == ElementTheme.Dark
            ? Color.FromArgb(24, 255, 255, 255)
            : Color.FromArgb(24, 0, 0, 0);
        window.AppWindow.TitleBar.ButtonHoverBackgroundColor = hoverBackgroundColor;
    }
}
