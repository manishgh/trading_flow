using System;
using TradingFlow.Web.Services;
using Xunit;

namespace TradingFlow.Tests;

public sealed class UiDisplayFormatterTests
{
    /// <summary>Audit clocks retain both dates when New York is on the previous day, with real DST offsets.</summary>
    [Fact]
    public void FormatAuditInstant_ShowsNewYorkBerlinAndUtcAcrossMidnight()
    {
        var result = UiDisplayFormatter.FormatAuditInstant(new DateTimeOffset(2026, 6, 8, 1, 2, 3, TimeSpan.Zero));
        Assert.Equal("2026-06-07 21:02:03 NY · 2026-06-08 03:02:03 Berlin · 2026-06-08 01:02:03 UTC", result);
    }

    [Fact]
    public void NormalizeRejectionReasonKey_RemovesMeasuredDetails()
    {
        var key = UiDisplayFormatter.NormalizeRejectionReasonKey(
            "relative_volume_below_minimum (Actual: 0.46, Required: 1.00)");

        Assert.Equal("relative_volume_below_minimum", key);
    }

    [Fact]
    public void FormatRejectionReason_KeepsMeasuredDetailsReadable()
    {
        var formatted = UiDisplayFormatter.FormatRejectionReason(
            "relative_volume_below_minimum (Actual: 0.46, Required: 1.00)");

        Assert.Equal("Relative Volume Below Minimum (Actual: 0.46, Required: 1.00)", formatted);
    }

    [Fact]
    public void FormatLocal_UsesCentralEuropeanTradingTime()
    {
        var formatted = UiDisplayFormatter.FormatLocal(
            new DateTimeOffset(2026, 6, 8, 12, 0, 0, TimeSpan.Zero));

        Assert.Contains("14:00:00", formatted);
    }

    [Fact]
    public void FormatLocalDate_UsesDateOnlyForDailyBars()
    {
        var formatted = UiDisplayFormatter.FormatLocalDate(
            new DateTimeOffset(2026, 6, 11, 4, 0, 0, TimeSpan.Zero));

        Assert.Equal("2026-06-11", formatted);
    }
}
