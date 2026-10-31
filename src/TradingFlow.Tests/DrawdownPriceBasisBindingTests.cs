using System.Collections.Immutable;
using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed class DrawdownPriceBasisBindingTests
{
    private static readonly DateTimeOffset Start = new(2025, 1, 2, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AsOf = Start.AddDays(50);
    private static readonly string IndependentSourceHash = new('a', 64);
    private static OhlcvBar[] Bars(string ticker = "TEST") => Enumerable.Range(0, 50).Select(index =>
        new OhlcvBar(ticker, Start.AddDays(index), "1d", 100, 110, 90, 100, 1000, "sip", "all")).ToArray();
    private static DrawdownPricePrefixBinding Binding(string securityId, IReadOnlyList<OhlcvBar> bars) =>
        new(securityId, "all-v1", bars[^1].Timestamp, AsOf, bars.Count,
            DrawdownPriceBasisBinding.ComputeSha256(securityId, "all-v1", bars), IndependentSourceHash);
    private static DrawdownSecurityIdentity Identity() => new("security-test", "TEST", "sip", "all", "all-v1",
        Start, AsOf.AddYears(1), Start, IndependentSourceHash, Binding("security-test", Bars()));
    private static DrawdownDecisionContext Context()
    {
        DrawdownBenchmarkEvidence Benchmark(string symbol, string securityId) {
            var bars = Bars(symbol).ToImmutableArray();
            return new(symbol, securityId, "all-v1", AsOf, IndependentSourceHash, bars, Binding(securityId, bars));
        }
        return new(Sector: new(Identity(), "technology", "XLK", Start, AsOf.AddYears(1), Start,
            IndependentSourceHash, Start, IndependentSourceHash, Bars().Select(bar => bar.Timestamp).ToImmutableArray(),
            Benchmark("XLK", "security-xlk"), Benchmark("SPY", "security-spy")));
    }

    [Fact]
    public void ActualValidCloseChangeInvalidatesFrozenPriceBinding()
    {
        var bars = Bars();
        Assert.Null(DrawdownContextAnalyzer.ValidateIdentity(Identity(), bars, 49, AsOf, Start));
        bars[49] = bars[49] with { Close = 105 };
        Assert.Equal("price_prefix_binding_content_mismatch",
            DrawdownContextAnalyzer.ValidateIdentity(Identity(), bars, 49, AsOf, Start));
    }

    [Fact]
    public void RenamingBothRevisionStringsCannotReuseAnOldContentDigest()
    {
        var identity = Identity();
        identity = identity with { AdjustmentVersion = "all-v2",
            PricePrefixBinding = identity.PricePrefixBinding! with { AdjustmentVersion = "all-v2" } };
        Assert.Equal("price_prefix_binding_content_mismatch",
            DrawdownContextAnalyzer.ValidateIdentity(identity, Bars(), 49, AsOf, Start));
        Assert.NotEqual(DrawdownPriceBasisBinding.ComputeSha256("security-test", "all-v1", Bars()),
            DrawdownPriceBasisBinding.ComputeSha256("security-test", "all-v2", Bars()));
    }

    [Fact]
    public void MissingPriceBindingIsExplicitlyUnknownWithoutChangingAbsentSerialization()
    {
        var identity = Identity() with { PricePrefixBinding = null };
        Assert.Equal("price_prefix_binding_unavailable",
            DrawdownContextAnalyzer.ValidateIdentity(identity, Bars(), 49, AsOf, Start));
        Assert.DoesNotContain("PricePrefixBinding", JsonSerializer.Serialize(identity));
        var benchmark = Context().Sector!.SectorBenchmark with { PricePrefixBinding = null };
        Assert.DoesNotContain("PricePrefixBinding", JsonSerializer.Serialize(benchmark));
    }

    [Theory]
    [InlineData("count", "price_prefix_binding_interval_mismatch")]
    [InlineData("through", "price_prefix_binding_interval_mismatch")]
    [InlineData("future", "price_prefix_binding_not_available_asof")]
    [InlineData("predated", "price_prefix_binding_interval_mismatch")]
    public void PriceBindingRequiresExactCountThroughAndCausalReceipt(string mutation, string reason)
    {
        var identity = Identity();
        var binding = identity.PricePrefixBinding!;
        binding = mutation switch {
            "count" => binding with { BarCount = binding.BarCount - 1 },
            "through" => binding with { ThroughSourceTimestamp = binding.ThroughSourceTimestamp.AddDays(-1) },
            "future" => binding with { KnownAtUtc = AsOf.AddSeconds(1) },
            _ => binding with { KnownAtUtc = AsOf.AddSeconds(-1) }
        };
        Assert.Equal(reason, DrawdownContextAnalyzer.ValidateIdentity(identity with { PricePrefixBinding = binding },
            Bars(), 49, AsOf, Start));
    }

    [Fact]
    public void BenchmarkPriceChangeRequiresItsOwnMatchingFrozenBinding()
    {
        var context = Context();
        var original = DrawdownContextAnalyzer.Analyze(Bars(), 49, AsOf,
            new("gate", RequireSectorContext: true), context);
        Assert.Null(original!.SectorUnknownReason);
        var sector = context.Sector!;
        var benchmark = sector.SectorBenchmark;
        context = context with { Sector = sector with { SectorBenchmark = benchmark with {
            Bars = benchmark.Bars.SetItem(49, benchmark.Bars[49] with { Close = 105 }) } } };
        var changed = DrawdownContextAnalyzer.Analyze(Bars(), 49, AsOf,
            new("gate", RequireSectorContext: true), context);
        Assert.Equal("sector_benchmark_price_prefix_binding_content_mismatch", changed!.SectorUnknownReason);
    }

    [Fact]
    public void FutureStockAndBenchmarkSuffixDoesNotChangeTheBoundConsumedPrefix()
    {
        var context = Context();
        var rules = new DrawdownQualificationRules("gate", RequireSectorContext: true);
        var first = DrawdownContextAnalyzer.Analyze(Bars(), 49, AsOf, rules, context);
        var benchmark = context.Sector!.SectorBenchmark;
        var appended = context with { Sector = context.Sector with { SectorBenchmark = benchmark with {
            Bars = benchmark.Bars.Add(benchmark.Bars[^1] with { Timestamp = AsOf.AddDays(1), Close = 105 }) } } };
        var bars = Bars().Append(Bars()[^1] with { Timestamp = AsOf.AddDays(1), Close = 105 }).ToArray();
        var second = DrawdownContextAnalyzer.Analyze(bars, 49, AsOf, rules, appended);
        Assert.Null(second!.SectorUnknownReason);
        Assert.Equal(EvidenceCanonicalJson.ComputeSha256(first!), EvidenceCanonicalJson.ComputeSha256(second));
    }
}

public sealed partial class DecisionContextProviderTests
{
    private static DrawdownDecisionContext WithStockBinding(DrawdownDecisionContext context,
        DrawdownPricePrefixBinding binding)
    {
        var identity = context.Sector!.Identity with { PricePrefixBinding = binding };
        return context with {
            History = context.History! with { Identity = identity },
            Sector = context.Sector with { Identity = identity },
            Events = context.Events! with { Identity = identity }
        };
    }

    [Fact]
    public void AVerifiedMetadataOnlyFileCannotStandInForARealPriceSourceBundle()
    {
        using var fixture = new EvidenceFixture();
        var binding = fixture.Context.Sector!.Identity.PricePrefixBinding!;
        var hash = fixture.RewriteSource("synthetic-test-security", new {
            SecurityId = binding.SecurityId, AdjustmentVersion = binding.AdjustmentVersion, KnownAtUtc = fixture.AsOf });
        fixture.ReplaceContext(WithStockBinding(fixture.Context, binding with { EvidenceSha256 = hash }));
        Assert.Throws<InvalidDataException>(() => fixture.Provider());
    }

    [Fact]
    public void VerifiedCrossSecuritySourceHashCannotProveAnotherSecurityPricePrefix()
    {
        using var fixture = new EvidenceFixture();
        var binding = fixture.Context.Sector!.Identity.PricePrefixBinding!;
        var otherSource = fixture.Manifest.Sources.Single(source => source.Id == "synthetic-SPY");
        fixture.ReplaceContext(WithStockBinding(fixture.Context, binding with { EvidenceSha256 = otherSource.Sha256 }));
        var error = Assert.Throws<InvalidDataException>(() => fixture.Provider());
        Assert.Contains("security, adjustment revision or receipt", error.Message);
    }

    [Theory]
    [InlineData("price")]
    [InlineData("revision")]
    [InlineData("duplicate")]
    [InlineData("unordered")]
    public void VerifiedPhysicalSourceMustMatchTheFrozenTypedPricePrefix(string mutation)
    {
        using var fixture = new EvidenceFixture();
        var binding = fixture.Context.Sector!.Identity.PricePrefixBinding!;
        var bars = fixture.Bars.ToImmutableArray();
        var revision = "all-v1";
        switch (mutation) {
            case "price": bars = bars.SetItem(bars.Length - 1, bars[^1] with { Close = 99.5m }); break;
            case "revision": revision = "all-v2"; break;
            case "duplicate": bars = bars.Insert(1, bars[0]); break;
            case "unordered": bars = bars.Reverse().ToImmutableArray(); break;
        }
        var hash = fixture.RewriteSource("synthetic-test-security",
            new DrawdownPriceSourceBundle(binding.SecurityId, revision, fixture.AsOf, bars));
        fixture.ReplaceContext(WithStockBinding(fixture.Context, binding with { EvidenceSha256 = hash }));
        Assert.Throws<InvalidDataException>(() => fixture.Provider());
    }

    [Theory]
    [InlineData("bundle")]
    [InlineData("manifest")]
    public void PriceSourceReceiptCannotPredateItsConsumedCompletedRows(string receipt)
    {
        using var fixture = new EvidenceFixture();
        var binding = fixture.Context.Sector!.Identity.PricePrefixBinding!;
        if (receipt == "bundle") {
            var hash = fixture.RewriteSource("synthetic-test-security", new DrawdownPriceSourceBundle(
                binding.SecurityId, binding.AdjustmentVersion, fixture.AsOf.AddSeconds(-1), fixture.Bars.ToImmutableArray()));
            fixture.ReplaceContext(WithStockBinding(fixture.Context, binding with { EvidenceSha256 = hash }));
        }
        else {
            var index = Enumerable.Range(0, fixture.Manifest.Sources.Length)
                .Single(i => fixture.Manifest.Sources[i].Id == "synthetic-test-security");
            fixture.Manifest = fixture.Manifest with { Sources = fixture.Manifest.Sources.SetItem(index,
                fixture.Manifest.Sources[index] with { KnownAtUtc = fixture.AsOf.AddSeconds(-1) }) };
        }
        var error = Assert.Throws<InvalidDataException>(() => fixture.Provider());
        Assert.Contains("receipt predates", error.Message);
    }

    [Fact]
    public async Task ActualRequestPriceChangeRemainsUnknownAfterPhysicalSourceVerification()
    {
        using var fixture = new EvidenceFixture();
        var changedBars = fixture.Bars.ToArray();
        changedBars[^1] = changedBars[^1] with { Close = 99.5m };
        var context = await fixture.Provider().ResolveAsync(new("TEST", fixture.AsOf, changedBars, "backtest"));
        var result = DrawdownContextAnalyzer.Analyze(changedBars, changedBars.Length - 1, fixture.AsOf,
            new("gate", RequireSectorContext: true), context);
        Assert.Equal("price_prefix_binding_content_mismatch", result!.SectorUnknownReason);
    }

    [Fact]
    public async Task FutureBenchmarkSnapshotCannotShadowEarlierAvailableSnapshot()
    {
        using var fixture = new EvidenceFixture();
        var benchmark = fixture.Context.Sector!.SectorBenchmark;
        var future = fixture.Context with { Sector = fixture.Context.Sector with { SectorBenchmark = benchmark with {
            Bars = benchmark.Bars.Add(benchmark.Bars[^1] with { Timestamp = fixture.AsOf.AddDays(1),
                KnownAtUtc = fixture.AsOf.AddDays(2) }) } } };
        // Entry metadata cannot backdate the immutable snapshot's embedded future benchmark evidence.
        // Keep the earlier immutable as-of snapshot so the adapter can select it.
        fixture.AddContext("future-benchmark-context.json", future, fixture.AsOf, fixture.AsOf);
        var result = await fixture.Provider().ResolveAsync(new("TEST", fixture.AsOf, fixture.Bars, "backtest"));
        Assert.NotNull(result);
        Assert.Equal(benchmark.Bars.Length, result.Sector!.SectorBenchmark.Bars.Length);
        Assert.Equal(fixture.AsOf, result.Sector.Identity.PricePrefixBinding!.KnownAtUtc);
    }
}
