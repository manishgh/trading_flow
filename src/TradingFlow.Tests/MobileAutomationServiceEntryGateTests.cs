using Microsoft.Extensions.Configuration;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class MobileAutomationServiceEntryGateTests
{
    [Fact]
    public void GetLongEntryGateRejection_WhenV8RelativeVolumeIsTooLow_RejectsNotificationStyleEntry()
    {
        var root = FindRepositoryRoot();
        var service = CreateService(root);
        var reader = new SimpleYamlReader();
        var strategy = reader.ReadStrategy(Path.Combine(root, "configs", "strategies", "intraday-ross-vwap-ema-cumulative-volume.v8-adaptive-guard.yaml"));

        var snapshot = new IndicatorSnapshot(
            "TDIC",
            new DateTimeOffset(2026, 6, 16, 12, 27, 0, TimeSpan.Zero),
            "1m",
            9.17m,
            75856m,
            10.87m,
            59.65m,
            0.35m,
            8.77m,
            8.43m,
            8.10m,
            8.90m,
            10.10m,
            7.70m,
            0.65m,
            0.12m,
            0.03m,
            0.08m,
            Sma10: 9.00m,
            Sma20: 8.95m,
            Sma50: 8.40m,
            SlotRelativeVolume: 1.52m,
            SessionRelativeVolume: 0.33m,
            Ema10: 8.95m,
            SlotAverageVolume: 49854.04m,
            CumulativeAverageVolume: 117000m,
            AverageSessionVolume: 350000m,
            RelativeVolumeSampleCount: 63,
            Sma150: 7.50m,
            Sma200: 7.10m,
            Ema5: 9.05m);

        var signal = CreatePassingSignal() with
        {
            Ticker = "TDIC",
            Timestamp = snapshot.Timestamp,
            CurrentPrice = snapshot.CurrentPrice,
            CurrentVolume = snapshot.CurrentVolume,
            CurrentRsi = snapshot.Rsi!.Value,
            CurrentAtr = snapshot.Atr!.Value,
            SessionRelativeVolume = snapshot.SessionRelativeVolume,
            SlotRelativeVolume = snapshot.SlotRelativeVolume,
            VolumeSma = 90216.8m,
            PreviousVolumeSma = 53543.4m,
            VolumeSmaRisePct = 68.49m
        };

        var rejection = service.GetLongEntryGateRejection(strategy, snapshot, signal);

        Assert.Equal("relative_volume_below_minimum (Actual: 0.65, Required: 2.00)", rejection);
    }

    private static TradeSignal CreatePassingSignal()
    {
        return new TradeSignal(
            "TEST",
            DateTimeOffset.UtcNow,
            "1m",
            10m,
            100000m,
            60m,
            0.5m,
            true,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            true,
            true,
            true,
            0.1m,
            true,
            true,
            true,
            IsAboveSessionOpen: true,
            CloseLocationValue: 0.80m,
            IsPriceAboveEma10: true,
            IsEma10AboveEma20: true,
            IsPriceAboveSma20: true,
            IsPriceAboveSma50: true,
            IsSma10AboveSma20: true,
            IsSma20AboveSma50: true,
            IsPriceAboveSma150: true,
            IsPriceAboveSma200: true);
    }

    private static MobileAutomationService CreateService(string root)
    {
        var configuration = new ConfigurationBuilder().Build();
        var credentialProvider = new AlpacaCredentialProvider(configuration);
        var paths = new ProjectPaths(root);
        return new MobileAutomationService(
            new SimpleYamlReader(),
            new PaperRuntimeFactory(credentialProvider, paths),
            paths,
            new MobileAutomationSessionStore(paths, AtomicFileArtifactWriter.Instance));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TradingFlow.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate TradingFlow repository root.");
    }
}
