using ClashSuki.ServiceContract;

namespace ClashSuki.Service;

internal sealed class CoreLaunchRequestValidator(ServiceRuntimeContext runtimeContext)
{
    public CoreLaunchOptions Validate(ServiceRequest request)
    {
        var corePath = runtimeContext.CorePath;
        var configPath = NormalizeRequiredPath(request.ConfigPath, "配置文件路径");
        var configDirectory = NormalizeRequiredPath(request.ConfigDir, "工作目录");
        var controlPipePath = string.IsNullOrWhiteSpace(request.CoreIpcPath)
            ? ServiceProtocol.CoreControlPipePath
            : request.CoreIpcPath.Trim();
        var priority = NormalizePriority(request.CorePriority);

        if (!File.Exists(corePath))
        {
            throw new FileNotFoundException("找不到 mihomo 内核文件。", corePath);
        }

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("找不到 mihomo 运行时配置。", configPath);
        }

        if (!Directory.Exists(configDirectory))
        {
            throw new DirectoryNotFoundException($"找不到内核工作目录：{configDirectory}");
        }

        EnsureNotReparsePoint(configPath, "运行时配置");
        EnsureNotReparsePoint(configDirectory, "内核工作目录");

        if (!string.Equals(Path.GetFileName(corePath), "mihomo.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("服务仅允许启动受管的 mihomo.exe。");
        }

        if (!string.Equals(Path.GetFileName(configPath), "mihomo-runtime.yaml", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(Path.GetDirectoryName(configPath)), "config", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("运行时配置路径不符合 ClashSuki 数据目录规范。");
        }

        var configRoot = runtimeContext.IsPortable
            ? runtimeContext.PortableRegistration!.DataRoot
            : Directory.GetParent(Path.GetDirectoryName(configPath)!)?.FullName
              ?? throw new InvalidOperationException("无法确定 ClashSuki 数据目录。");
        configRoot = Path.GetFullPath(configRoot);
        EnsurePathHasNoReparsePoints(configPath, configRoot, "运行时配置");
        EnsurePathHasNoReparsePoints(configDirectory, configRoot, "内核工作目录");
        if (runtimeContext.IsPortable)
        {
            var expectedConfigPath = Path.GetFullPath(Path.Combine(
                configRoot,
                "config",
                "mihomo-runtime.yaml"));
            if (!string.Equals(configPath, expectedConfigPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("运行时配置不属于已登记的便携版数据目录。");
            }
        }

        if (!IsWithinDirectory(configDirectory, configRoot))
        {
            throw new InvalidOperationException("内核工作目录不属于当前 ClashSuki 数据目录。");
        }

        if (!string.Equals(controlPipePath, ServiceProtocol.CoreControlPipePath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("不允许使用非 ClashSuki 的内核控制管道。");
        }

        return new CoreLaunchOptions(corePath, configPath, configDirectory, controlPipePath, priority);
    }

    public static string NormalizePriority(string? priority) =>
        priority?.Trim().ToLowerInvariant() switch
        {
            "idle" => "idle",
            "below_normal" => "below_normal",
            "above_normal" => "above_normal",
            "high" => "high",
            "real_time" => "real_time",
            _ => "normal"
        };

    private static string NormalizeRequiredPath(string? path, string displayName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"必须提供{displayName}。");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException($"{displayName}必须是绝对路径。");
        }

        return Path.GetFullPath(path);
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return relative == "." ||
               (!Path.IsPathRooted(relative) &&
                !relative.Equals("..", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static void EnsureNotReparsePoint(string path, string displayName)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"{displayName}不能是重解析点。");
        }
    }

    private static void EnsurePathHasNoReparsePoints(
        string path,
        string root,
        string displayName)
    {
        if (!IsWithinDirectory(path, root))
        {
            throw new InvalidOperationException($"{displayName}不属于 ClashSuki 数据目录。");
        }

        var current = Path.GetFullPath(path);
        var normalizedRoot = Path.GetFullPath(root);
        while (true)
        {
            EnsureNotReparsePoint(current, displayName);
            if (string.Equals(current, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            current = Path.GetDirectoryName(current)
                      ?? throw new InvalidOperationException($"无法验证{displayName}路径。");
        }
    }
}

internal sealed record CoreLaunchOptions(
    string CorePath,
    string ConfigPath,
    string ConfigDirectory,
    string ControlPipePath,
    string Priority);
