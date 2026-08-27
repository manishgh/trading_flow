using System.Collections.Concurrent;
using System.Web;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Engine.Storage;
using TradingFlow.Finviz;

namespace TradingFlow.Web.Services.Wishlists;

/// <summary>
/// Horizon a screener result belongs to. This is not a label: a swing screen is
/// a persistent universe, and an intraday screen is scoped to one session and
/// must be discarded at the session boundary so it cannot leak into the next
/// day's universe.
/// </summary>
public enum ScreenerScope
{
    Swing,
    Intraday
}

/// <summary>
/// What a screener query would return, without changing anything.
/// </summary>
/// <param name="Scope">Swing or intraday.</param>
/// <param name="Name">Operator-facing name: the saved screener, or the query itself.</param>
/// <param name="NormalizedQuery">The query actually sent to Finviz.</param>
/// <param name="Symbols">Every symbol the screen returned.</param>
/// <param name="NotInWishlist">Symbols not already active in the compared wishlist.</param>
/// <param name="SyncedAtUtc">When the read was taken.</param>
/// <param name="SessionKey">Session this result belongs to; null for swing.</param>
/// <param name="Error">Why the read failed, or null when it succeeded.</param>
public sealed record ScreenerSyncResult(
    ScreenerScope Scope,
    string Name,
    string NormalizedQuery,
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string> NotInWishlist,
    DateTimeOffset SyncedAtUtc,
    string? SessionKey,
    string? Error)
{
    public bool Succeeded => Error is null;

    public static ScreenerSyncResult Failed(ScreenerScope scope, string name, string query, string error) =>
        new(scope, name, query, [], [], DateTimeOffset.UtcNow, null, error);
}

