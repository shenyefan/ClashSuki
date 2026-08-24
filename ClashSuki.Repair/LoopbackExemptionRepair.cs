using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ClashSuki.PrivilegedOperations;

internal static class LoopbackExemptionRepair
{
    public static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 &&
        string.Equals(args[0], LoopbackExemptionPolicy.Command, StringComparison.Ordinal);

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        try
        {
            EnsureElevated();
            var payloadPath = ParsePayloadPath(args);
            var payload = new FileInfo(payloadPath);
            if (!payload.Exists || payload.Length > LoopbackExemptionPolicy.MaxPayloadBytes)
            {
                throw new InvalidOperationException("回环配置载荷不存在或过大。");
            }

            if ((payload.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("回环配置载荷不能是重解析点。");
            }

            var requestedSids = await File.ReadAllLinesAsync(payload.FullName);
            var sids = LoopbackExemptionPolicy.Normalize(requestedSids);
            LoopbackExemptionWriter.SetExemptions(sids);
            ClashSuki.Repair.Program.WriteLog(
                "INFO",
                $"已更新 {sids.Length} 个 AppContainer 回环豁免");
            return 0;
        }
        catch (Exception ex)
        {
            ClashSuki.Repair.Program.WriteLog("ERROR", "回环豁免写入失败", ex.ToString());
            return 1;
        }
    }

    private static string ParsePayloadPath(IReadOnlyList<string> args)
    {
        if (args.Count != 3 ||
            !string.Equals(
                args[1],
                LoopbackExemptionPolicy.PayloadArgument,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(args[2]) ||
            !Path.IsPathFullyQualified(args[2]))
        {
            throw new ArgumentException(
                $"用法：{LoopbackExemptionPolicy.Command} " +
                $"{LoopbackExemptionPolicy.PayloadArgument} <绝对路径>");
        }

        return Path.GetFullPath(args[2]);
    }

    private static void EnsureElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException("保存商店应用回环权限需要管理员权限。");
        }
    }
}

internal static class LoopbackExemptionWriter
{
    private const uint ErrorSuccess = 0;
    private const byte SecurityAppPackageRidCount = 8;
    private const uint SecurityAppPackageBaseRid = 2;

    public static void SetExemptions(IEnumerable<string?> requestedSids)
    {
        var sids = LoopbackExemptionPolicy.Normalize(requestedSids);
        var nativeSids = new List<IntPtr>(sids.Length);
        IntPtr sidItems = IntPtr.Zero;
        try
        {
            foreach (var sid in sids)
            {
                if (!sid.StartsWith("S-1-15-2-", StringComparison.OrdinalIgnoreCase) ||
                    sid.Length > LoopbackExemptionPolicy.MaxSidCharacters ||
                    !ConvertStringSidToSid(sid, out var nativeSid) ||
                    nativeSid == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"无效的 AppContainer SID：{sid}");
                }

                if (!IsIndividualAppContainerSid(nativeSid))
                {
                    _ = LocalFree(nativeSid);
                    throw new InvalidOperationException($"不是独立 AppContainer 的 SID：{sid}");
                }

                nativeSids.Add(nativeSid);
            }

            var itemSize = Marshal.SizeOf<SidAndAttributes>();
            if (nativeSids.Count > 0)
            {
                sidItems = Marshal.AllocHGlobal(checked(itemSize * nativeSids.Count));
                for (var index = 0; index < nativeSids.Count; index++)
                {
                    Marshal.StructureToPtr(
                        new SidAndAttributes { Sid = nativeSids[index], Attributes = 0 },
                        IntPtr.Add(sidItems, checked(index * itemSize)),
                        fDeleteOld: false);
                }
            }

            var result = NetworkIsolationSetAppContainerConfig(
                checked((uint)nativeSids.Count),
                sidItems);
            if (result != ErrorSuccess)
            {
                throw new InvalidOperationException(
                    "Windows 无法保存 AppContainer 回环配置。",
                    new Win32Exception(checked((int)result)));
            }
        }
        finally
        {
            if (sidItems != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(sidItems);
            }

            foreach (var nativeSid in nativeSids)
            {
                _ = LocalFree(nativeSid);
            }
        }
    }

    private static bool IsIndividualAppContainerSid(IntPtr sid)
    {
        if (!IsValidSid(sid))
        {
            return false;
        }

        var countPointer = GetSidSubAuthorityCount(sid);
        if (countPointer == IntPtr.Zero ||
            Marshal.ReadByte(countPointer) != SecurityAppPackageRidCount)
        {
            return false;
        }

        var baseRidPointer = GetSidSubAuthority(sid, 0);
        return baseRidPointer != IntPtr.Zero &&
               unchecked((uint)Marshal.ReadInt32(baseRidPointer)) == SecurityAppPackageBaseRid;
    }

    [DllImport("FirewallAPI.dll", ExactSpelling = true)]
    private static extern uint NetworkIsolationSetAppContainerConfig(
        uint sidCount,
        IntPtr appContainerSids);

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSidToSidW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSidToSid(string stringSid, out IntPtr sid);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSid(IntPtr sid);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthorityIndex);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }
}
