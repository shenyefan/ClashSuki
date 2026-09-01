using System.IO;
using ClashSuki.ServiceContract;

namespace ClashSuki.Services;

public static class WindowsFirewallService
{
    public static async Task SetupMihomoRulesAsync(
        MihomoServiceManager serviceManager,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceManager);
        await AppPaths.BootstrapAsync(cancellationToken);
        var rules = ResolveRules();
        if (rules.Count == 0)
        {
            throw new InvalidOperationException("未找到可写入防火墙规则的程序路径。");
        }

        await serviceManager.ConfigureFirewallAsync(rules, cancellationToken);
    }

    private static List<FirewallRuleRequest> ResolveRules()
    {
        var candidates = new[]
        {
            (FirewallRuleNames.Mihomo, AppPaths.ManagedCorePath),
            (FirewallRuleNames.MihomoAlpha, Path.Combine(AppPaths.CoreDirectory, "mihomo-alpha.exe"))
        };

        return candidates
            .Where(item => !string.IsNullOrWhiteSpace(item.Item2) && File.Exists(item.Item2))
            .Select(item => new FirewallRuleRequest
            {
                Name = item.Item1,
                ProgramPath = Path.GetFullPath(item.Item2)
            })
            .ToList();
    }
}
