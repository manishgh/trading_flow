using System.Globalization;
using System.Text;
using System.Text.Json;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Data.Csv;

public sealed record RollingCandleWarmRequest(
    IReadOnlyCollection<string> Tickers,
    IReadOnlyCollection<string> Timeframes,
    string OutputRoot,
    int LookbackDays,
    DateTimeOffset End,
    bool WriteCalculatedIndicators = true);

public sealed record RollingCandleWarmResult(
    string OutputRoot,
    DateTimeOffset Start,
    DateTimeOffset End,
    int LookbackDays,
    int TickerCount,
    int TimeframeCount,
    int BarCount,
    IReadOnlyList<RollingCandleFileSummary> Files);

public sealed record RollingCandleFileSummary(
    string Ticker,
    string Timeframe,
    string Path,
    string? CalculatedPath,
    int BarCount,
    DateTimeOffset? FirstTimestamp,
    DateTimeOffset? LastTimestamp);

public sealed class RollingCandleCacheWarmer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
    private readonly IndicatorEngine indicatorEngine = new();

    public async Task<RollingCandleWarmResult> WarmAsync(
        IMarketDataProvider provider,
        RollingCandleWarmRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LookbackDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "LookbackDays must be greater than zero.");
        }

        var tickers = request.Tickers
            .Select(x => x.Trim().ToUpperInvariant())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var timeframes = request.Timeframes
            .Select(x => x.Trim().ToLowerInvariant())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tickers.Length == 0)
        {
            throw new ArgumentException("At least one ticker is required.", nameof(request));
        }

        if (timeframes.Length == 0)
        {
            throw new ArgumentException("At least one timeframe is required.", nameof(request));
        }

        var outputRoot = Path.GetFullPath(request.OutputRoot);
        var start = request.End.AddDays(-request.LookbackDays);
        var barsByKey = new Dictionary<(string Ticker, string Timeframe), List<OhlcvBar>>();

        await foreach (var bar in provider.GetBarsAsync(tickers, timeframes, start, request.End, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bar.Timestamp < start || bar.Timestamp > request.End)
            {
                continue;
            }

            var key = (bar.Ticker.Trim().ToUpperInvariant(), bar.Timeframe.Trim().ToLowerInvariant());
            if (!barsByKey.TryGetValue(key, out var bars))
            {
                bars = new List<OhlcvBar>();
                barsByKey[key] = bars;
            }

            bars.Add(bar with { Ticker = key.Item1, Timeframe = key.Item2 });
        }

        var summaries = new List<RollingCandleFileSummary>();
        foreach (var ticker in tickers)
        {
            foreach (var timeframe in timeframes)
            {
                barsByKey.TryGetValue((ticker, timeframe), out var bars);
                var normalizedBars = (bars ?? [])
                    .Where(bar => bar.Timestamp >= start && bar.Timestamp <= request.End)
                    .GroupBy(bar => bar.Timestamp)
                    .Select(group => group.Last())
                    .OrderBy(bar => bar.Timestamp)
                    .ToArray();
                var path = Path.Combine(outputRoot, ticker, $"bars_{timeframe}.csv");
                await WriteBarsAtomicAsync(path, normalizedBars, cancellationToken);
                string? calculatedPath = null;
                if (request.WriteCalculatedIndicators)
                {
                    calculatedPath = Path.Combine(outputRoot, ticker, $"indicators_{timeframe}.csv");
                    await WriteIndicatorsAtomicAsync(
                        calculatedPath,
                        indicatorEngine.Compute(normalizedBars),
                        cancellationToken);
                }

                summaries.Add(new RollingCandleFileSummary(
                    ticker,
                    timeframe,
                    path,
                    calculatedPath,
                    normalizedBars.Length,
                    normalizedBars.FirstOrDefault()?.Timestamp,
                    normalizedBars.LastOrDefault()?.Timestamp));
            }
        }

        var result = new RollingCandleWarmResult(
            outputRoot,
            start,
            request.End,
            request.LookbackDays,
            tickers.Length,
            timeframes.Length,
            summaries.Sum(x => x.BarCount),
            summaries);
        await WriteManifestAsync(outputRoot, result, cancellationToken);
        return result;
    }

    private static async Task WriteIndicatorsAtomicAsync(
        string path,
        IReadOnlyCollection<IndicatorSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Open(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteLineAsync(
                    "ticker,timestamp,timeframe,close,volume,vwap,rsi,atr,ema10,ema20,ema50,ema200,bollinger_middle,bollinger_upper,bollinger_lower,relative_volume,slot_relative_volume,session_relative_volume,slot_average_volume,cumulative_average_volume,average_session_volume,relative_volume_sample_count,macd_line,macd_signal,macd_histogram".AsMemory(),
                    cancellationToken);
                foreach (var snapshot in snapshots.OrderBy(x => x.Timestamp))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(
                        FormattableString.Invariant(
                            $"{snapshot.Ticker},{snapshot.Timestamp:O},{snapshot.Timeframe},{snapshot.CurrentPrice},{snapshot.CurrentVolume},{FormatNullable(snapshot.Vwap)},{FormatNullable(snapshot.Rsi)},{FormatNullable(snapshot.Atr)},{FormatNullable(snapshot.Ema10)},{FormatNullable(snapshot.Ema20)},{FormatNullable(snapshot.Ema50)},{FormatNullable(snapshot.Ema200)},{FormatNullable(snapshot.BollingerMiddle)},{FormatNullable(snapshot.BollingerUpper)},{FormatNullable(snapshot.BollingerLower)},{FormatNullable(snapshot.RelativeVolume)},{FormatNullable(snapshot.SlotRelativeVolume)},{FormatNullable(snapshot.SessionRelativeVolume)},{FormatNullable(snapshot.SlotAverageVolume)},{FormatNullable(snapshot.CumulativeAverageVolume)},{FormatNullable(snapshot.AverageSessionVolume)},{snapshot.RelativeVolumeSampleCount},{FormatNullable(snapshot.MacdLine)},{FormatNullable(snapshot.MacdSignal)},{FormatNullable(snapshot.MacdHistogram)}").AsMemory(),
                        cancellationToken);
                }
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static string FormatNullable(decimal? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture) ?? String.Empty;
    }

    private static async Task WriteBarsAtomicAsync(
        string path,
        IReadOnlyCollection<OhlcvBar> bars,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Open(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteLineAsync("ticker,timestamp,open,high,low,close,volume".AsMemory(), cancellationToken);
                foreach (var bar in bars)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(
                        FormattableString.Invariant($"{bar.Ticker},{bar.Timestamp:O},{bar.Open},{bar.High},{bar.Low},{bar.Close},{bar.Volume}").AsMemory(),
                        cancellationToken);
                }
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static async Task WriteManifestAsync(
        string outputRoot,
        RollingCandleWarmResult result,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputRoot);
        var manifestPath = Path.Combine(outputRoot, "_warmup_manifest.json");
        var tempPath = $"{manifestPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                JsonSerializer.Serialize(result, JsonOptions),
                new UTF8Encoding(false),
                cancellationToken);
            File.Move(tempPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
