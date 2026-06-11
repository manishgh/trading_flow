using System.Globalization;
using System.Text;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Data.Csv;

public sealed class CachedMarketDataProvider(
    IMarketDataProvider innerProvider,
    string normalizedRoot,
    string cachePolicy) : IMarketDataProvider
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
                var path = GetPath(ticker, timeframe);
                var useExisting = File.Exists(path) &&
                    !cachePolicy.Equals("refresh", StringComparison.OrdinalIgnoreCase);

                if (!useExisting)
                {
                    var downloaded = new List<OhlcvBar>();
                    await foreach (var bar in innerProvider.GetBarsAsync([ticker], [timeframe], start, end, cancellationToken))
                    {
                        downloaded.Add(bar);
                    }

                    await WriteBarsAsync(path, downloaded, cancellationToken);
                }

                await foreach (var cached in ReadBarsAsync(path, timeframe, start, end, cancellationToken))
                {
                    yield return cached;
                }
            }
        }
    }

    private string GetPath(string ticker, string timeframe)
    {
        var safeTicker = ticker.Trim().ToUpperInvariant();
        var safeTimeframe = timeframe.Trim().ToLowerInvariant();
        return Path.Combine(normalizedRoot, safeTicker, $"bars_{safeTimeframe}.csv");
    }

    private static async Task WriteBarsAsync(string path, IReadOnlyCollection<OhlcvBar> bars, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Open(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            await writer.WriteLineAsync("ticker,timestamp,open,high,low,close,volume".AsMemory(), cancellationToken);
            foreach (var bar in bars.OrderBy(x => x.Timestamp))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(
                    FormattableString.Invariant($"{bar.Ticker},{bar.Timestamp:O},{bar.Open},{bar.High},{bar.Low},{bar.Close},{bar.Volume}").AsMemory(),
                    cancellationToken);
            }
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private static async IAsyncEnumerable<OhlcvBar> ReadBarsAsync(
        string path,
        string timeframe,
        DateTimeOffset start,
        DateTimeOffset end,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            yield break;
        }

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
            var timestamp = DateTimeOffset.Parse(GetString(fields, index, "timestamp"), CultureInfo.InvariantCulture);
            if (timestamp < start || timestamp > end)
            {
                continue;
            }

            yield return new OhlcvBar(
                GetString(fields, index, "ticker"),
                timestamp,
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
