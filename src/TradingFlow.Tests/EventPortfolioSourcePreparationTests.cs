using System.Net;
using System.Text;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class EventPortfolioSourcePreparationTests
{
    /// <summary>The ordinary provider collector supplies exact full-session and prefix archives, with effective shared controls frozen.</summary>
    [Fact]
    public async Task PreparationCapturesCalendarMetadataAndFullSessionSourcesWithoutChangingEarlierFiles()
    {
        using var fixture = new Fixture();
        var prepared = await fixture.Preparer.PrepareAsync(fixture.Run, DateTimeOffset.Parse("2026-05-22T00:00:00Z"),
            fixture.Attempt, 1750, 17m);
        var manifest = EventPortfolioSourceBinding.ReadManifest(prepared.ManifestPath, prepared.ManifestSha256);
        var session = Assert.Single(manifest.Sessions);
        Assert.Equal("2026-05-21T13:30:00.000000000Z", session.RegularQuotesRequest.StartInclusive);
        Assert.Equal("2026-05-21T20:00:00.000000000Z", session.RegularQuotesRequest.EndExclusive);
        Assert.Equal("2026-05-21T13:29:58.250000000Z", session.PrefixQuotesRequest.StartInclusive);
        Assert.Equal("2026-05-21", session.RegularQuotesRequest.MappingAsOf);
        Assert.Equal(1_750_000_000L, manifest.Assumptions.MaximumQuoteAgeNanoseconds);
        Assert.Equal(17m, manifest.Assumptions.MaxEntrySpreadBps);
        Assert.Equal(7, prepared.Metadata.Count);
        Assert.Equal(10, fixture.Handler.Requests.Count);
        Assert.All(fixture.Handler.Requests.Where(x => x.AbsolutePath is "/v2/stocks/quotes" or "/v2/stocks/trades"),
            uri => Assert.Contains("feed=sip", uri.Query));
        Assert.True(File.Exists(Path.Combine(fixture.Attempt, "preparation-completed.json")));
        var previousBytes = await File.ReadAllBytesAsync(prepared.ManifestPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Preparer.PrepareAsync(fixture.Run,
            DateTimeOffset.Parse("2026-05-22T00:00:00Z"), fixture.Attempt, 1750, 17m));
        Assert.Equal(previousBytes, await File.ReadAllBytesAsync(prepared.ManifestPath));
    }

    /// <summary>Reuse the retained reviewed production matrix, including tape B's literal SPACE base and ineligible quote roles.</summary>
    [Fact]
    public async Task ReviewedSemanticsRequireExactCapturedVocabulary()
    {
        using var fixture = new Fixture();
        var captures = await fixture.Metadata.CaptureConditionsAsync(Path.Combine(fixture.Root, "conditions"));
        var policy = SipReviewedConditionPolicy.Bind(captures);
        Assert.Equal(SipExecutionConditionDisposition.Eligible,
            policy.Conditions.Rules.Single(x => x.Kind == SipArchiveEventKind.Trade && x.Tape == "B" && x.Code == " ").Disposition);
        Assert.Equal(SipExecutionConditionDisposition.Eligible,
            policy.Conditions.Rules.Single(x => x.Kind == SipArchiveEventKind.Quote && x.Tape == "C" && x.Code == "O").Disposition);
        Assert.Equal(SipExecutionConditionDisposition.Ineligible,
            policy.Conditions.Rules.Single(x => x.Kind == SipArchiveEventKind.Quote && x.Tape == "A" && x.Code == "C").Disposition);
        fixture.Handler.ChangeMetadata = true;
        var changed = await fixture.Metadata.CaptureConditionsAsync(Path.Combine(fixture.Root, "changed"));
        var changedPolicy = SipReviewedConditionPolicy.Bind(changed);
        Assert.All(changedPolicy.Conditions.Rules, rule => Assert.Equal(SipExecutionConditionDisposition.Unknown, rule.Disposition));
    }

    /// <summary>An actual provider denial is retained as failure, and does not produce a completed source manifest.</summary>
    [Fact]
    public async Task ProviderFailureRetainsAttemptAndReportsStatus()
    {
        using var fixture = new Fixture();
        fixture.Handler.FailMetadata = true;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Preparer.PrepareAsync(fixture.Run,
            DateTimeOffset.Parse("2026-05-22T00:00:00Z"), fixture.Attempt, 2000, 20m));
        Assert.Contains("403", error.Message);
        Assert.True(File.Exists(Path.Combine(fixture.Attempt, "preparation-request.json")));
        Assert.True(File.Exists(Path.Combine(fixture.Attempt, "preparation-failed.json")));
        Assert.False(File.Exists(Path.Combine(fixture.Attempt, "event-source-manifest.json")));
    }

    /// <summary>Identical use-cache requests verify existing pages; refresh retains those bytes and captures a separate version.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedArchiveCacheReusesOrVersionsWithoutOverwriting(bool refresh)
    {
        using var fixture = new Fixture();
        var first = await fixture.Preparer.PrepareAsync(fixture.Run, DateTimeOffset.Parse("2026-05-22T00:00:00Z"),
            fixture.Attempt, 2000, 20m);
        var firstSession = Assert.Single(EventPortfolioSourceBinding.ReadManifest(first.ManifestPath, first.ManifestSha256).Sessions);
        var oldManifestBytes = await File.ReadAllBytesAsync(Path.Combine(firstSession.QuotesDirectory, "manifest.json"));
        var second = await fixture.Preparer.PrepareAsync(fixture.Run with { CachePolicy = refresh ? "refresh" : "use_cache" },
            DateTimeOffset.Parse("2026-05-22T00:00:00Z"), Path.Combine(fixture.Root, "second"), 2000, 20m);
        var secondSession = Assert.Single(EventPortfolioSourceBinding.ReadManifest(second.ManifestPath, second.ManifestSha256).Sessions);
        Assert.Equal(refresh ? 20 : 17, fixture.Handler.Requests.Count);
        Assert.Equal(!refresh, firstSession.QuotesDirectory == secondSession.QuotesDirectory);
        Assert.Equal(oldManifestBytes, await File.ReadAllBytesAsync(Path.Combine(firstSession.QuotesDirectory, "manifest.json")));
    }

    /// <summary>Binding preserves the saved strategy values and supports the repository's simple YAML path parser.</summary>
    [Fact]
    public void BoundConfigAddsOnlyTheImmutableSourceBinding()
    {
        const string source = "engine:\n  pipeline: ticker\n  worker_count: 4\nportfolio:\n  starting_capital: 10000\n";
        var result = BacktestEventSourcePreparation.BindConfig(source, @"C:\data with spaces\source.json", new string('a', 64));
        Assert.Contains("execution_source_manifest_path: \"C:/data with spaces/source.json\"", result);
        Assert.Contains("  worker_count: 4", result);
        Assert.Contains("  starting_capital: 10000", result);
        Assert.DoesNotContain("execution_source_manifest_path", source);
        Assert.Throws<InvalidDataException>(() => BacktestEventSourcePreparation.BindConfig(result, @"C:\other.json", new string('b', 64)));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sip-preparation-fixture-" + Guid.NewGuid().ToString("N"));
        public string Attempt => Path.Combine(Root, "attempt");
        public Handler Handler { get; } = new();
        private readonly HttpClient client;
        public SipExecutionMetadataCollector Metadata { get; }
        public EventPortfolioSourcePreparation Preparer { get; }
        public BacktestRunConfig Run { get; }
        public Fixture()
        {
            client = new HttpClient(Handler);
            Metadata = new(client, ProductionProfile.Paper);
            Preparer = new(new SipExecutionArchiveCollector(client), Metadata);
            var original = new SimpleYamlReader().ReadBacktestRun(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "swing-backtest-profile.yaml"));
            Run = original with { Provider = "alpaca", Tickers = ["FIXTURE"], RawRoot = Path.Combine(Root, "raw"), CachePolicy = "use_cache",
                Engine = original.Engine with { ExecutionReplayPolicyId = ExecutionReplayPolicy.IntradayRthFiveMinuteQuoteMarketV1 },
                Providers = original.Providers with { Alpaca = original.Providers.Alpaca with { DataFeed = "sip" } },
                TimeWindow = new("fixed", 0, DateTimeOffset.Parse("2026-05-21T00:00:00Z"), DateTimeOffset.Parse("2026-05-22T00:00:00Z")) };
        }
        public void Dispose()
        {
            client.Dispose();
            // Synthetic fixture-owned files only; never collected market data.
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public bool ChangeMetadata { get; set; }
        public bool FailMetadata { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!; Requests.Add(uri);
            string payload;
            if (uri.AbsolutePath == "/v2/calendar")
                payload = "[{\"date\":\"2026-05-21\",\"open\":\"09:30\",\"close\":\"16:00\"}]";
            else if (uri.AbsolutePath.Contains("/meta/conditions/", StringComparison.Ordinal))
            {
                if (FailMetadata) return new(HttpStatusCode.Forbidden) { Content = new StringContent("denied") };
                var kind = uri.Segments[^1]; var tape = uri.Query.Split('=')[1];
                payload = ChangeMetadata ? "{\"NEW\":\"Unreviewed new provider condition\"}"
                    : await File.ReadAllTextAsync(Path.Combine(TestRepository.FindRoot(), "src", "TradingFlow.Tests", "Fixtures", "SipConditionMetadata", kind + "-" + tape + ".json"), cancellationToken);
            }
            else
            {
                var kind = uri.Segments[^1];
                payload = JsonSerializer.Serialize(new Dictionary<string, object?> { [kind] = new Dictionary<string, object>(), ["next_page_token"] = null });
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        }
    }
}
