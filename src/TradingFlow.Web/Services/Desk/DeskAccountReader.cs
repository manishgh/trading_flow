using System.Globalization;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Web.Services.Desk;

public sealed record DeskHolding(string Symbol, decimal Quantity, decimal? AverageEntry, decimal? CostBasis,
    decimal? MarketValue, decimal? UnrealizedPl);
public sealed record DeskAccount(bool Available, string Detail, DateTimeOffset ReadAtUtc, IReadOnlyList<DeskHolding> Holdings);
public interface IDeskAccountReader { Task<DeskAccount> ReadAsync(CancellationToken ct); }

/// <summary>Account-level, GET-only broker projection, including positions without a running job.</summary>
public sealed class DeskAccountReader(AlpacaCredentialProvider credentials, IConfiguration configuration,
    IHttpClientFactory clients, IRawArchiveWriter archives, TimeProvider clock,
    ILogger<DeskAccountReader> logger) : IDeskAccountReader, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DeskAccount? cached;
    public async Task<DeskAccount> ReadAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (cached is not null && clock.GetUtcNow() - cached.ReadAtUtc < TimeSpan.FromSeconds(15)) return cached;
            var now = clock.GetUtcNow();
            if (!credentials.IsConfigured) return cached = new(false, "Paper account unavailable: credentials are not configured.", now, []);
            try
            {
                using var client = new AlpacaTradingRestClient(clients.CreateClient(), AlpacaOptions.Create(ProductionProfile.Paper) with {
                    KeyId = credentials.KeyId, SecretKey = credentials.SecretKey }, archives);
                using var accountRequest = new HttpRequestMessage(HttpMethod.Get, "v2/account");
                var account = await client.SendAsync(accountRequest, "desk_account", cancellationToken: ct);
                if (!account.IsSuccessStatusCode) throw new InvalidOperationException("Account read failed.");
                using var accountJson = JsonDocument.Parse(account.Payload);
                var expected = configuration["Alpaca:ExpectedAccountId"];
                if (String.IsNullOrWhiteSpace(expected) || !String.Equals(accountJson.RootElement.GetProperty("id").GetString(), expected, StringComparison.OrdinalIgnoreCase))
                    return cached = new(false, "Paper account binding is unavailable or does not match.", now, []);
                using var request = new HttpRequestMessage(HttpMethod.Get, "v2/positions");
                var response = await client.SendAsync(request, "desk_positions", cancellationToken: ct);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Positions read failed.");
                using var json = JsonDocument.Parse(response.Payload);
                return cached = new(true, "Alpaca paper account · broker unrealized P&L", now, ParsePositions(json.RootElement));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Desk paper holdings read failed.");
                return cached = new(false, "Paper holdings unavailable; quantities and P&L are unknown.", now, []);
            }
        }
        finally { gate.Release(); }
    }
    public static IReadOnlyList<DeskHolding> ParsePositions(JsonElement root) => root.EnumerateArray().Select(x => new DeskHolding(
        x.GetProperty("symbol").GetString()!.ToUpperInvariant(), Number(x, "qty") ?? throw new JsonException("Missing quantity"),
        Number(x, "avg_entry_price"), Number(x, "cost_basis"), Number(x, "market_value"), Number(x, "unrealized_pl"))).ToArray();
    private static decimal? Number(JsonElement x, string key) => x.TryGetProperty(key, out var value) &&
        Decimal.TryParse(value.ToString(), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    public void Dispose() => gate.Dispose();
}