/// <summary>
/// Point-in-time screener read used when resolving an immutable run universe.
/// </summary>
public interface IScreenerSnapshotSource
{
    Task<ScreenerSyncResult> PreviewAsync(
        string input,
        ScreenerScope scope,
        Guid? wishlistId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads a Finviz screen and says what it would return, so an operator can see
/// the result before anything is added to a wishlist.
///
/// Three input shapes are accepted and normalised to one query: a full Finviz
/// Elite URL, the name of a saved screener preset, or a bare query string. They
/// are the three things an operator actually has to hand, and making the caller
/// pick between them would only move the guesswork.
///
/// Intraday results are cached against a session key and dropped when the
/// session rolls. That discard is the point: an intraday screen describes one
/// session's conditions, and carrying it into the next day would silently widen
/// tomorrow's universe with yesterday's reasoning.
/// </summary>
public sealed class ScreenerSyncService : IScreenerSnapshotSource
{
    private readonly IDbContextFactory<TradingFlowDbContext> dbFactory;
    private readonly IWishlistRepository wishlists;
    private readonly IRawArchiveWriter rawArchiveWriter;
    private readonly IConfiguration configuration;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ScreenerSyncService> logger;

    /// <summary>
    /// Intraday results, keyed by session. Only the current session is retained;
    /// a read in a new session clears everything older.
    /// </summary>
    private readonly ConcurrentDictionary<string, ScreenerSyncResult> intradayBySession = new(StringComparer.Ordinal);

    public ScreenerSyncService(
        IDbContextFactory<TradingFlowDbContext> dbFactory,
        IWishlistRepository wishlists,
        IRawArchiveWriter rawArchiveWriter,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<ScreenerSyncService> logger)
    {
        this.dbFactory = dbFactory;
        this.wishlists = wishlists;
        this.rawArchiveWriter = rawArchiveWriter;
        this.configuration = configuration;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// Reads the screen and diffs it against <paramref name="wishlistId"/>.
    /// Adds nothing: the caller decides whether to import.
    /// </summary>
    public async Task<ScreenerSyncResult> PreviewAsync(
        string input,
        ScreenerScope scope,
        Guid? wishlistId,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(input))
        {
            return ScreenerSyncResult.Failed(scope, "No screen", String.Empty, "Paste a Finviz URL, a saved screener name, or a query string.");
        }

        var token = ResolveFinvizToken();
        if (String.IsNullOrWhiteSpace(token))
        {
            return ScreenerSyncResult.Failed(scope, input.Trim(), String.Empty, "FINVIZ_API_KEY is not configured.");
        }

        var (name, query) = await NormalizeAsync(input, scope, cancellationToken);
        if (String.IsNullOrWhiteSpace(query))
        {
            return ScreenerSyncResult.Failed(scope, name, String.Empty, "No screener filter could be read from that input.");
        }

        IReadOnlyList<string> symbols;
        try
        {
            using var client = new FinvizClient(
                new HttpClient(),
                FinvizOptions.CreateDefault() with { AuthToken = token },
                rawArchiveWriter);
            symbols = (await client.GetScreenerTickersAsync(query, cancellationToken))
                .Where(ticker => !String.IsNullOrWhiteSpace(ticker))
                .Select(ticker => ticker.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(250)
                .ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Finviz screener read failed for {Query}.", query);
            return ScreenerSyncResult.Failed(scope, name, query, $"Finviz read failed: {exception.Message}");
        }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (wishlistId is { } id)
        {
            var wishlist = await wishlists.GetByIdAsync(id, cancellationToken);
            foreach (var item in wishlist?.Items.Where(item => item.Active) ?? [])
            {
                existing.Add(item.Ticker.Trim().ToUpperInvariant());
            }
        }

        var sessionKey = scope == ScreenerScope.Intraday ? CurrentSessionKey() : null;
        var result = new ScreenerSyncResult(
            scope,
            name,
            query,
            symbols,
            symbols.Where(symbol => !existing.Contains(symbol)).ToArray(),
            timeProvider.GetUtcNow(),
            sessionKey,
            null);

        if (sessionKey is not null)
        {
            // Replace rather than accumulate: only the current session may be held.
            intradayBySession.Clear();
            intradayBySession[sessionKey] = result;
        }

        return result;
    }

    /// <summary>
    /// The intraday result for the current session, or null once the session has
    /// rolled. A caller that reads this after the boundary gets nothing rather
    /// than yesterday's universe.
    /// </summary>
    public ScreenerSyncResult? GetCurrentIntradayResult()
    {
        var key = CurrentSessionKey();
        if (intradayBySession.TryGetValue(key, out var result))
        {
            return result;
        }

        if (!intradayBySession.IsEmpty)
        {
            intradayBySession.Clear();
        }
        return null;
    }

    /// <summary>
    /// Resolves the three accepted input shapes to one Finviz filter query.
    /// </summary>
    private async Task<(string Name, string Query)> NormalizeAsync(
        string input,
        ScreenerScope scope,
        CancellationToken cancellationToken)
    {
        var trimmed = input.Trim();

        // 1. A full Finviz URL: the filter lives in the query string, and the
        //    rest of the URL (view, order, auth) is not ours to forward.
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var parameters = HttpUtility.ParseQueryString(uri.Query);
            var filter = parameters["f"];
            return (String.IsNullOrWhiteSpace(filter) ? trimmed : $"Finviz screen {filter}", filter ?? String.Empty);
        }

        // 2. A saved screener preset, matched by name within the scope's category
        //    so an intraday and a swing preset may share a name.
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var category = scope.ToString();
        var preset = await db.ScreenerPresets
            .FirstOrDefaultAsync(
                candidate => candidate.Name == trimmed && candidate.Category == category,
                cancellationToken)
            ?? await db.ScreenerPresets.FirstOrDefaultAsync(candidate => candidate.Name == trimmed, cancellationToken);
        if (preset is not null)
        {
            return (preset.Name, preset.FilterQuery);
        }

        // 3. A bare query string, with or without a leading `f=`.
        var query = trimmed.StartsWith("f=", StringComparison.OrdinalIgnoreCase) ? trimmed[2..] : trimmed;
        return ($"Query {query}", query);
    }

    /// <summary>
    /// Identifies one trading session by its New York date. A screen taken at
    /// 09:35 and one taken at 15:50 belong to the same session; one taken the
    /// next morning does not.
    /// </summary>
    private string CurrentSessionKey()
    {
        var newYork = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), ResolveNewYorkTimeZone());
        return newYork.ToString("yyyy-MM-dd");
    }

    private string? ResolveFinvizToken() =>
        Environment.GetEnvironmentVariable("FINVIZ_API_KEY")
        ?? (OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("FINVIZ_API_KEY", EnvironmentVariableTarget.User)
            : null)
        ?? configuration["Finviz:ApiKey"];

    private static TimeZoneInfo ResolveNewYorkTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }
}
