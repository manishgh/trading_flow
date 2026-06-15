using Microsoft.Extensions.Options;
using TradingFlow.WarmupService.Models;

namespace TradingFlow.WarmupService.Services;

public sealed class WarmupRunStore(IOptions<WarmupOptions> options)
    : WarmupJsonStore<WarmupRunRecord>(options, "runs.json")
{
    public Task AddAsync(WarmupRunRecord run, CancellationToken cancellationToken)
    {
        return MutateAsync(items =>
        {
            items.RemoveAll(x => x.RunId.Equals(run.RunId, StringComparison.OrdinalIgnoreCase));
            items.Add(run);
            items.Sort((left, right) => right.StartedAtUtc.CompareTo(left.StartedAtUtc));
            if (items.Count > 250)
            {
                items.RemoveRange(250, items.Count - 250);
            }

            return Array.Empty<WarmupRunRecord>();
        }, cancellationToken);
    }
}
