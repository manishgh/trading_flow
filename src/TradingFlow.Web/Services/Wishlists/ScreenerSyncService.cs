using System.Web;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Engine.Storage;
using TradingFlow.Finviz;

namespace TradingFlow.Web.Services.Wishlists;

/// <summary>
/// Horizon a screener result belongs to. TradingFlow supports swing discovery.
/// </summary>
public enum ScreenerScope
{
    Swing
}

/// <summary>
/// What a screener query would return, without changing anything.
/// </summary>
/// <param name="Scope">Swing.</param>
/// <param name="Name">Operator-facing name: the saved screener, or the query itself.</param>
/// <param name="NormalizedQuery">The query actually sent to Finviz.</param>
/// <param name="Symbols">Every symbol the screen returned.</param>
/// <param name="NotInWishlist">Symbols not already active in the compared wishlist.</param>
/// <param name="SyncedAtUtc">When the read was taken.</param>
/// <param name="SessionKey">Reserved for provider diagnostics; null for swing.</param>
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

    /// <summary>
    /// Provider values retained for discovery display and audit only. Strategy
    /// admission must calculate RVOL from Alpaca market evidence.
    /// </summary>
    public IReadOnlyList<ScreenerSymbolResult> SymbolDetails { get; init; } = [];

    public DateTimeOffset? ProviderTimestampUtc { get; init; }

    public string? RawReference { get; init; }

    public static ScreenerSyncResult Failed(ScreenerScope scope, string name, string query, string error) =>
        new(scope, name, query, [], [], DateTimeOffset.UtcNow, null, error);
}

public sealed record ScreenerSymbolResult(
    string Symbol,
    decimal? VendorReportedRvol);

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
/// Every result is point-in-time evidence and is persisted by the downstream
/// universe workflow before it can authorize a candidate.
/// </summary>
public sealed class ScreenerSyncService : IScreenerSnapshotSource
{
    private readonly IDbContextFactory<TradingFlowDbContext> dbFactory;
    private readonly IWishlistRepository wishlists;
    private readonly IRawArchiveWriter rawArchiveWriter;
    private readonly IConfiguration configuration;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ScreenerSyncService> logger;

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

        FinvizScreenerSnapshot snapshot;
        IReadOnlyList<ScreenerSymbolResult> symbolDetails;
        try
        {
            using var client = new FinvizClient(
                new HttpClient(),
                FinvizOptions.CreateDefault() with { AuthToken = token },
                rawArchiveWriter);
            snapshot = await client.GetScreenerSnapshotAsync(query, cancellationToken);
            symbolDetails = snapshot.Rows
                .Where(row => !String.IsNullOrWhiteSpace(row.Ticker))
                .Select(row => new ScreenerSymbolResult(
                    row.Ticker.Trim().ToUpperInvariant(),
                    row.RelativeVolume))
                .GroupBy(row => row.Symbol, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .OrderBy(row => row.Symbol, StringComparer.Ordinal)
                .Take(250)
                .ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Finviz screener read failed for {Query}.", query);
            return ScreenerSyncResult.Failed(scope, name, query, $"Finviz read failed: {exception.Message}");
        }

        var symbols = symbolDetails.Select(row => row.Symbol).ToArray();

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (wishlistId is { } id)
        {
            var wishlist = await wishlists.GetByIdAsync(id, cancellationToken);
            foreach (var item in wishlist?.Items.Where(item => item.Active) ?? [])
            {
                existing.Add(item.Ticker.Trim().ToUpperInvariant());
            }
        }

        var result = new ScreenerSyncResult(
            scope,
            name,
            snapshot.NormalizedQuery,
            symbols,
            symbols.Where(symbol => !existing.Contains(symbol)).ToArray(),
            timeProvider.GetUtcNow(),
            null,
            null)
        {
            SymbolDetails = symbolDetails,
            ProviderTimestampUtc = snapshot.ReceivedAtUtc,
            RawReference = $"finviz:{snapshot.NormalizedQuery}|{snapshot.RawReference}"
        };

        return result;
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

        // 2. A saved swing screener preset, matched by name.
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

    private string? ResolveFinvizToken() =>
        Environment.GetEnvironmentVariable("FINVIZ_API_KEY")
        ?? (OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("FINVIZ_API_KEY", EnvironmentVariableTarget.User)
            : null)
        ?? configuration["Finviz:ApiKey"];

}
