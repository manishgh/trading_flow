using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using TradingFlow.Contracts.V1;

namespace TradingFlow.Web.Services.Charts;

public sealed class ChartHistoryCursor(IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector protector = protection.CreateProtector("TradingFlow.ChartHistory.v1");
    private sealed record Payload(string User, ChartSeriesKey Key, DateTimeOffset Before, DateTimeOffset Expires);
    // Bind the opaque cursor to the exact operator, symbol, interval, session and context.
    public string? Create(string user, ChartSeriesKey key, DateTimeOffset? before) => before is null ? null :
        protector.Protect(JsonSerializer.Serialize(new Payload(user, key, before.Value, clock.GetUtcNow().AddMinutes(15))));
    // Untrusted or expired cursors cannot widen the source read or cross chart contexts.
    public DateTimeOffset Read(string value, string user, ChartSeriesKey key)
    {
        if (value.Length > 4096) throw new ArgumentException("Invalid chart history cursor.");
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(protector.Unprotect(value));
            if (payload is null || payload.User != user || payload.Key != key || payload.Expires < clock.GetUtcNow())
                throw new ArgumentException("The chart history cursor expired or belongs to another view.");
            return payload.Before;
        }
        catch (System.Security.Cryptography.CryptographicException)
        { throw new ArgumentException("Invalid chart history cursor."); }
        catch (JsonException) { throw new ArgumentException("Invalid chart history cursor."); }
    }
}
