using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Backtesting;

/// <summary>Run-owned writable inputs. Published outputs and read-only CSV sources are never deletion targets.</summary>
internal sealed class BacktestRunWorkspace(BacktestRunner runner, BacktestRunConfig run,
    string inputRoot, string resultOwner, ILogger logger)
{
    public BacktestRunner Runner { get; } = runner;
    public BacktestRunConfig Run { get; } = run;
    public string InputRoot { get; } = inputRoot;
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> execute)
    {
        Exception? primaryFailure = null;
        try { return await execute(); }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            try
            {
                var resolved = Path.GetFullPath(InputRoot);
                if (Path.GetDirectoryName(resolved) != Path.GetFullPath(resultOwner) ||
                    !Path.GetFileName(resolved).StartsWith(".run-inputs-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Run input cleanup escaped its owned result directory.");
                if (Directory.Exists(resolved))
                {
                    if ((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("Run input cleanup refused a reparse point.");
                    for (var attempt = 0; ; attempt++)
                    {
                        try { Directory.Delete(resolved, recursive: true); break; }
                        catch (IOException) when (attempt < 4) { await Task.Delay(100); }
                    }
                }
            }
            catch (Exception cleanupFailure)
            {
                logger.LogError(cleanupFailure, "Backtest input cleanup failed for {InputRoot}", InputRoot);
                if (primaryFailure is null) throw;
                primaryFailure.Data["InputCleanupFailure"] = cleanupFailure.Message;
            }
        }
    }
}
