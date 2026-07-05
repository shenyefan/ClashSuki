using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Jint;
using Microsoft.Win32;

namespace ClashSuki.Services;

public sealed class WindowsSystemProxyService
{
    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string InternetConnectionsKey = InternetSettingsKey + @"\Connections";
    private const int InternetOptionPerConnectionOption = 75;
    private const int InternetOptionRefresh = 37;
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionProxySettingsChanged = 95;
    private const int InternetPerConnFlags = 1;
    private const int InternetPerConnProxyServer = 2;
    private const int InternetPerConnProxyBypass = 3;
    private const int InternetPerConnAutoconfigUrl = 4;
    private const int ProxyTypeDirect = 0x00000001;
    private const int ProxyTypeProxy = 0x00000002;
    private const int ProxyTypeAutoProxyUrl = 0x00000004;
    private const int HwndBroadcast = 0xffff;
    private const int WmSettingChange = 0x001A;
    private const int SmtoAbortIfHung = 0x0002;
    public const string DefaultBypass = "localhost;127.*;192.168.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;<local>";
    public const string DefaultPacScript = """
        function FindProxyForURL(url, host) {
          return "PROXY %proxy-host%:%mixed-port%; SOCKS5 %proxy-host%:%mixed-port%; DIRECT;";
        }
        """;

    public bool IsEnabledFor(int mixedPort) => IsEnabledFor(mixedPort, "127.0.0.1", "manual");

    public bool IsEnabledFor(int mixedPort, AppSettings settings) =>
        IsEnabledFor(mixedPort, settings.SystemProxyHost, settings.SystemProxyMode);

