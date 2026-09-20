using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingFlow.Domain.Audit;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Pages
{
    public class AuditModel : PageModel
    {
        private readonly IDecisionAuditRepository _auditRepo;

        public AuditModel(IDecisionAuditRepository auditRepo)
        {
            _auditRepo = auditRepo;
        }

        public string RunName { get; set; } = string.Empty;
        public string BackUrl { get; private set; } = "/Paper";
        public IReadOnlyList<DecisionAuditRecord> Records { get; set; } = Array.Empty<DecisionAuditRecord>();
        public IReadOnlyList<AuditRecordView> RecordViews { get; private set; } = Array.Empty<AuditRecordView>();
        public IReadOnlyList<AuditReasonSummary> TopRejectionReasons { get; private set; } = Array.Empty<AuditReasonSummary>();
        public int AcceptedCount { get; private set; }
        public int RejectedCount { get; private set; }
        public int TickerCount { get; private set; }
        public int StrategyCount { get; private set; }
        public string LocalTimeZoneLabel => UiDisplayFormatter.LocalTradingTimeZoneLabel;

        public async Task<IActionResult> OnGetAsync(string runName, string? returnUrl)
        {
            RunName = runName;
            BackUrl = IsSafeLocalUrl(returnUrl) ? returnUrl! : "/Paper";
            Records = await _auditRepo.GetAuditsByRunNameAsync(runName, default);
            BuildViewState();
            await BuildComparisonAsync(default);
            return Page();
        }

        public async Task<IActionResult> OnGetSnapshotAsync(string runName)
        {
            RunName = runName;
            Records = await _auditRepo.GetAuditsByRunNameAsync(runName, default);
            BuildViewState();

            return new JsonResult(new
            {
                success = true,
                refreshedAt = UiDisplayFormatter.FormatLocal(DateTimeOffset.UtcNow),
                evaluations = Records.Count,
                accepted = AcceptedCount,
                rejected = RejectedCount,
                tickerCount = TickerCount,
                strategyCount = StrategyCount,
                reasons = TopRejectionReasons.Select(reason => new
                {
                    reasonKey = reason.ReasonKey,
                    displayReason = reason.DisplayReason,
                    exampleReason = reason.ExampleReason,
                    count = reason.Count,
                    percentOfRejected = reason.PercentOfRejected
                }),
                records = RecordViews.Select(record => new
                {
                    id = record.Id,
                    localTime = record.LocalTime,
                    ticker = record.Ticker,
                    strategy = record.StrategyName,
                    decision = record.Decision,
                    reasonKey = record.ReasonKey,
                    reason = record.DisplayReason,
                    rawReason = record.RawReason,
                    explanation = record.Explanation,
                    chips = record.Chips,
                    gateEvidence = record.GateEvidence,
                    signalJson = record.FormattedSignalJson
                })
            });
        }

        private void BuildViewState()
        {
            AcceptedCount = Records.Count(x => x.Decision.Equals("Accepted", StringComparison.OrdinalIgnoreCase));
            RejectedCount = Records.Count(x => x.Decision.Equals("Rejected", StringComparison.OrdinalIgnoreCase));
            TickerCount = Records.Select(x => x.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            StrategyCount = Records.Select(x => x.StrategyName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            TopRejectionReasons = Records
                .Where(x => !String.IsNullOrWhiteSpace(x.RejectionReason))
                .GroupBy(x => UiDisplayFormatter.NormalizeRejectionReasonKey(x.RejectionReason), StringComparer.OrdinalIgnoreCase)
                .Select(group => new AuditReasonSummary(
                    group.Key,
                    UiDisplayFormatter.FormatRejectionReason(group.Key),
                    group.First().RejectionReason!,
                    group.Count(),
                    RejectedCount == 0 ? 0 : Decimal.Round(group.Count() * 100m / RejectedCount, 1)))
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.DisplayReason, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            RecordViews = Records.Select(ToView).ToArray();
            Funnel = BuildFunnel();
        }

        /// <summary>
        /// Stages from evaluated candidates down to accepted entries.
        /// </summary>
        /// <remarks>
        /// Answers "why did nothing trade" in one view. Each rejection reason is a
        /// stage that removed candidates; the survivor count after a stage is what
        /// reached the next one. Stages are ordered by how many they eliminated, so
        /// the dominant blocker is first.
        /// </remarks>
        public IReadOnlyList<AuditFunnelStage> Funnel { get; private set; } = Array.Empty<AuditFunnelStage>();

        private IReadOnlyList<AuditFunnelStage> BuildFunnel()
        {
            if (Records.Count == 0)
            {
                return Array.Empty<AuditFunnelStage>();
            }

            var stages = new List<AuditFunnelStage>
            {
                new("Evaluated", Records.Count, Records.Count, 0, null)
            };

            var remaining = Records.Count;
            foreach (var reason in TopRejectionReasons)
            {
                remaining -= reason.Count;
                stages.Add(new AuditFunnelStage(
                    reason.DisplayReason,
                    Math.Max(remaining, 0),
                    Records.Count,
                    reason.Count,
                    reason.ReasonKey));
            }

            stages.Add(new AuditFunnelStage("Accepted", AcceptedCount, Records.Count, 0, null));
            return stages;
        }

        /// <summary>
        /// Streams the decision records as CSV so a run can be analysed outside the UI.
        /// </summary>
        /// <remarks>
        /// Read-only: it re-reads the same repository the page renders from and never
        /// mutates run state.
        /// </remarks>
        public async Task<IActionResult> OnGetExportAsync(string runName, CancellationToken cancellationToken)
        {
            RunName = runName;
            Records = await _auditRepo.GetAuditsByRunNameAsync(runName, cancellationToken);
            BuildViewState();

            var builder = new StringBuilder();
            builder.AppendLine("LocalTime,Ticker,Strategy,Decision,ReasonKey,Reason,Explanation");
            foreach (var record in RecordViews)
            {
                builder.Append(Csv(record.LocalTime)).Append(',')
                    .Append(Csv(record.Ticker)).Append(',')
                    .Append(Csv(record.StrategyName)).Append(',')
                    .Append(Csv(record.Decision)).Append(',')
                    .Append(Csv(record.ReasonKey)).Append(',')
                    .Append(Csv(record.RawReason)).Append(',')
                    .Append(Csv(record.Explanation)).AppendLine();
            }

            var safeName = String.Concat((runName ?? "run").Where(character =>
                Char.IsLetterOrDigit(character) || character is '-' or '_'));
            return File(
                Encoding.UTF8.GetBytes(builder.ToString()),
                "text/csv",
                $"audit-{(safeName.Length == 0 ? "run" : safeName)}.csv");
        }

        /// <summary>Quotes a CSV field, doubling any embedded quote.</summary>
        private static string Csv(string? value)
        {
            var text = value ?? String.Empty;
            return $"\"{text.Replace("\"", "\"\"")}\"";
        }

        /// <summary>Run this one is compared against, when the operator picks one.</summary>
        [BindProperty(SupportsGet = true)]
        public string? CompareWith { get; set; }

        /// <summary>Side-by-side metrics for the two runs, or empty when none is chosen.</summary>
        public IReadOnlyList<AuditRunComparisonRow> Comparison { get; private set; } =
            Array.Empty<AuditRunComparisonRow>();

        /// <summary>Name of the run being compared against, once loaded.</summary>
        public string? ComparisonRunName { get; private set; }

        /// <summary>
        /// Builds a metric diff between this run and another.
        /// </summary>
        /// <remarks>
        /// Comparison is the question "did the change help", and answering it by opening
        /// two tabs and reading counts is how subtle regressions get missed. Metrics are
        /// derived from the same records the page already summarises, so the two sides
        /// cannot disagree with their own detail views.
        /// </remarks>
        private async Task BuildComparisonAsync(CancellationToken cancellationToken)
        {
            if (String.IsNullOrWhiteSpace(CompareWith) ||
                String.Equals(CompareWith, RunName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var other = await _auditRepo.GetAuditsByRunNameAsync(CompareWith.Trim(), cancellationToken);
            if (other.Count == 0)
            {
                return;
            }

            ComparisonRunName = CompareWith.Trim();
            Comparison =
            [
                Row("Evaluated", Records.Count, other.Count),
                Row("Accepted", AcceptedCount, CountDecision(other, "Accepted")),
                Row("Rejected", RejectedCount, CountDecision(other, "Rejected")),
                Row("Acceptance rate %",
                    Percent(AcceptedCount, Records.Count),
                    Percent(CountDecision(other, "Accepted"), other.Count)),
                Row("Tickers", TickerCount, Distinct(other, record => record.Ticker)),
                Row("Strategies", StrategyCount, Distinct(other, record => record.StrategyName)),
                Row("Distinct rejection reasons",
                    TopRejectionReasons.Count,
                    other.Where(record => !String.IsNullOrWhiteSpace(record.RejectionReason))
                        .Select(record => UiDisplayFormatter.NormalizeRejectionReasonKey(record.RejectionReason))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count())
            ];

            static AuditRunComparisonRow Row(string label, decimal current, decimal other) =>
                new(label, current, other);

            static int CountDecision(IReadOnlyList<DecisionAuditRecord> source, string decision) =>
                source.Count(record => record.Decision.Equals(decision, StringComparison.OrdinalIgnoreCase));

            static decimal Percent(int part, int whole) =>
                whole == 0 ? 0m : Decimal.Round(part * 100m / whole, 1);

            static int Distinct(IReadOnlyList<DecisionAuditRecord> source, Func<DecisionAuditRecord, string> select) =>
                source.Select(select).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        }

        private static bool IsSafeLocalUrl(string? url)
        {
            return !String.IsNullOrWhiteSpace(url) &&
                url.StartsWith("/", StringComparison.Ordinal) &&
                !url.StartsWith("//", StringComparison.Ordinal);
        }

        public string FormatLocal(DateTimeOffset timestamp, bool includeMilliseconds = false)
        {
            return UiDisplayFormatter.FormatLocal(timestamp, includeMilliseconds);
        }

        public string FormatRejectionReason(string? reason)
        {
            return UiDisplayFormatter.FormatRejectionReason(reason);
        }

        public string FormatJson(string json)
        {
            try
            {
                var parsed = JsonDocument.Parse(json);
                return JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                return json;
            }
        }

        private AuditRecordView ToView(DecisionAuditRecord record)
        {
            var reasonKey = UiDisplayFormatter.NormalizeRejectionReasonKey(record.RejectionReason);
            var chips = ExtractSignalChips(record.SignalJson);
            var explanation = BuildExplanation(record, chips);
            return new AuditRecordView(
                record.Id,
                FormatLocal(record.Timestamp, includeMilliseconds: true),
                record.Ticker,
                record.StrategyName,
                record.Decision,
                reasonKey,
                FormatRejectionReason(record.RejectionReason),
                record.RejectionReason ?? String.Empty,
                explanation,
                chips,
                ExtractGateEvidence(record.RejectionReason),
                FormatJson(record.SignalJson));
        }

        private static string BuildExplanation(DecisionAuditRecord record, IReadOnlyList<AuditChip> chips)
        {
            if (record.Decision.Equals("Accepted", StringComparison.OrdinalIgnoreCase))
            {
                var matched = chips
                    .Where(chip => chip.Passed)
                    .Take(4)
                    .Select(chip => chip.Label)
                    .ToArray();
                return matched.Length == 0
                    ? "Accepted because the signal passed the strategy gates and no veto rejected it."
                    : $"Accepted because these gates matched: {String.Join(", ", matched)}.";
            }

            if (!String.IsNullOrWhiteSpace(record.RejectionReason))
            {
                return $"Rejected at gate: {UiDisplayFormatter.FormatRejectionReason(record.RejectionReason)}.";
            }

            return $"{record.Decision} evaluation recorded by the engine.";
        }

        /// <summary>
        /// Pulls the matched value and the threshold it was compared against out of a
        /// rejection reason.
        /// </summary>
        /// <remarks>
        /// `AGENTS.md` requires audit screens to show the exact matched values, not just
        /// that a gate failed. The engine already writes the comparison into the reason
        /// text (for example `rvol 0.82 < 1.50`), so this reads what the engine emitted
        /// rather than re-deriving or guessing a threshold. Reasons that carry no
        /// comparison simply produce no evidence rows.
        /// </remarks>
        internal static IReadOnlyList<AuditGateEvidence> ExtractGateEvidence(string? rawReason)
        {
            if (String.IsNullOrWhiteSpace(rawReason))
            {
                return Array.Empty<AuditGateEvidence>();
            }

            var evidence = new List<AuditGateEvidence>();
            foreach (Match match in GateComparisonPattern.Matches(rawReason))
            {
                var label = match.Groups["label"].Value.Trim();
                var actual = match.Groups["actual"].Value;
                var op = match.Groups["op"].Value;
                var threshold = match.Groups["threshold"].Value;
                if (label.Length == 0)
                {
                    label = "value";
                }

                evidence.Add(new AuditGateEvidence(label, actual, op, threshold));
            }

            return evidence;
        }

        /// <summary>
        /// Matches `label 1.23 &lt; 4.56` and the other comparison operators, allowing an
        /// optional %, x, or bps suffix on either number.
        /// </summary>
        private static readonly Regex GateComparisonPattern = new(
            @"(?<label>[A-Za-z][A-Za-z0-9_ ./-]{0,40}?)\s*" +
            @"(?<actual>-?\d+(?:\.\d+)?)\s*(?:%|x|bps)?\s*" +
            @"(?<op><=|>=|<|>|!=|==|=)\s*" +
            @"(?<threshold>-?\d+(?:\.\d+)?)\s*(?:%|x|bps)?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static IReadOnlyList<AuditChip> ExtractSignalChips(string signalJson)
        {
            var chips = new List<AuditChip>();
            try
            {
                using var document = JsonDocument.Parse(signalJson);
                var root = document.RootElement;
                AddDecimalChip(chips, root, "CurrentPrice", "Price", "C2");
                AddDecimalChip(chips, root, "CurrentRsi", "RSI", "F2");
                AddDecimalChip(chips, root, "CurrentAtr", "ATR", "F2");
                AddDecimalChip(chips, root, "CurrentVolume", "Volume", "N0");
                AddBoolChip(chips, root, "IsAboveVwap", "Above VWAP");
                AddBoolChip(chips, root, "IsPriceAboveEma10", "Above EMA10");
                AddBoolChip(chips, root, "IsPriceAboveEma20", "Above EMA20");
                AddBoolChip(chips, root, "IsEma10AboveEma20", "EMA10 > EMA20");
                AddBoolChip(chips, root, "IsPriceAboveSma10", "Above SMA10");
                AddBoolChip(chips, root, "IsPriceAboveSma20", "Above SMA20");
                AddBoolChip(chips, root, "IsMacdNotBearish", "MACD not bearish");
                AddBoolChip(chips, root, "IsMacdHistogramPositive", "MACD histogram positive");
                AddTrueBoolChip(chips, root, "IsSwingReclaim", "Swing reclaim");
                AddTrueBoolChip(chips, root, "IsSwingRollover", "Swing rollover");
                AddDecimalChip(chips, root, "CloseLocationValue", "Close location", "F2");
                AddDecimalChip(chips, root, "VwapExtensionAtr", "VWAP ext ATR", "F2");
                AddDecimalChip(chips, root, "ReclaimPullbackDepthPct", "Reclaim pullback %", "F2");
                AddDecimalChip(chips, root, "RolloverAdvancePct", "Rollover advance %", "F2");
            }
            catch
            {
                // Keep the analyzer usable if an older audit row has non-signal JSON.
            }

            return chips;
        }

        private static void AddBoolChip(List<AuditChip> chips, JsonElement root, string propertyName, string label)
        {
            if (TryGetProperty(root, propertyName, out var property) &&
                property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var passed = property.GetBoolean();
                chips.Add(new AuditChip(label, passed ? "true" : "false", passed));
            }
        }

        private static void AddTrueBoolChip(List<AuditChip> chips, JsonElement root, string propertyName, string label)
        {
            if (TryGetProperty(root, propertyName, out var property) &&
                property.ValueKind is JsonValueKind.True &&
                property.GetBoolean())
            {
                chips.Add(new AuditChip(label, "true", true));
            }
        }

        private static void AddDecimalChip(List<AuditChip> chips, JsonElement root, string propertyName, string label, string format)
        {
            if (!TryGetProperty(root, propertyName, out var property) ||
                property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return;
            }

            if (property.TryGetDecimal(out var value))
            {
                chips.Add(new AuditChip(label, value.ToString(format, CultureInfo.InvariantCulture), true));
            }
        }

        private static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement property)
        {
            if (root.TryGetProperty(propertyName, out property))
            {
                return true;
            }

            var camelName = Char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
            return root.TryGetProperty(camelName, out property);
        }
    }

    public sealed record AuditReasonSummary(
        string ReasonKey,
        string DisplayReason,
        string ExampleReason,
        int Count,
        decimal PercentOfRejected);

    public sealed record AuditRecordView(
        long Id,
        string LocalTime,
        string Ticker,
        string StrategyName,
        string Decision,
        string ReasonKey,
        string DisplayReason,
        string RawReason,
        string Explanation,
        IReadOnlyList<AuditChip> Chips,
        IReadOnlyList<AuditGateEvidence> GateEvidence,
        string FormattedSignalJson);

    public sealed record AuditChip(string Label, string Value, bool Passed);

    /// <summary>One gate comparison, showing what was measured beside what was required.</summary>
    /// <param name="Label">Gate or field name as the engine wrote it.</param>
    /// <param name="Actual">The value the run actually measured.</param>
    /// <param name="Operator">The comparison the gate applied.</param>
    /// <param name="Threshold">The configured limit the value was tested against.</param>
    public sealed record AuditGateEvidence(string Label, string Actual, string Operator, string Threshold);

    /// <summary>One stage of the decision funnel.</summary>
    /// <param name="Label">Stage name, or the rejection reason that removed candidates.</param>
    /// <param name="Remaining">Candidates still alive after this stage.</param>
    /// <param name="Total">Evaluated candidates, used for the bar width.</param>
    /// <param name="Removed">Candidates this stage eliminated.</param>
    /// <param name="ReasonKey">Reason key so the stage can drive the record filter.</param>
    /// <summary>One metric compared across two runs.</summary>
    /// <param name="Label">Metric name.</param>
    /// <param name="Current">Value for the run being viewed.</param>
    /// <param name="Other">Value for the comparison run.</param>
    public sealed record AuditRunComparisonRow(string Label, decimal Current, decimal Other)
    {
        /// <summary>Current minus other.</summary>
        public decimal Delta => Current - Other;

        /// <summary>Semantic class for the delta, so direction is not colour-only.</summary>
        public string DeltaClass => Delta > 0m ? "value-good" : Delta < 0m ? "value-bad" : "value-flat";

        /// <summary>Signed delta with an explicit sign so it reads without colour.</summary>
        public string DeltaText => Delta > 0m ? $"+{Delta:0.##}" : Delta.ToString("0.##");
    }

    public sealed record AuditFunnelStage(
        string Label,
        int Remaining,
        int Total,
        int Removed,
        string? ReasonKey)
    {
        /// <summary>Share of evaluated candidates still alive, as a percentage.</summary>
        public decimal RemainingPercent => Total == 0 ? 0m : Decimal.Round(Remaining * 100m / Total, 1);
    }
}
