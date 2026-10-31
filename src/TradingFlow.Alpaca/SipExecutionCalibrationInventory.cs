using System.Collections.Immutable;

namespace TradingFlow.Alpaca;

/// <summary>Vocabulary recognition only. Recognition never grants stop, OHLC or fill eligibility.</summary>
public sealed record SipConditionVocabulary(string Version, string ArtifactSha256,
    ImmutableArray<string> QuoteCodes, ImmutableArray<string> TradeCodes);

public sealed record SipExecutionSourceMeasurements(string Version, string MeasurementClass,
    SipArchiveReaderIdentity Quotes, SipArchiveReaderIdentity Trades, long DeclaredStaleAfterNanoseconds,
    long QuoteRows, long TradeRows, long QuoteInvalidPriceRows, long QuoteNonpositiveDisplayedSideRows,
    long TradeInvalidPriceOrSizeRows, long SameNanosecondQuoteTradeBatches, long SameNanosecondQuoteTradeRows,
    long MultipleDistinctQuotesAtOneNanosecondBatches, long TradesWithoutStrictlyPriorQuote,
    long TradesWithAmbiguousPriorQuote, long TradesWithInvalidPriorQuote, long TradesComparedToPriorQuote,
    long TradesInsidePriorQuote, long TradesOutsidePriorQuote, long TradesWithStalePriorQuote,
    long QuoteRowsWithUnrecognizedConditions, long TradeRowsWithUnrecognizedConditions,
    long QuoteRowsWithMissingOrEmptyConditions, long TradeRowsWithMissingOrEmptyConditions,
    long OddLotConditionITradeRows, ImmutableSortedDictionary<long, long> SpreadBpsFloorHistogram,
    ImmutableSortedDictionary<long, long> DisplayedBidSharesHistogram,
    ImmutableSortedDictionary<long, long> DisplayedAskSharesHistogram,
    ImmutableSortedDictionary<long, long> PriorQuoteAgeMillisecondsFloorHistogram,
    ImmutableSortedDictionary<string, long> QuoteConditionCounts, ImmutableSortedDictionary<string, long> TradeConditionCounts,
    ImmutableSortedDictionary<string, long> UnrecognizedQuoteConditionCounts, ImmutableSortedDictionary<string, long> UnrecognizedTradeConditionCounts,
    ImmutableSortedDictionary<string, long> QuoteTapeCounts, ImmutableSortedDictionary<string, long> TradeTapeCounts,
    ImmutableSortedDictionary<string, long> BidExchangeCounts, ImmutableSortedDictionary<string, long> AskExchangeCounts,
    ImmutableSortedDictionary<string, long> TradeExchangeCounts, SipConditionVocabulary? ConditionVocabulary,
    bool FullyConsumedAndVerified, bool OriginalReceiptProven = false, bool BrokerLatencyCalibrated = false,
    bool MarketImpactCalibrated = false, bool FillEligibilityCertified = false, bool HistoricalSecurityIdentityProven = false);

/// <summary>
/// Retrospective source measurements, not measured broker latency/impact or executable fills.
/// Quote ages are differences of source timestamps, never receipt latency estimates.
/// </summary>
public static class SipExecutionCalibrationInventory
{
    public const string Version = "sip-retrospective-source-measurements-v1";
    public const string MeasurementClass = "RETROSPECTIVE_SOURCE_MEASUREMENTS";

