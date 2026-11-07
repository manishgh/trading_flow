using System.Text.Json;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class ContinuationProtectionConfigurationTests
{
    // An omitted policy must preserve existing canonical JSON and disabled behavior.
    [Fact]
    public void AbsentPolicy_RemainsDisabledAndDoesNotAddCanonicalField()
    {
        var strategy = ReadBaseline();
        Assert.Null(strategy.ExitRules.ContinuationProtection);
        Assert.DoesNotContain("continuationProtection", JsonSerializer.Serialize(strategy,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), StringComparison.Ordinal);
        Assert.DoesNotContain("continuation_protection", RunConfigWriter.SerializeStrategyYaml(strategy), StringComparison.Ordinal);
    }

    // A declared mapping enables the policy while unspecified policy members resolve the approved defaults.
    [Fact]
    public void ExplicitMapping_UsesApprovedDefaults()
    {
        var yaml = RunConfigWriter.SerializeStrategyYaml(ValidStrategy());
        var simplified = yaml.Replace("    cost_buffer_ticks: 1" + Environment.NewLine, "", StringComparison.Ordinal)
            .Replace("    confirmation_bars: 2" + Environment.NewLine, "", StringComparison.Ordinal)
            .Replace("    target_step_r: 1" + Environment.NewLine, "", StringComparison.Ordinal);
        var actual = ReadTemporary(simplified);
        Assert.Equal(new ContinuationProtectionRules(), actual.ExitRules.ContinuationProtection);
    }

    // Generated strategy snapshots retain every policy value, including nondefault confirmation and cost buffers.
    [Fact]
    public void WriterAndReader_PreserveExplicitPolicy()
    {
        var strategy = ValidStrategy(new ContinuationProtectionRules(2.2m, 3, 4, 1.5m));
        var actual = ReadTemporary(RunConfigWriter.SerializeStrategyYaml(strategy));
        Assert.Equal(strategy.ExitRules, actual.ExitRules);
        Assert.Equal(IdentityHash(strategy), IdentityHash(actual));
    }

    // Each policy parameter changes immutable strategy identity so treatments cannot alias one another.
    [Fact]
    public void CanonicalIdentity_IncludesEveryPolicyValue()
    {
        var baseline = ReadBaseline();
        var enabled = ValidStrategy();
        var policies = new[]
        {
            new ContinuationProtectionRules(),
            new ContinuationProtectionRules(ActivationR: 2.3m),
            new ContinuationProtectionRules(CostBufferTicks: 2),
            new ContinuationProtectionRules(ConfirmationBars: 3),
            new ContinuationProtectionRules(TargetStepR: 2m)
        };
        var hashes = policies.Select(policy => IdentityHash(enabled with
        {
            ExitRules = enabled.ExitRules with { ContinuationProtection = policy }
        })).Append(IdentityHash(baseline)).ToArray();
        Assert.Equal(hashes.Length, hashes.Distinct(StringComparer.Ordinal).Count());
    }

    // Reject invalid numeric values in YAML before any runner can consume an ignored or unreachable policy.
    [Theory]
    [InlineData("activation_r: 2.4", "activation_r: 0")]
    [InlineData("activation_r: 2.4", "activation_r: 3")]
    [InlineData("cost_buffer_ticks: 1", "cost_buffer_ticks: 0")]
    [InlineData("confirmation_bars: 2", "confirmation_bars: 1")]
    [InlineData("target_step_r: 1", "target_step_r: 0")]
    [InlineData("target_r_multiple: 3", "target_r_multiple: 2.5")]
    [InlineData("enable_atr_trailing_stop: false", "enable_atr_trailing_stop: true")]
    [InlineData("profit_target_mode: r_multiple", "profit_target_mode: none")]
    public void Reader_RejectsInvalidOrCompetingPolicy(string before, string after)
    {
        var yaml = RunConfigWriter.SerializeStrategyYaml(ValidStrategy());
        Assert.Contains(before, yaml, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => ReadTemporary(yaml.Replace(before, after, StringComparison.Ordinal)));
    }

    // Nested typos and a scalar masquerading as the optional mapping cannot silently disable policy settings.
    [Theory]
    [InlineData("    confirmation_bars: 2", "    confirmation_barz: 2")]
    [InlineData("  continuation_protection:", "  continuation_protection: true")]
    [InlineData("    target_step_r: 1", "    target_step_r:\n      - 1")]
    [InlineData("    activation_r: 2.4", "    activation_r:")]
    [InlineData("    cost_buffer_ticks: 1", "    cost_buffer_ticks:")]
    [InlineData("    confirmation_bars: 2", "    confirmation_bars:")]
    [InlineData("    target_step_r: 1", "    target_step_r:")]
    public void Reader_RejectsUnknownKeysAndWrongShapes(string before, string after)
    {
        var yaml = RunConfigWriter.SerializeStrategyYaml(ValidStrategy());
        Assert.Throws<InvalidOperationException>(() => ReadTemporary(yaml.Replace(before, after, StringComparison.Ordinal)));
    }

    // Programmatic configuration receives the same conflict checks as a parsed YAML strategy.
    [Fact]
    public void Writer_RejectsConcurrentAtrTrailing()
    {
        var strategy = ValidStrategy();
        Assert.Throws<InvalidOperationException>(() => RunConfigWriter.SerializeStrategyYaml(strategy with
        {
            ExitRules = strategy.ExitRules with { EnableAtrTrailingStop = true }
        }));
    }

    // Reuse an existing deterministic recipe without changing or registering a catalog artifact.
    private static StrategyDefinition ReadBaseline() => new SimpleYamlReader().ReadStrategy(Path.Combine(
        TestRepository.FindRoot(), "src", "TradingFlow.Tests", "Fixtures", "Strategies", "daily-trailing-swing.fixture.yaml"));

    // Only isolated in-memory tests opt in to the approved optional exit settings.
    private static StrategyDefinition ValidStrategy(ContinuationProtectionRules? policy = null)
    {
        var baseline = ReadBaseline();
        return baseline with
        {
            ExitRules = baseline.ExitRules with
            {
                TargetRMultiple = 3m,
                EnableAtrTrailingStop = false,
                ProfitTargetMode = "r_multiple",
                ContinuationProtection = policy ?? new ContinuationProtectionRules()
            }
        };
    }

    // Hash the same resolved strategy document used by the artifact catalog.
    private static string IdentityHash(StrategyDefinition strategy) => EvidenceCanonicalJson.ComputeSha256(
        new CanonicalStrategyDocument(strategy.StrategyId, "0.0.0-test", strategy, StrategyAdmissionProfiles.DeterministicV1));

    // Parse a test-owned temporary YAML file and remove only that generated fixture after the assertion.
    private static StrategyDefinition ReadTemporary(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"continuation-config-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(path, yaml);
            return new SimpleYamlReader().ReadStrategy(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
