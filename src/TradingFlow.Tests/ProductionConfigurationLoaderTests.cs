using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Moq;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Tests;

public sealed class ProductionConfigurationLoaderTests
{
    private const string ExpectedAccountId = "paper-account-id";
    private const string CatalystModel = "pinned-catalyst-model-v1";

    [Fact]
    public void Load_AppliesTypedDefaultsAndProducesImmutableCompleteSnapshot()
    {
        var snapshot = Load();

        Assert.Equal(ProductionProfile.Paper, snapshot.Profile);
        Assert.Equal(ProductionParameterRegistry.Definitions.Count, snapshot.Values.Count);
        Assert.Equal(300, snapshot.Get<int>("pdt_recheck_interval_s"));
        Assert.Equal(0.92m, snapshot.Get<decimal>("dedup_similarity_threshold"));
        Assert.False(snapshot.Get<bool>("allow_extended_hours_trading"));
        Assert.Equal("strategy_gated", snapshot.Get<string>("manual_entry_policy"));
        Assert.Equal(new TimeOnly(3, 30), snapshot.Get<TimeOnly>("calendar_fetch_time"));
        Assert.Equal(ExpectedAccountId, snapshot.Get<string>("expected_account_id"));
        Assert.Matches("^[0-9a-f]{64}$", snapshot.ConfigHash);
        Assert.IsAssignableFrom<IImmutableDictionary<string, object>>(snapshot.Values);
    }

    [Fact]
    public void Load_RejectsUnknownMissingMalformedAndOutOfRangeValues()
    {
        var loader = new ProductionConfigurationLoader();

        AssertParameterError(
            "not_in_appendix",
            () => loader.Load(ProductionProfile.Paper, RequiredValues().Append("not_in_appendix", "1")));
        AssertParameterError(
            "expected_account_id",
            () => loader.Load(
                ProductionProfile.Paper,
                new Dictionary<string, string?> { ["catalyst_llm_model"] = CatalystModel }));
        AssertParameterError(
            "pdt_recheck_interval_s",
            () => loader.Load(ProductionProfile.Paper, RequiredValues().Append("pdt_recheck_interval_s", "fast")));
        AssertParameterError(
            "pdt_recheck_interval_s",
            () => loader.Load(ProductionProfile.Paper, RequiredValues().Append("pdt_recheck_interval_s", "59")));
        AssertParameterError(
            "account_mode",
            () => loader.Load(ProductionProfile.Paper, RequiredValues().Append("account_mode", "unsupported")));
        AssertParameterError(
            "swga_entry_window",
            () => loader.Load(ProductionProfile.Paper, RequiredValues().Append("swga_entry_window", "09:00-10:30")));
        AssertParameterError(
            "swga_entry_window",
            () => loader.Load(ProductionProfile.Paper, RequiredValues().Append("swga_entry_window", "11:00-10:30")));
    }

    [Fact]
    public void Load_NormalizesTextRepresentationsAndDictionaryOrderBeforeHashing()
    {
        var first = Load(RequiredValues()
            .Append("max_daily_loss_pct", "2.0")
            .Append("calendar_fetch_time", "03:30"));
        var second = Load(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["calendar_fetch_time"] = "3:30",
            ["max_daily_loss_pct"] = "2",
            ["catalyst_llm_model"] = CatalystModel,
            ["expected_account_id"] = ExpectedAccountId
        });

