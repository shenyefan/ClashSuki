using System.Collections;
using System.Runtime.InteropServices;
using ClashSuki.ServiceContract;

namespace ClashSuki.Service;

internal sealed class WindowsFirewallManager(
    ServiceRuntimeContext runtimeContext,
    ILogger<WindowsFirewallManager> logger)
{
    private const string FirewallPolicyProgId = "HNetCfg.FwPolicy2";
    private const string FirewallRuleProgId = "HNetCfg.FWRule";
    private const int FirewallProfilesAll = int.MaxValue;
    private const int FirewallProtocolAny = 256;
    private const int FirewallDirectionInbound = 1;
    private const int FirewallActionAllow = 1;

    private static readonly IReadOnlyDictionary<string, string> AllowedRuleFileNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FirewallRuleNames.Mihomo] = "mihomo.exe",
            [FirewallRuleNames.MihomoAlpha] = "mihomo-alpha.exe",
            [FirewallRuleNames.ClashSuki] = "ClashSuki.exe"
        };

    public void Configure(
        IReadOnlyList<FirewallRuleRequest?>? requestedRules,
        CancellationToken cancellationToken)
    {
        var rules = ValidateRules(requestedRules);
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("只能在 Windows 上配置防火墙规则。");
        }

        var policyType = Type.GetTypeFromProgID(FirewallPolicyProgId)
                         ?? throw new InvalidOperationException("Windows 防火墙策略 COM 组件不可用。");
        var ruleType = Type.GetTypeFromProgID(FirewallRuleProgId)
                       ?? throw new InvalidOperationException("Windows 防火墙规则 COM 组件不可用。");

        object? policyObject = null;
        object? rulesObject = null;
        try
        {
            policyObject = Activator.CreateInstance(policyType)
                           ?? throw new InvalidOperationException("无法创建 Windows 防火墙策略对象。");
            dynamic policy = policyObject;
            rulesObject = policy.Rules
                          ?? throw new InvalidOperationException("无法读取 Windows 防火墙规则集合。");
            dynamic nativeRules = rulesObject;
            var existingRuleNames = GetExistingRuleNames(rulesObject, cancellationToken);

            foreach (var rule in rules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                object? ruleObject = null;
                try
                {
                    ruleObject = CreateNativeRule(ruleType, rule);
                    if (existingRuleNames.Contains(rule.Name))
                    {
                        nativeRules.Remove(rule.Name);
                    }

                    nativeRules.Add(ruleObject);
                    existingRuleNames.Add(rule.Name);
                }
                finally
                {
                    ReleaseComObject(ruleObject);
                }
            }

            logger.LogInformation("已配置 {RuleCount} 条 ClashSuki 入站防火墙规则", rules.Count);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException(
                $"Windows 防火墙配置失败（HRESULT 0x{ex.HResult:X8}）。",
                ex);
        }
        finally
        {
            ReleaseComObject(rulesObject);
            ReleaseComObject(policyObject);
        }
    }

    private IReadOnlyList<ValidatedFirewallRule> ValidateRules(
        IReadOnlyList<FirewallRuleRequest?>? requestedRules)
    {
        if (requestedRules is null || requestedRules.Count == 0)
        {
            throw new InvalidOperationException("必须提供至少一条防火墙规则。");
        }

        if (requestedRules.Count > ServiceProtocol.MaxFirewallRuleCount)
        {
            throw new InvalidOperationException(
                $"防火墙规则不能超过 {ServiceProtocol.MaxFirewallRuleCount} 条。");
        }

        var validated = new List<ValidatedFirewallRule>(requestedRules.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var requestedRule in requestedRules)
        {
            if (requestedRule is null || string.IsNullOrWhiteSpace(requestedRule.Name))
            {
                throw new InvalidOperationException("防火墙规则名称不能为空。");
            }

            var name = requestedRule.Name;
            if (!AllowedRuleFileNames.TryGetValue(name, out var expectedFileName))
            {
                throw new InvalidOperationException($"不允许配置防火墙规则：{name}");
            }

            if (!names.Add(name))
            {
                throw new InvalidOperationException($"防火墙规则名称重复：{name}");
            }

            var programPath = ResolveProgramPath(name, requestedRule.ProgramPath);

            if (!string.Equals(Path.GetFileName(programPath), expectedFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"防火墙规则 {name} 只能指向 {expectedFileName}。");
            }

            if (!File.Exists(programPath))
            {
                throw new FileNotFoundException($"找不到防火墙规则对应的程序：{name}", programPath);
            }

            if ((File.GetAttributes(programPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"防火墙程序不能是重解析点：{name}");
            }

            if (!paths.Add(programPath))
            {
                throw new InvalidOperationException($"防火墙程序路径重复：{programPath}");
            }

            validated.Add(new ValidatedFirewallRule(name, programPath));
        }

        return validated;
    }

    private string ResolveProgramPath(string ruleName, string? requestedProgramPath)
    {
        if (string.Equals(ruleName, FirewallRuleNames.Mihomo, StringComparison.Ordinal))
        {
            return runtimeContext.CorePath;
        }

        if (string.Equals(ruleName, FirewallRuleNames.MihomoAlpha, StringComparison.Ordinal))
        {
            return Path.Combine(Path.GetDirectoryName(runtimeContext.CorePath)!, "mihomo-alpha.exe");
        }

        if (runtimeContext.IsPortable)
        {
            return runtimeContext.PortableRegistration!.ClientPath;
        }

        if (string.IsNullOrWhiteSpace(requestedProgramPath) ||
            !Path.IsPathFullyQualified(requestedProgramPath))
        {
            throw new InvalidOperationException($"防火墙程序路径必须是绝对路径：{ruleName}");
        }

        var normalizedPath = Path.GetFullPath(requestedProgramPath);
        if (normalizedPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            !runtimeContext.GetTrustedMsixClientPaths().Contains(normalizedPath))
        {
            throw new InvalidOperationException($"防火墙程序路径不属于受信任的 ClashSuki 安装目录：{ruleName}");
        }

        return normalizedPath;
    }

    private static object CreateNativeRule(Type ruleType, ValidatedFirewallRule rule)
    {
        var ruleObject = Activator.CreateInstance(ruleType)
                         ?? throw new InvalidOperationException("无法创建 Windows 防火墙规则对象。");
        try
        {
            dynamic nativeRule = ruleObject;
            nativeRule.Name = rule.Name;
            nativeRule.Description = "ClashSuki 入站程序规则";
            nativeRule.ApplicationName = rule.ProgramPath;
            nativeRule.Protocol = FirewallProtocolAny;
            nativeRule.Direction = FirewallDirectionInbound;
            nativeRule.Enabled = true;
            nativeRule.Profiles = FirewallProfilesAll;
            nativeRule.InterfaceTypes = "All";
            nativeRule.EdgeTraversal = false;
            nativeRule.Action = FirewallActionAllow;
            nativeRule.Grouping = "ClashSuki";
            return ruleObject;
        }
        catch
        {
            ReleaseComObject(ruleObject);
            throw;
        }
    }

    private static HashSet<string> GetExistingRuleNames(
        object rulesObject,
        CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enumerator = ((IEnumerable)rulesObject).GetEnumerator();
        try
        {
            while (enumerator.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var existingRuleObject = enumerator.Current;
                try
                {
                    if (existingRuleObject is null)
                    {
                        continue;
                    }

                    dynamic existingRule = existingRuleObject;
                    if (existingRule.Name is string name && !string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name);
                    }
                }
                finally
                {
                    ReleaseComObject(existingRuleObject);
                }
            }
        }
        finally
        {
            ReleaseComObject(enumerator);
        }

        return names;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private sealed record ValidatedFirewallRule(string Name, string ProgramPath);
}
