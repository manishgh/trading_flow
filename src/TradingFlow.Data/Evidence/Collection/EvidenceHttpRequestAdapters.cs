using System.Globalization;
using System.Text.Json;
using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence.Collection;

public sealed class AlpacaBarsEvidenceRequestAdapter(Uri marketDataRestBaseUri) :
    IEvidenceHttpRequestAdapter
{
    private readonly Uri defaultEndpoint = BuildEndpoint(
        marketDataRestBaseUri,
        "/v2/stocks/bars");

    public bool CanHandle(EvidenceCollectionRequest request) =>
        request.Provider.Equals("alpaca", StringComparison.Ordinal) &&
        request.Endpoint.EndsWith("/v2/stocks/bars", StringComparison.OrdinalIgnoreCase);

    public HttpRequestMessage CreateRequest(
        EvidenceCollectionRequest request,
        string? pageToken)
    {
        EnsureSupported(request);
        var timeframe = RequiredParameter(request, "timeframe");
        var limit = OptionalInteger(request, "limit", 1, 10_000) ?? 10_000;
        var sort = OptionalChoice(request, "sort", ["asc", "desc"]) ?? "asc";
        var query = BaseRangeQuery(request);
        query.Add(new("symbols", String.Join(',', request.Symbols)));
        query.Add(new("timeframe", timeframe));
        query.Add(new("limit", limit.ToString(CultureInfo.InvariantCulture)));
        query.Add(new("adjustment", request.Adjustment));
        query.Add(new("asof", request.AsOfDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        query.Add(new("feed", request.DataFeed));
        query.Add(new("currency", request.Currency));
        query.Add(new("sort", sort));
        AddPageToken(query, pageToken);
        return new HttpRequestMessage(HttpMethod.Get, BuildUri(request.Endpoint, defaultEndpoint, query));
    }

    public ValueTask<EvidenceHttpPageMetadata> ReadPublishedPageMetadataAsync(
        EvidenceSourceObservation observation,
        ReadOnlyMemory<byte> rawContent,
        CancellationToken cancellationToken) =>
        AlpacaPageMetadata.ReadAsync(rawContent, cancellationToken);

    private static void EnsureSupported(EvidenceCollectionRequest request)
    {
        if (request.Symbols.Count == 0 ||
            request.RequestedStartUtc is null ||
            request.RequestedEndUtc is null ||
            request.AsOfDate is null)
        {
            throw new InvalidOperationException(
                "Alpaca bars evidence requires symbols, an explicit UTC range, and an as-of date.");
        }

        EnsureOnlyParameters(request, "timeframe", "limit", "sort");
    }

    internal static List<KeyValuePair<string, string>> BaseRangeQuery(
        EvidenceCollectionRequest request) =>
    [
        new("start", request.RequestedStartUtc!.Value.ToString("O", CultureInfo.InvariantCulture)),
        // Evidence ranges are half-open. Alpaca includes records exactly at `end`,
        // so translate the frozen exclusive boundary to its inclusive API contract.
        new(
            "end",
            request.RequestedEndUtc!.Value
                .AddTicks(-1)
                .ToString("O", CultureInfo.InvariantCulture))
    ];

    internal static void AddPageToken(
        ICollection<KeyValuePair<string, string>> query,
        string? pageToken)
    {
        if (!String.IsNullOrWhiteSpace(pageToken))
        {
            query.Add(new("page_token", pageToken));
        }
    }

    internal static Uri BuildUri(
        string configuredEndpoint,
        Uri defaultEndpoint,
        IEnumerable<KeyValuePair<string, string>> query)
    {
        var endpoint = Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out var absolute)
            ? absolute
            : configuredEndpoint.StartsWith("/", StringComparison.Ordinal)
                ? new Uri(defaultEndpoint, configuredEndpoint)
                : defaultEndpoint;
        var builder = new UriBuilder(endpoint);
        var existing = builder.Query.TrimStart('?');
        var encoded = String.Join(
            "&",
            query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        builder.Query = String.IsNullOrEmpty(existing)
            ? encoded
            : String.IsNullOrEmpty(encoded) ? existing : $"{existing}&{encoded}";
        return builder.Uri;
    }

    internal static Uri BuildEndpoint(Uri baseUri, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri ||
            !baseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !String.IsNullOrEmpty(baseUri.UserInfo) ||
            !String.IsNullOrEmpty(baseUri.Query) ||
            !String.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new ArgumentException(
                "The Alpaca Market Data base URI must be an absolute HTTPS URI without credentials, query, or fragment.",
                nameof(baseUri));
        }

        return new Uri(baseUri, relativePath);
    }

    internal static string RequiredParameter(
        EvidenceCollectionRequest request,
        string name) =>
        request.Parameters.TryGetValue(name, out var value) && !String.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException(
                $"Evidence request '{request.RequestId}' requires parameter '{name}'.");

    internal static int? OptionalInteger(
        EvidenceCollectionRequest request,
        string name,
        int minimum,
        int maximum)
    {
        if (!request.Parameters.TryGetValue(name, out var value))
        {
            return null;
        }

        if (!Int32.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < minimum ||
            parsed > maximum)
        {
            throw new InvalidOperationException(
                $"Evidence request parameter '{name}' must be between {minimum} and {maximum}.");
        }

        return parsed;
    }

    internal static string? OptionalChoice(
        EvidenceCollectionRequest request,
        string name,
        IReadOnlyList<string> choices)
    {
        if (!request.Parameters.TryGetValue(name, out var value))
        {
            return null;
        }

        return choices.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? value.ToLowerInvariant()
            : throw new InvalidOperationException(
                $"Evidence request parameter '{name}' is not supported.");
    }

    internal static void EnsureOnlyParameters(
        EvidenceCollectionRequest request,
        params string[] allowed)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        var unsupported = request.Parameters.Keys
            .Where(key => !allowedSet.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        if (unsupported.Length > 0)
        {
            throw new InvalidOperationException(
                $"Evidence request contains unsupported parameters: {String.Join(", ", unsupported)}.");
        }
    }
}

public sealed class AlpacaQuotesEvidenceRequestAdapter(Uri marketDataRestBaseUri) :
    IEvidenceHttpRequestAdapter
{
    private readonly Uri defaultEndpoint = AlpacaBarsEvidenceRequestAdapter.BuildEndpoint(
        marketDataRestBaseUri,
        "/v2/stocks/quotes");

    public bool CanHandle(EvidenceCollectionRequest request) =>
        request.Provider.Equals("alpaca", StringComparison.Ordinal) &&
        request.Endpoint.EndsWith("/v2/stocks/quotes", StringComparison.OrdinalIgnoreCase);

    public HttpRequestMessage CreateRequest(
        EvidenceCollectionRequest request,
        string? pageToken)
    {
        if (request.Symbols.Count == 0 ||
            request.RequestedStartUtc is null ||
            request.RequestedEndUtc is null)
        {
            throw new InvalidOperationException(
                "Alpaca quote evidence requires symbols and an explicit UTC range.");
        }

        if (!request.DataFeed.Equals("sip", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Executable quote evidence requires the consolidated SIP feed.");
        }

        AlpacaBarsEvidenceRequestAdapter.EnsureOnlyParameters(request, "limit", "sort");
        var limit = AlpacaBarsEvidenceRequestAdapter.OptionalInteger(
            request,
            "limit",
            1,
            10_000) ?? 10_000;
        var sort = AlpacaBarsEvidenceRequestAdapter.OptionalChoice(
            request,
            "sort",
            ["asc", "desc"]) ?? "asc";
        var query = AlpacaBarsEvidenceRequestAdapter.BaseRangeQuery(request);
        query.Add(new("symbols", String.Join(',', request.Symbols)));
        query.Add(new("limit", limit.ToString(CultureInfo.InvariantCulture)));
        query.Add(new("feed", "sip"));
        query.Add(new("currency", request.Currency));
        query.Add(new("sort", sort));
        AlpacaBarsEvidenceRequestAdapter.AddPageToken(query, pageToken);

        return new HttpRequestMessage(
            HttpMethod.Get,
            AlpacaBarsEvidenceRequestAdapter.BuildUri(
                request.Endpoint,
                defaultEndpoint,
                query));
    }

    public ValueTask<EvidenceHttpPageMetadata> ReadPublishedPageMetadataAsync(
        EvidenceSourceObservation observation,
        ReadOnlyMemory<byte> rawContent,
        CancellationToken cancellationToken) =>
        AlpacaPageMetadata.ReadAsync(rawContent, cancellationToken);
}

public sealed class AlpacaNewsEvidenceRequestAdapter(Uri marketDataRestBaseUri) :
    IEvidenceHttpRequestAdapter
{
    private readonly Uri defaultEndpoint = AlpacaBarsEvidenceRequestAdapter.BuildEndpoint(
        marketDataRestBaseUri,
        "/v1beta1/news");

    public bool CanHandle(EvidenceCollectionRequest request) =>
        request.Provider.Equals("alpaca", StringComparison.Ordinal) &&
        request.Endpoint.EndsWith("/v1beta1/news", StringComparison.OrdinalIgnoreCase);

    public HttpRequestMessage CreateRequest(
        EvidenceCollectionRequest request,
        string? pageToken)
    {
        if (request.RequestedStartUtc is null || request.RequestedEndUtc is null)
        {
            throw new InvalidOperationException("Alpaca news evidence requires an explicit UTC range.");
        }

        AlpacaBarsEvidenceRequestAdapter.EnsureOnlyParameters(
            request,
            "limit",
            "sort",
            "include_content",
            "exclude_contentless");
        var limit = AlpacaBarsEvidenceRequestAdapter.OptionalInteger(request, "limit", 1, 50) ?? 50;
        var sort = AlpacaBarsEvidenceRequestAdapter.OptionalChoice(
            request,
            "sort",
            ["asc", "desc"]) ?? "asc";
        var includeContent = OptionalBoolean(request, "include_content", true);
        var excludeContentless = OptionalBoolean(request, "exclude_contentless", false);
        var query = AlpacaBarsEvidenceRequestAdapter.BaseRangeQuery(request);
        if (request.Symbols.Count > 0)
        {
            query.Add(new("symbols", String.Join(',', request.Symbols)));
        }

        query.Add(new("limit", limit.ToString(CultureInfo.InvariantCulture)));
        query.Add(new("sort", sort));
        query.Add(new("include_content", includeContent ? "true" : "false"));
        query.Add(new("exclude_contentless", excludeContentless ? "true" : "false"));
        AlpacaBarsEvidenceRequestAdapter.AddPageToken(query, pageToken);
        return new HttpRequestMessage(
            HttpMethod.Get,
            AlpacaBarsEvidenceRequestAdapter.BuildUri(request.Endpoint, defaultEndpoint, query));
    }

    public ValueTask<EvidenceHttpPageMetadata> ReadPublishedPageMetadataAsync(
        EvidenceSourceObservation observation,
        ReadOnlyMemory<byte> rawContent,
        CancellationToken cancellationToken) =>
        AlpacaPageMetadata.ReadAsync(rawContent, cancellationToken);

    private static bool OptionalBoolean(
        EvidenceCollectionRequest request,
        string name,
        bool defaultValue)
    {
        if (!request.Parameters.TryGetValue(name, out var value))
        {
            return defaultValue;
        }

        return Boolean.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Evidence request parameter '{name}' must be true or false.");
    }
}

/// <summary>
/// Builds frozen official US-equity calendar requests against Alpaca's trading API.
/// The request range is half-open in evidence storage and translated to Alpaca's
/// inclusive date query without introducing a timezone-dependent boundary.
/// </summary>
public sealed class AlpacaExchangeCalendarEvidenceRequestAdapter(Uri tradingRestBaseUri) :
    IEvidenceHttpRequestAdapter
{
    private readonly Uri endpoint = AlpacaBarsEvidenceRequestAdapter.BuildEndpoint(
        tradingRestBaseUri,
        "/v2/calendar");

    public bool CanHandle(EvidenceCollectionRequest request) =>
        request.Provider.Equals("alpaca", StringComparison.Ordinal) &&
        request.Endpoint.EndsWith("/v2/calendar", StringComparison.OrdinalIgnoreCase);

    public HttpRequestMessage CreateRequest(
        EvidenceCollectionRequest request,
        string? pageToken)
    {
        EnsureSupported(request, pageToken);
        var startDate = DateOnly.FromDateTime(request.RequestedStartUtc!.Value.UtcDateTime);
        var inclusiveEndDate = DateOnly.FromDateTime(
            request.RequestedEndUtc!.Value.AddDays(-1).UtcDateTime);
        var query = new List<KeyValuePair<string, string>>
        {
            new("start", startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new("end", inclusiveEndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        };

        return new HttpRequestMessage(
            HttpMethod.Get,
            AlpacaBarsEvidenceRequestAdapter.BuildUri(
                request.Endpoint,
                endpoint,
                query));
    }

    public ValueTask<EvidenceHttpPageMetadata> ReadPublishedPageMetadataAsync(
        EvidenceSourceObservation observation,
        ReadOnlyMemory<byte> rawContent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(rawContent);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The Alpaca calendar response must be a JSON array.");
        }

        return ValueTask.FromResult(new EvidenceHttpPageMetadata(null));
    }

    private static void EnsureSupported(
        EvidenceCollectionRequest request,
        string? pageToken)
    {
        if (pageToken is not null)
        {
            throw new InvalidOperationException(
                "The Alpaca calendar endpoint is not cursor paginated.");
        }

        if (request.Symbols.Count != 0 ||
            request.RequestedStartUtc is null ||
            request.RequestedEndUtc is null ||
            request.AsOfDate is null)
        {
            throw new InvalidOperationException(
                "Alpaca calendar evidence requires no symbols, an explicit UTC date range, and an as-of date.");
        }

        var start = request.RequestedStartUtc.Value;
        var end = request.RequestedEndUtc.Value;
        if (start.TimeOfDay != TimeSpan.Zero ||
            end.TimeOfDay != TimeSpan.Zero ||
            end - start < TimeSpan.FromDays(1))
        {
            throw new InvalidOperationException(
                "Alpaca calendar evidence ranges must use UTC midnight, half-open date boundaries.");
        }

        if (!request.DataFeed.Equals("alpaca-trading", StringComparison.Ordinal) ||
            !request.Adjustment.Equals("raw", StringComparison.Ordinal) ||
            !request.Currency.Equals("USD", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Alpaca calendar evidence requires feed 'alpaca-trading', raw adjustment, and USD.");
        }

        AlpacaBarsEvidenceRequestAdapter.EnsureOnlyParameters(request);
    }
}

public sealed class AlpacaAssetsEvidenceRequestAdapter(Uri tradingRestBaseUri) :
    IEvidenceHttpRequestAdapter
{
    private static readonly string[] SupportedExchanges =
        ["AMEX", "ARCA", "BATS", "NYSE", "NASDAQ", "NYSEARCA", "OTC"];

    private static readonly string[] SupportedAttributes =
    [
        "ptp_no_exception",
        "ptp_with_exception",
        "ipo",
        "has_options",
        "options_late_close",
        "fractional_eh_enabled",
        "overnight_tradable"
    ];

    private readonly Uri endpoint = AlpacaBarsEvidenceRequestAdapter.BuildEndpoint(
        tradingRestBaseUri,
        "/v2/assets");

    public bool CanHandle(EvidenceCollectionRequest request) =>
        request.Provider.Equals("alpaca", StringComparison.Ordinal) &&
        request.Endpoint.EndsWith("/v2/assets", StringComparison.OrdinalIgnoreCase);

    public HttpRequestMessage CreateRequest(
        EvidenceCollectionRequest request,
        string? pageToken)
    {
        EnsureSupported(request, pageToken);
        var status = AlpacaBarsEvidenceRequestAdapter.OptionalChoice(
            request,
            "status",
            ["active", "inactive", "all"]) ?? "all";
        var assetClass = AlpacaBarsEvidenceRequestAdapter.OptionalChoice(
            request,
            "asset_class",
            ["us_equity"]) ?? "us_equity";
        var exchange = AlpacaBarsEvidenceRequestAdapter.OptionalChoice(
            request,
            "exchange",
            SupportedExchanges);
        var attributes = OptionalCsvChoices(request, "attributes", SupportedAttributes);

        var query = new List<KeyValuePair<string, string>>
        {
            new("asset_class", assetClass)
        };
        if (!status.Equals("all", StringComparison.Ordinal))
        {
            query.Add(new("status", status));
        }

        if (exchange is not null)
        {
            query.Add(new("exchange", exchange.ToUpperInvariant()));
        }

        if (attributes is not null)
        {
            query.Add(new("attributes", attributes));
        }

        return new HttpRequestMessage(
            HttpMethod.Get,
            AlpacaBarsEvidenceRequestAdapter.BuildUri(
                endpoint.AbsoluteUri,
                endpoint,
                query));
    }

    public ValueTask<EvidenceHttpPageMetadata> ReadPublishedPageMetadataAsync(
        EvidenceSourceObservation observation,
        ReadOnlyMemory<byte> rawContent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(rawContent);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The Alpaca assets response must be a JSON array.");
        }

        return ValueTask.FromResult(new EvidenceHttpPageMetadata(null));
    }

    private static void EnsureSupported(
        EvidenceCollectionRequest request,
        string? pageToken)
    {
        if (pageToken is not null)
        {
            throw new InvalidOperationException("The Alpaca assets endpoint is not cursor paginated.");
        }

        if (request.Symbols.Count != 0 ||
            request.RequestedStartUtc is not null ||
            request.RequestedEndUtc is not null ||
            request.AsOfDate is null)
        {
            throw new InvalidOperationException(
                "Alpaca asset-master evidence requires an as-of date and no symbols or historical range.");
        }

        if (!request.DataFeed.Equals("alpaca-trading", StringComparison.Ordinal) ||
            !request.Adjustment.Equals("raw", StringComparison.Ordinal) ||
            !request.Currency.Equals("USD", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Alpaca asset-master evidence requires feed 'alpaca-trading', raw adjustment, and USD.");
        }

        AlpacaBarsEvidenceRequestAdapter.EnsureOnlyParameters(
            request,
            "status",
            "asset_class",
            "exchange",
            "attributes");
    }

    private static string? OptionalCsvChoices(
        EvidenceCollectionRequest request,
        string name,
        IReadOnlyCollection<string> supported)
    {
        if (!request.Parameters.TryGetValue(name, out var raw))
        {
            return null;
        }

        var values = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (values.Length == 0 ||
            values.Any(value => !supported.Contains(value, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Evidence request parameter '{name}' contains an unsupported value.");
        }

        return String.Join(',', values);
    }
}

public sealed class AlpacaCorporateActionsEvidenceRequestAdapter(Uri marketDataRestBaseUri) :
    IEvidenceHttpRequestAdapter
{
    internal static readonly string[] SupportedTypes =
    [
        "reverse_split",
        "forward_split",
        "unit_split",
        "cash_dividend",
        "stock_dividend",
        "spin_off",
        "cash_merger",
        "stock_merger",
        "stock_and_cash_merger",
        "redemption",
        "name_change",
        "worthless_removal",
        "rights_distribution",
        "partial_call",
        "reorganization"
    ];

    private readonly Uri endpoint = AlpacaBarsEvidenceRequestAdapter.BuildEndpoint(
        marketDataRestBaseUri,
        "/v1/corporate-actions");

    public bool CanHandle(EvidenceCollectionRequest request) =>
        request.Provider.Equals("alpaca", StringComparison.Ordinal) &&
        request.Endpoint.EndsWith("/v1/corporate-actions", StringComparison.OrdinalIgnoreCase);

    public HttpRequestMessage CreateRequest(
        EvidenceCollectionRequest request,
        string? pageToken)
    {
        EnsureSupported(request);
        var start = DateOnly.FromDateTime(request.RequestedStartUtc!.Value.UtcDateTime);
        var inclusiveEnd = DateOnly.FromDateTime(
            request.RequestedEndUtc!.Value.AddTicks(-1).UtcDateTime);
        var limit = AlpacaBarsEvidenceRequestAdapter.OptionalInteger(
            request,
            "limit",
            1,
            1_000) ?? 1_000;
        var sort = AlpacaBarsEvidenceRequestAdapter.OptionalChoice(
            request,
            "sort",
            ["asc", "desc"]) ?? "asc";
        var types = NormalizeTypes(request);
        var query = new List<KeyValuePair<string, string>>
        {
            new("start", start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new("end", inclusiveEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new("types", types),
            new("region", "us"),
            new("limit", limit.ToString(CultureInfo.InvariantCulture)),
            new("sort", sort)
        };
        if (request.Symbols.Count > 0)
        {
            query.Add(new("symbols", String.Join(',', request.Symbols)));
        }

        AlpacaBarsEvidenceRequestAdapter.AddPageToken(query, pageToken);
        return new HttpRequestMessage(
            HttpMethod.Get,
            AlpacaBarsEvidenceRequestAdapter.BuildUri(
                endpoint.AbsoluteUri,
                endpoint,
                query));
    }

    public ValueTask<EvidenceHttpPageMetadata> ReadPublishedPageMetadataAsync(
        EvidenceSourceObservation observation,
        ReadOnlyMemory<byte> rawContent,
        CancellationToken cancellationToken) =>
        AlpacaCorporateActionPageMetadata.ReadAsync(rawContent, cancellationToken);

    private static void EnsureSupported(EvidenceCollectionRequest request)
    {
        if (request.RequestedStartUtc is null ||
            request.RequestedEndUtc is null ||
            request.AsOfDate is null)
        {
            throw new InvalidOperationException(
                "Alpaca corporate-action evidence requires an explicit UTC date range and as-of date.");
        }

        if (request.RequestedStartUtc.Value.TimeOfDay != TimeSpan.Zero ||
            request.RequestedEndUtc.Value.TimeOfDay != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Alpaca corporate-action ranges must use UTC midnight boundaries.");
        }

        if (!request.DataFeed.Equals("alpaca-market-data", StringComparison.Ordinal) ||
            !request.Adjustment.Equals("raw", StringComparison.Ordinal) ||
            !request.Currency.Equals("USD", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Alpaca corporate-action evidence requires feed 'alpaca-market-data', raw adjustment, and USD.");
        }

        AlpacaBarsEvidenceRequestAdapter.EnsureOnlyParameters(
            request,
            "types",
            "limit",
            "sort");
    }

    private static string NormalizeTypes(EvidenceCollectionRequest request)
    {
        if (!request.Parameters.TryGetValue("types", out var raw))
        {
            return String.Join(',', SupportedTypes);
        }

        var values = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (values.Length == 0 ||
            values.Any(value => !SupportedTypes.Contains(value, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException(
                "Evidence request parameter 'types' contains an unsupported corporate-action type.");
        }

        return String.Join(',', values);
    }
}

public sealed class FinvizRawEvidenceRequestAdapter(Uri? baseUri = null) : IEvidenceHttpRequestAdapter
{
    private readonly Uri baseUri = baseUri ?? new Uri("https://elite.finviz.com/");

    public bool CanHandle(EvidenceCollectionRequest request) =>
        request.Provider.Equals("finviz", StringComparison.Ordinal);

    public HttpRequestMessage CreateRequest(
        EvidenceCollectionRequest request,
        string? pageToken)
    {
        if (pageToken is not null)
        {
            throw new InvalidOperationException(
                "Finviz raw response requests do not define cursor pagination.");
        }

        var endpoint = Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var absolute)
            ? absolute
            : new Uri(baseUri, request.Endpoint.TrimStart('/'));
        return new HttpRequestMessage(
            HttpMethod.Get,
            AlpacaBarsEvidenceRequestAdapter.BuildUri(
                endpoint.AbsoluteUri,
                endpoint,
                request.Parameters));
    }

    public ValueTask<EvidenceHttpPageMetadata> ReadPublishedPageMetadataAsync(
        EvidenceSourceObservation observation,
        ReadOnlyMemory<byte> rawContent,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EvidenceHttpPageMetadata(null));
}

internal static class AlpacaCorporateActionPageMetadata
{
    public static ValueTask<EvidenceHttpPageMetadata> ReadAsync(
        ReadOnlyMemory<byte> rawContent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(rawContent);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("corporate_actions", out var actions) ||
            actions.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("next_page_token", out var token) ||
            token.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
            token.ValueKind == JsonValueKind.String && String.IsNullOrWhiteSpace(token.GetString()))
        {
            throw new JsonException(
                "The Alpaca corporate-actions response has an invalid envelope or pagination token.");
        }

        return ValueTask.FromResult(
            new EvidenceHttpPageMetadata(
                token.ValueKind == JsonValueKind.String ? token.GetString() : null));
    }
}

internal static class AlpacaPageMetadata
{
    public static ValueTask<EvidenceHttpPageMetadata> ReadAsync(
        ReadOnlyMemory<byte> rawContent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(rawContent);
        var nextPageToken = document.RootElement.TryGetProperty(
                "next_page_token",
                out var tokenElement) &&
            tokenElement.ValueKind == JsonValueKind.String
                ? tokenElement.GetString()
                : null;
        return ValueTask.FromResult(new EvidenceHttpPageMetadata(nextPageToken));
    }
}
