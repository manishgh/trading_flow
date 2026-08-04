using System.Collections.Concurrent;

namespace TradingFlow.Web.Services.Wishlists;

public sealed class StockPulseReceiverService
{
    private readonly ConcurrentBag<string> recentPulses = new();

    public void RegisterPulse(string ticker)
    {
        if (!string.IsNullOrWhiteSpace(ticker))
        {
            recentPulses.Add(ticker.Trim().ToUpperInvariant());
        }
    }

    public IReadOnlyList<string> ConsumePulses()
    {
        var items = recentPulses.Distinct().ToList();
        recentPulses.Clear();
        return items;
    }
}
