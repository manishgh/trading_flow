using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;

namespace TradingFlow.Web.Services.Charts;

/// <summary>Allows read-only charts for recorded app stocks and recently displayed authorized page members.</summary>
public sealed class ChartAccessPolicy(IDbContextFactory<TradingFlowDbContext> contexts, TimeProvider clock)
{
    private readonly object sync = new();
    private readonly Dictionary<string, Dictionary<string, DateTimeOffset>> displayed = new();
    private DateTimeOffset lastSweep;

    // Preserve a bounded union across tabs. Only server-rendered stock evidence can extend a read-only view grant.
    public void RecordDisplayed(ClaimsPrincipal user, IEnumerable<string> symbols, TimeSpan? lifetime = null)
    {
        var id = UserId(user);
        var now = clock.GetUtcNow();
        var age = lifetime ?? TimeSpan.FromMinutes(5);
        if (age <= TimeSpan.Zero || age > TimeSpan.FromMinutes(30)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        lock (sync)
        {
            if (now - lastSweep >= TimeSpan.FromMinutes(1))
            {
                foreach (var entry in displayed.ToArray())
                {
                    foreach (var symbol in entry.Value.Where(x => now >= x.Value).Select(x => x.Key).ToArray())
                        entry.Value.Remove(symbol);
                    if (entry.Value.Count == 0) displayed.Remove(entry.Key);
                }
                lastSweep = now;
            }
            if (!displayed.TryGetValue(id, out var recent))
            {
                if (displayed.Count >= 256) return;
                recent = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
                displayed[id] = recent;
            }
            foreach (var supplied in symbols.Take(100))
            {
                string symbol;
                try { symbol = ChartSeriesService.Normalize(new(supplied, "1d", "regular", "swing")).Symbol; }
                catch (ArgumentException) { continue; }
                var expires = now + age;
                if (!recent.TryGetValue(symbol, out var prior) || expires > prior) recent[symbol] = expires;
            }
            if (recent.Count > 2000)
                foreach (var expired in recent.OrderByDescending(x => x.Value).Skip(2000).Select(x => x.Key).ToArray())
                    recent.Remove(expired);
        }
    }

    // Local membership checks do not start discovery, research, account refreshes or provider subscriptions.
    public async Task<bool> CanReadAsync(ClaimsPrincipal user, string symbol, CancellationToken token)
    {
        var id = UserId(user);
        lock (sync)
            if (displayed.TryGetValue(id, out var recent) && recent.TryGetValue(symbol, out var expires) &&
                clock.GetUtcNow() < expires) return true;
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.WishlistItems.AsNoTracking().AnyAsync(x => x.Active && x.Ticker == symbol, token)
            || await db.OrderIntents.AsNoTracking().AnyAsync(x => x.Symbol == symbol, token)
            || await db.Candidates.AsNoTracking().AnyAsync(x => x.Symbol == symbol, token)
            || await db.DecisionAudits.AsNoTracking().AnyAsync(x => x.Ticker == symbol, token)
            || await db.NewsItems.AsNoTracking().AnyAsync(x => x.Ticker == symbol, token)
            || await db.EarningsCalendarEvents.AsNoTracking().AnyAsync(x => x.Ticker == symbol, token);
    }

    // Reuse the authenticated application identity; anonymous requests never acquire a chart budget.
    public static string UserId(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
            ? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException()
            : throw new UnauthorizedAccessException();
}
