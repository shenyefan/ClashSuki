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
    private const string LogoIconPath = "Branding/logo.ico";
    private const string LogoImagePath = "Branding/logo.png";
    private const string TrayDefaultIconPath = "Tray/default.ico";
    private const string TraySystemProxyIconPath = "Tray/system-proxy.ico";
    private const string TrayTunIconPath = "Tray/tun.ico";
    private const string TraySystemProxyTunIconPath = "Tray/system-proxy-tun.ico";

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
            AppPaths.AssetsDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到应用图标资源", path);
        }

        return path;
    }
}
