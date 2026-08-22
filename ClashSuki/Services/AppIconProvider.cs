using System.Runtime.InteropServices.WindowsRuntime;
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
    private const string LogoIconPath = "Assets/Branding/logo.ico";
    private const string LogoImagePath = "Assets/Branding/logo.png";
    private const string TrayDefaultIconPath = "Assets/Tray/default.ico";
    private const string TraySystemProxyIconPath = "Assets/Tray/system-proxy.ico";
    private const string TrayTunIconPath = "Assets/Tray/tun.ico";
    private const string TraySystemProxyTunIconPath = "Assets/Tray/system-proxy-tun.ico";

    public static void ApplyWindowIcon(AppWindow appWindow)
    {
        ArgumentNullException.ThrowIfNull(appWindow);
        appWindow.SetIcon(GetAssetPath(LogoIconPath));
    }

    public static ImageIconSource CreateTitleBarIcon() =>
        new()
        {
            ImageSource = LoadBitmapImage(LogoImagePath)
        };

    public static BitmapImage CreateTrayIconSource(TrayIconState state) =>
        new(new Uri(GetAssetPath(GetTrayIconPath(state)), UriKind.Absolute));

    public static void EnsureTrayIconsAvailable()
    {
        foreach (var state in Enum.GetValues<TrayIconState>())
        {
            _ = GetAssetPath(GetTrayIconPath(state));
        }
    }

    private static string GetTrayIconPath(TrayIconState state) =>
        state switch
        {
            TrayIconState.SystemProxyAndTun => TraySystemProxyTunIconPath,
            TrayIconState.Tun => TrayTunIconPath,
            TrayIconState.SystemProxy => TraySystemProxyIconPath,
            _ => TrayDefaultIconPath
        };

    private static BitmapImage LoadBitmapImage(string relativePath)
    {
        var image = new BitmapImage();
        using var fileStream = File.OpenRead(GetAssetPath(relativePath));
        using var randomAccessStream = fileStream.AsRandomAccessStream();
        image.SetSource(randomAccessStream);
        return image;
    }

    private static string GetAssetPath(string relativePath)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到应用图标资源", path);
        }

        return path;
    }
}
