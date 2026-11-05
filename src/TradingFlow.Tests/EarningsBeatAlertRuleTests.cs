using TradingFlow.Mobile.Services;

namespace TradingFlow.Tests;

/// <summary>Which EPS beats raise an Android notification.</summary>
public sealed class EarningsBeatAlertRuleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T15:00:00Z");

    /// <summary>A new install records the beats already listed and does not alert for any of them.</summary>
    [Fact]
    public void The_first_read_without_history_only_records_beats()
    {
        var history = new List<string>();

        var alerts = EarningsBeatAlertRule.SelectAlerts(
            [new("sony", true, Now.AddMinutes(-5)), new("miss", false, Now.AddMinutes(-5))],
            history, historyExisted: false, Now);

        Assert.Empty(alerts);
        Assert.Equal(["sony"], history);
    }

    /// <summary>
    /// With history, a beat first seen within two hours alerts once; an older beat, a miss, and a beat
    /// already in the history do not.
    /// </summary>
    [Fact]
    public void A_recent_new_beat_alerts_once()
    {
        var history = new List<string> { "known" };
        EarningsBeatCandidate[] rows =
        [
            new("known", true, Now.AddMinutes(-1)),
            new("recent", true, Now.AddMinutes(-119)),
            new("old", true, Now.AddHours(-3)),
            new("unseen", true, null),
            new("miss", false, Now.AddMinutes(-1))
        ];

        var first = EarningsBeatAlertRule.SelectAlerts(rows, history, historyExisted: true, Now);
        var second = EarningsBeatAlertRule.SelectAlerts(rows, history, historyExisted: true, Now);

        Assert.Equal(["recent"], first);
        Assert.Empty(second);
        Assert.Equal(["known", "recent", "old", "unseen"], history);
    }
}
