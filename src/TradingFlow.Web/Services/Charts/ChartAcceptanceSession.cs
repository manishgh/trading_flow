using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Web.Services.Charts;

public sealed record ChartAcceptanceOptions(bool Enabled = false, bool RuntimeEnabled = false);
public sealed record ChartAcceptanceStart(Guid SessionId, string Correlation, DateTimeOffset DeadlineUtc, string State);
public sealed record ChartAcceptanceStatus(Guid? SessionId, DateTimeOffset? DeadlineUtc, string State, string? Reason,
    string? ResultPath, bool WatchAttached, bool ScopeRemovalRequested, bool? RemovalAcknowledged);

internal sealed record ChartAcceptanceResult(Guid SessionId, DateTimeOffset StartedAtUtc, DateTimeOffset DeadlineUtc,
    string State, string Reason, long Fence, long Generation, string? Epoch, DateTimeOffset? WatchAttachedAtUtc,
    DateTimeOffset? SubscriptionAcknowledgedAtUtc, DateTimeOffset? WarmedAtUtc, long LastIngressOrdinal,
    long IgnoredIngressEvents, int UnresolvedCanonicalReceipts, DateTimeOffset? ScopeRemovedAtUtc,
    bool? RemovalAcknowledged, ChartAcceptanceRemoval Removal,
    IReadOnlyList<ChartAcceptanceMinute> MatchedMinutes, IReadOnlyList<ChartAcceptanceChunk> Chunks);

internal sealed record ChartAcceptanceRemoval(string Outcome, DateTimeOffset? ProviderAcknowledgedAtUtc = null,
    long? ProviderAcknowledgedOrdinal = null);
internal sealed record ChartAcceptanceMinute(DateTimeOffset StartUtc, long PreviewSequence, long CompletedSequence,
    long CanonicalReceiptOrdinal, DateTimeOffset CanonicalObservedAtUtc);
internal readonly record struct ChartAcceptanceRawBarReceipt(long Fence, long Generation, long Ordinal);