    public bool IsEnabledFor(int mixedPort, string? host, string? mode)
    {
        if (mixedPort <= 0)
        {
            return false;
        }

        if (NormalizeMode(mode) == "auto")
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false);
            var autoConfigUrl = Convert.ToString(key?.GetValue("AutoConfigURL") ?? "");
            return IsOwnPacUrl(autoConfigUrl);
        }

        using (var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false))
        {
            var enabled = Convert.ToInt32(key?.GetValue("ProxyEnable") ?? 0) == 1;
            var server = Convert.ToString(key?.GetValue("ProxyServer") ?? "");
            var expected = BuildManualProxyServer(NormalizeHost(host), mixedPort);
            return enabled && ProxyServerMatches(server, expected);
        }
    }

    public string GetSnapshot()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false);
        var enabled = Convert.ToInt32(key?.GetValue("ProxyEnable") ?? 0);
        var server = Convert.ToString(key?.GetValue("ProxyServer") ?? "");
        var migrateProxy = Convert.ToString(key?.GetValue("MigrateProxy") ?? "");
        var autoDetect = Convert.ToString(key?.GetValue("AutoDetect") ?? "");
        var autoConfig = Convert.ToString(key?.GetValue("AutoConfigURL") ?? "");
        var overrideText = Convert.ToString(key?.GetValue("ProxyOverride") ?? "");
        return $"代理启用: {enabled}，代理服务器: {server}，迁移标记: {migrateProxy}，自动检测: {autoDetect}，PAC 地址: {autoConfig}，绕过列表: {overrideText}";
    }

    public void VerifyEnabled(int mixedPort)
    {
        VerifyEnabled(mixedPort, "127.0.0.1");
    }

    public void VerifyEnabled(int mixedPort, string? host)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false)
                        ?? throw new InvalidOperationException("无法打开 Windows Internet Settings 注册表项。");
        var enabled = Convert.ToInt32(key.GetValue("ProxyEnable") ?? 0) == 1;
        var server = Convert.ToString(key.GetValue("ProxyServer") ?? "");
        var expected = BuildManualProxyServer(NormalizeHost(host), mixedPort);

        if (!enabled)
        {
            throw new InvalidOperationException($"系统代理写入后校验失败，代理启用状态不是 1，当前状态: {GetSnapshot()}");
        }

        if (!ProxyServerMatches(server, expected))
        {
            throw new InvalidOperationException($"系统代理写入后校验失败，代理服务器不是 {expected}，当前状态: {GetSnapshot()}");
        }

        EnsureProxyPortReachable(mixedPort);
    }

    public string GetDetailedDiagnostics(int mixedPort)
    {
        var parts = new[]
        {
            $"WinINET 状态: {GetSnapshot()}",
            $"连接设置: {GetConnectionSettingsSnapshot()}",
            $"端口探测，地址: 127.0.0.1:{mixedPort}，可连接: {CanConnect(mixedPort)}",
            $"代理探测: {ProbeHttpProxy(mixedPort)}",
            DiagnosticLog.RunProcess("netsh.exe", "winhttp", "show", "proxy"),
            DiagnosticLog.RunProcess("reg.exe", "query", @"HKCU\Software\Policies\Google\Chrome", "/v", "ProxyMode"),
            DiagnosticLog.RunProcess("reg.exe", "query", @"HKCU\Software\Policies\Google\Chrome", "/v", "ProxyServer"),
            DiagnosticLog.RunProcess("reg.exe", "query", @"HKLM\Software\Policies\Google\Chrome", "/v", "ProxyMode"),
            DiagnosticLog.RunProcess("reg.exe", "query", @"HKLM\Software\Policies\Google\Chrome", "/v", "ProxyServer"),
            DiagnosticLog.RunProcess("reg.exe", "query", @"HKCU\Software\Policies\Microsoft\Edge", "/v", "ProxyMode"),
            DiagnosticLog.RunProcess("reg.exe", "query", @"HKCU\Software\Policies\Microsoft\Edge", "/v", "ProxyServer"),
            DiagnosticLog.RunProcess("reg.exe", "query", @"HKLM\Software\Policies\Microsoft\Edge", "/v", "ProxyMode"),
            DiagnosticLog.RunProcess("reg.exe", "query", @"HKLM\Software\Policies\Microsoft\Edge", "/v", "ProxyServer")
        };
        return string.Join(Environment.NewLine, parts);
    }

    public void Enable(int mixedPort, string? bypassList = null)
    {
        EnableManual(mixedPort, "127.0.0.1", bypassList);
    }

    public void Enable(int mixedPort, AppSettings settings)
    {
        var mode = NormalizeMode(settings.SystemProxyMode);
        if (mode == "auto")
        {
            EnableAuto(mixedPort, settings.SystemProxyHost, settings.SystemProxyPacScript);
            return;
        }

        EnableManual(mixedPort, settings.SystemProxyHost, settings.SystemProxyBypass);
    }

    public void EnableManual(int mixedPort, string? host, string? bypassList = null)
    {
        EnsureProxyPortReachable(mixedPort);

        DisableCore(notify: false);
        var proxyHost = NormalizeHost(host);
        var proxyServer = BuildManualProxyServer(proxyHost, mixedPort);
        var proxyBypass = NormalizeBypassList(bypassList);

        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true)
                        ?? throw new InvalidOperationException("无法打开 Windows Internet Settings 注册表项。");
        key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        key.SetValue("MigrateProxy", 1, RegistryValueKind.DWord);
        key.SetValue("AutoDetect", 0, RegistryValueKind.DWord);
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", proxyServer, RegistryValueKind.String);
        key.SetValue("ProxyOverride", proxyBypass, RegistryValueKind.String);
        ApplyPerConnectionProxy(proxyServer, proxyBypass, null);
        NotifySettingsChanged();
        VerifyEnabled(mixedPort, proxyHost);
    }

    public void EnableAuto(int mixedPort, string? host, string? pacScript)
    {
        EnsureProxyPortReachable(mixedPort);

        DisableCore(notify: false);
        var proxyHost = NormalizeHost(host);
        var pacUrl = WritePacFile(mixedPort, proxyHost, pacScript);

        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true)
                        ?? throw new InvalidOperationException("无法打开 Windows Internet Settings 注册表项。");
        key.SetValue("MigrateProxy", 1, RegistryValueKind.DWord);
        key.SetValue("AutoDetect", 0, RegistryValueKind.DWord);
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", "", RegistryValueKind.String);
        key.SetValue("ProxyOverride", "", RegistryValueKind.String);
        key.SetValue("AutoConfigURL", pacUrl, RegistryValueKind.String);
        ApplyPerConnectionProxy(null, null, pacUrl);
        NotifySettingsChanged();
        VerifyAutoEnabled(pacUrl);
        CleanupOldPacFiles(pacUrl);
    }

    public void Disable()
    {
        DisableCore(notify: true);
    }

    private static void DisableCore(bool notify)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true)
                        ?? throw new InvalidOperationException("无法打开 Windows Internet Settings 注册表项。");
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", "", RegistryValueKind.String);
        key.SetValue("ProxyOverride", "", RegistryValueKind.String);
        key.SetValue("MigrateProxy", 1, RegistryValueKind.DWord);
        key.SetValue("AutoDetect", 0, RegistryValueKind.DWord);
        key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        ApplyPerConnectionProxy(null, null, null);
        if (notify)
        {
            NotifySettingsChanged();
        }
    }

    private static string GetConnectionSettingsSnapshot()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetConnectionsKey, writable: false);
        if (key is null)
        {
            return "连接设置注册表项不存在";
        }

        return string.Join("，", new[] { "DefaultConnectionSettings", "SavedLegacySettings" }
            .Select(name => $"{name}: {FormatBinaryValue(key.GetValue(name) as byte[])}"));
    }

    private static string FormatBinaryValue(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return "<missing>";
        }

        var prefix = string.Join("", bytes.Take(Math.Min(bytes.Length, 32)).Select(b => b.ToString("X2")));
        return bytes.Length > 32 ? $"{prefix}... ({bytes.Length} bytes)" : $"{prefix} ({bytes.Length} bytes)";
    }

    private static void ApplyPerConnectionProxy(string? proxyServer, string? proxyBypass, string? autoConfigUrl)
    {
        ApplyPerConnectionProxyCore(proxyServer, proxyBypass, autoConfigUrl, null);
        foreach (var connectionName in GetConnectionNames())
        {
            ApplyPerConnectionProxyCore(
                proxyServer,
                proxyBypass,
                autoConfigUrl,
                connectionName);
        }
    }

    private static void ApplyPerConnectionProxyCore(
        string? proxyServer,
        string? proxyBypass,
        string? autoConfigUrl,
        string? connectionName)
    {
        var enabled = !string.IsNullOrWhiteSpace(proxyServer);
        var autoEnabled = !string.IsNullOrWhiteSpace(autoConfigUrl);
        var allocatedStrings = new List<IntPtr>();
        var optionSize = Marshal.SizeOf<InternetPerConnOption>();
        var options = new[]
        {
            new InternetPerConnOption
            {
                Option = InternetPerConnFlags,
                Value = new InternetPerConnOptionValue
                {
                    Value = enabled
                        ? ProxyTypeDirect | ProxyTypeProxy
                        : autoEnabled
                            ? ProxyTypeDirect | ProxyTypeAutoProxyUrl
                            : ProxyTypeDirect
                }
            },
            new InternetPerConnOption
            {
                Option = InternetPerConnProxyServer,
                Value = new InternetPerConnOptionValue { String = AllocateString(enabled ? proxyServer! : "") }
            },
            new InternetPerConnOption
            {
                Option = InternetPerConnProxyBypass,
                Value = new InternetPerConnOptionValue { String = AllocateString(enabled ? proxyBypass ?? "" : "") }
            },
            new InternetPerConnOption
            {
                Option = InternetPerConnAutoconfigUrl,
                Value = new InternetPerConnOptionValue { String = AllocateString(autoEnabled ? autoConfigUrl! : "") }
            }
        };

        var optionsPtr = Marshal.AllocHGlobal(optionSize * options.Length);
        try
        {
            for (var i = 0; i < options.Length; i++)
            {
                Marshal.StructureToPtr(options[i], IntPtr.Add(optionsPtr, i * optionSize), false);
            }

            var optionList = new InternetPerConnOptionList
            {
                Size = Marshal.SizeOf<InternetPerConnOptionList>(),
                Connection = string.IsNullOrWhiteSpace(connectionName)
                    ? IntPtr.Zero
                    : AllocateString(connectionName),
                OptionCount = options.Length,
                OptionError = 0,
                Options = optionsPtr
            };

            if (!InternetSetOption(IntPtr.Zero, InternetOptionPerConnectionOption, ref optionList, optionList.Size))
            {
                throw new InvalidOperationException(
                    $"WinINET 按连接写入代理设置失败，Win32 错误码: {Marshal.GetLastWin32Error()}");
            }

            _ = InternetSetOption(IntPtr.Zero, InternetOptionProxySettingsChanged, IntPtr.Zero, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(optionsPtr);
            foreach (var ptr in allocatedStrings)
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        IntPtr AllocateString(string value)
        {
            var ptr = Marshal.StringToHGlobalUni(value);
            allocatedStrings.Add(ptr);
            return ptr;
        }
    }

    private static IReadOnlyList<string> GetConnectionNames()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetConnectionsKey, writable: false);
        if (key is null)
        {
            return [];
        }

        return key.GetValueNames()
            .Where(name =>
                !name.Equals("DefaultConnectionSettings", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("SavedLegacySettings", StringComparison.OrdinalIgnoreCase) &&
                key.GetValue(name) is byte[])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildManualProxyServer(string host, int port)
    {
        return $"{host}:{port}";
    }

    private static string NormalizeHost(string? host)
    {
        var value = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        return value.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : value;
    }

    public static string NormalizeMode(string? mode) =>
        string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase) ? "auto" : "manual";

    public static string NormalizePacScript(string? script) =>
        string.IsNullOrWhiteSpace(script) ? DefaultPacScript : script.Trim();

    public static void ValidatePacScript(string script)
    {
        var normalized = NormalizePacScript(script);
        try
        {
            var engine = new Engine(options =>
            {
                options.TimeoutInterval(TimeSpan.FromSeconds(1));
                options.LimitRecursion(64);
                options.MaxStatements(20_000);
            });
            engine.Execute(normalized);
            engine.Execute(
                """
                if (typeof FindProxyForURL !== 'function') {
                    throw new Error('PAC 脚本必须定义 FindProxyForURL 函数');
                }
                """);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("PAC 脚本语法无效", ex);
        }
    }

    private static string WritePacFile(int mixedPort, string proxyHost, string? script)
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        var content = NormalizePacScript(script)
            .Replace("%mixed-port%", mixedPort.ToString(), StringComparison.Ordinal)
            .Replace("%proxy-host%", proxyHost, StringComparison.Ordinal);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16]
            .ToLowerInvariant();
        var path = Path.Combine(AppPaths.DataRoot, $"sysproxy-{hash}.pac");
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new Uri(path).AbsoluteUri;
    }

    private static bool IsOwnPacUrl(string? actual)
    {
        if (!Uri.TryCreate(actual?.Trim(), UriKind.Absolute, out var uri) || !uri.IsFile)
        {
            return false;
        }

        var path = Path.GetFullPath(uri.LocalPath);
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        return string.Equals(directory, AppPaths.DataRoot, StringComparison.OrdinalIgnoreCase) &&
               (fileName.Equals("sysproxy.pac", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("sysproxy-", StringComparison.OrdinalIgnoreCase) &&
                fileName.EndsWith(".pac", StringComparison.OrdinalIgnoreCase));
    }

    private static void VerifyAutoEnabled(string expectedPacUrl)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false)
                        ?? throw new InvalidOperationException("无法打开 Windows Internet Settings 注册表项。");
        var autoConfigUrl = Convert.ToString(key.GetValue("AutoConfigURL") ?? "");
        if (!string.Equals(autoConfigUrl, expectedPacUrl, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"系统代理写入后校验失败，PAC 地址不是 {expectedPacUrl}，当前状态: {GetStaticSnapshot()}");
        }
    }

    private static void CleanupOldPacFiles(string currentPacUrl)
    {
        try
        {
            var currentPath = new Uri(currentPacUrl).LocalPath;
            foreach (var path in Directory.EnumerateFiles(AppPaths.DataRoot, "sysproxy*.pac"))
            {
                if (!string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException(
                LogSources.SystemProxy,
                ex,
                "清理旧 PAC 文件失败",
                "WARN");
        }
    }

    private static string GetStaticSnapshot()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false);
        var enabled = Convert.ToInt32(key?.GetValue("ProxyEnable") ?? 0);
        var server = Convert.ToString(key?.GetValue("ProxyServer") ?? "");
        var migrateProxy = Convert.ToString(key?.GetValue("MigrateProxy") ?? "");
        var autoDetect = Convert.ToString(key?.GetValue("AutoDetect") ?? "");
        var autoConfig = Convert.ToString(key?.GetValue("AutoConfigURL") ?? "");
        var overrideText = Convert.ToString(key?.GetValue("ProxyOverride") ?? "");
        return $"代理启用: {enabled}，代理服务器: {server}，迁移标记: {migrateProxy}，自动检测: {autoDetect}，PAC 地址: {autoConfig}，绕过列表: {overrideText}";
    }

    private static bool ProxyServerMatches(string? actual, string expected)
    {
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        return actual
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Contains('=') ? part[(part.IndexOf('=') + 1)..] : part)
            .Any(part => string.Equals(part, expected, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeBypassList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DefaultBypass;
        }

        var parts = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? DefaultBypass : string.Join(';', parts);
    }

    public static string FormatBypassListForDisplay(string? text) =>
        string.Join(Environment.NewLine, NormalizeBypassList(text).Split(';', StringSplitOptions.RemoveEmptyEntries));

    private static void EnsureProxyPortReachable(int port)
    {
        try
        {
            if (!CanConnect(port))
            {
                throw new InvalidOperationException($"127.0.0.1:{port} 未监听，请先确认 mihomo 内核已启动且 mixed-port 可用。");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"代理端口连接失败，地址: 127.0.0.1:{port}", ex);
        }
    }

    private static bool CanConnect(int port)
    {
        try
        {
            return CanConnectAsync(port, TimeSpan.FromMilliseconds(300))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> CanConnectAsync(int port, TimeSpan timeout)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port).WaitAsync(timeout);
        return client.Connected;
    }

    private static string ProbeHttpProxy(int port)
    {
        var proxy = new WebProxy($"http://127.0.0.1:{port}");
        using var handler = new HttpClientHandler
        {
            Proxy = proxy,
            UseProxy = true
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(2)
        };

        return string.Join(" | ", Probe("http://www.gstatic.com/generate_204"), Probe("https://www.gstatic.com/generate_204"));

        string Probe(string url)
        {
            try
            {
                using var response = client.GetAsync(url).GetAwaiter().GetResult();
                return $"{url} => {(int)response.StatusCode} {response.ReasonPhrase}";
            }
            catch (Exception ex)
            {
                return $"{url} => {ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    private static void NotifySettingsChanged()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionProxySettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        SendMessageTimeout(
            new IntPtr(HwndBroadcast),
            WmSettingChange,
            IntPtr.Zero,
            InternetSettingsKey,
            SmtoAbortIfHung,
            300,
            out _);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool InternetSetOption(
        IntPtr hInternet,
        int dwOption,
        ref InternetPerConnOptionList lpBuffer,
        int dwBufferLength);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        int msg,
        IntPtr wParam,
        string lParam,
        int flags,
        int timeout,
        out IntPtr result);

    [StructLayout(LayoutKind.Sequential)]
    private struct InternetPerConnOptionList
    {
        public int Size;
        public IntPtr Connection;
        public int OptionCount;
        public int OptionError;
        public IntPtr Options;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InternetPerConnOption
    {
        public int Option;
        public InternetPerConnOptionValue Value;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InternetPerConnOptionValue
    {
        [FieldOffset(0)] public int Value;
        [FieldOffset(0)] public IntPtr String;
    }
}
