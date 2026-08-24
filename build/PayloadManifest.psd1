@{
    RuntimeAssets = @(
        'Assets\Branding\logo.ico'
        'Assets\Branding\logo.png'
        'Assets\Tray\default.ico'
        'Assets\Tray\system-proxy.ico'
        'Assets\Tray\tun.ico'
        'Assets\Tray\system-proxy-tun.ico'
        'Assets\Age\age.exe'
        'Assets\Age\age-keygen.exe'
        'Assets\Age\LICENSE'
        'Assets\Core\mihomo.exe'
        'Assets\Core\LICENSE'
        'Assets\Fonts\TwemojiMozilla.ttf'
        'Assets\Fonts\TwemojiMozilla.LICENSE.md'
        'Assets\GeoData\Country.mmdb'
        'Assets\GeoData\geoip.dat'
        'Assets\GeoData\geosite.dat'
    )

    ForbiddenRuntimeAssets = @(
        'Assets\Age\age-inspect.exe'
        'Assets\Age\age-plugin-batchpass.exe'
        'Assets\UWP\enableLoopback.exe'
    )
}
