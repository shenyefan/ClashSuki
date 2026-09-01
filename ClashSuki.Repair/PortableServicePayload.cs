using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ClashSuki.ServiceContract;

namespace ClashSuki.Repair;

internal static class PortableServicePayload
{
    public static void Stage(
        string sourceExecutable,
        string sourceCorePath,
        string stagingDirectory,
        PortableServiceConfiguration.Registration registration)
    {
        Directory.CreateDirectory(stagingDirectory);
        ApplyProtectedAcl(stagingDirectory);

        var stagedExecutable = Path.Combine(stagingDirectory, "ClashSuki.Service.exe");
        CopyLockedAndVerify(sourceExecutable, stagedExecutable);
        EnsureRegularFile(stagedExecutable, "服务安装载荷");

        var stagedCoreDirectory = Path.Combine(stagingDirectory, "Core");
        Directory.CreateDirectory(stagedCoreDirectory);
        CopyLockedAndVerify(sourceCorePath, Path.Combine(stagedCoreDirectory, "mihomo.exe"));

        var registrationJson = JsonSerializer.Serialize(
            registration,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(
            Path.Combine(stagingDirectory, PortableServiceConfiguration.RegistrationFileName),
            registrationJson);
        ApplyProtectedAcl(stagingDirectory);
    }

    public static void ApplyProtectedAcl(string directory)
    {
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(CreateDirectoryAccessRule(
            WellKnownSidType.LocalSystemSid,
            FileSystemRights.FullControl,
            inheritance));
        security.AddAccessRule(CreateDirectoryAccessRule(
            WellKnownSidType.BuiltinAdministratorsSid,
            FileSystemRights.FullControl,
            inheritance));
        security.AddAccessRule(CreateDirectoryAccessRule(
            WellKnownSidType.BuiltinUsersSid,
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            inheritance));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    public static void EnsureElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException("管理便携服务需要管理员权限。");
        }
    }

    public static string NormalizeAbsoluteLocalPath(string path, string displayName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"{displayName}必须是绝对路径。");
        }

        var normalized = Path.GetFullPath(path);
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{displayName}必须位于本机磁盘。");
        }

        return normalized;
    }

    public static void EnsureRegularFile(string path, string displayName)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到{displayName}。", path);
        }

        EnsureNotReparsePoint(path, displayName);
    }

    public static void EnsureNotReparsePoint(string path, string displayName)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"{displayName}不能是重解析点：{path}");
        }
    }

    public static void EnsureExpectedSiblingPath(string path, string parent, string displayName)
    {
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var actualParent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.Equals(actualParent, normalizedParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"便携服务{displayName}不在预期目录中。");
        }
    }

    public static void EnsureInstallDirectoryIsReplaceable(string serviceDirectory)
    {
        if (!PortableServiceConfiguration.PathsEqual(
                serviceDirectory,
                PortableServiceConfiguration.GetInstallDirectory()))
        {
            throw new InvalidOperationException("拒绝替换非预期的服务目录。");
        }

        EnsureNotReparsePoint(serviceDirectory, "现有便携服务目录");
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Program.WriteLog("WARN", $"无法删除便携服务目录：{path}", ex.ToString());
        }
    }

    private static void CopyLockedAndVerify(string sourcePath, string destinationPath)
    {
        var sourceHash = FileIntegrity.ComputeSha256(sourcePath);
        using (var source = new FileStream(
                   sourcePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 128 * 1024,
                   FileOptions.SequentialScan))
        using (var destination = new FileStream(
                   destinationPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 128 * 1024,
                   FileOptions.SequentialScan))
        {
            source.CopyTo(destination);
            destination.Flush(flushToDisk: true);
        }

        if (!string.Equals(
                sourceHash,
                FileIntegrity.ComputeSha256(destinationPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"复制服务文件时完整性校验失败：{Path.GetFileName(sourcePath)}");
        }
    }

    private static FileSystemAccessRule CreateDirectoryAccessRule(
        WellKnownSidType sidType,
        FileSystemRights rights,
        InheritanceFlags inheritance) =>
        new(
            new SecurityIdentifier(sidType, null),
            rights,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow);
}
