using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Wishlists;

namespace TradingFlow.Web.Services.Wishlists;

/// <summary>
/// Named Finviz screens the operator keeps.
///
/// Finviz Elite exposes exactly one screener call - hand it filters, get a CSV
/// back. There is no endpoint that lists the screens you saved in the Finviz
/// browser UI, so the list of named screens has to live here. That is what this
/// is: the local catalogue, not a mirror of anything remote.
///
/// A preset is scoped to a horizon because an intraday screen and a swing screen
/// answer different questions and must not be substituted for one another. The
/// horizon also decides the discard rule: an intraday result belongs to one
/// session.
/// </summary>
public sealed class ScreenerPresetService
{
    private readonly IDbContextFactory<TradingFlowDbContext> dbFactory;
    private readonly TimeProvider timeProvider;

    public ScreenerPresetService(
        IDbContextFactory<TradingFlowDbContext> dbFactory,
        TimeProvider timeProvider)
    {
        this.dbFactory = dbFactory;
        this.timeProvider = timeProvider;
    }

    /// <summary>Every saved preset, newest first, optionally narrowed to one horizon.</summary>
    public async Task<IReadOnlyList<ScreenerPreset>> ListAsync(
        ScreenerScope? scope,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.ScreenerPresets.AsNoTracking();
        if (scope is { } wanted)
        {
            var category = wanted.ToString();
            query = query.Where(preset => preset.Category == category);
        }

        return await query
            .OrderBy(preset => preset.Category)
            .ThenBy(preset => preset.Name)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Creates or updates a preset by name within its horizon.
    /// </summary>
    /// <remarks>
    /// Saving is idempotent on (name, horizon): re-saving the same name replaces
    /// the query rather than accumulating near-duplicates the operator then has
    /// to tell apart. Editing the query clears the verification, because a
    /// verification describes the query that was scored, not the name.
    /// </remarks>
    public async Task<ScreenerPreset> SaveAsync(
        string name,
        ScreenerScope scope,
        string filterQuery,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(filterQuery);

        var trimmedName = name.Trim();
        var category = scope.ToString();
        var normalizedQuery = NormalizeQuery(filterQuery);
        var now = timeProvider.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.ScreenerPresets
            .FirstOrDefaultAsync(
                preset => preset.Name == trimmedName && preset.Category == category,
                cancellationToken);

        if (existing is null)
        {
            existing = new ScreenerPreset
            {
                Id = Guid.NewGuid(),
                Name = trimmedName,
                Category = category,
                FilterQuery = normalizedQuery,
                IsVerified = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.ScreenerPresets.Add(existing);
        }
        else
        {
            if (!String.Equals(existing.FilterQuery, normalizedQuery, StringComparison.Ordinal))
            {
                // The score belonged to the old query. Keeping it would let a
                // rewritten screen inherit a verification it never earned.
                existing.IsVerified = false;
                existing.AverageMlScore = null;
            }
            existing.FilterQuery = normalizedQuery;
            existing.UpdatedAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task DeleteAsync(Guid presetId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var preset = await db.ScreenerPresets.FindAsync([presetId], cancellationToken);
        if (preset is null)
        {
            return;
        }

        db.ScreenerPresets.Remove(preset);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Reduces the three shapes an operator has to hand - a Finviz Elite URL, a
    /// bare <c>f=</c> string, or the filter list itself - to the filter list.
    /// </summary>
    /// <remarks>
    /// The same normalisation the desk applies when reading an ad-hoc query, so a
    /// screen saved from a pasted URL and the same screen typed by hand produce
    /// one preset rather than two.
    /// </remarks>
    internal static string NormalizeQuery(string input)
    {
        var trimmed = input.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var filter = System.Web.HttpUtility.ParseQueryString(uri.Query)["f"];
            return String.IsNullOrWhiteSpace(filter) ? trimmed : filter;
        }

        return trimmed.StartsWith("f=", StringComparison.OrdinalIgnoreCase) ? trimmed[2..] : trimmed;
    }
}
