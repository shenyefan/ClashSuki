using System.Security.Principal;

namespace ClashSuki.ServiceContract;

internal static class PortableServiceConfiguration
{
    public const string RegistrationFileName = "portable-service.json";
    private const int RegistrationSchemaVersion = 1;

    public static string GetInstallDirectory()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            throw new InvalidOperationException("无法确定 Program Files 目录。");
        }

        return Path.GetFullPath(Path.Combine(programFiles, "ClashSuki", "PortableService"));
    }

    public static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    internal sealed class Registration
    {
        public int SchemaVersion { get; init; } = RegistrationSchemaVersion;

        public string OwnerSid { get; init; } = "";

        public string DataRoot { get; init; } = "";

        public string ClientPath { get; init; } = "";

        public string ClientExeSha256 { get; init; } = "";

        public string ClientDllSha256 { get; init; } = "";

        public void Validate()
        {
            if (SchemaVersion != RegistrationSchemaVersion)
            {
                throw new InvalidDataException("便携服务注册信息版本不受支持。");
            }

            _ = new SecurityIdentifier(OwnerSid);
            ValidateAbsoluteLocalPath(DataRoot, "数据目录");
            ValidateAbsoluteLocalPath(ClientPath, "客户端路径");
            if (!string.Equals(Path.GetFileName(ClientPath), "ClashSuki.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("便携服务客户端必须是 ClashSuki.exe。");
            }

            ValidateSha256(ClientExeSha256, "客户端 EXE");
            ValidateSha256(ClientDllSha256, "客户端 DLL");
        }

        private static void ValidateAbsoluteLocalPath(string path, string displayName)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !Path.IsPathFullyQualified(path) ||
                Path.GetFullPath(path).StartsWith(@"\\", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"便携服务{displayName}必须是本机绝对路径。");
            }
        }

        private static void ValidateSha256(string value, string displayName)
        {
            if (value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException($"便携服务{displayName}哈希无效。");
            }
        }
    }
}
