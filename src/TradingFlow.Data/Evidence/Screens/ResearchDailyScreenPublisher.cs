using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Data.Reference;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Data.Evidence.Screens;

/// <summary>Frozen inputs for publishing one point-in-time daily research screen from cataloged evidence datasets.</summary>
public sealed record ResearchDailyScreenRequest(
    string DailyBarsDatasetId,
    string CorporateActionsDatasetId,
    DateOnly From,
    DateOnly To,
    string OutputFolder,
    DailyScreenRules Rules,
    string? ExcludedSymbolsFile = null,
    string? IncludedSymbolsFile = null,
    int? SplitLookbackSessions = null,
    DateOnly? RequireCompleteFrom = null,
    string? ExchangeSessionsDatasetId = null,
    string? SecurityMasterDatasetId = null,
    string? OpeningBarsDatasetId = null,
    DailyRankingRules? RankingRules = null,
    string? FredVixSeries = null,
    ResearchScreenLiquidityTier? LiquidityTier = null);

/// <summary>Summary returned after a point-in-time research screen and its manifest are published to disk.</summary>
public sealed record ResearchDailyScreenResult(
    string OutputFolder,
    string ManifestSha256,
    string RankedListSha256,
    DateOnly From,
    DateOnly To,
    int SessionCount,
    int EligibleStockDays,
    int ExcludedSymbolCount,
    int SplitExcludedCount,
    int SuspectedSplitExcludedCount,
    DateOnly? RequireCompleteFrom,
    int? CompleteStocksCount,
    int? IncompleteStocksCount,
    bool HasFredVix,
    string? FredVixSha256,
    IReadOnlyDictionary<DateOnly, int> SessionCounts,
    IReadOnlyList<SessionRanking>? RankedSessions);

/// <summary>One suspected unrecorded split: overnight price jump and mean share volume on either side.</summary>
public sealed record SuspectedSplitEvent(
    DateOnly Session,
    decimal PriorClose,
    decimal Open,
    decimal Move,
    decimal VolumeBefore,
    decimal VolumeAfter);

