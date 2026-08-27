using TradingFlow.Engine.Configuration;

namespace TradingFlow.Tests;

public sealed class StrategyYamlValidationTests
{
    [Fact]
    public void ReadStrategy_RejectsUnknownRuleInsteadOfSilentlyIgnoringIt()
    {
        var path = CopyCanonicalStrategy(content => content.Replace(
            "  setup_type: indicator_stack",
            "  setup_type: indicator_stack" + Environment.NewLine +
            "  misspelled_risk_gate: true",
            StringComparison.Ordinal));
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                new SimpleYamlReader().ReadStrategy(path));

            Assert.Contains("entry_rules.misspelled_risk_gate", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadStrategy_RejectsDuplicateKey()
    {
        var path = CopyCanonicalStrategy(content => content + Environment.NewLine + "version: 99" + Environment.NewLine);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                new SimpleYamlReader().ReadStrategy(path));

            Assert.Contains("Duplicate YAML key 'version'", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadStrategy_RejectsMalformedLine()
    {
        var path = CopyCanonicalStrategy(content => content + Environment.NewLine + "this is not yaml" + Environment.NewLine);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                new SimpleYamlReader().ReadStrategy(path));

            Assert.Contains("Malformed YAML line", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CopyCanonicalStrategy(Func<string, string> transform)
    {
        var source = Path.Combine(
            TestRepository.FindRoot(),
            "configs",
            "strategies",
            "intraday-ema10-ema20-macd-volume.v1.yaml");
        var target = Path.Combine(Path.GetTempPath(), $"strategy-validation-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(target, transform(File.ReadAllText(source)));
        return target;
    }
}
