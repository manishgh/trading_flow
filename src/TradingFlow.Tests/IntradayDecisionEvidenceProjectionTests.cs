using System.Text.Json;
using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting;
using TradingFlow.Contracts.Presentation;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Tests;

public sealed class IntradayDecisionEvidenceProjectionTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-01T13:30:00Z");

    /// <summary>Both clients retain fractional observed extrema separately from submitted trigger/stop and the actual gap fill.</summary>
    [Fact]
    public void OpeningRangeDetails_RoundTripStopIntentAndUseSharedOpeningLabels()
    {
        var candidate = Candidate("orb", 0);
        candidate = candidate with { StopLossPrice = 98.99m, IntradayExecution = candidate.IntradayExecution! with {
            EntryOrderMode = IntradayEntryOrderMode.StopMarket, LimitPrice = 100.02m,
            OpeningRangeHigh = 100.0051m, OpeningRangeLow = 98.9976m, OpeningRangeOpen = 99.2m, OpeningRangeClose = 99.8m,
            OpeningRangeCompletedAtUtc = Start.AddMinutes(15), OpeningRelativeVolume = 1.6m,
            OpeningVolumeBaselineSampleCount = 20, MaxFilledParentTradesPerSession = 2, RequireRangeReturnBeforeReentry = true
        }};
        var state = State() with { Fills = [new("orb", "day", "ABC", true, true, 10, 100.07m, 1, Start.AddMinutes(15), new(2026, 10, 1))] };
        var replay = new IntradayPortfolioReplayOutcome([], [], [], [], state);
        var projected = RunPortfolioStateProjection.Project(IntradayDecisionEvidenceProjection.Attach(state, [candidate], replay))!;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var detail = Assert.Single(JsonSerializer.Deserialize<RunPortfolioStateResponse>(JsonSerializer.Serialize(projected, options), options)!.IntradayDecisions!);
        Assert.Equal("stop_market", detail.EntryOrderMode);
        Assert.Equal(100.02m, detail.TriggerPrice);
        Assert.Equal(100.07m, detail.ActualEntryPrice);
        Assert.Equal(100.0051m, detail.OpeningRangeHigh);
        Assert.Equal(98.9976m, detail.OpeningRangeLow);
        Assert.Equal(98.99m, detail.FrozenStopPrice);
        Assert.Equal(99.2m, detail.OpeningRangeOpen);
        Assert.Equal(99.8m, detail.OpeningRangeClose);
        Assert.Equal(Start.AddMinutes(15), detail.OpeningRangeCompletedAtUtc);
        Assert.Equal(1.6m, detail.OpeningRelativeVolume);
        Assert.Equal(20, detail.OpeningVolumeBaselineSampleCount);
        Assert.Equal(2, detail.MaxFilledParentTradesPerSession);
        Assert.True(detail.RequireRangeReturnBeforeReentry);
        var lines = IntradayDecisionPresentation.Lines(detail);
        Assert.Contains(lines, line => line.Contains("Opening range high / low: 100.0051 / 98.9976 (observed)", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Submitted trigger price: 100.02 · Actual average entry: 100.07", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Frozen structural stop: 98.99 (submitted)", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("20 / 20 (median)", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Previous session context", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("Limit price:", StringComparison.Ordinal) || line.Contains("candle B", StringComparison.Ordinal) || line.Contains("candle C", StringComparison.Ordinal));
    }

    /// <summary>Web/API/Android consume the same explicit next-opening contract, source close and candle-stop description.</summary>
    [Fact]
    public void NextBarMarketDetails_ExposeActualTimingAndCandleStop()
    {
        var candidate = Candidate("market", 0);
        candidate = candidate with { IntradayExecution = candidate.IntradayExecution! with {
            EntryOrderMode = IntradayEntryOrderMode.NextBarMarket, BreakoutTriggerPrice = 99.99m,
            OpeningRangeHigh = 99.98m, OpeningRangeLow = 95m, OpeningRangeCompletedAtUtc = Start.AddMinutes(15)
        }};
        var state = State();
        var replay = new IntradayPortfolioReplayOutcome([], [], [], [], state);
        var projected = RunPortfolioStateProjection.Project(IntradayDecisionEvidenceProjection.Attach(state, [candidate], replay))!;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var detail = Assert.Single(JsonSerializer.Deserialize<RunPortfolioStateResponse>(JsonSerializer.Serialize(projected, options), options)!.IntradayDecisions!);
        Assert.Equal("next_bar_market", detail.EntryOrderMode);
        Assert.Equal(99.99m, detail.TriggerPrice);
        var lines = IntradayDecisionPresentation.Lines(detail);
        Assert.Contains(lines, line => line.Contains("next bar opening market", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("completed breakout candle low", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("09:50 New York", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("Entry order: stop-market", StringComparison.Ordinal));
    }

    /// <summary>Persisted limit results written before ORB still deserialize with their historical labels.</summary>
    [Fact]
    public void LegacyLimitJson_DefaultsToLimitPresentation()
    {
        var candidate = Candidate("legacy", 0);
        var state = State();
        var replay = new IntradayPortfolioReplayOutcome([], [], [], [], state);
        var wire = RunPortfolioStateProjection.Project(IntradayDecisionEvidenceProjection.Attach(state, [candidate], replay))!;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.SerializeToNode(wire, options)!;
        json["intradayDecisions"]![0]!.AsObject().Remove("entryOrderMode");
        var detail = Assert.Single(json.Deserialize<RunPortfolioStateResponse>(options)!.IntradayDecisions!);
        Assert.Equal("next_bar_limit", detail.EntryOrderMode);
        Assert.Null(detail.TriggerPrice);
        var lines = IntradayDecisionPresentation.Lines(detail);
        Assert.Contains(lines, line => line.Contains("Limit price:", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Breakout candle B", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("Opening range high", StringComparison.Ordinal));
    }

    /// <summary>A target filled at an opening cannot acquire protection decided at that same timestamp afterward.</summary>
    [Fact]
    public void SharedDetails_PreserveFrozenIntentAndOnlyProtectionEffectiveBeforeExit()
    {
        var candidate = Candidate("first", 0);
        var exit = Start.AddMinutes(20);
        var state = State() with { Fills = [new("first", "day", "ABC", true, true, 10, 100, 1, Start.AddMinutes(10), new(2026, 10, 1))] };
        var trade = new BacktestTrade("ABC", "Day", "long", Start.AddMinutes(10), exit, 10, 100, 103.5m,
            99, 103, "take_profit", 35, 2, 33, StrategyId: "day", CandidateId: "first");
        var replay = new IntradayPortfolioReplayOutcome([trade], [], [], [
            new("first", "ABC", Start.AddMinutes(10), "protection_initialized", "actual_fill_three_r_initial_protection", StopPrice: 99, TargetPrice: 103),
            new("first", "ABC", exit, "protection_updated", "strong_continuation", StopPrice: 101, TargetPrice: 104, EffectiveAfterUtc: exit),
            new("first", "ABC", exit, "resolved", "take_profit", StopPrice: 101, TargetPrice: 104)
        ], state);
        var projected = IntradayDecisionEvidenceProjection.Attach(state, [candidate], replay);
        var wire = RunPortfolioStateProjection.Project(projected)!;
        var json = JsonSerializer.Serialize(wire, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var detail = Assert.Single(JsonSerializer.Deserialize<RunPortfolioStateResponse>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.IntradayDecisions!);
        Assert.Equal(100, detail.PreviousSessionHigh);
        Assert.Equal(95, detail.PreviousSessionLow);
        Assert.Equal("shallow_pullback", detail.EntryMethod);
        Assert.Equal(Start, detail.BreakoutTimestampUtc);
        Assert.Equal(Start.AddMinutes(5), detail.ConfirmationTimestampUtc);
        Assert.Equal(Start.AddMinutes(10), detail.ActiveAtUtc);
        Assert.Equal(Start.AddMinutes(15), detail.ExpiresAtUtc);
        Assert.Equal(Start.AddHours(6).AddMinutes(25), detail.SessionDeadlineUtc);
        Assert.Equal(99, detail.FrozenStopPrice);
        Assert.Equal(99, detail.CurrentStopPrice);
        Assert.Equal(103, detail.TargetPrice);
        Assert.Equal(100, detail.ActualEntryPrice);
        Assert.Equal("closed", detail.Status);
        Assert.Equal("take_profit", detail.ResolutionReason);
        Assert.Null(detail.ProtectionEffectiveAfterUtc);
        Assert.Contains(IntradayDecisionPresentation.Lines(detail), line => line.Contains("Last active stop: 99", StringComparison.Ordinal));
    }

    /// <summary>A real gap fill through its structural stop must display no valid target rather than a fabricated prefill 3R price.</summary>
    [Fact]
    public void GapFillWithoutPositiveRisk_PreservesActualEntryAndAbsentTarget()
    {
        var candidate = Candidate("gap", 0);
        var state = State() with { Fills = [new("gap", "day", "ABC", true, true, 10, 98, 1, Start.AddMinutes(10), new(2026, 10, 1))] };
        var replay = new IntradayPortfolioReplayOutcome([], [], [], [
            new("gap", "ABC", Start.AddMinutes(10), "protection_initialized", "gap_fill_has_no_positive_r_target", StopPrice: 99, TargetPrice: 0),
            new("gap", "ABC", Start.AddMinutes(10), "resolved", "gap_fill_through_structural_stop", StopPrice: 99, TargetPrice: 0)
        ], state);
        var detail = Assert.Single(RunPortfolioStateProjection.Project(
            IntradayDecisionEvidenceProjection.Attach(state, [candidate], replay))!.IntradayDecisions!);
        Assert.Equal(98, detail.ActualEntryPrice);
        Assert.Equal(99, detail.FrozenStopPrice);
        Assert.Null(detail.TargetPrice);
        Assert.Equal("gap_fill_through_structural_stop", detail.ResolutionReason);
        Assert.Contains(IntradayDecisionPresentation.Lines(detail), line => line == "Target (planned/last active): not placed / unavailable");
    }

    /// <summary>The display remains bounded while its total makes omitted older hypotheses explicit.</summary>
    [Fact]
    public void Projection_RetainsFiftyMostRecentHypothesesAndOriginalTotal()
    {
        var candidates = Enumerable.Range(0, 51).Select(index => Candidate("id-" + index, index)).ToArray();
        var state = State();
        var replay = new IntradayPortfolioReplayOutcome([], [], [], [], state);
        var result = IntradayDecisionEvidenceProjection.Attach(state, candidates, replay);
        Assert.Equal(51, result.IntradayDecisionCount);
        Assert.Equal(IntradayDecisionEvidenceProjection.MaximumDisplayDecisions, result.IntradayDecisions.Count);
        Assert.DoesNotContain(result.IntradayDecisions, detail => detail.CandidateId == "id-0");
        Assert.Equal("id-50", result.IntradayDecisions[0].CandidateId);
    }

    /// <summary>Creates only frozen intent; no strategy or fill rule executes inside these projection tests.</summary>
    private static BacktestCandidateTrade Candidate(string id, int minute) => new("ABC", "Day", "long", Start.AddMinutes(10 + minute),
        100, 99, 103, null, null, "pending", 1, StrategyId: "day", CandidateId: id) {
        IntradayExecution = new(100, .01m, Start.AddMinutes(10), Start.AddMinutes(15), Start.AddHours(6).AddMinutes(25),
            100, 95, Start, Start.AddMinutes(5), "shallow_pullback")
    };

    /// <summary>Provides an isolated terminal ledger sufficient for copying evidence into shared contracts.</summary>
    private static BacktestPortfolioState State() => new(IntradayPortfolioExecution.ValuationBasis, Start.AddHours(7),
        0, 0, 0, [], [], [], [], []) { StartUtc = Start };
}
