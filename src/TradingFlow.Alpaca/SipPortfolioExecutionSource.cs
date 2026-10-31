using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Alpaca;

public enum SipPortfolioKnownAtBasis { Unspecified, RetrospectiveSourceTimePlusDeclaredDelay }
public enum SipExecutionConditionDisposition { Unknown, Ineligible, Eligible }
public sealed record SipExecutionConditionMetadata(SipArchiveEventKind Kind, string Tape, string Sha256);
public sealed record SipExecutionConditionRule(SipArchiveEventKind Kind, string Tape, string Code,
    string Name, SipExecutionConditionDisposition Disposition, bool IsModifierOnly = false);
public sealed record SipExecutionConditionContract(string Version,
    ImmutableArray<SipExecutionConditionMetadata> MetadataSources, ImmutableArray<SipExecutionConditionRule> Rules);
public sealed record SipPortfolioSourcePolicy(string Version, SipPortfolioKnownAtBasis KnownAtBasis,
    long SourceAvailabilityDelayNanoseconds, SipExecutionConditionContract Conditions);
public sealed record SipPortfolioSessionArchive(string SessionKey, SipArchiveRequest RegularQuotesRequest,
    string QuotesDirectory, string QuotesManifestSha256, string TradesDirectory, string TradesManifestSha256,
    SipArchiveRequest PrefixQuotesRequest, string PrefixQuotesDirectory, string PrefixQuotesManifestSha256);

/// <summary>Prefix quotes seed source state only. They must never route regular-session orders.</summary>
public sealed record SipPortfolioExecutionBatch(ExecutionEventBatch Market, bool InRegularSession, string SessionKey,
    ImmutableArray<SipArchiveEvent> Observations, ImmutableArray<string> UnknownEvidenceReasons,
    ImmutableArray<string> IneligibleObservationReasons, long UnknownObservationRows = 0, long KnownIneligibleObservationRows = 0);
/// <summary>One global source nanosecond; the array order does not prove economic order across symbols.</summary>
public sealed record SipPortfolioExecutionFrame(long EventTimeNanoseconds, long KnownAtNanoseconds,
    ImmutableArray<SipPortfolioExecutionBatch> Batches, bool CrossSymbolOrderingProven = false);
public sealed record SipPortfolioArchiveBinding(string Directory, string ManifestSha256, string RequestSha256,
    long Rows, DateTimeOffset EarliestCapturedUtc, DateTimeOffset LatestCapturedUtc);
public sealed record SipPortfolioVerifiedSession(string SessionKey, SipArchiveRequest RegularQuotesRequest,
    SipArchiveRequest PrefixQuotesRequest, SipPortfolioArchiveBinding PrefixQuotes, SipPortfolioArchiveBinding Quotes,
    SipPortfolioArchiveBinding Trades);
public sealed record SipPortfolioSourceAudit(string Version, string PolicySha256, string ConditionContractSha256,
    long Frames, long SymbolBatches, long QuoteRows, long TradeRows, long UnknownConditionOrGeometryRows,
    long KnownIneligibleRows, ImmutableArray<SipPortfolioVerifiedSession> Sessions, bool FullyConsumedAndVerified,
    bool OriginalReceiptProven = false, bool HistoricalSecurityIdentityProven = false,
    bool OrderPriceQuantityConversionProven = false);

/// <summary>
/// Single-pass, bounded merge of frozen raw archives. One sequential stream per symbol is open;
/// a stream keeps at most its current prefix page or current quote/trade pages. No bar fabrication,
/// price adjustment, original-receipt assertion or OHLC-derived condition whitelist occurs here.
/// </summary>
public sealed class SipPortfolioExecutionSource
{
    public const string Version = "sip-portfolio-retrospective-source-v1";
    private readonly ImmutableArray<SipPortfolioSessionArchive> sessions;
    private readonly Dictionary<(SipArchiveEventKind Kind, string Tape, string Code), SipExecutionConditionRule> rules;
    private readonly SipPortfolioSourcePolicy policy;
    private int state;
    public string PolicySha256 { get; }
    public string ConditionContractSha256 { get; }
    public SipPortfolioSourceAudit? CompletedAudit { get; private set; }

