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
            return Task.FromResult<IReadOnlyList<DecisionAuditRecord>>(
                records.Where(record => record.RunName == runName).ToArray());
        }
    }
}