        Assert.Equal(first.ConfigHash, second.ConfigHash);
    }

    [Fact]
    public void Load_ChangesHashForEffectiveValueOrProfileChange()
    {
        var baseline = Load();
        var changedValue = Load(RequiredValues().Append("max_daily_loss_pct", "2.5"));
        var changedProfile = new ProductionConfigurationLoader().Load(ProductionProfile.Development, RequiredValues());

        Assert.NotEqual(baseline.ConfigHash, changedValue.ConfigHash);
        Assert.NotEqual(baseline.ConfigHash, changedProfile.ConfigHash);
    }

    [Fact]
    public void Load_DetachesSnapshotFromMutableInput()
    {
        var values = RequiredValues().ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var snapshot = Load(values);
        var originalHash = snapshot.ConfigHash;

        values["expected_account_id"] = "mutated-after-load";
        values["max_daily_loss_pct"] = "5";

        Assert.Equal(ExpectedAccountId, snapshot.Get<string>("expected_account_id"));
        Assert.Equal(2m, snapshot.Get<decimal>("max_daily_loss_pct"));
        Assert.Equal(originalHash, snapshot.ConfigHash);
    }

    [Theory]
    [InlineData("allow_multi_strategy_same_symbol")]
    [InlineData("allow_day_to_swing_conversion")]
    public void Load_LiveProfileRejectsLockedV1Overrides(string parameterName)
    {
        var loader = new ProductionConfigurationLoader();

        AssertParameterError(
            parameterName,
            () => loader.Load(ProductionProfile.Live, RequiredValues().Append(parameterName, "true")));

        var paper = loader.Load(ProductionProfile.Paper, RequiredValues().Append(parameterName, "true"));
        Assert.True(paper.Get<bool>(parameterName));
    }

    [Fact]
    public void Load_LiveProfileRejectsOperatorDirectManualEntry()
    {
        var loader = new ProductionConfigurationLoader();

        AssertParameterError(
            "manual_entry_policy",
            () => loader.Load(
                ProductionProfile.Live,
                RequiredValues().Append("manual_entry_policy", "operator_direct")));

        var paper = loader.Load(
            ProductionProfile.Paper,
            RequiredValues().Append("manual_entry_policy", "operator_direct"));
        Assert.Equal("operator_direct", paper.Get<string>("manual_entry_policy"));
    }

    [Theory]
    [InlineData(ProductionProfile.Paper)]
    [InlineData(ProductionProfile.Live)]
    public void Load_AllowsExplicitExtendedHoursTradingInTradingProfiles(ProductionProfile profile)
    {
        var snapshot = new ProductionConfigurationLoader().Load(
            profile,
            RequiredValues().Append("allow_extended_hours_trading", "true"));

        Assert.True(snapshot.Get<bool>("allow_extended_hours_trading"));
    }

    [Theory]
    [InlineData(ProductionProfile.Paper)]
    [InlineData(ProductionProfile.Live)]
    public void Load_RejectsDevelopmentOnlyIexFallbackOutsideDevelopment(ProductionProfile profile)
    {
        var loader = new ProductionConfigurationLoader();

        AssertParameterError(
            "allow_iex_fallback",
            () => loader.Load(profile, RequiredValues().Append("allow_iex_fallback", "true")));

        var development = loader.Load(
            ProductionProfile.Development,
            RequiredValues().Append("allow_iex_fallback", "true"));
        Assert.True(development.Get<bool>("allow_iex_fallback"));
    }

    [Fact]
    public void Load_LogsOnlyProfileHashAndParameterCountAtStartup()
    {
        var logger = new Mock<ILogger<ProductionConfigurationLoader>>();
        var loader = new ProductionConfigurationLoader(logger.Object);

        var snapshot = loader.Load(ProductionProfile.Paper, RequiredValues());

        logger.Verify(
            candidate => candidate.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains(snapshot.ConfigHash, StringComparison.Ordinal) &&
                    state.ToString()!.Contains("Paper", StringComparison.Ordinal) &&
                    !state.ToString()!.Contains(ExpectedAccountId, StringComparison.Ordinal) &&
                    !state.ToString()!.Contains(CatalystModel, StringComparison.Ordinal)),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void ResolveParameter_UsesRegistryDefaultAndValidatedOverride()
    {
        var loader = new ProductionConfigurationLoader();

        Assert.Equal(15, loader.ResolveParameter<int>(ProductionProfile.Paper, "order_poll_interval_s"));
        Assert.Equal(30, loader.ResolveParameter<int>(ProductionProfile.Paper, "order_poll_interval_s", "30"));
        AssertParameterError(
            "order_poll_interval_s",
            () => loader.ResolveParameter<int>(ProductionProfile.Paper, "order_poll_interval_s", "4"));
    }

    private static ProductionConfigurationSnapshot Load(IReadOnlyDictionary<string, string?>? values = null) =>
        new ProductionConfigurationLoader().Load(ProductionProfile.Paper, values ?? RequiredValues());

    private static IReadOnlyDictionary<string, string?> RequiredValues() =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["expected_account_id"] = ExpectedAccountId,
            ["catalyst_llm_model"] = CatalystModel
        };

    private static void AssertParameterError(string parameterName, Action action)
    {
        var exception = Assert.Throws<ProductionConfigurationException>(action);
        Assert.Equal(parameterName, exception.ParameterName);
    }
}

internal static class ProductionConfigurationTestDictionaryExtensions
{
    public static IReadOnlyDictionary<string, string?> Append(
        this IReadOnlyDictionary<string, string?> source,
        string name,
        string? value)
    {
        var result = source.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        result[name] = value;
        return result;
    }
}