    // Keep the frozen descriptors and an exact source-bound condition lookup for the single pass.
    private SipPortfolioExecutionSource(ImmutableArray<SipPortfolioSessionArchive> sessions, SipPortfolioSourcePolicy policy)
    {
        this.sessions = sessions; this.policy = policy;
        rules = policy.Conditions.Rules.ToDictionary(rule => (rule.Kind, rule.Tape, rule.Code));
        PolicySha256 = ComputePolicySha256(policy); ConditionContractSha256 = ComputeConditionContractSha256(policy.Conditions);
    }

    // Reject ambiguous source windows or undeclared availability/condition assumptions before opening archives.
    public static SipPortfolioExecutionSource Create(IEnumerable<SipPortfolioSessionArchive> sessions, SipPortfolioSourcePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(sessions); ArgumentNullException.ThrowIfNull(policy);
        if (String.IsNullOrWhiteSpace(policy.Version) || policy.KnownAtBasis != SipPortfolioKnownAtBasis.RetrospectiveSourceTimePlusDeclaredDelay ||
            policy.SourceAvailabilityDelayNanoseconds < 0 || policy.Conditions is null || String.IsNullOrWhiteSpace(policy.Conditions.Version) ||
            policy.Conditions.MetadataSources.IsDefaultOrEmpty || policy.Conditions.Rules.IsDefaultOrEmpty)
            throw new InvalidDataException("A frozen source-time diagnostic assumption and explicit condition contract are required.");
        var metadata = new HashSet<(SipArchiveEventKind, string)>();
        foreach (var source in policy.Conditions.MetadataSources) {
            if (!Enum.IsDefined(source.Kind) || String.IsNullOrWhiteSpace(source.Tape) || !IsHash(source.Sha256) || !metadata.Add((source.Kind, source.Tape)))
                throw new InvalidDataException("Condition metadata must have distinct kind/tape bindings and source hashes.");
        }
        var ruleKeys = new HashSet<(SipArchiveEventKind, string, string)>();
        foreach (var rule in policy.Conditions.Rules) {
            // A single SPACE is the real CTA regular-sale code. Never trim it away.
            if (!Enum.IsDefined(rule.Kind) || !Enum.IsDefined(rule.Disposition) || String.IsNullOrEmpty(rule.Code) ||
                String.IsNullOrWhiteSpace(rule.Name) || !metadata.Contains((rule.Kind, rule.Tape)) ||
                !ruleKeys.Add((rule.Kind, rule.Tape, rule.Code)) || rule.IsModifierOnly && rule.Kind != SipArchiveEventKind.Trade)
                throw new InvalidDataException("Condition rules need distinct source-bound codes, names and explicit dispositions.");
        }
        var frozen = sessions.ToImmutableArray();
        if (frozen.IsEmpty || frozen.Any(session => session is null || String.IsNullOrWhiteSpace(session.SessionKey) ||
                session.RegularQuotesRequest is null || session.PrefixQuotesRequest is null || String.IsNullOrWhiteSpace(session.RegularQuotesRequest.Symbol)) ||
            frozen.Select(session => session.SessionKey).Distinct(StringComparer.Ordinal).Count() != frozen.Length)
            throw new InvalidDataException("Explicit distinct frozen session sources are required.");
        foreach (var group in frozen.GroupBy(session => session.RegularQuotesRequest.Symbol, StringComparer.Ordinal)) {
            long? priorEnd = null;
            foreach (var session in group.OrderBy(session => SipNanosecondTime.Parse(session.PrefixQuotesRequest.StartInclusive))) {
                var regular = session.RegularQuotesRequest; var prefix = session.PrefixQuotesRequest;
                var start = SipNanosecondTime.Parse(regular.StartInclusive); var end = SipNanosecondTime.Parse(regular.EndExclusive);
                var prefixStart = SipNanosecondTime.Parse(prefix.StartInclusive); var prefixEnd = SipNanosecondTime.Parse(prefix.EndExclusive);
                if (regular.Kind != "quotes" || prefix.Kind != "quotes" || prefix.Symbol != regular.Symbol || prefix.MappingAsOf != regular.MappingAsOf ||
                    prefixEnd != start || prefixStart >= start || start >= end || prefixStart < priorEnd ||
                    new[] { session.QuotesDirectory, session.TradesDirectory, session.PrefixQuotesDirectory }.Any(path => !Path.IsPathFullyQualified(path)) ||
                    !IsHash(session.QuotesManifestSha256) || !IsHash(session.TradesManifestSha256) || !IsHash(session.PrefixQuotesManifestSha256))
                    throw new InvalidDataException("Session quote/trade/prefix identities, bounds or source hashes are invalid or overlap.");
                priorEnd = end;
            }
        }
        return new(frozen, policy);
    }

