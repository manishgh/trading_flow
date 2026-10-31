using Microsoft.Extensions.Options;
using TradingFlow.WarmupService.Models;

namespace TradingFlow.WarmupService.Services;

public sealed class WarmupQueueFullException(int maximum) : InvalidOperationException(
    $"The warmup queue is full (configured budget: {maximum}). No work was accepted.");

/// <summary>Uses the warmup host's existing atomic JSON state store; no application database or second writer is introduced.</summary>
public sealed class WarmupJobStore(IOptions<WarmupOptions> options)
    : WarmupJsonStore<WarmupQueuedJob>(options, "jobs.json")
{
    /// <summary>Persists acceptance before returning; a duplicate ID must name the same original request.</summary>
    public Task EnqueueAsync(WarmupJobRequest request, int maximum, CancellationToken token) => MutateAsync(items =>
    {
        var existing = items.FirstOrDefault(item => item.Request.RunId == request.RunId);
        if (existing is not null)
        {
            RequireSameRequest(existing.Request, request);
            return [];
        }
        if (items.Count(item => item.Status is "queued" or "running" or "cancelling") >= maximum)
            throw new WarmupQueueFullException(maximum);
        items.Add(new(request, "queued"));
        return [];
    }, token);

    /// <summary>A new exclusive host resumes interrupted work with the same frozen inputs; cancellation remains final.</summary>
    public Task RecoverAsync(CancellationToken token) => MutateAsync(items =>
    {
        for (var index = 0; index < items.Count; index++)
            if (items[index].Status is "running" or "cancelling")
                items[index] = items[index] with { Status = items[index].Status == "cancelling" ? "cancelled" : "queued" };
        return [];
    }, token);

    /// <summary>Claims the next accepted job while holding the single host's state-store lock.</summary>
    public async Task<WarmupJobRequest?> TakeAsync(CancellationToken token)
    {
        WarmupJobRequest? result = null;
        await MutateAsync(items =>
        {
            var index = items.FindIndex(item => item.Status == "queued");
            if (index >= 0)
            {
                result = items[index].Request;
                items[index] = items[index] with { Status = "running" };
            }
            return [];
        }, token);
        return result;
    }

    /// <summary>Records completion/failure or releases interrupted work; a requested cancellation cannot become success.</summary>
    public Task FinishAsync(string id, string status, string? error, CancellationToken token) => MutateAsync(items =>
    {
        var index = items.FindIndex(item => item.Request.RunId == id);
        if (index < 0) throw new InvalidDataException("Accepted warmup job disappeared.");
        items[index] = items[index] with { Status = items[index].Status == "cancelling" ? "cancelled" : status, Error = error };
        return [];
    }, token);

    /// <summary>Persists cancellation before the running worker receives its cancellation token.</summary>
    public async Task<bool> CancelAsync(string id, CancellationToken token)
    {
        var found = false;
        await MutateAsync(items =>
        {
            var index = items.FindIndex(item => item.Request.RunId == id);
            if (index < 0) return [];
            found = true;
            if (items[index].Status is "queued" or "running")
                items[index] = items[index] with { Status = items[index].Status == "queued" ? "cancelled" : "cancelling" };
            return [];
        }, token);
        return found;
    }

    /// <summary>Compares command intent independently from the transport retry's timestamp.</summary>
    public static void RequireSameRequest(WarmupJobRequest original, WarmupJobRequest retry)
    {
        var originalSymbols = (original.Tickers ?? []).Order(StringComparer.OrdinalIgnoreCase);
        var retrySymbols = (retry.Tickers ?? []).Order(StringComparer.OrdinalIgnoreCase);
        if (original.Reason != retry.Reason || !originalSymbols.SequenceEqual(retrySymbols, StringComparer.OrdinalIgnoreCase) ||
            original.CommandSha256 != retry.CommandSha256)
            throw new InvalidOperationException("This warmup retry key already names a different request.");
    }
}
