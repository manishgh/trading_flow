using System.Globalization;
using TradingFlow.Contracts.V1;

namespace TradingFlow.Contracts.Presentation;

/// <summary>Displays resolved plan settings, retaining the server's immutable cost and strategy identities.</summary>
public static class BacktestSettingsPresentation
{
    /// <summary>Lists frozen settings without deriving fees, returns or execution behavior in the client.</summary>
    public static IReadOnlyList<string> Lines(BacktestResolvedSettingsResponse? settings)
    {
        if (settings is null) return [];
        var lines = new List<string> {
            $"Profile: {settings.ConfigurationKey}",
            $"Configuration SHA-256: {settings.ConfigurationSha256}",
            $"Fixed buy fee USD: {Number(settings.FixedBuyFee)} · Fixed sell fee USD: {Number(settings.FixedSellFee)}",
            $"SEC fee rate: {Number(settings.SecFeeRate)} · FINRA TAF per share USD: {Number(settings.FinraTafPerShare)} · FINRA TAF cap USD: {Number(settings.FinraTafCap)}",
            $"Maximum bar participation percent: {Number(settings.MaxBarParticipationPct)} · Impact bps at maximum participation: {Number(settings.ImpactBpsAtMaxParticipation)}",
            $"Execution replay policy: {settings.ExecutionReplayPolicyId ?? "Not specified"}",
            $"Strategy horizon: {settings.Horizon}",
            $"Cutoff policy: {settings.CutoffPolicy ?? "Not specified"}",
            $"Precommitted liquidation source UTC: {settings.PrecommittedLiquidationSourceUtc?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "Not specified"}"
        };
        if (settings.RequiredMarketDataContext is { } context) lines.Add($"Required market data: {context}");
        foreach (var strategy in settings.Strategies)
        {
            lines.Add($"Recipe: {strategy.Name} · {Identity(strategy.Identity)}");
            lines.Add($"Adverse slippage bps: {Number(strategy.SlippageBps)}");
            if (strategy.Zone is { } zone)
            {
                lines.Add($"Long bounce volume: {zone.VolumeMode} · short RVOL minimum: {Number(zone.MinimumRelativeVolume)} · target policy: {zone.TargetPolicy}");
                if (zone.FallbackNetRiskMultiple is { } multiple)
                    lines.Add($"No-zone target: estimated net {Number(multiple)}R, frozen before execution; realized reward/risk can differ.");
                lines.Add($"Stop basis: {zone.StopBasis} · synthetic full spread bps: {Number(zone.SyntheticSpreadBps)}");
                lines.Add($"Planning costs: {zone.PlanningCostAssumption}");
            }
            if (strategy.DerivedFromIdentity is { } parent) lines.Add($"Parent: {Identity(parent)}");
            if (strategy.AdmissionProfile is { } profile)
            {
                lines.Add($"Admission: {profile.ProfileId} {profile.ProfileVersion}");
                lines.Add($"Execution timing: {profile.ExecutionTimingPolicy} · Risk policy: {profile.RiskPolicy}");
            }
        }
        return lines;
    }

    /// <summary>Retains the exact version and hash beside the descriptive recipe name.</summary>
    private static string Identity(StrategyReference identity) =>
        $"{identity.StrategyId} {identity.SemanticVersion} ({identity.ContentSha256})";

    /// <summary>Uses invariant decimal text matching the Web JSON number presentation.</summary>
    private static string Number(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
}