    // Merge all symbols by exact source nanosecond and publish completeness only after every physical source is exhausted.
    public async IAsyncEnumerable<SipPortfolioExecutionFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0) throw new InvalidOperationException("A source instance is single-use; do not reverify raw JSON in a hidden prepass.");
        var completedSessions = new List<SipPortfolioVerifiedSession>();
        var cursors = new List<Cursor>(); var queue = new PriorityQueue<Cursor, long>();
        long frames = 0, batches = 0, quotes = 0, trades = 0, unknown = 0, ineligible = 0;
        var complete = false;
        try {
            foreach (var group in sessions.GroupBy(session => session.RegularQuotesRequest.Symbol, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)) {
                var ordered = group.OrderBy(session => SipNanosecondTime.Parse(session.PrefixQuotesRequest.StartInclusive)).ToImmutableArray();
                var cursor = new Cursor(SymbolStream(ordered, completedSessions, ct).GetAsyncEnumerator(ct));
                cursors.Add(cursor);
                if (await cursor.Stream.MoveNextAsync()) queue.Enqueue(cursor, cursor.Stream.Current.Market.EventTimeNanoseconds);
            }
            while (queue.TryPeek(out _, out var at)) {
                var sameTime = ImmutableArray.CreateBuilder<SipPortfolioExecutionBatch>();
                while (queue.TryPeek(out _, out var next) && next == at) {
                    var cursor = queue.Dequeue(); var batch = cursor.Stream.Current;
                    sameTime.Add(batch); quotes += batch.Market.Quotes.Count; trades += batch.Market.Trades.Count;
                    unknown += cursor.UnknownRows; ineligible += cursor.IneligibleRows;
                    if (await cursor.Stream.MoveNextAsync()) queue.Enqueue(cursor, cursor.Stream.Current.Market.EventTimeNanoseconds);
                }
                // Stable transport order only. Every member still belongs to the SAME timestamp frame.
                var ordered = sameTime.OrderBy(batch => batch.Market.Symbol, StringComparer.Ordinal).ToImmutableArray();
                frames++; batches += ordered.Length;
                yield return new(at, checked(at + policy.SourceAvailabilityDelayNanoseconds), ordered);
            }
            complete = true;
        }
        finally {
            foreach (var cursor in cursors) await cursor.Stream.DisposeAsync();
            state = 2;
            if (complete) {
                if (completedSessions.Count != sessions.Length ||
                    quotes != completedSessions.Sum(session => checked(session.PrefixQuotes.Rows + session.Quotes.Rows)) ||
                    trades != completedSessions.Sum(session => session.Trades.Rows) ||
                    checked(unknown + ineligible) > checked(quotes + trades))
                    throw new InvalidDataException("Full source session and observation consumption did not reconcile.");
                CompletedAudit = new(Version, PolicySha256, ConditionContractSha256, frames, batches, quotes, trades, unknown, ineligible,
                    completedSessions.OrderBy(session => session.SessionKey, StringComparer.Ordinal).ToImmutableArray(), true);
            }
        }
    }

    // Read one symbol's consecutive prefix and regular archives without keeping later sessions open.
    private async IAsyncEnumerable<SipPortfolioExecutionBatch> SymbolStream(ImmutableArray<SipPortfolioSessionArchive> windows,
        List<SipPortfolioVerifiedSession> completed, [EnumeratorCancellation] CancellationToken ct)
    {
        long sequence = 0;
        foreach (var session in windows) {
            var prefix = await SipExecutionArchiveReader.OpenAsync(session.PrefixQuotesDirectory, session.PrefixQuotesRequest, session.PrefixQuotesManifestSha256, ct);
            // Prefix quote rows are grouped before regular-session observations, with no synthetic boundary event.
            var prefixRows = ImmutableArray.CreateBuilder<SipArchiveEvent>(); long? prefixAt = null;
            await foreach (var row in prefix.ReadEventsAsync(ct)) {
                if (prefixAt is { } at && at != row.TimestampNanoseconds) {
                    yield return Project(session.SessionKey, at, ++sequence, false, prefixRows.ToImmutable(), []);
                    prefixRows.Clear();
                }
                prefixAt = row.TimestampNanoseconds; prefixRows.Add(row);
            }
            if (prefixAt is { } last) yield return Project(session.SessionKey, last, ++sequence, false, prefixRows.ToImmutable(), []);
            var quotes = await SipExecutionArchiveReader.OpenAsync(session.QuotesDirectory, session.RegularQuotesRequest, session.QuotesManifestSha256, ct);
            var trades = await SipExecutionArchiveReader.OpenAsync(session.TradesDirectory, session.RegularQuotesRequest with { Kind = "trades" }, session.TradesManifestSha256, ct);
            await foreach (var batch in SipExecutionArchiveReader.MergeAsync(quotes, trades, ct))
                yield return Project(session.SessionKey, batch.TimestampNanoseconds, ++sequence, true, batch.Quotes, batch.Trades);
            completed.Add(new(session.SessionKey, session.RegularQuotesRequest, session.PrefixQuotesRequest, Binding(prefix.Identity), Binding(quotes.Identity), Binding(trades.Identity)));
        }
    }

    // Preserve every raw row while attaching only the eligibility supported by the injected diagnostic contract.
    private SipPortfolioExecutionBatch Project(string key, long at, long sequence, bool regular,
        ImmutableArray<SipArchiveEvent> quotes, ImmutableArray<SipArchiveEvent> trades)
    {
        var unknown = new HashSet<string>(StringComparer.Ordinal); var ineligible = new HashSet<string>(StringComparer.Ordinal);
        var quoteTicks = ImmutableArray.CreateBuilder<ExecutionQuoteTick>(); var tradeTicks = ImmutableArray.CreateBuilder<ExecutionTradeTick>();
        long unknownRows = 0, ineligibleRows = 0;
        foreach (var row in quotes) {
            var status = Classify(row, unknown, ineligible);
            if (row.Bid <= 0 || row.Ask <= 0 || row.Ask < row.Bid || row.BidSizeShares <= 0 || row.AskSizeShares <= 0) {
                unknown.Add("quote_geometry_or_displayed_size_unavailable"); status = (false, true, status.Ineligible);
            }
            if (status.Unknown) unknownRows++; else if (status.Ineligible) ineligibleRows++;
            quoteTicks.Add(new(EventId(row), at, row.Bid!.Value, row.Ask!.Value, row.BidSizeShares!.Value, row.AskSizeShares!.Value, status.Eligible));
        }
        var tradeIds = trades.Select(EventId).ToArray();
        var duplicateTrades = tradeIds.Length <= 1 ? null : tradeIds.GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < trades.Length; index++) {
            var row = trades[index]; var id = tradeIds[index];
            var status = Classify(row, unknown, ineligible);
            if (row.TradePrice <= 0 || row.TradeSizeShares <= 0) { unknown.Add("trade_price_or_size_unavailable"); status = (false, true, status.Ineligible); }
            if (duplicateTrades?.Contains(id) == true) { unknown.Add("duplicate_source_trade_observation"); status = (false, true, status.Ineligible); }
            if (status.Unknown) unknownRows++; else if (status.Ineligible) ineligibleRows++;
            tradeTicks.Add(new(id, at, row.TradePrice!.Value, row.TradeSizeShares!.Value, status.Eligible));
        }
        var observations = quotes.AddRange(trades);
        var market = new ExecutionEventBatch(observations[0].Symbol, at, checked(at + policy.SourceAvailabilityDelayNanoseconds), sequence,
            quoteTicks.ToImmutable(), tradeTicks.ToImmutable(), CoverageComplete: true);
        return new(market, regular, key, observations, unknown.Order(StringComparer.Ordinal).ToImmutableArray(), ineligible.Order(StringComparer.Ordinal).ToImmutableArray(), unknownRows, ineligibleRows);
    }

    // Fail unknown modifiers closed and require an explicit ordinary base instead of treating informational codes as a sale type.
    private (bool Eligible, bool Unknown, bool Ineligible) Classify(SipArchiveEvent row, HashSet<string> unknown, HashSet<string> ineligible)
    {
        if (row.Tape is null || row.Conditions.IsDefaultOrEmpty) { unknown.Add("condition_tape_or_codes_unavailable:" + row.Kind); return (false, true, false); }
        var eligible = true; var ordinary = row.Kind == SipArchiveEventKind.Quote; var unknownRow = false; var ineligibleRow = false;
        foreach (var code in row.Conditions) {
            if (!rules.TryGetValue((row.Kind, row.Tape, code), out var rule) || rule.Disposition == SipExecutionConditionDisposition.Unknown) {
                unknown.Add($"condition_unknown:{row.Kind}:{row.Tape}:{code}"); eligible = false; unknownRow = true;
            } else if (rule.Disposition == SipExecutionConditionDisposition.Ineligible) {
                ineligible.Add($"condition_ineligible:{row.Kind}:{row.Tape}:{code}"); eligible = false; ineligibleRow = true;
            } else if (!rule.IsModifierOnly) ordinary = true;
        }
        if (eligible && !ordinary) { unknown.Add("ordinary_trade_base_condition_unavailable:" + row.Tape); return (false, true, false); }
        return (eligible, unknownRow, ineligibleRow);
    }

    // Share the actual C# serialization hash with external freezes, independent of JSON pretty-print bytes.
    public static string ComputePolicySha256(SipPortfolioSourcePolicy policy) => Hash(JsonSerializer.SerializeToUtf8Bytes(policy));
    // Bind the actual condition semantics independently of the broader source availability scenario.
    public static string ComputeConditionContractSha256(SipExecutionConditionContract contract) => Hash(JsonSerializer.SerializeToUtf8Bytes(contract));
    // Retain small manifest/request/capture bindings after releasing a completed reader's page inventory.
    private static SipPortfolioArchiveBinding Binding(SipArchiveReaderIdentity identity) => new(identity.Directory, identity.ManifestSha256, identity.RequestSha256,
        identity.Rows, identity.EarliestCapturedUtc, identity.LatestCapturedUtc);
    // Preserve duplicate observation identity across pagination rather than inventing extra executable capacity.
    private static string EventId(SipArchiveEvent row) => $"sip:{row.Symbol}:{row.Kind}:{row.ContentSha256}";
    // Reject malformed frozen source digests before physical I/O.
    private static bool IsHash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    // Hash exact deterministic record bytes for source and condition policy identity.
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    // Hold just one current same-symbol batch in the global merge queue.
    private sealed class Cursor(IAsyncEnumerator<SipPortfolioExecutionBatch> stream)
    {
        public IAsyncEnumerator<SipPortfolioExecutionBatch> Stream { get; } = stream;
        public long UnknownRows => Stream.Current.UnknownObservationRows;
        public long IneligibleRows => Stream.Current.KnownIneligibleObservationRows;
    }
}
