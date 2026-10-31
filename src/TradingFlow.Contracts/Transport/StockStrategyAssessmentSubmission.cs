using TradingFlow.Contracts.V1;

namespace TradingFlow.Contracts.Transport;

/// <summary>Retains one observation request across an uncertain HTTP response; a retry cannot silently create a second job.</summary>
public sealed class StockStrategyAssessmentSubmission
{
    private long connectionVersion;
    public StartStockStrategyAssessmentRequest Request { get; }
    public StockStrategyAssessmentJob? Job { get; private set; }
    public bool IsTerminal => Job?.Status is "completed" or "failed" or "cancelled";

    /// <summary>Copies exact selection inputs and allocates one key for this collection attempt.</summary>
    public StockStrategyAssessmentSubmission(StrategyReference strategy, string source, IReadOnlyList<string> symbols)
    {
        Request = new(ContractVersions.VersionOne, Guid.NewGuid().ToString("N"), strategy, source, symbols.ToArray());
    }

    /// <summary>Retries an ambiguous submission with its original key, then polls only the known durable job.</summary>
    public async Task<StockStrategyAssessmentJob> ResolveAsync(TradingWorkflowClient client, CancellationToken token = default)
    {
        var version = Interlocked.Read(ref connectionVersion);
        var job = Job is null ? await client.StartStockStrategyAssessmentAsync(Request, token) :
            IsTerminal ? Job : await client.StockStrategyAssessmentJobAsync(Job.JobId, token);
        token.ThrowIfCancellationRequested();
        if (version != Interlocked.Read(ref connectionVersion)) throw new OperationCanceledException("The strategy assessment connection changed.");
        if (job.Strategy != Request.Strategy || Job is not null && job.JobId != Job.JobId)
            throw new InvalidDataException("Strategy assessment response does not match this collection request.");
        Job = job;
        return job;
    }

    /// <summary>After authentication/backend changes, retain the request key but resolve its owner-scoped job again via POST.</summary>
    public void ForgetConnectionResult()
    {
        Interlocked.Increment(ref connectionVersion);
        Job = null;
    }
}
