using System.Collections;
using System.Text.Json;
using TradingFlow.Backtesting.Optimization;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Optimization;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Tests;

public sealed class OptimizationSearchSpaceTests
{
    /// <summary>Preserves the old nested-loop order and independent parameter dictionaries while evaluating lazily.</summary>
    [Fact]
    public void ParameterGrid_PreservesOrderAndDoesNotShareMutableCombinations()
    {
        var parameters = new Dictionary<string, IReadOnlyList<object>> {
            ["entry_rules.min_entry_rsi"] = [20, 30],
            ["exit_rules.target_r_multiple"] = [1m, 2m, 3m]
        };
        Assert.Equal(6, StrategyOptimizer.CountPermutations(parameters, 6));
        var combinations = StrategyOptimizer.EnumeratePermutations(parameters).ToArray();
        Assert.Equal(new[] { (20, 1m), (20, 2m), (20, 3m), (30, 1m), (30, 2m), (30, 3m) },
            combinations.Select(row => ((int)row["entry_rules.min_entry_rsi"], (decimal)row["exit_rules.target_r_multiple"])));
        Assert.Equal(combinations.Length, combinations.Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    /// <summary>A search without tuned parameters still performs the existing single baseline evaluation.</summary>
    [Fact]
    public void EmptyParameterSet_IsOneBaselineCombination()
    {
        var parameters = new Dictionary<string, IReadOnlyList<object>>();
        Assert.Equal(1, StrategyOptimizer.CountPermutations(parameters, 1));
        Assert.Empty(Assert.Single(StrategyOptimizer.EnumeratePermutations(parameters)));
    }

    /// <summary>Rejects huge products from counts alone, before reading values or overflowing the product.</summary>
    [Fact]
    public void ExcessiveProduct_RejectsBeforeValueAccessOrIntegerOverflow()
    {
        var parameters = new Dictionary<string, IReadOnlyList<object>> {
            ["first"] = new CountOnlyValues(Int32.MaxValue),
            ["second"] = new CountOnlyValues(Int32.MaxValue)
        };
        Assert.Throws<InvalidOperationException>(() => StrategyOptimizer.CountPermutations(parameters, Int32.MaxValue));
    }

    /// <summary>Taking one combination reads only its values even when a dimension advertises billions of choices.</summary>
    [Fact]
    public void FirstCombination_DoesNotMaterializeTheCartesianProduct()
    {
        var parameters = new Dictionary<string, IReadOnlyList<object>> { ["first"] = new FirstValueOnlyValues() };
        Assert.Equal(42, StrategyOptimizer.EnumeratePermutations(parameters).First()["first"]);
    }

    /// <summary>Includes the known frozen stock count in the existing strategy-work budget.</summary>
    [Fact]
    public void ConfigurationBudget_CountsCombinationsAcrossStocks()
    {
        var run = ReadRun();
        run = run with { Tickers = ["AAA", "BBB", "CCC"], Engine = run.Engine with { MaxStrategyWorkItems = 12 } };
        var config = new OptimizationConfig("fixture", "unused", "unused", "NetProfit", 2,
            new Dictionary<string, IReadOnlyList<object>> { ["exit_rules.target_r_multiple"] = [1, 2, 3, 4] });
        Assert.Equal(4, StrategyOptimizer.ValidateConfiguration(config, run));
        Assert.Throws<InvalidOperationException>(() => StrategyOptimizer.ValidateConfiguration(config, run with {
            Engine = run.Engine with { MaxStrategyWorkItems = 11 }
        }));
    }

    /// <summary>Misspelled objectives never silently rank total return, even for an otherwise empty result.</summary>
    [Theory]
    [InlineData("TotalRetunPct")]
    [InlineData("")]
    [InlineData("sharpe")]
    public void UnknownMetric_IsRejectedBeforeSearch(string metric)
    {
        var config = new OptimizationConfig("fixture", "unused", "unused", metric, 1,
            new Dictionary<string, IReadOnlyList<object>>());
        Assert.Throws<ArgumentException>(() => StrategyOptimizer.ValidateConfiguration(config, ReadRun()));
        Assert.Throws<ArgumentException>(() => StrategyOptimizer.ExtractMetric(Result(), "target", metric));
    }

    /// <summary>Reads only the named strategy and preserves existing objective units and drawdown direction.</summary>
    [Theory]
    [InlineData("TotalReturnPct", 12)]
    [InlineData("AverageDailyReturnPct", 2)]
    [InlineData("NetProfit", 1200)]
    [InlineData("MaxDrawdownPct", -7)]
    [InlineData("WinRate", 80)]
    public void Metric_UsesExactStrategyRatherThanFirstResult(string metric, decimal expected)
    {
        Assert.Equal(expected, StrategyOptimizer.ExtractMetric(Result(), "target", metric));
        Assert.Throws<InvalidOperationException>(() => StrategyOptimizer.ExtractMetric(Result(), "absent", metric));
    }

    /// <summary>Rejects empty axes instead of turning a requested search into a different baseline.</summary>
    [Fact]
    public void EmptyParameterAxis_IsRejected()
    {
        var parameters = new Dictionary<string, IReadOnlyList<object>> { ["exit_rules.target_r_multiple"] = [] };
        Assert.Throws<ArgumentException>(() => StrategyOptimizer.CountPermutations(parameters, 10));
    }

    /// <summary>Checks the same empty-axis rule at the actual YAML boundary and deletes the fixture afterward.</summary>
    [Fact]
    public void EmptyYamlParameterAxis_IsRejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                run_name: empty-axis
                base_strategy: unused.yaml
                backtest_config: unused.yaml
                metric: NetProfit
                parameters:
                  exit_rules.target_r_multiple: []
                """);
            Assert.Throws<InvalidDataException>(() => new SimpleYamlReader().ReadOptimizationConfig(path));
        }
        finally { File.Delete(path); }
    }

    /// <summary>Loads only source-controlled profile choices; no market/provider work occurs.</summary>
    private static BacktestRunConfig ReadRun() => new SimpleYamlReader().ReadBacktestRun(
        Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "swing-backtest-profile.yaml"));

    /// <summary>Supplies two intentionally different strategy outcomes to exercise identity-based objective selection.</summary>
    private static BacktestResult Result() => JsonSerializer.Deserialize<BacktestResult>("""
        { "strategyResults": [
            { "strategyId": "other", "totalReturnPct": 999, "netProfit": 99999 },
            { "strategyId": "target", "totalReturnPct": 12, "averageDailyReturnPct": 2,
              "netProfit": 1200, "maxDrawdownPct": 7, "acceptedTradeCount": 5, "winningTradeCount": 4 }
        ] }
        """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private sealed class CountOnlyValues(int count) : IReadOnlyList<object>
    {
        public int Count => count;
        public object this[int index] => throw new InvalidOperationException("Product validation must not read parameter values.");
        /// <summary>Prevents accidental full-axis enumeration during budget validation.</summary>
        public IEnumerator<object> GetEnumerator() => throw new InvalidOperationException("Product validation must not enumerate parameter values.");
        /// <summary>Uses the same guard for nongeneric enumeration.</summary>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class FirstValueOnlyValues : IReadOnlyList<object>
    {
        public int Count => Int32.MaxValue;
        public object this[int index] => index == 0 ? 42 : throw new InvalidOperationException("Only the first combination was requested.");
        /// <summary>The grid must index the current combination, not enumerate the entire dimension.</summary>
        public IEnumerator<object> GetEnumerator() => throw new InvalidOperationException("Do not enumerate this large dimension.");
        /// <summary>Uses the same guard for nongeneric enumeration.</summary>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
