using System.Text;
using TradingFlow.Alpaca;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Web.Services;

public interface IBacktestEventSourcePreparation
{
    Task<string> PrepareAsync(string configPath, BacktestRunConfig run, DateTimeOffset cutoff,
        CancellationToken cancellationToken);
}

/// <summary>Bind an ordinary saved plan to newly retained SIP sources before immutable durable-job inputs are captured.</summary>
public sealed class BacktestEventSourcePreparation(ConfiguredDataProviders providers, EntryGateOptions entryOptions,
    SimpleYamlReader yaml) : IBacktestEventSourcePreparation
{
    /// <summary>Preserve the original plan and write a new source-bound configuration; never request a custom operator profile.</summary>
    public async Task<string> PrepareAsync(string configPath, BacktestRunConfig run, DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        if (run.Engine.ExecutionReplayPolicyId != ExecutionReplayPolicy.IntradayRthFiveMinuteQuoteMarketV1)
            return configPath;
        if (run.Engine.ExecutionSourceManifestPath is not null && run.Engine.ExecutionSourceManifestSha256 is not null)
            return configPath;
        if (run.Engine.ExecutionSourceManifestPath is not null || run.Engine.ExecutionSourceManifestSha256 is not null)
            throw new InvalidDataException("A supplied execution manifest needs both its absolute path and SHA-256.");
        var options = providers.AlpacaOptionsFor(run);
        if (String.IsNullOrWhiteSpace(options.KeyId) || String.IsNullOrWhiteSpace(options.SecretKey))
            throw new InvalidOperationException("Historical SIP preparation needs the host's configured Alpaca credentials; no provider request was made.");
        // This is the same effective production quote policy used by normal order admission.
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetFullPath(run.RawRoot), "execution-sip", id);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("APCA-API-KEY-ID", options.KeyId);
        client.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", options.SecretKey);
        var preparer = new EventPortfolioSourcePreparation(new SipExecutionArchiveCollector(client),
            new SipExecutionMetadataCollector(client, options.Profile));
        var prepared = await preparer.PrepareAsync(run, cutoff, directory, entryOptions.QuoteMaxAgeMilliseconds,
            entryOptions.MaxSpreadBps, cancellationToken);
        var sourceText = await File.ReadAllTextAsync(configPath, cancellationToken);
        var boundText = BindConfig(sourceText, prepared.ManifestPath, prepared.ManifestSha256);
        var boundPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!,
            Path.GetFileNameWithoutExtension(configPath) + ".sip-prepared-" + id + ".yaml");
        await using (var output = new FileStream(boundPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await output.WriteAsync(Encoding.UTF8.GetBytes(boundText), cancellationToken);
        var bound = yaml.ReadBacktestRun(boundPath);
        if (bound.Engine.ExecutionSourceManifestPath is not { } capturedPath ||
            !Path.GetFullPath(capturedPath).Equals(prepared.ManifestPath, StringComparison.OrdinalIgnoreCase) ||
            bound.Engine.ExecutionSourceManifestSha256 != prepared.ManifestSha256 ||
            bound.Engine.ExecutionReplayPolicyId != run.Engine.ExecutionReplayPolicyId)
            throw new InvalidDataException("Prepared execution configuration did not retain its exact source binding.");
        return boundPath;
    }

    /// <summary>Insert only the source binding into the existing engine block; all reviewed strategy and portfolio values remain literal.</summary>
    internal static string BindConfig(string source, string manifestPath, string hash)
    {
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var engine = lines.FindIndex(line => line == "engine:");
        if (engine < 0 || lines.Count(line => line == "engine:") != 1)
            throw new InvalidDataException("Saved execution configuration needs exactly one engine section.");
        var end = engine + 1;
        while (end < lines.Count && (String.IsNullOrWhiteSpace(lines[end]) || Char.IsWhiteSpace(lines[end][0]) || lines[end].StartsWith('#')))
            end++;
        if (lines.Skip(engine + 1).Take(end - engine - 1).Any(line =>
                line.TrimStart().StartsWith("execution_source_manifest_path:", StringComparison.Ordinal) ||
                line.TrimStart().StartsWith("execution_source_manifest_sha256:", StringComparison.Ordinal)))
            throw new InvalidDataException("A source binding cannot be added over existing execution source settings.");
        // The repository's intentionally small YAML reader strips quotes without JSON unescaping.
        if (manifestPath.IndexOfAny(['\r', '\n', '"']) >= 0)
            throw new InvalidDataException("Execution manifest path contains an unsupported YAML scalar character.");
        lines.Insert(engine + 1, "  execution_source_manifest_path: \"" + manifestPath.Replace('\\', '/') + "\"");
        lines.Insert(engine + 2, "  execution_source_manifest_sha256: " + hash);
        return String.Join(Environment.NewLine, lines);
    }
}
