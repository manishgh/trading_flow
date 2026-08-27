using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Discovery;

namespace TradingFlow.Data.Discovery;

/// <summary>
/// Durable point-in-time discovery ledger. Source snapshots are append-only;
/// aggregate/source rows are the restartable current projection.
/// </summary>
public sealed class SqliteDiscoveryRepository(
    IDbContextFactory<TradingFlowDbContext> contextFactory) : IDiscoveryRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<long> GetSourceVersionAsync(
        Guid scopeId,
        string sourceKind,
        string sourceKey,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.DiscoverySnapshots
            .Where(snapshot =>
                snapshot.ScopeId == scopeId &&
                snapshot.SourceKind == NormalizeSourceKind(sourceKind) &&
                snapshot.SourceKey == NormalizeSourceKey(sourceKey))
            .Select(snapshot => (long?)snapshot.SourceVersion)
            .MaxAsync(cancellationToken) ?? 0L;
    }

    public async Task<DiscoverySnapshotCommit> CommitSnapshotAsync(
        DiscoverySnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(request);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.DiscoverySnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(snapshot =>
                snapshot.ScopeId == normalized.ScopeId &&
                snapshot.SourceKind == normalized.SourceKind &&
                snapshot.SourceKey == normalized.SourceKey &&
                snapshot.ObservationId == normalized.ObservationId,
                cancellationToken);
        if (existing is not null)
        {
            return await BuildDuplicateResultAsync(context, existing, normalized, cancellationToken);
        }

        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var actualVersion = await context.DiscoverySnapshots
            .Where(snapshot =>
                snapshot.ScopeId == normalized.ScopeId &&
                snapshot.SourceKind == normalized.SourceKind &&
                snapshot.SourceKey == normalized.SourceKey)
            .Select(snapshot => (long?)snapshot.SourceVersion)
            .MaxAsync(cancellationToken) ?? 0L;
        if (actualVersion != normalized.ExpectedSourceVersion)
        {
            throw new DiscoveryConcurrencyException(
                $"Discovery source {normalized.SourceKind}/{normalized.SourceKey} expected version " +
                $"{normalized.ExpectedSourceVersion} but current version is {actualVersion}.");
        }

        var latestObservation = await context.DiscoverySnapshots
            .Where(candidate =>
                candidate.ScopeId == normalized.ScopeId &&
                candidate.SourceKind == normalized.SourceKind &&
                candidate.SourceKey == normalized.SourceKey)
            .OrderByDescending(candidate => candidate.SourceVersion)
            .Select(candidate => (DateTimeOffset?)candidate.ObservedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (latestObservation is { } previous && normalized.ObservedAtUtc < previous)
        {
            throw new InvalidOperationException(
                $"Discovery source {normalized.SourceKind}/{normalized.SourceKey} observation " +
                $"{normalized.ObservedAtUtc:O} is older than committed observation {previous:O}.");
        }

        var snapshot = new DiscoverySnapshotRecord
        {
            SnapshotId = Guid.NewGuid(),
            ScopeId = normalized.ScopeId,
            ObservationId = normalized.ObservationId,
            SourceKind = normalized.SourceKind,
            SourceKey = normalized.SourceKey,
            Horizon = normalized.Horizon,
            SourceVersion = actualVersion + 1,
            ObservedAtUtc = normalized.ObservedAtUtc,
            ExpiresAtUtc = normalized.ExpiresAtUtc,
            ProviderTimestampUtc = normalized.ProviderTimestampUtc,
            IsDiagnostic = normalized.IsDiagnostic,
            RawReference = normalized.RawReference,
            ContentSha256 = ComputeSnapshotHash(normalized),
            SymbolsJson = JsonSerializer.Serialize(normalized.Symbols, JsonOptions)
        };
        context.DiscoverySnapshots.Add(snapshot);

        var existingLinks = await context.DiscoverySourceMemberships
            .Include(link => link.Aggregate)
            .Where(link =>
                link.Aggregate!.ScopeId == normalized.ScopeId &&
                link.SourceKind == normalized.SourceKind &&
                link.SourceKey == normalized.SourceKey)
            .ToDictionaryAsync(link => link.Aggregate!.Symbol, StringComparer.Ordinal, cancellationToken);
        var allAggregates = await context.DiscoveryAggregates
            .Include(aggregate => aggregate.Sources)
            .Where(aggregate => aggregate.ScopeId == normalized.ScopeId)
            .ToDictionaryAsync(aggregate => aggregate.Symbol, StringComparer.Ordinal, cancellationToken);

        var incoming = normalized.Symbols.ToDictionary(item => item.Symbol, StringComparer.Ordinal);
        var added = new List<string>();
        var retained = new List<string>();
        var potentiallyDropped = new HashSet<string>(StringComparer.Ordinal);

        foreach (var symbol in incoming.Keys)
        {
            if (!allAggregates.TryGetValue(symbol, out var aggregate))
            {
                aggregate = new DiscoveryAggregateRecord
                {
                    AggregateId = Guid.NewGuid(),
                    ScopeId = normalized.ScopeId,
                    Symbol = symbol,
                    Horizon = normalized.Horizon,
                    FirstDiscoveredAtUtc = normalized.ObservedAtUtc,
                    LastObservedAtUtc = normalized.ObservedAtUtc,
                    ExpiresAtUtc = normalized.ExpiresAtUtc,
                    IsActive = true,
                    Version = 1
                };
                context.DiscoveryAggregates.Add(aggregate);
                allAggregates.Add(symbol, aggregate);
                added.Add(symbol);
            }
            else
            {
                if (!aggregate.IsActive)
                {
                    added.Add(symbol);
                }
                else
                {
                    retained.Add(symbol);
                }

                aggregate.Horizon = normalized.Horizon;
                aggregate.LastObservedAtUtc = Max(aggregate.LastObservedAtUtc, normalized.ObservedAtUtc);
                aggregate.IsActive = true;
                aggregate.Version++;
            }

            if (!existingLinks.TryGetValue(symbol, out var link))
            {
                link = new DiscoverySourceMembershipRecord
                {
                    MembershipId = Guid.NewGuid(),
                    AggregateId = aggregate.AggregateId,
                    Aggregate = aggregate,
                    SourceKind = normalized.SourceKind,
                    SourceKey = normalized.SourceKey,
                    FirstObservedAtUtc = normalized.ObservedAtUtc,
                    Version = 1
                };
                context.DiscoverySourceMemberships.Add(link);
                aggregate.Sources.Add(link);
            }
            else
            {
                link.Version++;
            }

            link.LatestSnapshotId = snapshot.SnapshotId;
            link.LatestSnapshot = snapshot;
            link.LastObservedAtUtc = normalized.ObservedAtUtc;
            link.ExpiresAtUtc = normalized.ExpiresAtUtc;
            link.IsActive = true;
            link.MetadataJson = incoming[symbol].MetadataJson;
        }

        foreach (var (symbol, link) in existingLinks)
        {
            if (incoming.ContainsKey(symbol) || !link.IsActive)
            {
                continue;
            }

            link.IsActive = false;
            link.ExpiresAtUtc = normalized.ObservedAtUtc;
            link.Version++;
            allAggregates[symbol].Version++;
            potentiallyDropped.Add(symbol);
        }

        var dropped = new List<string>();
        foreach (var symbol in potentiallyDropped)
        {
            var aggregate = allAggregates[symbol];
            RefreshAggregateProjection(aggregate, normalized.ObservedAtUtc);
            if (!aggregate.IsActive)
            {
                dropped.Add(symbol);
            }
        }

        foreach (var symbol in incoming.Keys)
        {
            RefreshAggregateProjection(allAggregates[symbol], normalized.ObservedAtUtc);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            await using var retryContext = await contextFactory.CreateDbContextAsync(cancellationToken);
            var concurrent = await retryContext.DiscoverySnapshots
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate =>
                    candidate.ScopeId == normalized.ScopeId &&
                    candidate.SourceKind == normalized.SourceKind &&
                    candidate.SourceKey == normalized.SourceKey &&
                    candidate.ObservationId == normalized.ObservationId,
                    cancellationToken);
            if (concurrent is not null)
            {
                return await BuildDuplicateResultAsync(retryContext, concurrent, normalized, cancellationToken);
            }

            throw new DiscoveryConcurrencyException(
                $"Discovery source {normalized.SourceKind}/{normalized.SourceKey} changed concurrently.");
        }

        return new DiscoverySnapshotCommit(
            snapshot.SnapshotId,
            snapshot.SourceVersion,
            false,
            added.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            retained.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            dropped.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    public async Task<IReadOnlyList<ActiveDiscoveryAggregate>> GetActiveAsync(
        Guid scopeId,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var utc = asOfUtc.ToUniversalTime();
        var aggregates = await context.DiscoveryAggregates
            .AsNoTracking()
            .Include(aggregate => aggregate.Sources)
                .ThenInclude(source => source.LatestSnapshot)
            .Where(aggregate => aggregate.ScopeId == scopeId && aggregate.IsActive)
            .OrderBy(aggregate => aggregate.Symbol)
            .ToArrayAsync(cancellationToken);

        return aggregates
            // SQLite cannot translate ordered DateTimeOffset comparisons. Scope and
            // active-state filtering remain server-side; expiry is evaluated as UTC
            // while materializing the bounded discovery set for this run.
            .Where(aggregate => aggregate.ExpiresAtUtc > utc)
            .Select(aggregate => ToDomain(aggregate, utc))
            .Where(aggregate => aggregate.Sources.Count > 0)
            .ToArray();
    }

    public async Task<int> ExpireAsync(
        Guid scopeId,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ExpireOnceAsync(scopeId, asOfUtc.ToUniversalTime(), cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), cancellationToken);
            }
            catch (SqliteException exception) when (attempt < 2 && exception.SqliteErrorCode is 5 or 6)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), cancellationToken);
            }
        }
    }

    private async Task<int> ExpireOnceAsync(
        Guid scopeId,
        DateTimeOffset utc,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var aggregates = await context.DiscoveryAggregates
            .Include(aggregate => aggregate.Sources)
            .Where(aggregate => aggregate.ScopeId == scopeId && aggregate.IsActive)
            .ToArrayAsync(cancellationToken);
        var expired = 0;
        foreach (var aggregate in aggregates)
        {
            var sourceExpired = false;
            foreach (var source in aggregate.Sources.Where(source => source.IsActive && source.ExpiresAtUtc <= utc))
            {
                source.IsActive = false;
                source.Version++;
                sourceExpired = true;
            }

            if (!sourceExpired)
            {
                continue;
            }

            aggregate.Version++;
            var wasActive = aggregate.IsActive;
            RefreshAggregateProjection(aggregate, utc);
            if (wasActive && !aggregate.IsActive)
            {
                expired++;
            }
        }

        if (context.ChangeTracker.HasChanges())
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return expired;
    }

    private static DiscoverySnapshotRequest Normalize(DiscoverySnapshotRequest request)
    {
        if (request.ScopeId == Guid.Empty || request.ObservationId == Guid.Empty)
        {
            throw new ArgumentException("Discovery scope and observation identities are required.", nameof(request));
        }

        var sourceKind = NormalizeSourceKind(request.SourceKind);
        if (!DiscoverySourceKinds.All.Contains(sourceKind))
        {
            throw new ArgumentException($"Unsupported discovery source kind '{request.SourceKind}'.", nameof(request));
        }

        var observedAt = request.ObservedAtUtc.ToUniversalTime();
        var expiresAt = request.ExpiresAtUtc.ToUniversalTime();
        if (expiresAt <= observedAt)
        {
            throw new ArgumentException("Discovery expiry must be later than its observation timestamp.", nameof(request));
        }

        var symbols = request.Symbols
            .Where(item => !String.IsNullOrWhiteSpace(item.Symbol))
            .Select(item => new DiscoverySymbolObservation(
                item.Symbol.Trim().ToUpperInvariant(),
                String.IsNullOrWhiteSpace(item.MetadataJson) ? "{}" : item.MetadataJson))
            .GroupBy(item => item.Symbol, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(item => item.Symbol, StringComparer.Ordinal)
            .ToArray();
        foreach (var item in symbols)
        {
            _ = JsonDocument.Parse(item.MetadataJson);
        }

        return request with
        {
            SourceKind = sourceKind,
            SourceKey = NormalizeSourceKey(request.SourceKey),
            Horizon = String.IsNullOrWhiteSpace(request.Horizon) ? "unknown" : request.Horizon.Trim().ToLowerInvariant(),
            ObservedAtUtc = observedAt,
            ExpiresAtUtc = expiresAt,
            ProviderTimestampUtc = request.ProviderTimestampUtc?.ToUniversalTime(),
            Symbols = symbols
        };
    }

    private static void RefreshAggregateProjection(DiscoveryAggregateRecord aggregate, DateTimeOffset asOfUtc)
    {
        var active = aggregate.Sources
            .Where(source => source.IsActive && source.ExpiresAtUtc > asOfUtc)
            .ToArray();
        aggregate.IsActive = active.Length > 0;
        aggregate.ExpiresAtUtc = active.Length > 0
            ? active.Max(source => source.ExpiresAtUtc)
            : asOfUtc;
        if (active.Length > 0)
        {
            aggregate.LastObservedAtUtc = active.Max(source => source.LastObservedAtUtc);
        }
    }

    private static ActiveDiscoveryAggregate ToDomain(DiscoveryAggregateRecord aggregate, DateTimeOffset asOfUtc)
    {
        var sources = aggregate.Sources
            .Where(source => source.IsActive && source.ExpiresAtUtc > asOfUtc)
            .OrderBy(source => source.SourceKind, StringComparer.Ordinal)
            .ThenBy(source => source.SourceKey, StringComparer.Ordinal)
            .Select(source => new DiscoverySourceEvidence(
                source.SourceKind,
                source.SourceKey,
                source.LastObservedAtUtc,
                source.ExpiresAtUtc,
                source.MetadataJson,
                source.LatestSnapshotId,
                source.LatestSnapshot?.SourceVersion ?? source.Version,
                source.LatestSnapshot?.ProviderTimestampUtc,
                source.LatestSnapshot?.IsDiagnostic ?? false,
                source.LatestSnapshot?.RawReference,
                source.LatestSnapshot?.ContentSha256 ?? String.Empty))
            .ToArray();
        return new ActiveDiscoveryAggregate(
            aggregate.AggregateId,
            aggregate.ScopeId,
            aggregate.Symbol,
            aggregate.Horizon,
            aggregate.FirstDiscoveredAtUtc,
            aggregate.LastObservedAtUtc,
            aggregate.ExpiresAtUtc,
            aggregate.Version,
            sources);
    }

    private static async Task<DiscoverySnapshotCommit> BuildDuplicateResultAsync(
        TradingFlowDbContext context,
        DiscoverySnapshotRecord existing,
        DiscoverySnapshotRequest request,
        CancellationToken cancellationToken)
    {
        var existingSymbols = JsonSerializer.Deserialize<DiscoverySymbolObservation[]>(existing.SymbolsJson, JsonOptions) ?? [];
        if (!String.Equals(existing.ContentSha256, ComputeSnapshotHash(request), StringComparison.Ordinal) ||
            !existingSymbols.Select(item => item.Symbol).SequenceEqual(request.Symbols.Select(item => item.Symbol), StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Discovery observation {request.ObservationId} was replayed with different content.");
        }

        var active = await context.DiscoveryAggregates
            .AsNoTracking()
            .Where(aggregate => aggregate.ScopeId == request.ScopeId && aggregate.IsActive)
            .Select(aggregate => aggregate.Symbol)
            .ToArrayAsync(cancellationToken);
        return new DiscoverySnapshotCommit(
            existing.SnapshotId,
            existing.SourceVersion,
            true,
            [],
            active.Intersect(request.Symbols.Select(item => item.Symbol), StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            []);
    }

    private static string ComputeSnapshotHash(DiscoverySnapshotRequest request)
    {
        var canonical = String.Join(
            '\n',
            request.Symbols.OrderBy(item => item.Symbol, StringComparer.Ordinal)
                .Select(item => $"{item.Symbol}\u001f{item.MetadataJson}"));
        canonical = String.Join(
            '\u001e',
            request.ScopeId.ToString("D"),
            request.ObservationId.ToString("D"),
            request.SourceKind,
            request.SourceKey,
            request.Horizon,
            request.ObservedAtUtc.ToUniversalTime().ToString("O"),
            request.ExpiresAtUtc.ToUniversalTime().ToString("O"),
            request.ProviderTimestampUtc?.ToUniversalTime().ToString("O") ?? String.Empty,
            request.IsDiagnostic.ToString(),
            request.RawReference ?? String.Empty,
            canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string NormalizeSourceKind(string value) =>
        !String.IsNullOrWhiteSpace(value)
            ? value.Trim().ToLowerInvariant()
            : throw new ArgumentException("Discovery source kind is required.", nameof(value));

    private static string NormalizeSourceKey(string value) =>
        !String.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException("Discovery source key is required.", nameof(value));

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;
}
