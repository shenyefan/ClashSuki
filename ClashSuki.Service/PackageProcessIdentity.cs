using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClashSuki.Service;

internal static class PackageProcessIdentity
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    public static string GetCurrentFamilyName()
    {
        uint length = 0;
        var result = GetCurrentPackageFamilyName(ref length, null);
        if (result != ErrorInsufficientBuffer)
        {
            throw CreateReadException(result, "当前服务进程");
        }

        var buffer = new char[length];
        result = GetCurrentPackageFamilyName(ref length, buffer);
        if (result != ErrorSuccess)
        {
            throw CreateReadException(result, "当前服务进程");
        }

        return ReadNullTerminated(buffer, length);
    }

    public static bool TryGetFamilyName(Process process, out string familyName)
    {
        ArgumentNullException.ThrowIfNull(process);

        uint length = 0;
        var result = GetPackageFamilyName(process.Handle, ref length, null);
        if (result == AppModelErrorNoPackage)
        {
            familyName = string.Empty;
            return false;
        }

        if (result != ErrorInsufficientBuffer)
        {
            throw CreateReadException(result, $"客户端进程 {process.Id}");
        }

        var buffer = new char[length];
        result = GetPackageFamilyName(process.Handle, ref length, buffer);
        if (result != ErrorSuccess)
        {
            throw CreateReadException(result, $"客户端进程 {process.Id}");
        }

        familyName = ReadNullTerminated(buffer, length);
        return true;
    }

    private static string ReadNullTerminated(char[] buffer, uint length)
    {
        if (length <= 1 || length > buffer.Length)
        {
            throw new InvalidOperationException("Windows 返回了无效的包系列名称。");
        }

        return new string(buffer, 0, checked((int)length - 1));
    }

    private static Exception CreateReadException(int error, string processDescription) =>
        error == AppModelErrorNoPackage
            ? new InvalidOperationException($"{processDescription}没有 MSIX 包身份。")
            : new Win32Exception(error, $"无法读取{processDescription}的包系列名称。");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFamilyName(
        ref uint packageFamilyNameLength,
        char[]? packageFamilyName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackageFamilyName(
        IntPtr process,
        ref uint packageFamilyNameLength,
        char[]? packageFamilyName);
}
