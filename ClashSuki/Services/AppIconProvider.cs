using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClashSuki.Services;

public enum TrayIconState
{
    Default,
    SystemProxy,
    Tun,
    SystemProxyAndTun
}

public static class AppIconProvider
{
    private const string AssetDirectory = "Assets/Img";
    private const string LogoIconFile = "logo.ico";
    private const string LogoImageFile = "logo.png";

    public static void ApplyWindowIcon(AppWindow appWindow)
    {
        ArgumentNullException.ThrowIfNull(appWindow);
        appWindow.SetIcon(GetAssetPath(LogoIconFile));
    }

    public static ImageIconSource CreateTitleBarIcon() =>
        new()
        {
            ImageSource = CreateBitmapImage(LogoImageFile)
        };

    public static BitmapImage CreateTrayIcon(TrayIconState state) =>
        CreateBitmapImage(state switch
        {
            TrayIconState.SystemProxyAndTun => "red.ico",
            TrayIconState.Tun => "green.ico",
            TrayIconState.SystemProxy => "orange.ico",
            _ => LogoIconFile
        });

    private static BitmapImage CreateBitmapImage(string fileName) =>
        new(new Uri($"ms-appx:///{AssetDirectory}/{fileName}"));

    private static string GetAssetPath(string fileName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            AssetDirectory.Replace('/', Path.DirectorySeparatorChar),
            fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到应用图标资源", path);
        }

        return path;
    }
}
