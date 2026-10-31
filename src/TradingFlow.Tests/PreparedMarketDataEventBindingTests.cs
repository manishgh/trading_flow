using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class PreparedMarketDataEventBindingTests
{
    /// <summary>The existing SQLite journal keeps one immutable selection across host instances and rejects a replacement.</summary>
    [Fact]
    public async Task Binding_PersistsOnceAndRejectsConflictingSelection()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var store = new PreparedMarketDataEventBindingStore(database.Workflows);
        var now = DateTimeOffset.Parse("2026-10-21T12:00:00Z");
        var summary = new PreparedMarketDataSummary("comparison-test", new string('b', 64), now,
            now.AddMonths(-6), now.AddDays(-1), ["synthetic"], ["test"], ["all"], 0, 2, "Synthetic evidence only.");
        var binding = new PreparedMarketDataBinding(new string('a', 64), "synthetic-snapshot.json", summary.SnapshotSha256, summary);
        Assert.Null(await store.GetAsync("comparison-test", default));
        await Task.WhenAll(store.BindAsync("comparison-test", binding, default), store.BindAsync("comparison-test", binding, default));
        var reloaded = new PreparedMarketDataEventBindingStore(database.Workflows);
        Assert.Equal(JsonSerializer.Serialize(binding), JsonSerializer.Serialize(await reloaded.GetAsync("comparison-test", default)));
        await Assert.ThrowsAsync<InvalidDataException>(() => reloaded.BindAsync("comparison-test",
            binding with { SnapshotSha256 = new string('c', 64) }, default));
        Assert.Single(await database.Workflows.ListAsync("backtest-candles:comparison-test", 0, 10, default));
    }

    /// <summary>A conflicting journal record fails instead of silently treating the first or latest payload as authoritative.</summary>
    [Fact]
    public async Task ConflictingJournalEntries_AreRejectedOnRead()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        await database.Workflows.AppendAsync("backtest-candles:comparison-test", "backtest.candles.selected", DateTimeOffset.UtcNow, "{}", default);
        await database.Workflows.AppendAsync("backtest-candles:comparison-test", "backtest.candles.selected", DateTimeOffset.UtcNow, "{\"different\":true}", default);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PreparedMarketDataEventBindingStore(database.Workflows).GetAsync("comparison-test", default));
    }
}
