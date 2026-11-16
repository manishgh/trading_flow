using System.Text.Json;

namespace TradingFlow.Alpaca;

/// <summary>The original UTF-8 event span, valid only during the synchronous observer call.</summary>
public interface IAlpacaMarketDataReceiptObserver
{
    bool IsCapturing { get; }
    void Observe(JsonElement element, ReadOnlySpan<byte> originalUtf8, DateTimeOffset receivedAtUtc, long arrivalOrdinal);
}

/// <summary>The chosen post-command reply and whether that same observation proves the mutation channels.</summary>
public readonly record struct AlpacaDiagnosticSubscriptionAcknowledgement(
    long ArrivalOrdinal, DateTimeOffset ReceivedAtUtc, bool MutationChannelsConfirmed);
