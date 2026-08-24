namespace ClashSuki.PrivilegedOperations;

internal static class LoopbackExemptionPolicy
{
    public const string Command = "--set-loopback-exemptions";
    public const string PayloadArgument = "--payload";
    public const int MaxExemptionCount = 512;
    public const int MaxSidCharacters = 184;
    public const int MaxPayloadBytes = 128 * 1024;

    public static string[] Normalize(IEnumerable<string?> requestedSids)
    {
        ArgumentNullException.ThrowIfNull(requestedSids);
        var sids = requestedSids
            .Where(static sid => !string.IsNullOrWhiteSpace(sid))
            .Select(static sid => sid!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sids.Length > MaxExemptionCount)
        {
            throw new InvalidOperationException(
                $"回环豁免不能超过 {MaxExemptionCount} 项。");
        }

        return sids;
    }
}
