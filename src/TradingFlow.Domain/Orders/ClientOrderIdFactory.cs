namespace TradingFlow.Domain.Orders;

public static class ClientOrderIdFactory
{
    public static string CreatePrefix(string runName)
    {
        var chars = runName
            .Where(ch => Char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            .Take(32)
            .ToArray();
        var safe = chars.Length == 0 ? "tradingflow" : new string(chars);
        return $"tf-{safe}-";
    }

    public static string Create(string runName, string ticker)
    {
        var tickerPart = new string(ticker
            .Where(Char.IsLetterOrDigit)
            .Take(10)
            .ToArray());
        if (String.IsNullOrWhiteSpace(tickerPart))
        {
            tickerPart = "order";
        }

        var suffix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var raw = $"{CreatePrefix(runName)}{tickerPart}-{suffix}";
        return raw[..Math.Min(48, raw.Length)];
    }
}
