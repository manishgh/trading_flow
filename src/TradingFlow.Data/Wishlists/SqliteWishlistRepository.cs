using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Wishlists;

namespace TradingFlow.Data.Wishlists;

public sealed class SqliteWishlistRepository : IWishlistRepository
{
    private readonly IDbContextFactory<TradingFlowDbContext> dbFactory;

    public SqliteWishlistRepository(IDbContextFactory<TradingFlowDbContext> dbFactory)
    {
        this.dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<Wishlist>> ListAsync(CancellationToken cancellationToken)
    {
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Wishlists
            .AsNoTracking()
            .Include(wishlist => wishlist.Items)
            .OrderByDescending(wishlist => wishlist.IsDefault)
            .ThenBy(wishlist => wishlist.Name)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<Wishlist?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Wishlists
            .AsNoTracking()
            .Include(wishlist => wishlist.Items)
            .FirstOrDefaultAsync(wishlist => wishlist.Id == id, cancellationToken);
    }

    public async Task<Wishlist?> GetByNameAsync(string name, CancellationToken cancellationToken)
    {
        var normalizedName = NormalizeName(name);
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Wishlists
            .AsNoTracking()
            .Include(wishlist => wishlist.Items)
            .FirstOrDefaultAsync(wishlist => wishlist.Name == normalizedName, cancellationToken);
    }

    public async Task<Wishlist> SaveAsync(Wishlist wishlist, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wishlist);
        var now = DateTimeOffset.UtcNow;
        var normalizedName = NormalizeName(wishlist.Name);

        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (wishlist.IsDefault)
        {
            await db.Wishlists
                .Where(existing => existing.IsDefault && existing.Id != wishlist.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(existing => existing.IsDefault, false), cancellationToken);
        }

        var existingWishlist = wishlist.Id == Guid.Empty
            ? null
            : await db.Wishlists.Include(existing => existing.Items).FirstOrDefaultAsync(existing => existing.Id == wishlist.Id, cancellationToken);

        if (existingWishlist is null)
        {
            wishlist.Id = wishlist.Id == Guid.Empty ? Guid.NewGuid() : wishlist.Id;
            wishlist.Name = normalizedName;
            wishlist.CreatedAtUtc = wishlist.CreatedAtUtc == default ? now : wishlist.CreatedAtUtc;
            wishlist.UpdatedAtUtc = now;
            foreach (var item in wishlist.Items)
            {
                item.Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id;
                item.WishlistId = wishlist.Id;
                item.Ticker = NormalizeTicker(item.Ticker);
                item.AddedAtUtc = item.AddedAtUtc == default ? now : item.AddedAtUtc;
            }

            db.Wishlists.Add(wishlist);
            await db.SaveChangesAsync(cancellationToken);
            return wishlist;
        }

        existingWishlist.Name = normalizedName;
        existingWishlist.Description = wishlist.Description;
        existingWishlist.IsDefault = wishlist.IsDefault;
        existingWishlist.IncludeExtendedHours = wishlist.IncludeExtendedHours;
        existingWishlist.IsObserved = wishlist.IsObserved;
        existingWishlist.UpdatedAtUtc = now;

        await db.SaveChangesAsync(cancellationToken);
        return existingWishlist;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Wishlists.FindAsync([id], cancellationToken);
        if (existing is null)
        {
            return;
        }

        db.Wishlists.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetObservedAsync(Guid id, bool isObserved, CancellationToken cancellationToken)
    {
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Wishlists.FindAsync([id], cancellationToken);
        if (existing is null)
        {
            throw new InvalidOperationException($"Wishlist {id} does not exist.");
        }

        existing.IsObserved = isObserved;
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<WishlistItem> AddOrUpdateItemAsync(Guid wishlistId, string ticker, string? displayName, string? notes, CancellationToken cancellationToken)
    {
        var normalizedTicker = NormalizeTicker(ticker);
        var now = DateTimeOffset.UtcNow;
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var wishlistExists = await db.Wishlists.AnyAsync(wishlist => wishlist.Id == wishlistId, cancellationToken);
        if (!wishlistExists)
        {
            throw new InvalidOperationException($"Wishlist {wishlistId} does not exist.");
        }

        var existing = await db.WishlistItems
            .FirstOrDefaultAsync(item => item.WishlistId == wishlistId && item.Ticker == normalizedTicker, cancellationToken);
        if (existing is null)
        {
            existing = new WishlistItem
            {
                Id = Guid.NewGuid(),
                WishlistId = wishlistId,
                Ticker = normalizedTicker,
                DisplayName = NormalizeOptional(displayName),
                Notes = NormalizeOptional(notes),
                Active = true,
                AddedAtUtc = now
            };
            db.WishlistItems.Add(existing);
        }
        else
        {
            existing.DisplayName = NormalizeOptional(displayName) ?? existing.DisplayName;
            existing.Notes = NormalizeOptional(notes);
            existing.Active = true;
        }

        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task RemoveItemAsync(Guid wishlistId, string ticker, CancellationToken cancellationToken)
    {
        var normalizedTicker = NormalizeTicker(ticker);
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.WishlistItems
            .FirstOrDefaultAsync(item => item.WishlistId == wishlistId && item.Ticker == normalizedTicker, cancellationToken);
        if (existing is null)
        {
            return;
        }

        existing.Active = false;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<WishlistSignal> AddSignalAsync(WishlistSignal signal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signal);
        signal.Id = signal.Id == Guid.Empty ? Guid.NewGuid() : signal.Id;
        signal.Ticker = NormalizeTicker(signal.Ticker);
        signal.SignalType = NormalizeName(signal.SignalType);
        signal.Severity = String.IsNullOrWhiteSpace(signal.Severity) ? "info" : signal.Severity.Trim().ToLowerInvariant();
        signal.DetectedAtUtc = signal.DetectedAtUtc == default ? DateTimeOffset.UtcNow : signal.DetectedAtUtc;
        signal.Reason = signal.Reason.Trim();
        signal.SnapshotJson = String.IsNullOrWhiteSpace(signal.SnapshotJson) ? "{}" : signal.SnapshotJson;

        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.WishlistSignals.Add(signal);
        await db.SaveChangesAsync(cancellationToken);
        return signal;
    }

    public async Task<IReadOnlyList<WishlistSignal>> GetSignalsAsync(Guid? wishlistId, string? ticker, DateTimeOffset sinceUtc, int limit, CancellationToken cancellationToken)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);
        var normalizedTicker = String.IsNullOrWhiteSpace(ticker) ? null : NormalizeTicker(ticker);
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var parameters = new List<object>();
        var clauses = new List<string>();
        var parameterIndex = 0;

        clauses.Add($"DetectedAtUtc >= {{{parameterIndex++}}}");
        parameters.Add(sinceUtc);
        if (wishlistId.HasValue)
        {
            clauses.Add($"WishlistId = {{{parameterIndex++}}}");
            parameters.Add(wishlistId.Value);
        }

        if (normalizedTicker is not null)
        {
            clauses.Add($"Ticker = {{{parameterIndex++}}}");
            parameters.Add(normalizedTicker);
        }

        parameters.Add(safeLimit);
        var sql = $"SELECT * FROM WishlistSignals WHERE {String.Join(" AND ", clauses)} ORDER BY DetectedAtUtc DESC LIMIT {{{parameterIndex}}}";
        return await db.WishlistSignals
            .FromSqlRaw(sql, parameters.ToArray())
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);
    }

    public async Task AcknowledgeSignalAsync(Guid signalId, CancellationToken cancellationToken)
    {
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.WishlistSignals.FindAsync([signalId], cancellationToken);
        if (existing is null)
        {
            return;
        }

        existing.Acknowledged = true;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string NormalizeName(string value)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty.", nameof(value));
        }

        return value.Trim();
    }

    private static string NormalizeTicker(string value)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Ticker cannot be empty.", nameof(value));
        }

        return value.Trim().ToUpperInvariant();
    }

    private static string? NormalizeOptional(string? value) => String.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

