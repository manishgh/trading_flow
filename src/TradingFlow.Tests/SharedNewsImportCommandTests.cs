using System.Text.Json;
using TradingFlow.Cli;

namespace TradingFlow.Tests;

public sealed class SharedNewsImportCommandTests
{
    [Fact]
    public void HelpExplainsTrustAndWholePublicationBoundsWithoutOpeningFiles()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, SharedNewsImportCommand.Run(["--help"], output, error));
        Assert.Contains("does not authenticate", output.ToString());
        Assert.Contains("not pagination batch sizes", output.ToString());
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("--unknown", "value")]
    [InlineData("--inbox")]
    [InlineData("--inbox", "")]
    [InlineData("--inbox", "one", "--inbox", "two")]
    [InlineData("--max-verified-attempts", "-1")]
    [InlineData("--max-verified-receipts", "1.5")]
    [InlineData("--max-retained-bytes", "999999999999999999999999")]
    [InlineData("--max-verified-attempts", "100001")]
    [InlineData("--max-verified-receipts", "10001")]
    [InlineData("--max-retained-bytes", "268435457")]
    public void InvalidArgumentsFailBeforeOpeningConsumer(params string[] arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, SharedNewsImportCommand.Run(arguments, output, error));
        Assert.Empty(output.ToString());
        Assert.Contains("Shared-news import rejected:", error.ToString());
    }

    [Fact]
    public void CommandImportsFixtureThenAcknowledgesRestartWithoutAdmission()
    {
        using var fixture = new SharedNewsPublicationConsumerTests.PublicationFixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        string[] arguments = ["--trusted-collector-root", fixture.Source, "--plan-sha256", fixture.PlanPin, "--inbox", fixture.Inbox];
        Assert.Equal(0, SharedNewsImportCommand.Run(arguments, output, error));
        using var first = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, first.RootElement.GetProperty("Imported").GetInt32());
        Assert.False(first.RootElement.GetProperty("normalizedEvidenceAdmitted").GetBoolean());
        Assert.False(first.RootElement.GetProperty("tradingAdmitted").GetBoolean());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, SharedNewsImportCommand.Run(arguments, output, error));
        using var second = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, second.RootElement.GetProperty("AlreadyImported").GetInt32());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void MissingCommitReportsIncompleteRatherThanSuccessfulCoverage()
    {
        using var fixture = new SharedNewsPublicationConsumerTests.PublicationFixture();
        File.Delete(Path.Combine(fixture.Attempt(), "result", "result.json"));
        Directory.Delete(Path.Combine(fixture.Attempt(), "result"));
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, SharedNewsImportCommand.Run(["--trusted-collector-root", fixture.Source,
            "--plan-sha256", fixture.PlanPin, "--inbox", fixture.Inbox], output, error));
        using var report = JsonDocument.Parse(output.ToString());
        Assert.Equal("raw_import_collection_incomplete", report.RootElement.GetProperty("status").GetString());
        Assert.False(report.RootElement.GetProperty("providerPaginationTerminal").GetBoolean());
        Assert.False(Directory.Exists(fixture.Inbox));
    }

    [Fact]
    public void CancellationLeavesPublicationUnacknowledged()
    {
        using var fixture = new SharedNewsPublicationConsumerTests.PublicationFixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(130, SharedNewsImportCommand.Run(["--trusted-collector-root", fixture.Source,
            "--plan-sha256", fixture.PlanPin, "--inbox", fixture.Inbox], output, error, new CancellationToken(true)));
        Assert.Empty(output.ToString());
        Assert.Contains("cancelled", error.ToString());
        Assert.False(Directory.Exists(fixture.Inbox));
    }

    [Fact]
    public void InvalidPinIsAReportedRejectionNotAnUnhandledException()
    {
        using var fixture = new SharedNewsPublicationConsumerTests.PublicationFixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, SharedNewsImportCommand.Run(["--trusted-collector-root", fixture.Source,
            "--plan-sha256", "invalid", "--inbox", fixture.Inbox], output, error));
        Assert.Empty(output.ToString());
        Assert.Contains("Shared-news import rejected:", error.ToString());
        Assert.False(Directory.Exists(fixture.Inbox));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptOrWrongTypedPublicationIsAReportedRejection(bool wrongType)
    {
        using var fixture = new SharedNewsPublicationConsumerTests.PublicationFixture();
        if (wrongType)
            fixture.ChangePlan(plan => plan["windows"]![0]!["request"]!["include_content"] = "true");
        else
            File.AppendAllText(Path.Combine(fixture.Run, "plan", "plan.json"), " ");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, SharedNewsImportCommand.Run(["--trusted-collector-root", fixture.Source,
            "--plan-sha256", fixture.PlanPin, "--inbox", fixture.Inbox], output, error));
        Assert.Empty(output.ToString());
        Assert.Contains("Shared-news import rejected:", error.ToString());
        Assert.False(Directory.Exists(fixture.Inbox));
    }

    [Fact]
    public void MissingAcknowledgementFieldIsRejectedWithoutOverwritingIt()
    {
        using var fixture = new SharedNewsPublicationConsumerTests.PublicationFixture();
        fixture.Import();
        var path = Path.Combine(fixture.Bundle(), "acknowledgement.json");
        var record = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        record.Remove("imported_at_utc");
        var bytes = SharedNewsPublicationConsumerTests.PublicationFixture.Canonical(record);
        File.WriteAllBytes(path, bytes);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, SharedNewsImportCommand.Run(["--trusted-collector-root", fixture.Source,
            "--plan-sha256", fixture.PlanPin, "--inbox", fixture.Inbox], output, error));
        Assert.Empty(output.ToString());
        Assert.Contains("Shared-news import rejected:", error.ToString());
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
