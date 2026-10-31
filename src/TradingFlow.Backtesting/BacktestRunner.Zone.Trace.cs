using System.Text;
using System.Text.Json;
using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Backtesting;

public sealed partial class BacktestRunner
{
    private readonly List<string> zoneEvaluationJournals = [];

    // Stream one completed-bar observation, never its complete candle or zone history.
    private StreamWriter OpenZoneEvaluationJournal()
    {
        var path = CreateExecutionJournalPath("zone-evaluations");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        zoneEvaluationJournals.Add(path);
        return new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
    }

    // Copy the shared evaluator's predicates without recalculating them or embedding completed history prefixes.
    private static void AppendZoneEvaluation(StreamWriter? journal, string strategyId, string ticker,
        IntradayZoneDecisionContext context, string? candidateId = null, string? reason = null)
    {
        if (journal is null) return;
        if (context.Trace is null) throw new InvalidDataException("The selected zone policy did not retain its evaluated candle evidence.");
        journal.WriteLine(JsonSerializer.Serialize(new ZoneEvaluationRow(strategyId, ticker, candidateId,
            JsonSerializer.SerializeToElement(context.Trace), reason ?? context.RejectionReason)));
    }

    // Join admission facts by immutable candidate ID while publishing every source row through the ordinary manifest.
    private async Task<BacktestArtifactReference?> WriteZoneEvaluationsAsync(string resultPath, BacktestResult result,
        CancellationToken token)
    {
        if (zoneEvaluationJournals.Count == 0) return null;
        var path = Path.ChangeExtension(resultPath, ".zone-evaluations.ndjson");
        var decisions = (result.ZoneDecisions ?? []).ToDictionary(item => item.CandidateId, StringComparer.Ordinal);
        long count = 0;
        await _artifactWriter.WriteStreamExclusiveAsync(path, async (output, cancellation) =>
        {
            await using var writer = new StreamWriter(output, new UTF8Encoding(false), 65536, leaveOpen: true);
            foreach (var source in zoneEvaluationJournals)
            {
                using var reader = new StreamReader(source);
                while (await reader.ReadLineAsync(cancellation) is { } line)
                {
                    var row = JsonSerializer.Deserialize<ZoneEvaluationRow>(line)
                        ?? throw new InvalidDataException("Zone evaluation source row is empty.");
                    var admission = row.CandidateId is { } id ? decisions.GetValueOrDefault(id) : null;
                    await writer.WriteLineAsync(JsonSerializer.Serialize(row with { Admission = admission }).AsMemory(), cancellation);
                    count++;
                }
            }
            await writer.FlushAsync(cancellation);
        }, token);
        return await BacktestArtifactIntegrity.CreateReferenceAsync(Path.GetDirectoryName(resultPath)!,
            "zone_evaluations", path, count, token);
    }

    private sealed record ZoneEvaluationRow(string StrategyId, string Ticker, string? CandidateId,
        JsonElement Evidence, string? ResolutionReason, BacktestZoneDecisionDetail? Admission = null);
}
