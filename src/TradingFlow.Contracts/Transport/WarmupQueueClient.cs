using TradingFlow.Contracts.V1;

namespace TradingFlow.Contracts.Transport;

public sealed partial class TradingWorkflowClient
{
    /// <summary>Reads durable warmup acceptance/outcomes independently of completed cache runs.</summary>
    public Task<WarmupQueueStatus[]> WarmupJobsAsync(CancellationToken token = default) =>
        GetAsync<WarmupQueueStatus[]>("warmup/jobs", token);

    /// <summary>Requests cancellation through the same server route used by Web.</summary>
    public Task<WarmupQueueStatus> CancelWarmupJobAsync(string runId, CancellationToken token = default) =>
        PostAsync<object, WarmupQueueStatus>($"warmup/jobs/{Uri.EscapeDataString(runId)}/cancel", new { }, token);

    /// <summary>Retries captured archive delivery without rerunning provider collection.</summary>
    public Task<WarmupArchiveRetryResponse> RetryWarmupArchivesAsync(string runId, CancellationToken token = default) =>
        PostAsync<object, WarmupArchiveRetryResponse>($"warmup/runs/{Uri.EscapeDataString(runId)}/archive/retry", new { }, token);
}
