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
using TradingFlow.Web.Exports;

namespace TradingFlow.Web.Pages
{
    public class AuditModel : PageModel
    {
        private readonly IDecisionAuditRepository _auditRepo;
        private readonly IAuditEquityReader? _equity;

        public AuditModel(IDecisionAuditRepository auditRepo, IAuditEquityReader? equity = null)
        {
            _auditRepo = auditRepo;
            _equity = equity;
        }
        [BindProperty(SupportsGet = true)] public string? CompareRuns { get; set; }
        public IReadOnlyList<AuditMultiRun> MultiRuns { get; private set; } = [];
        public IReadOnlyList<AuditEquitySeries> EquitySeries { get; private set; } = [];
        public IReadOnlyList<string> EquityDetails { get; private set; } = [];
        public sealed record AuditMultiRun(string Name, DecisionAuditSummary Summary, bool Found);
        public static string[] ComparisonNames(string runName,string? compareRuns) {
            var names=new[]{runName}.Concat((compareRuns ?? "").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)).Distinct(StringComparer.Ordinal).ToArray();
            if(names.Length > 4 || names.Any(x=>x.Length is < 1 or > 120 || x.Any(c=>!(Char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '.' or ' '))))
                throw new InvalidOperationException("Compare at most four run names, using letters, digits, spaces, periods, hyphens or underscores.");
            return names;
        }
        private async Task BuildEquityAsync(CancellationToken token) {
            if(String.IsNullOrWhiteSpace(CompareRuns)) return;
            var summaries=new List<AuditMultiRun>(); var series=new List<AuditEquitySeries>(); var details=new List<string>();
            foreach(var name in ComparisonNames(RunName,CompareRuns)) {
                var summary=name==RunName ? Summary : await _auditRepo.SummarizeAsync(name,token);
                summaries.Add(new(name,summary,summary.Total > 0));
                if(summary.Total == 0) { details.Add($"{name}: no persisted audit decisions found; no curve inferred."); continue; }
                var result=_equity is null ? new AuditEquityResult([],"Published equity reader is unavailable.") : await _equity.ReadAsync(name,token);
                series.AddRange(result.Series); details.Add($"{name}: {result.Detail}");
            }
            MultiRuns=summaries; EquitySeries=series; EquityDetails=details;
        }
        public string CurvePath(AuditEquitySeries series) {
            var all=EquitySeries.SelectMany(x=>x.Points).ToArray();
            if(all.Length==0) return "";
            var first=all.Min(x=>x.Date.DayNumber); var last=all.Max(x=>x.Date.DayNumber);
            var min=Math.Min(0,all.Min(x=>x.ReturnPercent)); var max=Math.Max(1,all.Max(x=>x.ReturnPercent));
            var path=new StringBuilder(); decimal? previous=null;
            foreach(var point in series.Points) {
                var x=10+300m*(point.Date.DayNumber-first)/Math.Max(1,last-first);
                var y=190-180m*(point.ReturnPercent-min)/(max-min);
                if(previous is null) path.Append($"M{x.ToString("0.##",CultureInfo.InvariantCulture)},{y.ToString("0.##",CultureInfo.InvariantCulture)}");
                else path.Append($" H{x.ToString("0.##",CultureInfo.InvariantCulture)} V{y.ToString("0.##",CultureInfo.InvariantCulture)}");
                previous=y;
            }
            return path.ToString();
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
        [BindProperty(SupportsGet = true)] public string? Decision { get; set; }
        [BindProperty(SupportsGet = true)] public string? Ticker { get; set; }
        [BindProperty(SupportsGet = true)] public string? Reason { get; set; }
        [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "time";
        [BindProperty(SupportsGet = true)] public bool Descending { get; set; } = true;
        [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
        [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = 50;
        public int TotalMatching { get; private set; }
        public int PageCount => Math.Max(1, (int)Math.Ceiling(TotalMatching / (double)PageSize));
        public DecisionAuditSummary Summary { get; private set; } = new(0,0,0,0,0,0,0,[]);
        private DecisionAuditQuery Query(long? through = null) => new(Decision, Ticker, Reason, Sort,
            Descending, (PageNumber - 1) * PageSize, PageSize, through);

        private async Task LoadPageAsync(CancellationToken token)
        {
            PageSize = Math.Clamp(PageSize, 10, 200);
            PageNumber = Math.Clamp(PageNumber, 1, Int32.MaxValue / PageSize);
            Sort = Sort is "time" or "ticker" or "strategy" or "decision" or "reason" ? Sort : "time";
            Summary = await _auditRepo.SummarizeAsync(RunName, token);
            var page = await _auditRepo.QueryAsync(RunName, Query(Summary.LastId), token);
            TotalMatching = page.TotalMatching;
            if (PageNumber > PageCount) {
                PageNumber = PageCount;
                page = await _auditRepo.QueryAsync(RunName, Query(Summary.LastId), token);
            }
            Records = page.Records;
            BuildViewState();
        }


        public async Task<IActionResult> OnGetAsync(string runName, string? returnUrl)
        {
            RunName = runName;
            if(!String.IsNullOrWhiteSpace(CompareRuns)) {
                try { ComparisonNames(RunName,CompareRuns); }
                catch(InvalidOperationException ex) { return BadRequest(ex.Message); }
            }
            BackUrl = IsSafeLocalUrl(returnUrl) ? returnUrl! : "/Paper";
            await LoadPageAsync(HttpContext?.RequestAborted ?? default);
            await BuildComparisonAsync(HttpContext?.RequestAborted ?? default);
            await BuildEquityAsync(HttpContext?.RequestAborted ?? default);
            return Page();
        }

        public async Task<IActionResult> OnGetSnapshotAsync(string runName)
        {
            RunName = runName;
            await LoadPageAsync(HttpContext?.RequestAborted ?? default);
            await BuildComparisonAsync(HttpContext?.RequestAborted ?? default);

            return new JsonResult(new
            {
                success = true,
                refreshedAt = UiDisplayFormatter.FormatLocal(DateTimeOffset.UtcNow),
                evaluations = Summary.Total, sort = Sort, descending = Descending,
                totalMatching = TotalMatching, pageNumber = PageNumber, pageCount = PageCount, pageSize = PageSize,
                accepted = AcceptedCount,
                rejected = RejectedCount,
                tickerCount = TickerCount,
                strategyCount = StrategyCount,
                funnel = Funnel,
                comparison = Comparison, comparisonRunName = ComparisonRunName,
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
            AcceptedCount = Summary.Accepted;
            RejectedCount = Summary.Rejected;
            TickerCount = Summary.Tickers;
            StrategyCount = Summary.Strategies;
            TopRejectionReasons = Summary.TopReasons.Select(reason => new AuditReasonSummary(
                reason.Key, UiDisplayFormatter.FormatRejectionReason(reason.Key), reason.Example,
                reason.Count, RejectedCount == 0 ? 0 : Decimal.Round(reason.Count * 100m / RejectedCount, 1))).ToArray();
            RecordViews = Records.Select(ToView).ToArray();
            Funnel = BuildFunnel();
        }

        /// <summary>Outcome breakdown, grouped by rejection frequency; not engine gate order.</summary>
        public IReadOnlyList<AuditFunnelStage> Funnel { get; private set; } = Array.Empty<AuditFunnelStage>();

        private IReadOnlyList<AuditFunnelStage> BuildFunnel()
        {
            if (Summary.Total == 0)
            {
                return Array.Empty<AuditFunnelStage>();
            }

            var stages = new List<AuditFunnelStage>
            {
                new("Evaluated", Summary.Total, Summary.Total, 0, null)
            };

            var remaining = Summary.Total;
            foreach (var reason in TopRejectionReasons)
            {
                remaining -= reason.Count;
                stages.Add(new AuditFunnelStage(
                    reason.DisplayReason,
                    Math.Max(remaining, 0),
                    Summary.Total,
                    reason.Count,
                    reason.ReasonKey));
            }

            var otherRejected = RejectedCount - TopRejectionReasons.Sum(x => x.Count);
            if (otherRejected > 0) {
                remaining -= otherRejected;
                stages.Add(new("Other rejections / unspecified reason", remaining, Summary.Total, otherRejected, null));
            }
            var otherDecisions = Summary.Total - RejectedCount - AcceptedCount;
            if (otherDecisions > 0) {
                remaining -= otherDecisions;
                stages.Add(new("Other decisions (no signal, skipped or exits)", remaining, Summary.Total, otherDecisions, null));
            }
            stages.Add(new AuditFunnelStage("Accepted", AcceptedCount, Summary.Total, 0, null));
            return stages;
        }

        /// <summary>
        /// Streams the decision records as CSV so a run can be analysed outside the UI.
        /// </summary>
        /// <remarks>
        /// Read-only: it re-reads the same repository the page renders from and never
        /// mutates run state.
        /// </remarks>
        public async Task<IActionResult> OnGetExportAsync(string runName, CancellationToken cancellationToken, string format = "csv")
        {
            var snapshot = await _auditRepo.SummarizeAsync(runName, cancellationToken);
            var query = new DecisionAuditQuery(Decision, Ticker, Reason, "id", false, Take: 500, ThroughId: snapshot.LastId);
            return new AuditExportResult(_auditRepo, runName, query, format == "json", ToView);
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

            var other = await _auditRepo.SummarizeAsync(CompareWith.Trim(), cancellationToken);
            if (other.Total == 0)
            {
                return;
            }

            ComparisonRunName = CompareWith.Trim();
            Comparison =
            [
                Row("Evaluated", Summary.Total, other.Total),
                Row("Accepted", AcceptedCount, other.Accepted),
                Row("Rejected", RejectedCount, other.Rejected),
                Row("Acceptance rate %",
                    Percent(AcceptedCount, Summary.Total),
                    Percent(other.Accepted, other.Total)),
                Row("Tickers", TickerCount, other.Tickers),
                Row("Strategies", StrategyCount, other.Strategies),
                Row("Distinct rejection reasons",
                    Summary.ReasonCount, other.ReasonCount)
            ];

            static AuditRunComparisonRow Row(string label, decimal current, decimal other) =>
                new(label, current, other);

            static decimal Percent(int part, int whole) =>
                whole == 0 ? 0m : Decimal.Round(part * 100m / whole, 1);

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
