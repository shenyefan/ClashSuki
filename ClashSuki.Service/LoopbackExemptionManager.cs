using System.ComponentModel;
using System.Runtime.InteropServices;
using ClashSuki.ServiceContract;

namespace ClashSuki.Service;

internal sealed class LoopbackExemptionManager
{
    private const uint ErrorSuccess = 0;
    private const byte SecurityAppPackageRidCount = 8;
    private const uint SecurityAppPackageBaseRid = 2;

    public void SetExemptions(
        IReadOnlyCollection<string?>? requestedSids,
        CancellationToken cancellationToken)
    {
        if (requestedSids is null)
        {
            throw new InvalidOperationException("回环豁免列表不能为空。");
        }

        var sids = requestedSids
            .Where(static sid => !string.IsNullOrWhiteSpace(sid))
            .Select(static sid => sid!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sids.Length > ServiceProtocol.MaxLoopbackExemptionCount)
        {
            throw new InvalidOperationException(
                $"回环豁免不能超过 {ServiceProtocol.MaxLoopbackExemptionCount} 项。");
        }

        var nativeSids = new List<IntPtr>(sids.Length);
        IntPtr sidItems = IntPtr.Zero;
        try
        {
            foreach (var sid in sids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!sid.StartsWith("S-1-15-2-", StringComparison.OrdinalIgnoreCase) ||
                    sid.Length > ServiceProtocol.MaxLoopbackSidCharacters ||
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

            cancellationToken.ThrowIfCancellationRequested();
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
        if (countPointer == IntPtr.Zero || Marshal.ReadByte(countPointer) != SecurityAppPackageRidCount)
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
