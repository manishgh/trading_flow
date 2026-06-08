using System.Globalization;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Data.Csv;

public sealed class CsvMarketDataProvider(string normalizedRoot) : IMarketDataProvider
{
    public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
        IReadOnlyCollection<string> tickers,
        IReadOnlyCollection<string> timeframes,
        DateTimeOffset start,
        DateTimeOffset end,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var ticker in tickers)
        {
            foreach (var timeframe in timeframes)
            {
                var path = Path.Combine(normalizedRoot, ticker, $"bars_{timeframe}.csv");
                if (!File.Exists(path))
                {
                    continue;
                }

                await foreach (var bar in ReadBarsAsync(path, timeframe, cancellationToken))
                {
                    if (bar.Timestamp >= start && bar.Timestamp <= end)
                    {
                        yield return bar;
                    }
                }
            }
        }
    }

    private static async IAsyncEnumerable<OhlcvBar> ReadBarsAsync(
        string path,
        string timeframe,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(path);
        var header = await reader.ReadLineAsync(cancellationToken);
        if (header is null)
        {
            yield break;
        }

        var columns = header.Split(',');
        var index = columns
            .Select((name, i) => (Name: name.Trim(), Index: i))
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (String.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = line.Split(',');
            yield return new OhlcvBar(
                GetString(fields, index, "ticker"),
                DateTimeOffset.Parse(GetString(fields, index, "timestamp"), CultureInfo.InvariantCulture),
                timeframe,
                GetDecimal(fields, index, "open"),
                GetDecimal(fields, index, "high"),
                GetDecimal(fields, index, "low"),
                GetDecimal(fields, index, "close"),
                GetDecimal(fields, index, "volume"));
        }
    }

    private static string GetString(string[] fields, IReadOnlyDictionary<string, int> index, string name)
    {
        return fields[index[name]].Trim();
    }

    private static decimal GetDecimal(string[] fields, IReadOnlyDictionary<string, int> index, string name)
    {
        return Decimal.Parse(GetString(fields, index, name), CultureInfo.InvariantCulture);
    }
}
