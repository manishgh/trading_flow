using TradingFlow.Domain.ScreenCaptures;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Finviz;

public sealed partial class FinvizClient
{
    /// <summary>Records one bounded export and releases its rows only after the whole received payload passes structural checks.</summary>
    public async Task<FinvizScreenerSnapshot> GetBoundedMembershipAsync(
        string query, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || String.IsNullOrWhiteSpace(query) || query != CanonicalizeScheduledSelection(query))
            throw new InvalidOperationException("Finviz membership requires an authenticated, canonical stock selection.");

        // Bound source I/O like the desk's screen read, within one refresh interval.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readBudget = TimeSpan.FromSeconds(15);
        timeout.CancelAfter(readBudget);
        var deadline = clock.GetUtcNow() + readBudget;
        var requestUri = ScheduledRequestUri(query);
        var url = $"{requestUri.PathAndQuery}&auth={Uri.EscapeDataString(_options.AuthToken)}";
        var (response, _, failure) = await TrySendCsvExportAsync(url, requestUri, deadline, clock, timeout.Token);
        if (response is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(failure ?? "Finviz did not provide an HTTP response for the membership export.");
        }

        using (response)
        {
            var body = await ReadScheduledBodyAsync(response.Content, timeout.Token);
            var receivedAt = clock.GetUtcNow().ToUniversalTime();
            var contentType = response.Content.Headers.ContentType?.ToString();
            var receipt = await _rawArchiveWriter.ArchiveAsync(new RawArchiveRequest(
                "finviz", "paper_membership_csv", receivedAt, "csv", contentType, PresetId: CreateQueryPresetId(query),
                Http: new RawArchiveHttpMetadata(requestUri, (int)response.StatusCode, CollectResponseHeaders(response)),
                Attributes: new Dictionary<string, string> { ["bodyDisposition"] = body.Disposition }),
                body.Bytes, CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            if (body.Disposition != "complete")
                throw new InvalidDataException($"Finviz membership export body was {body.Disposition} (receipt {receipt.Manifest.ArchiveId}).");
            if (response.Content.Headers.ContentRange is not null)
                throw new InvalidDataException($"Finviz membership export advertised a partial body (receipt {receipt.Manifest.ArchiveId}).");

            var rows = new List<FinvizScreenerRow>();
            var receiptEvidence = new FinvizExportObservation(FinvizCaptureState.Failed, "Unvalidated membership export.",
                ReceivedAtUtc: receivedAt, HttpStatus: (int)response.StatusCode);
            var observation = InspectScheduledCsv(receiptEvidence, body.Bytes, contentType, deadline, rows);
            if (observation.State != FinvizCaptureState.Captured)
                throw new InvalidDataException($"{observation.Reason} Receipt {receipt.Manifest.ArchiveId}.");
            if (rows.Count == 0)
                throw new InvalidDataException($"Finviz returned a header-only membership export without verified zero results (receipt {receipt.Manifest.ArchiveId}).");
            return new FinvizScreenerSnapshot(rows, receivedAt, query, $"raw-archive:{receipt.Manifest.ArchiveId}");
        }
    }
}
