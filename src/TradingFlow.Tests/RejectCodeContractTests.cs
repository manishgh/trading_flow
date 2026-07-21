using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Tests;

public sealed partial class RejectCodeContractTests
{
    [Fact]
    public void RejectCode_IsBidirectionallySynchronizedWithBindingSpecifications()
    {
        var expected = ReadSpecificationCodes();
        var actual = Enum.GetNames<RejectCode>().ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected.Count, actual.Count);
        Assert.Empty(expected.Except(actual, StringComparer.Ordinal));
        Assert.Empty(actual.Except(expected, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(RejectCode.REJECT_WIDE_SPREAD, "\"REJECT_WIDE_SPREAD\"")]
    [InlineData(RejectCode.REJECT_RECONCILE_LOCK, "\"REJECT_RECONCILE_LOCK\"")]
    public void RejectCode_JsonValueMatchesSpecification(RejectCode code, string expectedJson)
    {
        Assert.Equal(expectedJson, JsonSerializer.Serialize(code));
        Assert.Equal(code, JsonSerializer.Deserialize<RejectCode>(expectedJson));
    }

    [Fact]
    public async Task GateEvaluation_StoresAndLoadsCanonicalRejectCode()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
            .UseSqlite(connection)
            .Options;
        var runId = Guid.NewGuid();

        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
            context.ProductionRuns.Add(new ProductionRun
            {
                RunId = runId,
                ConfigHash = new string('a', 64),
                CodeVersion = "reject-code-test",
                Profile = "paper",
                Status = "running",
                StartedAtUtc = DateTimeOffset.UtcNow
            });
            context.GateEvaluations.Add(new GateEvaluationRecord
            {
                RunId = runId,
                ConfigHash = new string('a', 64),
                CodeVersion = "reject-code-test",
                CandidateId = Guid.NewGuid(),
                GateOrder = 1,
                GateName = "spread",
                Passed = false,
                RejectCode = RejectCode.REJECT_WIDE_SPREAD,
                EvaluatedAtUtc = DateTimeOffset.UtcNow,
                InputsJson = "{}"
            });
            await context.SaveChangesAsync();
        }

        await using var verificationContext = new TradingFlowDbContext(options);
        var stored = await verificationContext.GateEvaluations.AsNoTracking().SingleAsync();
        Assert.Equal(RejectCode.REJECT_WIDE_SPREAD, stored.RejectCode);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT reject_code FROM gate_evaluations;";
        Assert.Equal("REJECT_WIDE_SPREAD", await command.ExecuteScalarAsync());
    }

    private static HashSet<string> ReadSpecificationCodes()
    {
        var root = TestRepository.FindRoot();
        var paths = new[]
        {
            Path.Combine(root, "docs", "spec", "automated_us_equities_trading_research_design.md"),
            Path.Combine(root, "docs", "spec", "automated_trading_production_spec_v1.md")
        };
        return paths
            .Select(File.ReadAllText)
            .SelectMany(text => RejectCodePattern().Matches(text).Select(match => match.Value))
            .ToHashSet(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"REJECT_[A-Z0-9_]+", RegexOptions.CultureInvariant)]
    private static partial Regex RejectCodePattern();
}
