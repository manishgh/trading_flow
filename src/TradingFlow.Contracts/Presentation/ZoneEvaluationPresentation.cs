using System.Text.Json;
using TradingFlow.Contracts.V1;

namespace TradingFlow.Contracts.Presentation;

/// <summary>Shows the engine's observed predicates and clocks identically on Web and Android.</summary>
public static class ZoneEvaluationPresentation
{
    // Combine the frozen evaluation and separately observed admission without inferring absent fills.
    public static IReadOnlyList<string> Lines(RunZoneEvaluationDetail row)
    {
        var lines = new List<string> { $"{row.Ticker} | {row.StrategyId} | {row.ResolutionReason ?? "pattern admitted"} | candidate {row.CandidateId ?? "none"}" };
        AppendEvidence(lines, row.Evidence, "Evidence");
        if (row.Admission is { } admission) lines.AddRange(IntradayZoneDecisionPresentation.Lines(admission));
        return lines;
    }

    // Render every retained source property, including future optional evidence; never recreate a trading predicate.
    private static void AppendEvidence(ICollection<string> lines, JsonElement value, string label)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) AppendEvidence(lines, property.Value, label + "." + property.Name);
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) AppendEvidence(lines, item, $"{label}[{index++}]");
            if (index == 0) lines.Add(label + ": none");
        }
        else lines.Add(label + ": " + (value.ValueKind == JsonValueKind.Null ? "unavailable / not evaluated" : value.ToString()));
    }
}
