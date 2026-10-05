using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Domain.Audit;
using TradingFlow.Web.Pages;
using Xunit;

namespace TradingFlow.Tests;

public sealed class AuditModelTests
{
    [Fact]
    public async Task OnGetAsync_BuildsCountsAndSignalExplanationChips()
    {
        var repo = new InMemoryDecisionAuditRepository([
            new DecisionAuditRecord
            {
                RunName = "paper-run",
                Ticker = "CRDO",
                StrategyName = "Swing",
                Timestamp = DateTimeOffset.UtcNow,
                Decision = "Accepted",
                SignalJson = """
                {
                  "ticker": "CRDO",
                  "currentPrice": 256.72,
                  "currentRsi": 68.66,
                  "currentVolume": 5274644,
                  "isAboveVwap": true,
                  "isMacdNotBearish": true,
                  "isSwingReclaim": true
                }
                """
            },
            new DecisionAuditRecord
            {
                RunName = "paper-run",
                Ticker = "MXL",
                StrategyName = "Swing",
                Timestamp = DateTimeOffset.UtcNow,
                Decision = "Rejected",
                RejectionReason = "relative_volume_below_minimum (Actual: 0.26, Required: 0.50)",
                SignalJson = """
                {
                  "ticker": "MXL",
                  "currentPrice": 71.45,
                  "currentRsi": 42.12,
                  "isAboveVwap": false
                }
                """
            }
        ]);
        var model = new AuditModel(repo);

        var result = await model.OnGetAsync("paper-run", "/Paper");

        Assert.IsType<PageResult>(result);
        Assert.Equal(2, model.Records.Count);
        Assert.Equal(1, model.AcceptedCount);
        Assert.Equal(1, model.RejectedCount);
        var accepted = Assert.Single(model.RecordViews, record => record.Decision == "Accepted");
        Assert.Contains(accepted.Chips, chip => chip.Label == "Above VWAP" && chip.Passed);
        Assert.Contains("Accepted because", accepted.Explanation);
        var rejected = Assert.Single(model.RecordViews, record => record.Decision == "Rejected");
        Assert.Equal("relative_volume_below_minimum", rejected.ReasonKey);
        Assert.Contains("Rejected at gate", rejected.Explanation);
    }

    private sealed class InMemoryDecisionAuditRepository : IDecisionAuditRepository
    {
        private readonly IReadOnlyList<DecisionAuditRecord> records;

        public InMemoryDecisionAuditRepository(IReadOnlyList<DecisionAuditRecord> records)
        {
            this.records = records;
        }

        public Task SaveAuditAsync(DecisionAuditRecord record, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<DecisionAuditRecord>> GetAuditsByRunNameAsync(string runName, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The audit UI must not load the complete run.");
        }

        public Task<DecisionAuditSummary> SummarizeAsync(string runName, CancellationToken token)
        {
            var all = records.Where(x => x.RunName == runName).ToArray();
            var reasons = all.Where(x => x.Decision == "Rejected" && !String.IsNullOrWhiteSpace(x.RejectionReason))
                .GroupBy(x => TradingFlow.Web.Services.UiDisplayFormatter.NormalizeRejectionReasonKey(x.RejectionReason))
                .Select(g => new DecisionAuditReasonCount(g.Key, g.First().RejectionReason!, g.Count())).ToArray();
            return Task.FromResult(new DecisionAuditSummary(all.Length, all.Count(x => x.Decision == "Accepted"),
                all.Count(x => x.Decision == "Rejected"), all.Select(x => x.Ticker).Distinct().Count(),
                all.Select(x => x.StrategyName).Distinct().Count(), reasons.Length,
                all.Length == 0 ? 0 : all.Max(x => x.Id), reasons.Take(20).ToArray()));
        }

        public Task<DecisionAuditPage> QueryAsync(string runName, DecisionAuditQuery query, CancellationToken token) =>
            GetAuditPageAsync(runName, query.Decision, query.Ticker, query.Skip, query.Take, token);

        public Task<DecisionAuditPage> GetAuditPageAsync(
            string runName,
            string? decision,
            string? ticker,
            int skip,
            int take,
            CancellationToken cancellationToken)
        {
            var matching = records
                .Where(record => record.RunName == runName)
                .Where(record => String.IsNullOrWhiteSpace(decision) || record.Decision == decision)
                .Where(record => String.IsNullOrWhiteSpace(ticker) ||
                    record.Ticker.Contains(ticker, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(record => record.Timestamp)
                .ToArray();
            return Task.FromResult(new DecisionAuditPage(
                matching.Skip(skip).Take(take).ToArray(),
                matching.Length));
        }
    }
}
