using TradingFlow.Data.ScreenCaptures;
using TradingFlow.Domain.ScreenCaptures;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Services.ScreenCaptures;

public sealed record FinvizScreenCaptureStatus(
    bool Enabled, string? Message, DateTimeOffset? NextSlotAtUtc, IReadOnlyList<FinvizScreenCaptureRow> Screens)
{
    // Keep exchange, operator and persisted dates explicit through DST and midnight boundaries.
    public static string ExchangeStamp(DateTimeOffset instant) =>
        UiDisplayFormatter.FormatAuditInstant(instant);
}

public sealed record FinvizScreenCaptureRow(
    Guid PresetId, string Name, string State, string? Reason, DateTimeOffset? LatestSlotAtUtc,
    DateTimeOffset? LastCaptureAtUtc, int? LastCaptureRows, string? LastCaptureReason, int Attempts,
    int? EvidenceHttpStatus, string? EvidenceArchiveId, string? EvidenceColumnCoverage, bool? EvidenceMembershipVerified);

/// <summary>Reads retained capture status in the signed-in workspace without contacting Finviz or the exchange calendar.</summary>
public sealed class FinvizScreenCaptureStatusReader(
    FinvizScreenCaptureOptions options, ScreenerPresetService presets, SqliteFinvizCaptureCatalog catalog, TimeProvider clock)
{
    // Operations and the authenticated read-only endpoint share the same list of current saved screens.
    public async Task<FinvizScreenCaptureStatus> ReadAsync(CancellationToken token) =>
        await ReadAsync(await presets.ListAsync(null, token), token);

    // Report saved-screen history even while scheduling is disabled.
    public async Task<FinvizScreenCaptureStatus> ReadAsync(IReadOnlyList<ScreenerPreset> screens, CancellationToken token)
    {
        var schedule = await catalog.ReadScheduleAsync(token);
        var now = clock.GetUtcNow();
        var next = options.Enabled && schedule?.NextSlotAtUtc > now ? schedule.NextSlotAtUtc : null;
        var message = !options.Enabled ? options.DisabledReason
            : schedule?.CalendarError ?? (next is null ? "The next slot awaits a verified exchange calendar." : null);
        var rows = new List<FinvizScreenCaptureRow>(screens.Count);
        foreach (var preset in screens)
            rows.Add(ToRow(preset, await catalog.GetHistoryAsync(preset.Id, token), options.Enabled, now));
        return new FinvizScreenCaptureStatus(options.Enabled, message, next, rows);
    }

    // Explain both the latest slot outcome and the last separately published receipt.
    private static FinvizScreenCaptureRow ToRow(
        ScreenerPreset preset, FinvizPresetCaptureHistory history, bool enabled, DateTimeOffset now)
    {
        var latest = history.Latest;
        var state = latest?.State switch
        {
            null => enabled ? "Pending" : "Disabled",
            FinvizCaptureState.VerifiedEmpty => "Verified empty",
            var known => known.ToString()!
        };
        var reason = latest?.Reason ?? (enabled ? "No scheduled capture slot has been recorded for this screen yet." : null);
        if (latest?.State == FinvizCaptureState.Pending && latest.PlannedAtUtc + FinvizPlannedSlot.Window <= now)
            reason = "The slot window elapsed and the collector has yet to record a final outcome.";
        if (latest?.PlannedRevisionKnown == false)
            reason += " The screen changed after the planned time, so the planned revision is unknown.";
        var evidence = history.LatestEvidence;
        var lastCapture = history.LastPublishedEvidence;
        var lastCaptureReason = latest?.Id == history.LastPublished?.Id ? null : lastCapture?.Reason;
        return new FinvizScreenCaptureRow(preset.Id, preset.Name, state, reason, latest?.PlannedAtUtc,
            lastCapture?.ReceivedAtUtc, lastCapture?.RowCount, lastCaptureReason, latest?.AttemptCount ?? 0,
            evidence?.HttpStatus, evidence?.ArchiveId, evidence?.ColumnCoverage.ToString(), evidence?.MembershipVerified);
    }
}