    public static async Task<SipExecutionSourceMeasurements> MeasureAsync(SipExecutionArchiveReader quotes,
        SipExecutionArchiveReader trades, long staleAfterNanoseconds, SipConditionVocabulary? vocabulary = null,
        CancellationToken ct = default)
    {
        SipExecutionArchiveReader.RequirePair(quotes, trades);
        if (staleAfterNanoseconds < 0) throw new ArgumentOutOfRangeException(nameof(staleAfterNanoseconds));
        if (vocabulary is not null && (String.IsNullOrWhiteSpace(vocabulary.Version) ||
            vocabulary.ArtifactSha256 is not { Length: 64 } || !vocabulary.ArtifactSha256.All(Uri.IsHexDigit) ||
            vocabulary.QuoteCodes.IsDefault || vocabulary.TradeCodes.IsDefault ||
            vocabulary.QuoteCodes.Concat(vocabulary.TradeCodes).Any(String.IsNullOrEmpty) ||
            vocabulary.QuoteCodes.Distinct(StringComparer.Ordinal).Count() != vocabulary.QuoteCodes.Length ||
            vocabulary.TradeCodes.Distinct(StringComparer.Ordinal).Count() != vocabulary.TradeCodes.Length))
            throw new InvalidDataException("A supplied condition vocabulary requires frozen version/hash and distinct explicit code sets.");
        var recognizedQuotes = (vocabulary?.QuoteCodes ?? []).ToHashSet(StringComparer.Ordinal);
        var recognizedTrades = (vocabulary?.TradeCodes ?? []).ToHashSet(StringComparer.Ordinal);
        long quoteRows = 0, tradeRows = 0, invalidQuotes = 0, zeroSizeQuotes = 0, invalidTrades = 0,
            sharedBatches = 0, sharedTrades = 0, multiQuotes = 0, missingPrior = 0, ambiguousPrior = 0,
            invalidPrior = 0, compared = 0, inside = 0, outside = 0, stale = 0,
            unknownQuoteRows = 0, unknownTradeRows = 0, emptyQuoteConditions = 0, emptyTradeConditions = 0, oddLots = 0;
        var spread = new SortedDictionary<long, long>(); var bids = new SortedDictionary<long, long>(); var asks = new SortedDictionary<long, long>();
        var ages = new SortedDictionary<long, long>(); var qc = Codes(); var tc = Codes(); var uq = Codes(); var ut = Codes();
        var qt = Codes(); var tt = Codes(); var bx = Codes(); var ax = Codes(); var tx = Codes();
        SipArchiveEvent? priorQuote = null; var priorQuoteAmbiguous = false;
        await foreach (var batch in SipExecutionArchiveReader.MergeAsync(quotes, trades, ct)) {
            if (batch.Quotes.Length > 0 && batch.Trades.Length > 0) { sharedBatches++; sharedTrades += batch.Trades.Length; }
            foreach (var quote in batch.Quotes) {
                quoteRows++; Count(qt, quote.Tape ?? "<missing>"); Count(bx, quote.BidExchange ?? "<missing>"); Count(ax, quote.AskExchange ?? "<missing>");
                Count(bids, quote.BidSizeShares!.Value); Count(asks, quote.AskSizeShares!.Value);
                if (!ValidPrices(quote)) invalidQuotes++;
                else {
                    var middle = quote.Bid!.Value / 2m + quote.Ask!.Value / 2m;
                    Count(spread, checked((long)Decimal.Floor((quote.Ask.Value - quote.Bid.Value) / middle * 10000m)));
                }
                if (quote.BidSizeShares <= 0 || quote.AskSizeShares <= 0) zeroSizeQuotes++;
                if (quote.Conditions.IsEmpty) emptyQuoteConditions++;
                if (quote.Conditions.Any(code => !recognizedQuotes.Contains(code))) unknownQuoteRows++;
                foreach (var code in quote.Conditions) { Count(qc, code); if (!recognizedQuotes.Contains(code)) Count(uq, code); }
            }
            // Trades compare only with the latest STRICTLY EARLIER nanosecond batch.
            // A same-ns quote is reported separately and cannot replace that prior quote.
            foreach (var trade in batch.Trades) {
                tradeRows++; Count(tt, trade.Tape ?? "<missing>"); Count(tx, trade.TradeExchange ?? "<missing>");
                var invalidTrade = trade.TradePrice <= 0 || trade.TradeSizeShares <= 0;
                if (invalidTrade) invalidTrades++;
                if (trade.Conditions.IsEmpty) emptyTradeConditions++;
                if (trade.Conditions.Contains("I", StringComparer.Ordinal)) oddLots++;
                if (trade.Conditions.Any(code => !recognizedTrades.Contains(code))) unknownTradeRows++;
                foreach (var code in trade.Conditions) { Count(tc, code); if (!recognizedTrades.Contains(code)) Count(ut, code); }
                if (invalidTrade) continue;
                if (priorQuoteAmbiguous) { ambiguousPrior++; continue; }
                if (priorQuote is null) { missingPrior++; continue; }
                if (!ValidPrices(priorQuote) || priorQuote.BidSizeShares <= 0 || priorQuote.AskSizeShares <= 0) { invalidPrior++; continue; }
                var age = checked(trade.TimestampNanoseconds - priorQuote.TimestampNanoseconds);
                if (age <= 0) throw new InvalidDataException("Source quote comparison must be strictly prior.");
                Count(ages, age / 1_000_000L);
                compared++; if (age > staleAfterNanoseconds) stale++;
                if (trade.TradePrice >= priorQuote.Bid && trade.TradePrice <= priorQuote.Ask) inside++; else outside++;
            }
            if (batch.Quotes.Length > 0) {
                // Identical repeated rows are retained and counted. Distinct quote contents
                // at the same nanosecond do not acquire an invented latest ordering.
                priorQuoteAmbiguous = batch.Quotes.Select(quote => quote.ContentSha256).Distinct(StringComparer.Ordinal).Skip(1).Any();
                if (priorQuoteAmbiguous) { multiQuotes++; priorQuote = null; }
                else priorQuote = batch.Quotes[0];
            }
        }
        if (quoteRows != quotes.Identity.Rows || tradeRows != trades.Identity.Rows || tradeRows != invalidTrades + missingPrior + ambiguousPrior + invalidPrior + compared || compared != inside + outside)
            throw new InvalidDataException("Complete source measurement counts failed reconciliation.");
        return new(Version, MeasurementClass, quotes.Identity, trades.Identity, staleAfterNanoseconds,
            quoteRows, tradeRows, invalidQuotes, zeroSizeQuotes, invalidTrades, sharedBatches, sharedTrades, multiQuotes,
            missingPrior, ambiguousPrior, invalidPrior, compared, inside, outside, stale, unknownQuoteRows, unknownTradeRows,
            emptyQuoteConditions, emptyTradeConditions, oddLots, spread.ToImmutableSortedDictionary(), bids.ToImmutableSortedDictionary(),
            asks.ToImmutableSortedDictionary(), ages.ToImmutableSortedDictionary(), qc.ToImmutableSortedDictionary(StringComparer.Ordinal),
            tc.ToImmutableSortedDictionary(StringComparer.Ordinal), uq.ToImmutableSortedDictionary(StringComparer.Ordinal), ut.ToImmutableSortedDictionary(StringComparer.Ordinal),
            qt.ToImmutableSortedDictionary(StringComparer.Ordinal), tt.ToImmutableSortedDictionary(StringComparer.Ordinal),
            bx.ToImmutableSortedDictionary(StringComparer.Ordinal), ax.ToImmutableSortedDictionary(StringComparer.Ordinal), tx.ToImmutableSortedDictionary(StringComparer.Ordinal),
            vocabulary, true);
    }

    private static bool ValidPrices(SipArchiveEvent quote) => quote.Bid > 0 && quote.Ask > 0 && quote.Ask >= quote.Bid;
    private static SortedDictionary<string, long> Codes() => new(StringComparer.Ordinal);
    private static void Count<TKey>(IDictionary<TKey, long> counts, TKey key) where TKey : notnull =>
        counts[key] = checked(counts.TryGetValue(key, out var value) ? value + 1 : 1);
}
