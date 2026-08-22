using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClashSuki.Services;

public sealed record UwpLoopbackApp(
    string Sid,
    string DisplayName,
    string PackageFamilyName,
    string Description,
    bool IsExempt);

/// <summary>
/// Reads AppContainer loopback state through FirewallAPI.dll. Changes are sent
/// to the packaged LocalSystem service so the UI process never starts an
/// elevated command or carries a third-party loopback utility.
/// </summary>
public static class UwpLoopbackToolService
{
    private const uint ErrorSuccess = 0;

    public static Task<IReadOnlyList<UwpLoopbackApp>> GetAppsAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() => GetApps(cancellationToken), cancellationToken);

    public static async Task SetExemptionsAsync(
        IEnumerable<string> selectedSids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedSids);

        var normalizedSids = selectedSids
            .Where(static sid => !string.IsNullOrWhiteSpace(sid))
            .Select(static sid => sid.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var serviceManager = new MihomoServiceManager();
        await serviceManager.SetLoopbackExemptionsAsync(normalizedSids, cancellationToken);
    }

    private static IReadOnlyList<UwpLoopbackApp> GetApps(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exemptSids = GetExemptSids(cancellationToken);

        var result = NetworkIsolationEnumAppContainers(
            flags: 0,
            out var appCount,
            out var appContainers);
        ThrowIfFailed(result, "枚举 AppContainer 应用");

        try
        {
            var apps = new Dictionary<string, UwpLoopbackApp>(StringComparer.OrdinalIgnoreCase);
            var itemSize = Marshal.SizeOf<InetFirewallAppContainer>();
            for (var index = 0u; index < appCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var itemPointer = IntPtr.Add(appContainers, checked((int)index * itemSize));
                var item = Marshal.PtrToStructure<InetFirewallAppContainer>(itemPointer);
                var sid = SidToString(item.AppContainerSid);
                if (string.IsNullOrWhiteSpace(sid))
                {
                    continue;
                }

                var familyName = ReadString(item.AppContainerName);
                var displayName = ResolveIndirectString(ReadString(item.DisplayName));
                var description = ResolveIndirectString(ReadString(item.Description));
                var packageFullName = ReadString(item.PackageFullName);

                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = !string.IsNullOrWhiteSpace(familyName)
                        ? familyName
                        : !string.IsNullOrWhiteSpace(packageFullName)
                            ? packageFullName
                            : sid;
                }

                apps[sid] = new UwpLoopbackApp(
                    sid,
                    displayName,
                    familyName,
                    description,
                    exemptSids.Contains(sid));
            }

            // Keep exemptions whose packages disappeared between Windows' two
            // enumeration calls. They stay selected by default instead of being
            // silently removed when the user saves an unrelated change.
            foreach (var sid in exemptSids.Where(sid => !apps.ContainsKey(sid)))
            {
                apps[sid] = new UwpLoopbackApp(
                    sid,
                    "未知或已卸载的 AppContainer",
                    sid,
                    string.Empty,
                    IsExempt: true);
            }

            return apps.Values
                .OrderBy(static app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(static app => app.PackageFamilyName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            if (appContainers != IntPtr.Zero)
            {
                var freeResult = NetworkIsolationFreeAppContainers(appContainers);
                if (freeResult != ErrorSuccess)
                {
                    DiagnosticLog.WriteAppException(
                        LogSources.Network,
                        new Win32Exception(checked((int)freeResult)),
                        "释放 AppContainer 枚举结果失败",
                        "WARN");
                }
            }
        }
    }

    private static HashSet<string> GetExemptSids(CancellationToken cancellationToken)
    {
        var result = NetworkIsolationGetAppContainerConfig(out var sidCount, out var sidItems);
        ThrowIfFailed(result, "读取 AppContainer 回环配置");

        try
        {
            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var itemSize = Marshal.SizeOf<SidAndAttributes>();
            for (var index = 0u; index < sidCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var itemPointer = IntPtr.Add(sidItems, checked((int)index * itemSize));
                var item = Marshal.PtrToStructure<SidAndAttributes>(itemPointer);
                var sid = SidToString(item.Sid);
                if (!string.IsNullOrWhiteSpace(sid))
                {
                    values.Add(sid);
                }
            }

            return values;
        }
        finally
        {
            FreeAppContainerConfig(sidCount, sidItems);
        }
    }

    private static void FreeAppContainerConfig(uint sidCount, IntPtr sidItems)
    {
        if (sidItems == IntPtr.Zero)
        {
            return;
        }

        var heap = GetProcessHeap();
        var itemSize = Marshal.SizeOf<SidAndAttributes>();
        for (var index = 0u; index < sidCount; index++)
        {
            var itemPointer = IntPtr.Add(sidItems, checked((int)index * itemSize));
            var item = Marshal.PtrToStructure<SidAndAttributes>(itemPointer);
            if (item.Sid != IntPtr.Zero)
            {
                _ = HeapFree(heap, 0, item.Sid);
            }
        }

        _ = HeapFree(heap, 0, sidItems);
    }

    private static string SidToString(IntPtr sid)
    {
        if (sid == IntPtr.Zero || !ConvertSidToStringSid(sid, out var stringSid))
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(stringSid) ?? string.Empty;
        }
        finally
        {
            _ = LocalFree(stringSid);
        }
    }

    private static string ResolveIndirectString(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('@'))
        {
            return value;
        }

        var buffer = new char[1024];
        if (SHLoadIndirectString(value, buffer, (uint)buffer.Length, IntPtr.Zero) != 0)
        {
            return value;
        }

        var terminator = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, terminator >= 0 ? terminator : buffer.Length);
    }

    private static string ReadString(IntPtr value) =>
        value == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(value) ?? string.Empty;

    private static void ThrowIfFailed(uint errorCode, string operation)
    {
        if (errorCode != ErrorSuccess)
        {
            throw new Win32Exception(checked((int)errorCode), $"{operation}失败");
        }
    }

    [DllImport("FirewallAPI.dll", ExactSpelling = true)]
    private static extern uint NetworkIsolationEnumAppContainers(
        uint flags,
        out uint appCount,
        out IntPtr appContainers);

    [DllImport("FirewallAPI.dll", ExactSpelling = true)]
    private static extern uint NetworkIsolationFreeAppContainers(IntPtr appContainers);

    [DllImport("FirewallAPI.dll", ExactSpelling = true)]
    private static extern uint NetworkIsolationGetAppContainerConfig(
        out uint sidCount,
        out IntPtr appContainerSids);

    [DllImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetProcessHeap();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HeapFree(IntPtr heap, uint flags, IntPtr memory);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(
        string source,
        [Out] char[] output,
        uint outputCharacters,
        IntPtr reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InetFirewallAcCapabilities
    {
        public uint Count;
        public IntPtr Capabilities;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InetFirewallAcBinaries
    {
        public uint Count;
        public IntPtr Binaries;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InetFirewallAppContainer
    {
        public IntPtr AppContainerSid;
        public IntPtr UserSid;
        public IntPtr AppContainerName;
        public IntPtr DisplayName;
        public IntPtr Description;
        public InetFirewallAcCapabilities Capabilities;
        public InetFirewallAcBinaries Binaries;
        public IntPtr WorkingDirectory;
        public IntPtr PackageFullName;
    }
}
