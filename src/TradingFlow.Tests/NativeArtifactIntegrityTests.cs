using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Tests;

public sealed class NativeArtifactIntegrityTests
{
    // A partly filled working parent must reconcile with both its exact original fill and cutoff command observation.
    [Fact]
    public async Task NativeBundle_AcceptsExactOriginalFillAndPendingState()
    {
        await using var fixture = new Fixture();
        var state = fixture.State(fixture.Fill);
        var manifest = await fixture.WriteAsync(state);
        await BacktestArtifactIntegrity.VerifyManifestAsync(manifest);
    }

    // Rehashing a result and recomputing its session ledger cannot authorize moving a physical fill to another source month.
    [Theory]
    [InlineData("2026-07-01T04:01:00Z")]
    [InlineData("2026-07-02T04:00:00Z")]
    public async Task NativeBundle_RejectsShiftedFillTimestampEvenWithRecomputedLedgerAndHashes(string changedTime)
    {
        await using var fixture = new Fixture();
        var shiftedTime = DateTimeOffset.Parse(changedTime);
        var shifted = fixture.Fill with { TimestampUtc = shiftedTime, SourceSessionDate = NativeDailySessionLedger.SourceSessionDate(shiftedTime) };
        var state = fixture.State(shifted);
        var manifest = await fixture.WriteAsync(state);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => BacktestArtifactIntegrity.VerifyManifestAsync(manifest));
        Assert.Contains("original execution event", error.Message);
    }

    // Aggregated quantity/notional/fees do not identify a buy; the original physical event side must agree too.
    [Fact]
    public async Task NativeBundle_RejectsChangedOriginalFillSide()
    {
        await using var fixture = new Fixture();
        var manifest = await fixture.WriteAsync(fixture.State(fixture.Fill), SimulatedOrderSide.Sell);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => BacktestArtifactIntegrity.VerifyManifestAsync(manifest));
        Assert.Contains("original execution event", error.Message);
    }

    // Every pending command field comes from the retained execution state, including quantities, reservation, known-at time and type.
    [Theory]
    [InlineData("quantity")]
    [InlineData("reservation")]
    [InlineData("known_at")]
    [InlineData("type")]
    [InlineData("status")]
    [InlineData("omitted")]
    public async Task NativeBundle_RejectsChangedPendingOrder(string field)
    {
        await using var fixture = new Fixture();
        var state = fixture.State(fixture.Fill);
        var original = state.PendingOrders.Single();
        var changed = field switch {
            "quantity" => original with { RemainingQuantity = 9 },
            "reservation" => original with { ReservedNotional = 999 },
            "known_at" => original with { KnownAtUtc = original.KnownAtUtc.AddMinutes(1) },
            "type" => original with { OrderType = "Limit" },
            "status" => original with { Status = "working" },
            _ => original
        };
        state = state with { PendingOrders = field == "omitted" ? [] : [changed] };
        var manifest = await fixture.WriteAsync(state);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => BacktestArtifactIntegrity.VerifyManifestAsync(manifest));
        Assert.Contains("original cutoff execution state", error.Message);
    }

    // Candidate identity remains independently bound even if a rewritten state uses the same altered ID everywhere.
    [Fact]
    public async Task NativeBundle_RejectsChangedStrategyIdentity()
    {
        await using var fixture = new Fixture();
        var original = fixture.State(fixture.Fill);
        var state = original with { AdmittedOrders = [original.AdmittedOrders.Single() with { StrategyId = "substitute" }] };
        var manifest = await fixture.WriteAsync(state);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => BacktestArtifactIntegrity.VerifyManifestAsync(manifest));
        Assert.Contains("original cutoff execution state", error.Message);
    }

    // An exit intention cannot be rewritten or omitted merely because no exit fill has happened yet.
    [Fact]
    public async Task NativeBundle_RejectsChangedPendingExitIntention()
    {
        await using var fixture = new Fixture();
        var original = fixture.State(fixture.Fill);
        var state = original with { PendingExits = [original.PendingExits.Single() with { Reason = "changed_reason" }] };
        var manifest = await fixture.WriteAsync(state);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => BacktestArtifactIntegrity.VerifyManifestAsync(manifest));
        Assert.Contains("original cutoff execution state", error.Message);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "native-integrity-" + Guid.NewGuid().ToString("N"));
        private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-06-30T04:00:00Z");
        private static readonly DateTimeOffset Cutoff = DateTimeOffset.Parse("2026-07-02T04:00:00Z");
        private static readonly IReadOnlyDictionary<DateOnly, MarketSessionSchedule> Calendar = new Dictionary<DateOnly, MarketSessionSchedule> {
            [new(2026, 6, 30)] = MarketSessionSchedule.RegularDay(new(2026, 6, 30)),
            [new(2026, 7, 1)] = MarketSessionSchedule.RegularDay(new(2026, 7, 1))
        };
        public BacktestPortfolioFill Fill { get; } = new("candidate", "strategy", "ABC", true, true, 10, 100, 1,
            DateTimeOffset.Parse("2026-07-01T04:00:00Z"), new(2026, 6, 30));

        // Recalculate the full accounting projection so timestamp tampering cannot be caught only by stale arithmetic.
        public BacktestPortfolioState State(BacktestPortfolioFill fill)
        {
            var state = new BacktestPortfolioState(NativeDailySessionLedger.ValuationBasis, Cutoff, 1, 1, 0,
                [fill], [new("candidate", "strategy", "ABC", 10, 100, null, null, null)],
                [new("candidate", "strategy", "ABC", "candidate:entry", "Buy", "Market", 10, "partially_filled", 1000, Start)],
                [new("candidate", "strategy", "ABC", Start.AddDays(1), "technical_exit")], []) {
                AdmittedOrders = [new("candidate", "strategy", "ABC", 20, 100, "long", Start) { DecisionStrategyId = "catalog.strategy" }]
            };
            OhlcvBar[] bars = [new("ABC", Start, "1d", 110, 110, 110, 110, 10000),
                new("ABC", Start.AddDays(1), "1d", 115, 115, 115, 115, 10000)];
            return NativeDailySessionLedger.Analyze(10000, state, bars, new(Start, Cutoff, Calendar, true));
        }

        // Freeze original physical/terminal evidence independently of the supplied potentially altered report, then hash every artifact anew.
        public async Task<string> WriteAsync(BacktestPortfolioState reported, SimulatedOrderSide originalSide = SimulatedOrderSide.Buy)
        {
            Directory.CreateDirectory(root);
            var original = State(Fill);
            var fillEvent = new SimulatedExecutionEvent("candidate:entry", "ABC", "partially_filled", 10, 10, 10,
                100, 1, 0, 0, 0, 10, 0, Fill.TimestampUtc, "filled", originalSide, false);
            var cutoff = new { CutoffUtc = Cutoff, AdmittedOrder = original.AdmittedOrders.Single(),
                original.PendingOrders, original.PendingExits, TerminalSignedQuantity = 10 };
            var execution = new[] {
                new { Ticker = "ABC", Timestamp = Fill.TimestampUtc, EvidenceScope = "portfolio_execution", ReferenceId = "candidate", EvidenceJson = JsonSerializer.Serialize(fillEvent) },
                new { Ticker = "ABC", Timestamp = Cutoff, EvidenceScope = "portfolio_execution", ReferenceId = "candidate", EvidenceJson = JsonSerializer.Serialize(cutoff) }
            };
            var ending = reported.Sessions[^1].Equity!.Value;
            var portfolio = new { StartingCapital = 10000m, EndingCapital = ending, NetProfit = ending - 10000,
                TotalReturnPct = (ending - 10000) / 10000 * 100, MaxDrawdownPct = 0m, AcceptedTradeCount = 1,
                WinningTradeCount = 0, LosingTradeCount = 0, CompletedTrades = Array.Empty<object>(), ExecutionFailures = Array.Empty<object>(),
                EconomicResultsComplete = true, PortfolioState = reported };
            var result = new { RunName = "test", StartingCapital = 10000m, EndingCapital = ending, NetProfit = ending - 10000,
                TotalReturnPct = (ending - 10000) / 10000 * 100, MaxDrawdownPct = 0m, WinningTradeCount = 0, LosingTradeCount = 0,
                CompletedTrades = Array.Empty<object>(), Winner = new { StrategyId = "strategy" }, EconomicResultsComplete = true,
                StrategyResults = Array.Empty<object>(), UnifiedPortfolio = portfolio, PortfolioState = reported };
            var artifacts = new (string Kind, object Value, long Count)[] {
                ("backtest_result", result, 1),
                ("candidate_decisions", new[] { new { Candidate = new { CandidateId = "candidate", Symbol = "ABC", SelectedStrategy = "catalog.strategy" } } }, 1),
                ("candidate_hypotheses", new[] { new { EvidenceScope = "candidate_hypothesis", ReferenceId = "candidate" } }, 1),
                ("unified_portfolio_execution", execution, 2), ("unified_completed_trades", Array.Empty<object>(), 0),
                ("native_daily_calendar", Calendar, 1)
            };
            var references = new List<BacktestArtifactReference>();
            foreach (var (kind, value, count) in artifacts)
            {
                var path = Path.Combine(root, kind + ".json");
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value));
                references.Add(await BacktestArtifactIntegrity.CreateReferenceAsync(root, kind, path, count));
            }
            var manifestPath = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new BacktestArtifactManifest(1,
                Guid.NewGuid().ToString(), "test", Cutoff, true, new(1, 1, 0, []), references)));
            return manifestPath;
        }

        // Remove only this fixture's synthetic files after verifying the resolved directory is its unique direct temporary child.
        public ValueTask DisposeAsync()
        {
            var full = Path.GetFullPath(root);
            if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(full).StartsWith("native-integrity-", StringComparison.Ordinal))
                throw new InvalidOperationException("Synthetic integrity cleanup escaped its owned directory.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
            return ValueTask.CompletedTask;
        }
    }
}
