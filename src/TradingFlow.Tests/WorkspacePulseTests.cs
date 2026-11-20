using TradingFlow.Engine.Regime;
using TradingFlow.Web;

namespace TradingFlow.Tests;

/// <summary>Focused checks for the workspace regime strip: VIX sizing labels, sector tail read and shell wiring.</summary>
public sealed class WorkspacePulseTests : IDisposable
{
    private static readonly DateOnly Session = new(2026, 11, 19);
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), "tf-pulse-" + Guid.NewGuid().ToString("N"));

    // Builds a one-day VIX series for the previous session.
    private static VixCloseSeries Vix(decimal close) =>
        VixCloseSeries.From([new KeyValuePair<DateOnly, decimal>(Session, close)]);

    [Theory]
    [InlineData(15.0, 1.0, 0.0)]
    [InlineData(20.0, 0.5, 0.5)]
    [InlineData(25.0, 0.0, 1.0)]
    public void VixPulseScalesLongAndShortRisk(double close, double longScale, double shortScale)
    {
        var pulse = WorkspacePulseEndpoints.BuildVixPulse(Vix((decimal)close), Session, Now);
        Assert.True(pulse.Available);
        Assert.Equal((decimal)longScale, pulse.LongRiskScale);
        Assert.Equal((decimal)shortScale, pulse.ShortRiskScale);
    }

    [Fact]
    public void MissingVixSeriesFailsClosedWithZeroScales()
    {
        var pulse = WorkspacePulseEndpoints.BuildVixPulse(null, Session, Now);
        Assert.False(pulse.Available);
        Assert.Equal(0m, pulse.LongRiskScale);
        Assert.Equal(0m, pulse.ShortRiskScale);
    }

    [Fact]
    public void TopSectorsReadsOnlyTheFinalSessionRows()
    {
        var folder = Path.Combine(root, "screens", "sector-funds-top3-test");
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "eligible.csv"),
        [
            "session_date,ticker,rank",
            "2026-11-18,XLK,1", "2026-11-18,XLE,2", "2026-11-18,XLU,3",
            "2026-11-19,XLI,1", "2026-11-19,XLV,2", "2026-11-19,XLF,3"
        ]);
        var (sectors, asOf) = WorkspacePulseEndpoints.TryReadLatestTopSectors(Path.Combine(root, "screens"));
        Assert.Equal(["XLI", "XLV", "XLF"], sectors);
        Assert.Equal("2026-11-19", asOf);
    }

    [Fact]
    public void TopSectorsAreEmptyWhenNoScreenIsPublished()
    {
        var (sectors, asOf) = WorkspacePulseEndpoints.TryReadLatestTopSectors(Path.Combine(root, "screens"));
        Assert.Empty(sectors);
        Assert.Null(asOf);
    }

    [Fact]
    public void ShellExposesFourWorkspacesRegimeStripAndAskButton()
    {
        var layout = File.ReadAllText(Path.Combine(TestRepository.FindRoot(), "src", "TradingFlow.Web", "Pages", "Shared", "_Layout.cshtml"));
        foreach (var expected in new[] { "\"Desk\"", "\"Strategy Lab\"", "\"Catalysts & Options\"", "\"Portfolio & Audit\"",
                     "data-regime-pulse", "id=\"OpenCommandPalette\"", "ui2-foundation.css", "regime-pulse.js" })
        {
            Assert.Contains(expected, layout);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
