using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Strategies;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed partial class DecisionContextProviderTests
{
    [Theory]
    [InlineData("backtest")]
    [InlineData("paper")]
    public async Task ExplicitFrozenEvidenceQualifiesThroughSharedAdapter(string mode)
    {
        using var fixture = new EvidenceFixture();
        var provider = fixture.Provider();
        var bars = new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["1d"] = fixture.Bars };
        var context = await StrategyDecisionRequestAssembler.ResolveDrawdownContextAsync(provider, "TEST",
            fixture.AsOf, bars, mode);
        Assert.NotNull(context);
        var snapshots = fixture.Bars.Select(bar => new IndicatorSnapshot(bar.Ticker, bar.Timestamp, "1d",
            bar.Close, bar.Volume, null, 50, 2, null, null, null, null, null, null, null, null, null, null,
            DataFeed: "sip", AdjustmentPolicy: "all", MarketEvidenceReliability: "verified_same_feed")).ToArray();
        var seed = StrategyDecisionKernelTests.CreateRequest();
        var strategy = seed.Strategy with {
            Timeframe = "1d", Confluence = seed.Strategy.Confluence with { Enabled = false },
            Execution = seed.Strategy.Execution with { Timeframe = "1d" },
            EntryRules = seed.Strategy.EntryRules with { TechnicalFactor = null,
                DrawdownQualification = new("gate", RequireSectorContext: true, RequireEventContext: true) }
        };
        var runtime = new AuthorizedRuntimeStrategy(seed.StrategyIdentity, StrategySelectionMode.Backtest,
            strategy, seed.AdmissionProfile);
        var request = StrategyDecisionRequestAssembler.Create(runtime, "TEST", bars,
            new Dictionary<string, IReadOnlyList<IndicatorSnapshot>> { ["1d"] = snapshots }, [], fixture.AsOf,
            seed.Candidate.Discovery, seed.Candidate.Universe, seed.Candidate.Regime,
            StrategyCandidateState.Discovered, 0, "synthetic-context", fixture.AsOf.AddDays(-1),
            fixture.AsOf.AddDays(1), 20, Guid.NewGuid(), drawdownContext: context);
        Assert.Same(context, request.DrawdownContext);
        var audit = DrawdownQualificationAnalyzer.Analyze(request.BarsByTimeframe["1d"], snapshots,
            fixture.Bars.Length - 1, request.AsOfUtc, strategy.EntryRules.DrawdownQualification,
            context: request.DrawdownContext);
        Assert.Equal("qualifies", audit.Verdict);
        Assert.Null(audit.Context!.SectorUnknownReason);
        Assert.Null(audit.Context.EventUnknownReason);
        Assert.Equal(0, audit.Context.EventCount);
        Assert.NotEqual(context.Sector!.Identity.PricePrefixBinding!.EvidenceSha256,
            context.Sector.Identity.PricePrefixBinding.ContentSha256);
    }

    [Fact]
    public async Task FutureFullContextDoesNotShadowEarlierAvailableContext()
    {
        using var fixture = new EvidenceFixture();
        var future = fixture.Context with { Events = fixture.Context.Events! with {
            Evaluation = fixture.Context.Events.Evaluation with { EvaluatedAtUtc = fixture.AsOf.AddDays(1) } } };
        fixture.AddContext("future.json", future, fixture.AsOf, fixture.AsOf);
        var result = await fixture.Provider().ResolveAsync(new("TEST", fixture.AsOf, fixture.Bars, "backtest"));
        Assert.NotNull(result);
        Assert.Equal(fixture.AsOf.AddDays(-1), result.Events!.Evaluation.EvaluatedAtUtc);
        Assert.Null(await fixture.Provider().ResolveAsync(new("TEST", fixture.AsOf.AddDays(-2), [], "backtest")));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("context")]
    [InlineData("escape")]
    [InlineData("undeclared")]
    [InlineData("version")]
    [InlineData("contract_version")]
    public void FrozenFilesAndManifestReferencesAreChecked(string mutation)
    {
        using var fixture = new EvidenceFixture();
        switch (mutation) {
            case "source": File.AppendAllText(Path.Combine(fixture.Root, "source.json"), "changed"); break;
            case "context": File.AppendAllText(Path.Combine(fixture.Root, "context.json"), "changed"); break;
            case "escape": fixture.Manifest = fixture.Manifest with { Sources =
                [fixture.Manifest.Sources[0] with { Path = "../outside.json" }] }; break;
            case "undeclared": fixture.Manifest = fixture.Manifest with { Contexts =
                [fixture.Manifest.Contexts[0] with { SourceIds = ["undeclared"] }] }; break;
            case "version": fixture.Manifest = fixture.Manifest with { Sources =
                [fixture.Manifest.Sources[0] with { Version = "" }] }; break;
            case "contract_version": fixture.Manifest = fixture.Manifest with { Sources =
                [fixture.Manifest.Sources[0] with { ContractVersions = ["unrelated-version"] }] }; break;
        }
        Assert.Throws<InvalidDataException>(() => fixture.Provider());
    }

    [Fact]
    public async Task MissingProviderRemainsUnknownAndNeverCreatesHistoricalClaims()
    {
        var context = await StrategyDecisionRequestAssembler.ResolveDrawdownContextAsync(null, "TEST",
            DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<OhlcvBar>>(), "paper");
        Assert.Null(context);
        var config = new EngineConfig("channels", 1, 10, 30, 60, false);
        Assert.Null(FrozenManifestDecisionContextProvider.FromConfig(config));
        Assert.DoesNotContain("EvidenceManifestPath", JsonSerializer.Serialize(config));
        Assert.Throws<InvalidDataException>(() => FrozenManifestDecisionContextProvider.FromConfig(
            config with { EvidenceManifestPath = "manifest.json" }));
    }

    [Fact]
    public async Task ExternalProviderReceivesOnlyCompletedKnownDailyPrefix()
    {
        using var fixture = new EvidenceFixture();
        var provider = new RecordingProvider();
        var bars = fixture.Bars.Append(fixture.Bars[^1] with { Timestamp = fixture.AsOf })
            .Append(fixture.Bars[0] with { KnownAtUtc = fixture.AsOf.AddDays(1) }).ToArray();
        await StrategyDecisionRequestAssembler.ResolveDrawdownContextAsync(provider, "TEST", fixture.AsOf,
            new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["1d"] = bars }, "paper");
        Assert.Equal("paper", provider.Request!.Mode);
        Assert.Equal(fixture.Bars.Length, provider.Request.DailyBars.Count);
        Assert.All(provider.Request.DailyBars, bar => Assert.True(bar.Timestamp.AddDays(1) <= fixture.AsOf));
    }

    [Fact]
    public void NewEventPolicyFieldsRoundtripWithoutChangingAbsentDefaults()
    {
        using var fixture = new EvidenceFixture();
        var strategy = StrategyDecisionKernelTests.CreateRequest().Strategy;
        var rules = new DrawdownQualificationRules("gate", RejectAdverseEvents: true,
            EventLookbackDays: 7, EventMaxCoverageAgeHours: 12);
        strategy = strategy with { EntryRules = strategy.EntryRules with { DrawdownQualification = rules } };
        var path = Path.Combine(fixture.Root, "strategy.yaml");
        File.WriteAllText(path, RunConfigWriter.SerializeStrategyYaml(strategy));
        Assert.Equal(rules, new SimpleYamlReader().ReadStrategy(path).EntryRules.DrawdownQualification);
    }

    private sealed class RecordingProvider : IStrategyDecisionContextProvider
    {
        public StrategyDecisionContextRequest? Request { get; private set; }
        public ValueTask<DrawdownDecisionContext?> ResolveAsync(StrategyDecisionContextRequest request,
            CancellationToken cancellationToken = default) { Request = request; return ValueTask.FromResult<DrawdownDecisionContext?>(null); }
    }

    private sealed class EvidenceFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tf-context-provider-" + Guid.NewGuid().ToString("N"));
        public OhlcvBar[] Bars { get; }
        public DateTimeOffset AsOf { get; }
        public DrawdownDecisionContext Context { get; }
        public FrozenDecisionContextManifest Manifest { get; set; }

        public EvidenceFixture()
        {
            Directory.CreateDirectory(Root);
            var start = new DateTimeOffset(2025, 1, 2, 5, 0, 0, TimeSpan.Zero);
            Bars = Enumerable.Range(0, 270).Select(i => start.AddDays(i))
                .Where(day => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).Take(180)
                .Select(day => new OhlcvBar("TEST", day, "1d", 100, 101, 99, 100, 1000, "sip", "all")).ToArray();
            AsOf = Bars[^1].Timestamp.AddDays(1);
            var source = Write("source.json", new { Synthetic = true, Version = "synthetic-test-v1" });
            var priceSources = ImmutableArray.CreateBuilder<FrozenDecisionContextSource>();
            DrawdownPricePrefixBinding Binding(string securityId, IReadOnlyList<OhlcvBar> prefix) {
                var path = securityId + ".json";
                // Physical bundle SHA is distinct from the canonical typed-prefix SHA.
                var physicalHash = Write(path, new DrawdownPriceSourceBundle(securityId, "all-v1", AsOf,
                    prefix.ToImmutableArray()));
                priceSources.Add(new(securityId, path, physicalHash, DrawdownPriceSourceBundle.ContractVersion,
                    AsOf, ["all-v1"]));
                return new(securityId, "all-v1", prefix[^1].Timestamp, AsOf, prefix.Count,
                    DrawdownPriceBasisBinding.ComputeSha256(securityId, "all-v1", prefix), physicalHash);
            }
            var identity = new DrawdownSecurityIdentity("synthetic-test-security", "TEST", "sip", "all", "all-v1",
                start.AddDays(-1), AsOf.AddDays(365), start.AddDays(-1), source,
                Binding("synthetic-test-security", Bars));
            var timestamps = Bars.Select(bar => bar.Timestamp).ToImmutableArray();
            DrawdownBenchmarkEvidence Benchmark(string symbol) => new(symbol, "synthetic-" + symbol, "all-v1",
                AsOf, source, Bars.Select(bar => bar with { Ticker = symbol }).ToImmutableArray(),
                Binding("synthetic-" + symbol, Bars.Select(bar => bar with { Ticker = symbol }).ToArray()));
            var sector = new DrawdownSectorContext(identity, "synthetic-sector", "XLK", start.AddDays(-1),
                AsOf.AddDays(365), start.AddDays(-1), source, start.AddDays(-1), source, timestamps,
                Benchmark("XLK"), Benchmark("SPY"));
            var evaluation = new DrawdownClassifierEvaluation("catalyst-v1", "synthetic-test-v1",
                "catalyst-human-labeling-v1", source, source, true, AsOf.AddDays(-1));
            var coverage = new[] { (DrawdownEventScope.Company, "TEST"), (DrawdownEventScope.Sector, "synthetic-sector"),
                (DrawdownEventScope.Global, "*") }.Select(item => new DrawdownEventScopeCoverage(item.Item1,
                    item.Item2, AsOf.AddDays(-5), AsOf, AsOf, source)).ToImmutableArray();
            Context = new(new("synthetic-test-security", "all-v1", timestamps, identity, start.AddDays(-1), source),
                sector, new(identity, "synthetic-sector", evaluation, coverage, [],
                    new("synthetic-sector", start.AddDays(-1), AsOf.AddDays(365), start.AddDays(-1), source)));
            Manifest = new(FrozenManifestDecisionContextProvider.SchemaVersion, "synthetic-test", "1",
                [new("synthetic-source", "source.json", source, "synthetic-test-v1", start.AddDays(-1),
                    ["all-v1", "catalyst-v1", "synthetic-test-v1", "catalyst-human-labeling-v1"])], []);
            Manifest = Manifest with { Sources = Manifest.Sources.AddRange(priceSources) };
            AddContext("context.json", Context, AsOf, AsOf);
        }

        public void AddContext(string path, DrawdownDecisionContext context, DateTimeOffset asOf, DateTimeOffset known)
        {
            var hash = Write(path, context);
            Manifest = Manifest with { Contexts = Manifest.Contexts.Add(new("TEST", asOf, known, path, hash, Manifest.Sources.Select(source => source.Id).ToImmutableArray())) };
        }
        public string RewriteSource(string id, object document)
        {
            var index = Enumerable.Range(0, Manifest.Sources.Length).Single(i => Manifest.Sources[i].Id == id);
            var source = Manifest.Sources[index];
            var hash = Write(source.Path, document);
            Manifest = Manifest with { Sources = Manifest.Sources.SetItem(index, source with { Sha256 = hash }) };
            return hash;
        }
        public void ReplaceContext(DrawdownDecisionContext context)
        {
            Manifest = Manifest with { Contexts = [] };
            AddContext("context.json", context, AsOf, AsOf);
        }
        public FrozenManifestDecisionContextProvider Provider()
        {
            Write("manifest.json", Manifest);
            return new(Root, "manifest.json");
        }
        private string Write(string path, object value) {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            File.WriteAllBytes(Path.Combine(Root, path), bytes);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