/// <summary>Holds evidence for one deadline, stream fence and initiating administrator's chart epoch.</summary>
internal sealed class ChartAcceptanceSession
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public const string Symbol = "AAPL";
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly TaskCompletionSource<(string State, string Reason)> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource capture = new();
    private readonly Dictionary<DateTimeOffset, long> previews = new();
    private readonly Dictionary<DateTimeOffset, CanonicalReceipt> canonical = new();
    private readonly Dictionary<DateTimeOffset, (ChartCandle Candle, long Sequence)> completed = new();
    private readonly Dictionary<DateTimeOffset, ChartAcceptanceMinute> matched = new();
    private readonly byte[] correlationBytes = RandomNumberGenerator.GetBytes(32);
    private string? connectionId;
    private string? epoch;
    private DateTimeOffset? watchAt;
    private long fence;
    private long generation;
    private long lastIngressOrdinal;
    private long unclaimedRawBarOrdinal;
    private long ignored;
    private long ignoredSinceReceipt;
    private int pendingCanonical;
    private DateTimeOffset? subscribedAt;
    private DateTimeOffset? warmAt;
    private DateTimeOffset? scopeRemovedAt;
    private ChartAcceptanceStatus status;

    // A session has a fixed budget beginning at the server's acceptance clock.
    public ChartAcceptanceSession(string owner, DateTimeOffset started, TimeProvider clock, IRawArchiveWriter rawArchive)
    {
        Owner = owner;
        StartedAtUtc = started;
        DeadlineUtc = started + Lifetime;
        this.clock = clock;
        status = new(Id, DeadlineUtc, "observing", null, null, false, false, null);
        Archive = new(rawArchive, Id, Fail);
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Owner { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset DeadlineUtc { get; }
    public ChartAcceptanceArchive Archive { get; }
    public CancellationToken CaptureToken => capture.Token;
    public Task<(string State, string Reason)> Ended => ended.Task;
    public bool IsCapturing => !ended.Task.IsCompleted && clock.GetUtcNow() < DeadlineUtc;
    public ChartAcceptanceStart StartResponse => new(Id, Convert.ToHexString(correlationBytes), DeadlineUtc, "observing");
    public ChartAcceptanceStatus Status => Volatile.Read(ref status);

    // A one-use secret stays outside the chart key and its history/series cache.
    public bool TryAttachWatch(string user, ChartSeriesKey key, string correlation, string connection, string watchEpoch)
    {
        if (user != Owner || key != new ChartSeriesKey(Symbol, "1m", "regular", "intraday") || correlation.Length != 64)
            return false;
        Span<byte> supplied = stackalloc byte[32];
        if (Convert.FromHexString(correlation, supplied, out _, out var written) != OperationStatus.Done ||
            written != supplied.Length ||
            !CryptographicOperations.FixedTimeEquals(correlationBytes, supplied)) return false;

        lock (gate)
        {
            if (!IsCapturing || epoch is not null) return false;
            connectionId = connection;
            epoch = watchEpoch;
            watchAt = clock.GetUtcNow();
            status = status with { WatchAttached = true };
            return true;
        }
    }

    // Claim the existing lease fence before putting the dedicated scope into the discovery sink.
    public void BindFence(long streamFence)
    {
        lock (gate)
            fence = streamFence;
    }

    // The first subscription acknowledgement belongs to this generation, never an earlier AAPL subscription.
    public void BindGeneration(long streamFence, long subscriptionGeneration)
    {
        lock (gate)
        {
            if (!IsCapturing || fence != streamFence || generation != 0) return;
            generation = subscriptionGeneration;
        }
    }

    // Use the same readiness clock that the production preview will apply to its first eligible minute.
    public void MarkWarm(long subscriptionGeneration, DateTimeOffset warmedAt)
    {
        lock (gate)
        {
            if (IsCapturing && generation == subscriptionGeneration)
                warmAt = warmedAt;
        }
    }

    // Count every ingress event, archiving only exact targeted spans and relevant subscription acknowledgements.
    public void ObserveProvider(long streamFence, JsonElement element, ReadOnlySpan<byte> original,
        DateTimeOffset receivedAtUtc, long ordinal)
    {
        lock (gate)
        {
            if (!IsCapturing || fence != streamFence || generation == 0 || receivedAtUtc < StartedAtUtc) return;
            if (lastIngressOrdinal != 0 && ordinal != lastIngressOrdinal + 1)
            {
                Fail("The market-data receipt sequence contains an unexplained gap.");
                return;
            }
            lastIngressOrdinal = ordinal;
            var type = ReadType(element);
            if (type == "subscription")
            {
                ObserveAcknowledgement(element, original, receivedAtUtc, ordinal);
                return;
            }

            JsonElement symbol = default;
            var hasSymbol = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("S", out symbol) &&
                symbol.ValueKind == JsonValueKind.String;
            if (type == "error" || type is null || (IsTargetType(type) && !hasSymbol))
            {
                Fail("The provider sent an error or market-data event whose coverage could not be accounted for.");
                return;
            }

            if (hasSymbol && symbol.ValueEquals(Symbol) && IsTargetType(type))
            {
                if (RecordProvider(type, original, receivedAtUtc, ordinal) && (type is "b" or "u"))
                {
                    unclaimedRawBarOrdinal = ordinal;
                    pendingCanonical++;
                }
                return;
            }

            ignored++;
            ignoredSinceReceipt++;
        }
    }

    // Trade mutations are automatically subscribed by Alpaca, but each must be named in the receipt.
    private void ObserveAcknowledgement(JsonElement element, ReadOnlySpan<byte> original, DateTimeOffset received, long ordinal)
    {
        var trades = HasSymbol(element, "trades");
        var corrections = HasSymbol(element, "corrections");
        var cancels = HasSymbol(element, "cancelErrors");
        if (RecordProvider("subscription", original, received, ordinal))
        {
            if (trades && corrections && cancels && HasSymbol(element, "bars") && HasSymbol(element, "updatedBars"))
                subscribedAt ??= received;
            else if (trades || subscribedAt is not null)
                Fail("The Alpaca acknowledgement did not cover AAPL trades, corrections, cancels and completed candles.");
        }
    }

    // A successful normal command acknowledgement must also have its additional exact coverage evidence.
    public bool HasSubscriptionCoverage()
    {
        lock (gate)
            return IsCapturing && subscribedAt is not null;
    }

    // Preserve the original provider event as its own byte span, with connection and ignored-event accounting.
    private bool RecordProvider(string kind, ReadOnlySpan<byte> original, DateTimeOffset received, long ordinal)
    {
        var header = FormattableString.Invariant(
            $"{{\"kind\":\"provider\",\"type\":\"{kind}\",\"receivedAtUtc\":\"{received:O}\",\"ordinal\":{ordinal},\"fence\":{fence},\"generation\":{generation},\"ignoredBefore\":{ignoredSinceReceipt}}}");
        var written = Archive.Offer(header, original, null, received);
        if (written) ignoredSinceReceipt = 0;
        return written;
    }

    // Issue a one-use dispatch receipt only when the current raw b/u span was actually retained.
    public ChartAcceptanceRawBarReceipt? ClaimRawBar(long streamFence, long subscriptionGeneration, long ordinal)
    {
        lock (gate)
        {
            if (!IsCapturing || streamFence != fence || subscriptionGeneration != generation) return null;
            if (ordinal <= 0 || ordinal != lastIngressOrdinal || ordinal != unclaimedRawBarOrdinal)
            {
                Fail("A canonical AAPL bar had no matching retained original receipt.");
                return null;
            }
            unclaimedRawBarOrdinal = 0;
            return new(streamFence, subscriptionGeneration, ordinal);
        }
    }

    // Associate the processor's actual disposition with the original span claimed at dispatch.
    public void ObserveCanonical(ChartAcceptanceRawBarReceipt receipt, MarketBarEvent? marketEvent,
        string disposition, DateTimeOffset dispositionAt)
    {
        lock (gate)
        {
            if (!IsCapturing || receipt.Fence != fence || receipt.Generation != generation) return;
            var ordinal = receipt.Ordinal;
            var header = FormattableString.Invariant(
                $"{{\"kind\":\"canonical-disposition\",\"ordinal\":{ordinal},\"fence\":{fence},\"generation\":{generation},\"observedAtUtc\":\"{marketEvent?.ObservedAtUtc:O}\",\"dispositionAtUtc\":\"{dispositionAt:O}\",\"disposition\":\"{disposition}\"}}");
            if (!Archive.Offer(header, [], null, dispositionAt)) return;
            pendingCanonical--;
            if (marketEvent is null) return;
            var bar = marketEvent.Bar;
            if ((disposition is nameof(MarketBarDisposition.Accepted) or nameof(MarketBarDisposition.RevisionAccepted)) &&
                bar.Timestamp >= StartedAtUtc.AddMinutes(-1) && bar.Timestamp < DeadlineUtc)
            {
                if (!canonical.TryGetValue(bar.Timestamp, out var prior) || prior.Ordinal < ordinal)
                    canonical[bar.Timestamp] = new(ordinal, marketEvent.ObservedAtUtc, bar);
                TryMatchMinute(bar.Timestamp);
            }
        }
    }

    // Capture only frames the hub emits from the claimed connection and epoch.
    public void ObserveChart(string connection, ChartFrame frame, ChartSeriesSnapshot? snapshot = null, ChartFormingCandle? forming = null)
    {
        lock (gate)
        {
            if (!IsCapturing || epoch != frame.Epoch || connectionId != connection) return;
            var now = clock.GetUtcNow();
            var header = FormattableString.Invariant(
                $"{{\"kind\":\"chart\",\"type\":\"{frame.Kind}\",\"receivedAtUtc\":\"{now:O}\",\"symbol\":\"AAPL\",\"interval\":\"1m\",\"sessionView\":\"regular\",\"context\":\"intraday\",\"indicators\":\"\",\"epoch\":\"{frame.Epoch}\",\"sequence\":{frame.Sequence},\"previewSequence\":{frame.PreviewSequence?.ToString(CultureInfo.InvariantCulture) ?? "null"},\"part\":{frame.Part},\"parts\":{frame.Parts}}}");
            if (!Archive.Offer(header, [], frame.Payload, now)) return;
            if (forming is { Status: "forming", StartUtc: { } start } && frame.PreviewSequence is { } sequence &&
                start >= StartedAtUtc && start < DeadlineUtc && subscribedAt < start && warmAt < start)
                previews[start] = sequence;
            if (snapshot is not null && frame.Part + 1 == frame.Parts)
                MatchCompletedBoundary(snapshot);
        }
    }

    // Distinguish the visible completed candle from another raw bar with the same minute label.
    private void MatchCompletedBoundary(ChartSeriesSnapshot snapshot)
    {
        foreach (var candle in snapshot.Bars)
        {
            if (!previews.ContainsKey(candle.StartUtc) || candle.SourceKind != "canonical_stream") continue;
            completed[candle.StartUtc] = (candle, snapshot.Sequence);
            TryMatchMinute(candle.StartUtc);
        }
    }

    // The processor continuation and hub emission may arrive in either order.
    private void TryMatchMinute(DateTimeOffset start)
    {
        if (!completed.TryGetValue(start, out var published) ||
            !canonical.TryGetValue(start, out var accepted) ||
            !previews.TryGetValue(start, out var previewSequence)) return;
        var candle = published.Candle;
        if (candle.KnownAtUtc != accepted.ObservedAtUtc || candle.Open != accepted.Bar.Open ||
            candle.High != accepted.Bar.High || candle.Low != accepted.Bar.Low ||
            candle.Close != accepted.Bar.Close || candle.Volume != accepted.Bar.Volume) return;
        matched[start] = new(start, previewSequence, published.Sequence, accepted.Ordinal, accepted.ObservedAtUtc);
    }

    // An interrupted browser watch never transfers acceptance collection to another epoch.
    public void DetachWatch(string connection, string watchEpoch)
    {
        lock (gate)
        {
            if (IsCapturing && connectionId == connection && epoch == watchEpoch)
                Fail("The initiating chart watch ended before the observation finished.");
        }
    }

    // An error triggers shutdown without doing I/O or running cancellation callbacks on the receive loop.
    public void Fail(string reason) => End("incomplete", reason);

    // Seal acceptance at the first stop/expiry/failure and release exclusive warming.
    public void End(string state, string reason)
    {
        lock (gate)
        {
            if (!ended.TrySetResult((state, reason))) return;
            status = status with { State = "finalizing", Reason = reason };
            Archive.Complete();
            _ = capture.CancelAsync();
        }
    }

    // Expose scope removal and provider acknowledgement as separate facts.
    public void MarkRemoval(bool requested, bool? acknowledged)
    {
        lock (gate)
        {
            if (requested) scopeRemovedAt ??= clock.GetUtcNow();
            status = status with { ScopeRemovalRequested = requested, RemovalAcknowledged = acknowledged };
        }
    }

    // Preserve collection facts and leave browser display/independent replay to the acceptance review.
    public ChartAcceptanceResult BuildResult(string state, string reason, ChartAcceptanceRemoval removal,
        IReadOnlyList<ChartAcceptanceChunk> chunks)
    {
        lock (gate)
        {
            if (state != "incomplete" && pendingCanonical != 0)
            {
                state = "incomplete";
                reason = "At least one retained AAPL bar had no finalized canonical disposition.";
            }
            else if (state != "incomplete" && matched.Count == 0)
            {
                state = "incomplete";
                reason = "No full covered minute had an emitted preview and a matching accepted canonical boundary.";
            }
            return new(Id, StartedAtUtc, DeadlineUtc, state, reason, fence, generation, epoch, watchAt, subscribedAt, warmAt,
                lastIngressOrdinal, ignored, pendingCanonical, scopeRemovedAt, status.RemovalAcknowledged, removal,
                matched.Values.ToArray(), chunks);
        }
    }

    // The endpoint only returns the opaque archive identity.
    public void PublishStatus(string state, string reason, string? resultId)
    {
        lock (gate)
            status = status with { State = state, Reason = reason, ResultPath = resultId };
    }

    // Only these price events are relevant to the AAPL minute and its canonical revisions.
    private static bool IsTargetType(string type) => type is "t" or "c" or "x" or "b" or "u";

    // Compare known type names in the document without copying any part of a refused payload.
    private static string? ReadType(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("T", out var type) ||
            type.ValueKind != JsonValueKind.String) return null;
        if (type.ValueEquals("t")) return "t";
        if (type.ValueEquals("c")) return "c";
        if (type.ValueEquals("x")) return "x";
        if (type.ValueEquals("b")) return "b";
        if (type.ValueEquals("u")) return "u";
        if (type.ValueEquals("subscription")) return "subscription";
        return type.ValueEquals("error") ? "error" : "ignored";
    }

    // A wildcard is not evidence of an acknowledgement of this exact symbol's three trade channels.
    internal static bool HasSymbol(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var property) &&
        property.ValueKind == JsonValueKind.Array && property.EnumerateArray().Any(item =>
            item.ValueKind == JsonValueKind.String && item.ValueEquals(Symbol));

    private sealed record CanonicalReceipt(long Ordinal, DateTimeOffset ObservedAtUtc, OhlcvBar Bar);
}