/// <summary>
/// Shared point-in-time daily screen publisher used by both <c>TradingFlow.Cli</c> and <c>TradingFlow.Web</c>.
/// Evaluates <see cref="DailyScreen"/> on split-adjusted prior sessions, optionally freezes FRED VIXCLS, and writes
/// immutable CSV outputs and <c>screen-manifest.json</c> while refusing to overwrite any existing screen artifacts.
/// </summary>
public sealed class ResearchDailyScreenPublisher(
    IEvidenceCatalog catalog,
    IImmutableArtifactStore artifactStore,
    Func<HttpClient>? fredHttpClientFactory = null,
    Func<DateTimeOffset>? utcNow = null)
{
    public const string SplitInLookbackReason = "split_in_lookback";
    public const string SuspectedUnrecordedSplitReason = "suspected_unrecorded_split";
    public const string SplitExclusionsFileName = "split-exclusions.csv";
    public const string SuspectedSplitsFileName = "suspected-splits.csv";
    public const string IncompleteStocksFileName = "incomplete-stocks.csv";

    private static readonly string[] ProtectedOutputFiles =
    [
        ResearchScreenManifest.FileName,
        "eligible.csv",
        ResearchScreenManifest.FredVixFileName,
        ResearchScreenManifest.FredVixReceiptFileName
    ];

    /// <summary>
    /// Validates all dataset inputs and core-data overwrite guards upfront, screens every symbol into a run-owned
    /// sibling staging folder, freezes optional FRED VIXCLS closes, publishes <c>screen-manifest.json</c>, and
    /// atomically promotes the staging folder to <see cref="ResearchDailyScreenRequest.OutputFolder"/> so transient
    /// failures never leave a partial screen folder behind.
    /// </summary>
    public async Task<ResearchDailyScreenResult> PublishAsync(
        ResearchDailyScreenRequest request,
        Func<string, int, CancellationToken, ValueTask>? onProgress = null,
        TextWriter? diagnosticOutput = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.From > request.To)
        {
            throw new ArgumentException($"Screen start date {request.From:yyyy-MM-dd} cannot follow end date {request.To:yyyy-MM-dd}.");
        }

        request.Rules.Validate();
        if (request.SplitLookbackSessions is <= 0)
        {
            throw new ArgumentException("SplitLookbackSessions must be positive when specified.");
        }

        if (request.ExcludedSymbolsFile is not null && request.IncludedSymbolsFile is not null)
        {
            throw new ArgumentException("Specify either ExcludedSymbolsFile or IncludedSymbolsFile, not both.");
        }

        if (request.FredVixSeries is not null &&
            !request.FredVixSeries.Trim().Equals(RegimeRules.FredVixSeries, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"--fred-vix supports only {RegimeRules.FredVixSeries}; got '{request.FredVixSeries}'.");
        }

        if ((request.OpeningBarsDatasetId is null) != (request.RankingRules is null))
        {
            throw new ArgumentException("OpeningBarsDatasetId and RankingRules must be provided together.");
        }

        var folder = Path.GetFullPath(request.OutputFolder);
        EnsureNoProtectedScreenFilesExist(folder);

        if (onProgress is not null) await onProgress("loading_datasets", 0, cancellationToken);
        var reader = new ParquetEvidencePartitionDataReader(new EvidenceParquetCodec(), artifactStore);
        var bars = await RequireManifestAsync(request.DailyBarsDatasetId, cancellationToken);
        var actions = await RequireManifestAsync(request.CorporateActionsDatasetId, cancellationToken);
        var sessionsManifest = await ValidateOptionalManifestAsync(
            request.ExchangeSessionsDatasetId,
            EvidenceDatasetKind.ExchangeSessions,
            "exchange sessions",
            cancellationToken);
        var securityMasterManifest = await ValidateOptionalManifestAsync(
            request.SecurityMasterDatasetId,
            EvidenceDatasetKind.SecurityMaster,
            "a security master",
            cancellationToken);

        var excluded = request.ExcludedSymbolsFile is not null ? ReadSymbolList(request.ExcludedSymbolsFile) : [];
        var included = request.IncludedSymbolsFile is not null ? ReadSymbolList(request.IncludedSymbolsFile) : null;
        var splits = ShareSplits.FromCorporateActions(await reader.ReadCorporateActionsAsync(actions, cancellationToken));
        var ranking = await BuildRankingAsync(request, reader, cancellationToken);
        var required = await LoadRequiredSessionsAsync(request, sessionsManifest, reader, cancellationToken);
        var tier = request.LiquidityTier;
        var writesReturn = tier?.Measure == ResearchScreenLiquidityTier.Return12Minus1;
        var writesRange = request.Rules.MinAverageRangePercent is not null;

        var parentDir = Path.GetDirectoryName(folder)
            ?? throw new InvalidOperationException($"Could not determine parent directory for '{folder}'.");
        Directory.CreateDirectory(parentDir);
        var stagingFolder = Path.Combine(parentDir, $"{Path.GetFileName(folder)}.staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingFolder);
        try
        {
            var counts = new SortedDictionary<DateOnly, int>();
            var splitExcluded = 0;
            var suspectedExcluded = 0;
            var completeStocks = 0;
            var incompleteStocks = 0;
            var processedSymbols = 0;

            if (onProgress is not null) await onProgress("screening_symbols", 0, cancellationToken);
            await using (var incomplete = required is null ? null : new StreamWriter(Path.Combine(stagingFolder, IncompleteStocksFileName)))
            await using (var eligible = new StreamWriter(Path.Combine(stagingFolder, "eligible.csv")))
            await using (var splitDays = request.SplitLookbackSessions is null ? null : new StreamWriter(Path.Combine(stagingFolder, SplitExclusionsFileName)))
            await using (var suspectList = request.SplitLookbackSessions is null ? null : new StreamWriter(Path.Combine(stagingFolder, SuspectedSplitsFileName)))
            {
                await eligible.WriteLineAsync("session_date,ticker,price,average_volume,average_dollar_volume,atr,history_sessions" +
                    (writesReturn ? "," + ResearchScreenLiquidityTier.Return12Minus1 : "") +
                    (writesRange ? ",average_range_pct" : ""));
                if (splitDays is not null) await splitDays.WriteLineAsync("session_date,ticker,reason");
                if (suspectList is not null) await suspectList.WriteLineAsync("session_date,ticker,prior_close,open,move,volume_before,volume_after");
                if (incomplete is not null) await incomplete.WriteLineAsync("ticker,reason");

                await foreach (var partition in reader.ReadMarketBarPartitionsAsync(bars, cancellationToken))
                {
                    foreach (var group in partition.GroupBy(row => row.Symbol, StringComparer.Ordinal))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        processedSymbols++;
                        if (onProgress is not null && processedSymbols % 100 == 0)
                        {
                            await onProgress("screening_symbols", processedSymbols, cancellationToken);
                        }

                        if (excluded.Contains(group.Key) || (included is not null && !included.Contains(group.Key))) continue;
                        var series = group.OrderBy(row => row.BarStartUtc).Select(ToBar).ToArray();
                        var sessions = series.Select(bar => DateOnly.FromDateTime(bar.Timestamp.UtcDateTime)).ToArray();
                        var tradedSessions = ranking is null ? [] : sessions;
                        var stockSplits = splits.GetValueOrDefault(group.Key, []);
                        var events = request.SplitLookbackSessions is null && required is null ? [] : SuspectedSplitEvents(series, stockSplits);
                        var suspects = events.Select(item => item.Session).ToArray();
                        if (suspectList is not null)
                        {
                            foreach (var item in events)
                            {
                                await suspectList.WriteLineAsync(string.Join(',',
                                    item.Session.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                                    group.Key,
                                    Text(item.PriorClose),
                                    Text(item.Open),
                                    Text(item.Move),
                                    Text(item.VolumeBefore),
                                    Text(item.VolumeAfter)));
                            }
                        }

                        if (required is not null)
                        {
                            if (IncompleteReason(sessions, required, stockSplits, suspects) is { } reason)
                            {
                                incompleteStocks++;
                                await incomplete!.WriteLineAsync($"{group.Key},{reason}");
                                continue;
                            }

                            completeStocks++;
                        }

                        foreach (var (date, values) in Screen(group.Key, series, stockSplits, request.Rules, request.From, request.To))
                        {
                            if (request.SplitLookbackSessions is { } lookback && SplitInLookback(sessions, date, stockSplits, lookback))
                            {
                                splitExcluded++;
                                await splitDays!.WriteLineAsync($"{date:yyyy-MM-dd},{group.Key},{SplitInLookbackReason}");
                                continue;
                            }

                            if (request.SplitLookbackSessions is { } window && SuspectInLookback(sessions, date, suspects, window))
                            {
                                suspectedExcluded++;
                                await splitDays!.WriteLineAsync($"{date:yyyy-MM-dd},{group.Key},{SuspectedUnrecordedSplitReason}");
                                continue;
                            }

                            counts[date] = counts.GetValueOrDefault(date) + 1;
                            ranking?.Add(date, group.First().SecurityId, group.Key, tradedSessions);
                            await eligible.WriteLineAsync(string.Join(',',
                                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                                values.Ticker,
                                Text(values.Price),
                                Text(values.AverageShareVolume),
                                Text(values.AverageDollarVolume),
                                values.Atr is { } atr ? Text(atr) : "",
                                values.HistorySessions.ToString(CultureInfo.InvariantCulture)) +
                                (writesReturn ? "," + (Return12Minus1(series, stockSplits, Array.IndexOf(sessions, date)) is { } r ? Text(r) : "") : "") +
                                (writesRange ? "," + (values.AverageRangePercent is { } range ? Text(range) : "") : ""));
                        }
                    }
                }
            }

            if (onProgress is not null) await onProgress("finalizing_outputs", processedSymbols, cancellationToken);
            if (tier is not null)
            {
                counts = await ApplyLiquidityTierAsync(Path.Combine(stagingFolder, "eligible.csv"), tier, cancellationToken);
            }

            await File.WriteAllLinesAsync(
                Path.Combine(stagingFolder, "daily-counts.csv"),
                new[] { "session_date,eligible" }.Concat(counts.Select(pair => $"{pair.Key:yyyy-MM-dd},{pair.Value}")),
                cancellationToken);

            IReadOnlyList<SessionRanking>? rankedSessions = null;
            if (ranking is not null)
            {
                rankedSessions = await ranking.WriteAsync(stagingFolder, cancellationToken);
            }

            string? fredVixSha = null;
            if (request.FredVixSeries is not null)
            {
                if (onProgress is not null) await onProgress("downloading_fred_vix", processedSymbols, cancellationToken);
                using var http = CreateFredHttpClient();
                fredVixSha = await WriteFrozenVixAsync(
                    request.FredVixSeries,
                    stagingFolder,
                    request.To,
                    http,
                    (utcNow ?? (() => DateTimeOffset.UtcNow))(),
                    diagnosticOutput ?? TextWriter.Null,
                    cancellationToken);
            }

            if (onProgress is not null) await onProgress("publishing_manifest", processedSymbols, cancellationToken);
            var inputs = new ResearchScreenInputs(
                bars.DatasetId,
                actions.DatasetId,
                request.OpeningBarsDatasetId,
                sessionsManifest?.DatasetId,
                securityMasterManifest?.DatasetId);
            var splitRule = new ResearchScreenSplitRule(
                request.SplitLookbackSessions,
                splitExcluded,
                suspectedExcluded,
                required?.Min(),
                required is null ? null : completeStocks,
                required is null ? null : incompleteStocks);
            var manifestSha = await ResearchScreenManifest.PublishAsync(
                stagingFolder,
                inputs,
                request.Rules,
                ranking?.Rules,
                request.From,
                request.To,
                DailyRankingOutput.IssuerIdentityBasis,
                request.ExcludedSymbolsFile is null ? null : Path.GetFullPath(request.ExcludedSymbolsFile),
                cancellationToken,
                splitRule,
                tier,
                request.IncludedSymbolsFile is null ? null : Path.GetFullPath(request.IncludedSymbolsFile));

            var listFile = ranking is not null ? ResearchScreenManifest.RankedListFileName : "eligible.csv";
            var rankedListSha = ResearchScreenManifest.Sha256(await File.ReadAllBytesAsync(Path.Combine(stagingFolder, listFile), cancellationToken));

            PromoteStagingFolder(stagingFolder, folder);

            if (onProgress is not null) await onProgress("completed", processedSymbols, cancellationToken);
            return new ResearchDailyScreenResult(
                folder,
                manifestSha,
                rankedListSha,
                request.From,
                request.To,
                counts.Count,
                counts.Values.Sum(),
                excluded.Count,
                splitExcluded,
                suspectedExcluded,
                required?.Min(),
                required is null ? null : completeStocks,
                required is null ? null : incompleteStocks,
                fredVixSha is not null,
                fredVixSha,
                counts,
                rankedSessions);
        }
        catch
        {
            TryDeleteStagingDirectory(stagingFolder);
            throw;
        }
    }

    // Atomically promotes a complete staging directory to targetFolder, refusing if targetFolder gained any files.
    private static void PromoteStagingFolder(string stagingFolder, string targetFolder)
    {
        EnsureNoProtectedScreenFilesExist(targetFolder);
        if (Directory.Exists(targetFolder))
        {
            if (Directory.EnumerateFileSystemEntries(targetFolder).Any())
            {
                throw new InvalidOperationException(
                    $"Output folder '{targetFolder}' already exists and is not empty. Refusing to overwrite existing artifacts.");
            }

            Directory.Delete(targetFolder, recursive: false);
        }

        Directory.Move(stagingFolder, targetFolder);
    }

    // Deletes only this run's owned sibling staging directory on error so failed runs never wedge ScreenName.
    private static void TryDeleteStagingDirectory(string stagingFolder)
    {
        try
        {
            if (Directory.Exists(stagingFolder))
            {
                Directory.Delete(stagingFolder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Downloads FRED's VIXCLS file once, validates it with <see cref="VixCloseSeries.ParseFredCsv"/>, writes
    /// <c>fred-vixcls.csv</c> and <c>fred-vixcls-receipt.json</c> into <paramref name="folder"/>, and returns the CSV
    /// SHA-256 (or null when <paramref name="series"/> is null). Refuses if either file already exists.
    /// </summary>
    public static async Task<string?> WriteFrozenVixAsync(
        string? series,
        string folder,
        DateOnly screenEnd,
        HttpClient http,
        DateTimeOffset requestedAtUtc,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var csvPath = Path.Combine(folder, ResearchScreenManifest.FredVixFileName);
        var receiptPath = Path.Combine(folder, ResearchScreenManifest.FredVixReceiptFileName);
        if (File.Exists(csvPath) || File.Exists(receiptPath))
        {
            throw new InvalidOperationException(
                $"'{folder}' already holds a frozen VIX copy or receipt ('{ResearchScreenManifest.FredVixFileName}' / " +
                $"'{ResearchScreenManifest.FredVixReceiptFileName}'); refusing to overwrite or reuse earlier provider evidence. Use an empty output folder.");
        }

        if (series is null) return null;
        if (!series.Trim().Equals(RegimeRules.FredVixSeries, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"--fred-vix supports only {RegimeRules.FredVixSeries}; got '{series}'.");
        }

        var download = await FredSeriesFile.DownloadAsync(http, RegimeRules.FredVixSeries, requestedAtUtc, cancellationToken);
        var closes = VixCloseSeries.ParseFredCsv(Encoding.UTF8.GetString(download.Bytes));
        await File.WriteAllBytesAsync(csvPath, download.Bytes, cancellationToken);
        var sha256 = ResearchScreenManifest.Sha256(download.Bytes);
        var receipt = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["series"] = RegimeRules.FredVixSeries,
            ["url"] = download.Url.ToString(),
            ["requested_at_utc"] = download.RequestedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["http_status"] = download.HttpStatus,
            ["bytes"] = download.Bytes.Length,
            ["sha256"] = sha256,
            ["published_closes"] = closes.Count,
            ["first_observation_date"] = closes.FirstDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["last_observation_date"] = closes.LastDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["note"] = "Downloaded once at requested_at_utc. Observation dates are FRED's; they are not the times the closes were first published."
        };
        await File.WriteAllTextAsync(
            receiptPath,
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        output.WriteLine(
            $"Frozen FRED VIXCLS copy: {closes.Count} closes from {closes.FirstDate:yyyy-MM-dd} to {closes.LastDate:yyyy-MM-dd}, " +
            $"HTTP {download.HttpStatus}, {download.Bytes.Length} bytes, SHA-256 {sha256}.");
        if (closes.LastDate is not { } last || last < screenEnd)
        {
            output.WriteLine($"Warning: the VIX copy ends before the screen's last session {screenEnd:yyyy-MM-dd}; entries after its last close are refused.");
        }

        return sha256;
    }

    /// <summary>
    /// Refuses publication upfront if <paramref name="folder"/> already contains any immutable screen manifest,
    /// eligibility list, or frozen provider receipt.
    /// </summary>
    public static void EnsureNoProtectedScreenFilesExist(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in ProtectedOutputFiles)
        {
            if (File.Exists(Path.Combine(folder, file)))
            {
                throw new InvalidOperationException(
                    $"Output folder '{folder}' already contains published screen or provider evidence ('{file}'). " +
                    "Refusing to overwrite existing research screen artifacts; choose a new screen name.");
            }
        }
    }

    /// <summary>Screens each traded session in [from, to] using strictly prior sessions adjusted to that date's split basis.</summary>
    public static IEnumerable<(DateOnly Date, DailyScreenValues Values)> Screen(
        string ticker,
        IReadOnlyList<OhlcvBar> series,
        IReadOnlyList<ShareSplit> splits,
        DailyScreenRules rules,
        DateOnly from,
        DateOnly to)
    {
        var window = rules.PriorSessionsNeeded;
        for (var index = 1; index < series.Count; index++)
        {
            var date = DateOnly.FromDateTime(series[index].Timestamp.UtcDateTime);
            if (date < from || date > to) continue;
            var start = Math.Max(0, index - window);
            var prior = SplitBasis.AsOf(series.Skip(start).Take(index - start).ToArray(), splits, date);
            var result = DailyScreen.Evaluate(ticker, prior, series[index].Open, rules);
            if (result.Eligible) yield return (date, result.Values!);
        }
    }

    /// <summary>Computes the 12-1 month return at <paramref name="index"/> using only the 253 earlier sessions on that date's split basis.</summary>
    public static decimal? Return12Minus1(IReadOnlyList<OhlcvBar> series, IReadOnlyList<ShareSplit> splits, int index)
    {
        const int earlier = 253;
        if (index < earlier) return null;
        var date = DateOnly.FromDateTime(series[index].Timestamp.UtcDateTime);
        var prior = SplitBasis.AsOf(series.Skip(index - earlier).Take(earlier).ToArray(), splits, date);
        var older = prior[^253].Close;
        return older > 0m ? prior[^22].Close / older - 1m : null;
    }

    /// <summary>Parses a 1-based inclusive rank tier ("from-to"), rejecting invalid or inverted bounds.</summary>
    public static ResearchScreenLiquidityTier ParseTier(string text)
    {
        var parts = text.Split('-');
        if (parts.Length != 2 ||
            !Int32.TryParse(parts[0], CultureInfo.InvariantCulture, out var from) ||
            !Int32.TryParse(parts[1], CultureInfo.InvariantCulture, out var to) ||
            from < 1 || to < from)
        {
            throw new ArgumentException($"A rank tier must be 'from-to' with 1 <= from <= to; got '{text}'.");
        }

        return new ResearchScreenLiquidityTier(from, to);
    }

    /// <summary>Filters eligible.csv to stocks ranked inside <paramref name="tier"/> on each session and returns updated daily counts.</summary>
    public static async Task<SortedDictionary<DateOnly, int>> ApplyLiquidityTierAsync(
        string eligiblePath,
        ResearchScreenLiquidityTier tier,
        CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(eligiblePath, cancellationToken);
        var header = lines[0];
        var column = Array.IndexOf(header.Split(','), tier.Measure);
        if (column < 0) throw new InvalidDataException($"eligible.csv has no {tier.Measure} column.");
        var kept = lines.Skip(1).Where(line => line.Length > 0).Select(line => (Line: line, Cells: line.Split(',')))
            .Where(row => row.Cells[column].Length > 0)
            .GroupBy(row => row.Cells[0], StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .SelectMany(group => group.OrderByDescending(row => Decimal.Parse(row.Cells[column], CultureInfo.InvariantCulture))
                .ThenBy(row => row.Cells[1], StringComparer.Ordinal).Skip(tier.FromRank - 1).Take(tier.ToRank - tier.FromRank + 1)
                .OrderBy(row => row.Cells[1], StringComparer.Ordinal))
            .ToArray();
        await File.WriteAllLinesAsync(eligiblePath, new[] { header }.Concat(kept.Select(row => row.Line)), cancellationToken);
        return new SortedDictionary<DateOnly, int>(kept.GroupBy(row => DateOnly.ParseExact(row.Cells[0], "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToDictionary(group => group.Key, group => group.Count()));
    }

    /// <summary>Returns session dates with large overnight jumps lacking a recorded corporate-action split.</summary>
    public static DateOnly[] UnrecordedSplitSessions(IReadOnlyList<OhlcvBar> series, IReadOnlyList<ShareSplit> splits) =>
        SuspectedSplitEvents(series, splits).Select(item => item.Session).ToArray();

    /// <summary>Detects overnight jumps (&lt;= 0.55x or &gt;= 1.90x prior close) that have no matching recorded split ex-date.</summary>
    public static IReadOnlyList<SuspectedSplitEvent> SuspectedSplitEvents(IReadOnlyList<OhlcvBar> series, IReadOnlyList<ShareSplit> splits)
    {
        var recorded = splits.Select(split => split.ExDate).ToHashSet();
        var suspects = new List<SuspectedSplitEvent>();
        for (var i = 1; i < series.Count; i++)
        {
            if (series[i - 1].Close <= 0) continue;
            var move = series[i].Open / series[i - 1].Close;
            var session = DateOnly.FromDateTime(series[i].Timestamp.UtcDateTime);
            if ((move <= 0.55m || move >= 1.90m) && !recorded.Contains(session))
            {
                suspects.Add(new(
                    session,
                    series[i - 1].Close,
                    series[i].Open,
                    move,
                    MeanVolume(series, Math.Max(0, i - 20), i),
                    MeanVolume(series, i, Math.Min(series.Count, i + 20))));
            }
        }

        return suspects;
    }

    /// <summary>Returns why a symbol fails completeness over <paramref name="required"/> sessions, or null if complete.</summary>
    public static string? IncompleteReason(
        IReadOnlyCollection<DateOnly> sessions,
        SortedSet<DateOnly> required,
        IReadOnlyList<ShareSplit> splits,
        IReadOnlyCollection<DateOnly> suspects)
    {
        var first = required.Min;
        var last = required.Max;
        var missing = required.Count - sessions.Count(day => day >= first && day <= last && required.Contains(day));
        if (missing > 0) return $"missing_sessions_{missing}";
        if (splits.Any(split => split.ExDate >= first && split.ExDate <= last)) return "split_recorded";
        if (suspects.Any(day => day >= first && day <= last)) return SuspectedUnrecordedSplitReason;
        return null;
    }

    /// <summary>True when a suspected unrecorded split occurred strictly inside the preceding <paramref name="lookbackSessions"/>.</summary>
    public static bool SuspectInLookback(IReadOnlyList<DateOnly> sessions, DateOnly date, IReadOnlyList<DateOnly> suspects, int lookbackSessions)
    {
        if (suspects.Count == 0) return false;
        var index = sessions is DateOnly[] array ? Array.BinarySearch(array, date) : sessions.ToList().BinarySearch(date);
        if (index < 0) index = ~index;
        var first = sessions[Math.Max(0, index - lookbackSessions)];
        return suspects.Any(session => session > first && session < date);
    }

    /// <summary>True when a recorded split known on or before <paramref name="date"/> falls inside its <paramref name="lookbackSessions"/>.</summary>
    public static bool SplitInLookback(IReadOnlyList<DateOnly> sessions, DateOnly date, IReadOnlyList<ShareSplit> splits, int lookbackSessions)
    {
        if (splits.Count == 0) return false;
        var index = sessions is DateOnly[] array ? Array.BinarySearch(array, date) : sessions.ToList().BinarySearch(date);
        if (index < 0) index = ~index;
        var first = sessions[Math.Max(0, index - lookbackSessions)];
        return splits.Any(split => split.ExDate > first && split.ExDate <= date);
    }

    // Creates either the injected HttpClient (tests/Web) or a compressed default client for FRED downloads.
    private HttpClient CreateFredHttpClient()
    {
        if (fredHttpClientFactory is not null)
        {
            return fredHttpClientFactory();
        }

        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "TradingFlow/1.0");
        return client;
    }

    // Loads and verifies a required catalog dataset manifest by ID.
    private async Task<EvidenceDatasetManifest> RequireManifestAsync(string datasetId, CancellationToken cancellationToken) =>
        await catalog.GetDatasetAsync(datasetId, cancellationToken)
            ?? throw new InvalidDataException($"Dataset '{datasetId}' was not found.");

    // Loads an optional dataset manifest and verifies it matches the expected EvidenceDatasetKind.
    private async Task<EvidenceDatasetManifest?> ValidateOptionalManifestAsync(
        string? datasetId,
        EvidenceDatasetKind expectedKind,
        string label,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(datasetId)) return null;
        var manifest = await RequireManifestAsync(datasetId, cancellationToken);
        if (manifest.Kind != expectedKind)
        {
            throw new InvalidDataException($"Dataset '{datasetId}' is a {manifest.Kind} dataset, not {label}.");
        }

        return manifest;
    }

    // Builds the optional opening-volume ranking output when both opening bars and ranking rules are configured.
    private async Task<DailyRankingOutput?> BuildRankingAsync(
        ResearchDailyScreenRequest request,
        ParquetEvidencePartitionDataReader reader,
        CancellationToken cancellationToken)
    {
        if (request.OpeningBarsDatasetId is null || request.RankingRules is null) return null;
        request.RankingRules.Validate();
        var openingBars = await RequireManifestAsync(request.OpeningBarsDatasetId, cancellationToken);
        var volumes = await DailyRankingOutput.ReadOpeningVolumesAsync(reader, openingBars, cancellationToken);
        var collected = await DailyRankingOutput.ReadCollectedSessionsAsync(catalog, openingBars, cancellationToken);
        return new DailyRankingOutput(request.RankingRules, volumes, collected);
    }

    // Resolves the required complete-session set when RequireCompleteFrom is configured.
    private static async Task<SortedSet<DateOnly>?> LoadRequiredSessionsAsync(
        ResearchDailyScreenRequest request,
        EvidenceDatasetManifest? sessionsManifest,
        ParquetEvidencePartitionDataReader reader,
        CancellationToken cancellationToken)
    {
        if (request.RequireCompleteFrom is not { } completeFrom) return null;
        if (sessionsManifest is null)
        {
            throw new ArgumentException("ExchangeSessionsDatasetId is required when RequireCompleteFrom is specified.");
        }

        var days = (await reader.ReadExchangeSessionsAsync(sessionsManifest, cancellationToken))
            .Select(row => row.TradeDate)
            .Where(day => day >= completeFrom && day <= request.To)
            .ToArray();
        if (days.Length == 0 || days.Max() < request.To.AddDays(-7) || days.Min() > completeFrom.AddDays(7))
        {
            throw new InvalidDataException(
                $"Exchange sessions dataset '{sessionsManifest.DatasetId}' does not cover {completeFrom:yyyy-MM-dd} to {request.To:yyyy-MM-dd}.");
        }

        return new SortedSet<DateOnly>(days);
    }

    // Reads a one-symbol-per-line text file into an upper-case ordinal set.
    private static HashSet<string> ReadSymbolList(string path) =>
        File.ReadLines(Path.GetFullPath(path))
            .Select(line => line.Trim().ToUpperInvariant())
            .Where(line => line.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    // Converts a normalized Parquet market-bar row into the shared OhlcvBar domain model.
    private static OhlcvBar ToBar(MarketBarEvidenceRow row) => new(
        row.Symbol,
        row.BarStartUtc,
        row.Timeframe,
        EvidenceFixedDecimal.FromPriceUnits(row.OpenPriceUnits),
        EvidenceFixedDecimal.FromPriceUnits(row.HighPriceUnits),
        EvidenceFixedDecimal.FromPriceUnits(row.LowPriceUnits),
        EvidenceFixedDecimal.FromPriceUnits(row.ClosePriceUnits),
        row.Volume);

    // Computes mean share volume over the half-open slice [from, to), returning 0 for an empty range.
    private static decimal MeanVolume(IReadOnlyList<OhlcvBar> series, int from, int to) =>
        to > from ? Enumerable.Range(from, to - from).Average(index => series[index].Volume) : 0m;

    // Formats decimal screen metrics rounded to 4 decimal places in invariant culture.
    private static string Text(decimal value) => decimal.Round(value, 4).ToString(CultureInfo.InvariantCulture);
}
