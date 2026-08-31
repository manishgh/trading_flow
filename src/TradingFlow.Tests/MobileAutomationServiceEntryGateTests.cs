using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class MobileAutomationServiceEntryGateTests
{
    [Fact]
    public void SessionCoordinator_RejectsConcurrentOwnershipOfTheSameTicker()
    {
        var coordinator = new MobileAutomationSessionCoordinator();
        var firstSession = Guid.NewGuid();
        var firstCancellation = coordinator.Reserve(firstSession, "rgti");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            coordinator.Reserve(Guid.NewGuid(), "RGTI"));

        Assert.Contains("already exists for RGTI", exception.Message, StringComparison.Ordinal);
        Assert.False(firstCancellation.IsCancellationRequested);
        Assert.True(coordinator.IsReserved(firstSession));
        coordinator.Release(firstSession);
    }

    [Fact]
    public void SessionCoordinator_CancelsImmediatelyAndAllowsOwnershipAfterRelease()
    {
        var coordinator = new MobileAutomationSessionCoordinator();
        var firstSession = Guid.NewGuid();
        var firstCancellation = coordinator.Reserve(firstSession, "POET");

        Assert.True(coordinator.TryCancel(firstSession));
        Assert.True(firstCancellation.IsCancellationRequested);

        coordinator.Release(firstSession);
        Assert.False(coordinator.IsReserved(firstSession));
        Assert.False(coordinator.TryCancel(firstSession));

        var replacementSession = Guid.NewGuid();
        coordinator.Reserve(replacementSession, "POET");
        Assert.True(coordinator.IsReserved(replacementSession));
        coordinator.Release(replacementSession);
    }

    [Fact]
    public async Task SessionCoordinator_AllowsOnlyOneParallelReservationPerTicker()
    {
        var coordinator = new MobileAutomationSessionCoordinator();
        var attempts = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() =>
            {
                var sessionId = Guid.NewGuid();
                try
                {
                    coordinator.Reserve(sessionId, "MXL");
                    return sessionId;
                }
                catch (InvalidOperationException)
                {
                    return Guid.Empty;
                }
            }))
            .ToArray();

        var winners = (await Task.WhenAll(attempts)).Where(x => x != Guid.Empty).ToArray();

        var winner = Assert.Single(winners);
        Assert.True(coordinator.IsReserved(winner));
        coordinator.Release(winner);
    }

    [Fact]
    public async Task SessionState_SupportsConcurrentMutationAndSnapshotsWithoutRegressingAudit()
    {
        var session = new MobileAutomationService.MutableAutomationSession(
            Guid.NewGuid(),
            "mobile-session",
            "paper.yaml",
            "strategy.yaml",
            "RGTI",
            "notification",
            "com.stockpulse",
            "RGTI alert",
            "RGTI is moving",
            DateTimeOffset.UtcNow);

        var writer = Task.Run(() =>
        {
            for (var index = 0; index < 1_000; index++)
            {
                session.Update(current =>
                {
                    current.Status = "running";
                    current.LastObservedPrice = index;
                    current.Report("monitoring", $"event-{index}");
                });
            }
        });
        var reader = Task.Run(() =>
        {
            for (var index = 0; index < 1_000; index++)
            {
                var snapshot = session.ToSnapshot();
                Assert.InRange(snapshot.Events.Count, 0, 80);
            }
        });

        await Task.WhenAll(writer, reader);

        var final = session.ToSnapshot();
        Assert.Equal("running", final.Status);
        Assert.Equal(999m, final.LastObservedPrice);
        Assert.Equal(80, final.Events.Count);
        Assert.EndsWith("event-999", final.Events[^1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "validate_strategy")]
    [InlineData("", "validate_strategy")]
    [InlineData("validate_strategy", "validate_strategy")]
    [InlineData("operator_direct", "operator_direct")]
    public void NormalizeEntryMode_AcceptsOnlyExplicitModes(string? value, string expected)
    {
        Assert.Equal(expected, MobileAutomationService.NormalizeEntryMode(value));
    }

    [Theory]
    [InlineData("immediate_paper")]
    [InlineData("bypass")]
    [InlineData("strategy")]
    public void NormalizeEntryMode_RejectsLegacyOrAmbiguousModes(string value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            MobileAutomationService.NormalizeEntryMode(value));
    }

    [Fact]
    public void MobileEntryPreparation_HasNoSyntheticStrategyOrSilentFallback()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "TradingFlow.Web",
            "Services",
            "MobileAutomationService.EntryPreparation.cs"));

        Assert.DoesNotContain("CanFallbackToStockPulseImmediateEntry", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PrepareImmediateEntryExecution", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new TradeSignal(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StrategyDecisionBrain", source, StringComparison.Ordinal);
        Assert.Contains("StrategyDecisionRequestAssembler.Create(", source, StringComparison.Ordinal);
        Assert.Contains("candidateDecisions.EvaluateAsync(", source, StringComparison.Ordinal);
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
