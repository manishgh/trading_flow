using TradingFlow.Backtesting;

namespace TradingFlow.Cli;

/// <summary>Normal reusable operation for removing owned run intermediates after the independent quant audit.</summary>
public static class BacktestInputCleanupCommand
{
    public const string Name = "backtest-cleanup-reviewed-inputs";

    /// <summary>Uses one saved exact-file review; cleanup can leave protected normalized originals at their existing paths.</summary>
    public static async Task RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken = default)
    {
        if (args.Length != 2 || args[0] != "--review")
            throw new ArgumentException("Usage: backtest-cleanup-reviewed-inputs --review <saved-quant-review.json>.");
        var inputRoot = await BacktestRunInputCleanup.CleanupAsync(args[1], cancellationToken);
        await output.WriteLineAsync($"Cleaned reviewed disposable inputs; protected files remain in place: {inputRoot}");
    }
}
