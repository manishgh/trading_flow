using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Backtesting;

/// <summary>One input graph for durable jobs and their direct callers; evidence files are never treated as executable configs.</summary>
public static class BacktestRunInputFiles
{
    /// <summary>Includes immutable strategy metadata, frozen SIP/calendar inputs and every verified context dependency.</summary>
    public static IReadOnlyList<string> Resolve(string configPath, BacktestRunConfig run) => ResolveCore(configPath, run, null);

    /// <summary>Reuses the owner's leased diagnostic only after checking its complete configured identity and active lifetime.</summary>
    public static IReadOnlyList<string> Resolve(string configPath, BacktestRunConfig run, FrozenBacktestEvidence verified,
        Action<EngineConfig> requireLiveScope) =>
        ResolveCore(configPath, run, FrozenBacktestEvidence.FromConfig(run.Engine, verified, requireLiveScope));

    /// <summary>Collects ordinary strategies and market inputs while retaining every verified context dependency.</summary>
    private static IReadOnlyList<string> ResolveCore(string configPath, BacktestRunConfig run, FrozenBacktestEvidence? verified)
    {
        var files = new List<string> { configPath };
        files.AddRange(run.Strategies);
        files.AddRange(run.Strategies.Select(path => path + ".artifact.json").Where(File.Exists));
        if ((verified ?? FrozenBacktestEvidence.FromConfig(run.Engine)) is { } evidence)
            files.AddRange(evidence.Dependencies.Select(item => item.Path));
        if (run.Engine.ExecutionSourceManifestPath is { } path)
        {
            if (run.Engine.ExecutionSourceManifestSha256 is not { } hash)
                throw new InvalidDataException("Frozen SIP manifest requires its configured SHA-256.");
            var manifest = EventPortfolioSourceBinding.ReadManifest(path, hash);
            var calendar = FrozenExchangeCalendar.Read(manifest.CalendarReceiptPath, manifest.CalendarReceiptSha256);
            files.AddRange([path, manifest.CalendarReceiptPath, calendar.PayloadPath]);
        }
        // A research-screen run's frozen ranked list: the verified manifest and every output file it fingerprints.
        if (run.Universe?.RankedMembership is { } ranked)
        {
            var screen = TradingFlow.Engine.Universe.PublishedResearchScreen.Read(Path.GetDirectoryName(ranked.ManifestPath)!, ranked.ManifestSha256);
            files.Add(screen.ManifestPath);
            files.AddRange(screen.Manifest.Outputs.Keys.Select(name => Path.Combine(screen.Folder, name)));
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
