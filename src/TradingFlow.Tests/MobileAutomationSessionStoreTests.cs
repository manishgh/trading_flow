using Microsoft.Extensions.Configuration;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class MobileAutomationSessionStoreTests
{
    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsSessions()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new MobileAutomationSessionStore(
                new ProjectPaths(root),
                AtomicFileArtifactWriter.Instance);

            var expected = new[]
            {
                new MobileAutomationSessionSnapshot(
                    Guid.NewGuid(),
                    "mobile_auto_1",
                    "config.yaml",
                    "strategy.yaml",
                    "POET",
                    "manual_run",
                    null,
                    "completed",
                    DateTimeOffset.UtcNow.AddMinutes(-10),
                    DateTimeOffset.UtcNow.AddMinutes(-9),
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    DateTimeOffset.UtcNow.AddMinutes(-9),
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    null,
                    "completed",
                    "order-1",
                    10.25m,
                    9.80m,
                    11.10m,
                    100,
                    10.95m,
                    70m,
                    "confirmed_vwap_failure",
                    new[] { "one", "two" },
                    "Alert title",
                    "Alert body")
            };

            await store.SaveAsync(expected);
            var actual = await store.LoadAsync();

            Assert.Single(actual);
            Assert.Equal(expected[0].SessionId, actual[0].SessionId);
            Assert.Equal("POET", actual[0].Ticker);
            Assert.Equal("completed", actual[0].Status);
            Assert.Equal("confirmed_vwap_failure", actual[0].ExitReason);
            Assert.Equal(2, actual[0].Events.Count);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task InitializeAsync_MarksRunningSessionsInterrupted()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new MobileAutomationSessionStore(
                new ProjectPaths(root),
                AtomicFileArtifactWriter.Instance);
            var sessionId = Guid.NewGuid();
            await store.SaveAsync(new[]
            {
                new MobileAutomationSessionSnapshot(
                    sessionId,
                    "mobile_auto_running",
                    "config.yaml",
                    "strategy.yaml",
                    "RGTI",
                    "notification",
                    "com.sample.alerts",
                    "running",
                    DateTimeOffset.UtcNow.AddMinutes(-30),
                    DateTimeOffset.UtcNow.AddMinutes(-29),
                    null,
                    DateTimeOffset.UtcNow.AddMinutes(-28),
                    null,
                    null,
                    "monitoring",
                    "order-2",
                    3.10m,
                    2.85m,
                    3.80m,
                    1000,
                    3.25m,
                    150m,
                    null,
                    new[] { "started", "running" },
                    "Title",
                    "Message")
            });

            var configuration = new ConfigurationBuilder().Build();
            var credentialProvider = new AlpacaCredentialProvider(configuration);
            var runtimeFactory = new PaperRuntimeFactory(credentialProvider, new ProjectPaths(root));
            var service = new MobileAutomationService(
                new SimpleYamlReader(),
                runtimeFactory,
                new ProjectPaths(root),
                store);

            await service.InitializeAsync();
            var snapshot = service.Get(sessionId);

            Assert.NotNull(snapshot);
            Assert.Equal("interrupted", snapshot!.Status);
            Assert.NotNull(snapshot.FinishedAt);
            Assert.Contains(snapshot.Events, item => item.Contains("Backend restarted", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "trading-flow-mobile-automation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
