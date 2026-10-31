using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Backtesting;

/// <summary>Retains private projections and journals until an independent quant audit approves their removal.</summary>
internal sealed class BacktestRunWorkspace
{
    private readonly ILogger logger;
    private readonly BacktestRunWorkspaceManifest ownership;
    private int executionStarted;
    public BacktestRunner Runner { get; }
    public BacktestRunConfig Run { get; }
    public string InputRoot { get; }
    public string ResultPath { get; }

    /// <summary>Records the uniquely generated spool owner outside its disposable directory before execution starts.</summary>
    public BacktestRunWorkspace(BacktestRunner runner, BacktestRunConfig run, string inputRoot,
        string resultOwner, ILogger logger, string resultPath)
    {
        Runner = runner;
        Run = run;
        InputRoot = Path.GetFullPath(inputRoot);
        ResultPath = Path.GetFullPath(resultPath);
        this.logger = logger;
        ownership = new(1, InputRoot, Path.GetFullPath(resultOwner), ResultPath,
            Path.GetFullPath(run.RawRoot), "pending", null, null);
        BacktestRunInputCleanup.WriteManifest(ownership);
    }

    /// <summary>Releases in-memory prepared data on every outcome, retaining disk inputs and a terminal record for later quant review.</summary>
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> execute)
    {
        if (Interlocked.Exchange(ref executionStarted, 1) != 0)
            throw new InvalidOperationException("A terminal or running workspace cannot execute again; start a new owned workspace.");
        Exception? primaryFailure = null;
        var outcome = "completed";
        try { return await execute(); }
        catch (OperationCanceledException failure) { primaryFailure = failure; outcome = "cancelled"; throw; }
        catch (Exception failure) { primaryFailure = failure; outcome = "failed"; throw; }
        finally
        {
            Runner.ReleasePreparedData();
            try
            {
                var resultSha256 = File.Exists(ResultPath) ? BacktestRunInputCleanup.FileSha256(ResultPath) : null;
                BacktestRunInputCleanup.WriteManifest(ownership with
                {
                    Outcome = outcome, FinishedAtUtc = DateTimeOffset.UtcNow, ResultSha256 = resultSha256
                });
            }
            catch (Exception manifestFailure)
            {
                logger.LogError(manifestFailure, "Backtest terminal workspace record failed for {InputRoot}; inputs remain retained", InputRoot);
                if (primaryFailure is null) throw;
                primaryFailure.Data["InputRetentionManifestFailure"] = manifestFailure.Message;
            }
        }
    }
}
