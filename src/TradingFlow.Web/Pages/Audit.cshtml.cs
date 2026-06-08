using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System;
using System.Collections.Generic;
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
                .Take(8)
                .ToArray();
            return Page();
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
    }

    public sealed record AuditReasonSummary(
        string ReasonKey,
        string DisplayReason,
        string ExampleReason,
        int Count,
        decimal PercentOfRejected);
}
