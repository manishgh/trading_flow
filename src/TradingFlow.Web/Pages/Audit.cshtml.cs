using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
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
                AddTrueBoolChip(chips, root, "IsBullFlagBreakout", "Bull flag breakout");
                AddTrueBoolChip(chips, root, "IsSwingReclaim", "Swing reclaim");
                AddTrueBoolChip(chips, root, "IsSwingRollover", "Swing rollover");
                AddDecimalChip(chips, root, "CloseLocationValue", "Close location", "F2");
                AddDecimalChip(chips, root, "DayGainPct", "Day gain %", "F2");
                AddDecimalChip(chips, root, "SessionGainPct", "Session gain %", "F2");
                AddDecimalChip(chips, root, "VwapExtensionAtr", "VWAP ext ATR", "F2");
                AddDecimalChip(chips, root, "BullFlagPoleMovePct", "Flag pole %", "F2");
                AddDecimalChip(chips, root, "BullFlagBreakoutVolumeRatio", "Breakout volume ratio", "F2");
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
        string FormattedSignalJson);

    public sealed record AuditChip(string Label, string Value, bool Passed);
}
