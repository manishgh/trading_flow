using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Audit;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Audit;
using TradingFlow.Web.Pages;

namespace TradingFlow.Tests;

public sealed class AuditPagingTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly DbContextOptions<TradingFlowDbContext> options;
    private readonly SqliteDecisionAuditRepository repository;

    public AuditPagingTests()
    {
        connection.Open();
        options = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(connection).Options;
        using var context = new TradingFlowDbContext(options);
        context.Database.EnsureCreated();
        repository = new(new Factory(options));
    }

    [Fact]
    public async Task HundredThousandDecisions_PageAndSummaryStayIndependent()
    {
        await SeedAsync(100_003);
        var summary = await repository.SummarizeAsync("large", default);
        Assert.Equal(100_003, summary.Total);
        Assert.Equal(25, summary.ReasonCount);
        Assert.Equal(20, summary.TopReasons.Count);
        var page = await repository.QueryAsync("large", new(Sort: "id", Descending: false, Skip: 99_950, Take: 50), default);
        Assert.Equal(100_003, page.TotalMatching);
        Assert.Equal(50, page.Records.Count);
        Assert.Equal(99_951L, page.Records[0].Id);
        Assert.Equal(100_000L, page.Records[^1].Id);

        var model = new AuditModel(repository) { PageNumber = Int32.MaxValue, PageSize = Int32.MaxValue };
        await model.OnGetAsync("large", null);
        Assert.Equal(200, model.PageSize);
        Assert.Equal(501, model.PageNumber);
        Assert.Equal(3, model.Records.Count);
        Assert.Equal(100_003, model.Summary.Total);
        Assert.Equal(summary.Total - summary.Accepted, model.Funnel.Sum(x => x.Removed));
        Assert.Contains(model.Funnel, x => x.Label.StartsWith("Other rejections"));
        Assert.Contains(model.Funnel, x => x.Label.StartsWith("Other decisions"));
    }

    [Fact]
    public async Task SortingUsesInstantAndIdTies_FiltersCannotInjectSql()
    {
        using var context = new TradingFlowDbContext(options);
        context.DecisionAudits.AddRange(
            Record("AAA", "2026-10-05T11:00:00+02:00"),
            Record("aaa", "2026-10-05T08:30:00Z"),
            Record("AAA", "2026-10-05T09:00:00Z"));
        await context.SaveChangesAsync();
        var page = await repository.QueryAsync("large", new(Decision: " rejected ", Ticker: "Aa", Reason: " rvol ", Take: 2), default);
        Assert.Equal(3, page.TotalMatching);
        Assert.Equal(new long[] { 3, 1 }, page.Records.Select(x => x.Id));
        page = await repository.QueryAsync("large", new(Sort: "ticker", Descending: false, Skip: 1, Take: 2), default);
        Assert.Equal(new long[] { 2, 3 }, page.Records.Select(x => x.Id));
        page = await repository.QueryAsync("large", new(Ticker: "' OR 1=1 --", Sort: "Ticker; DROP TABLE DecisionAudits", Take: 9999), default);
        Assert.Empty(page.Records);
        Assert.Equal(3, (await repository.SummarizeAsync("large", default)).Total);
        Assert.Empty((await repository.QueryAsync("large' OR 1=1 --", new(), default)).Records);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("csv")]
    public async Task ExportSpansPages_HonorsFiltersAndPinnedMembership(string format)
    {
        await SeedAsync(1_003);
        var model = new AuditModel(repository) { Decision = "Rejected", Reason = "reason_1" };
        var export = await model.OnGetExportAsync("large", default, format);
        var expected = await repository.QueryAsync("large", new(Decision: "Rejected", Reason: "reason_1"), default);
        await repository.SaveAuditAsync(Record("LATE", "2026-10-05T12:00:00Z") with { RejectionReason = "reason_1" }, default);
        using var output = new MemoryStream();
        var http = new DefaultHttpContext(); http.Response.Body = output;
        await export.ExecuteResultAsync(new ActionContext { HttpContext = http });
        output.Position = 0;
        using var reader = new StreamReader(output);
        var text = await reader.ReadToEndAsync();
        Assert.DoesNotContain("LATE", text);
        if (format == "json") {
            using var document = JsonDocument.Parse(text);
            Assert.Equal(expected.TotalMatching, document.RootElement.GetArrayLength());
        } else Assert.Equal(expected.TotalMatching + 1, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        // A separate unfiltered export must cross both 500-row boundaries.
        model = new AuditModel(repository);
        export = await model.OnGetExportAsync("large", default, format);
        using var full = new MemoryStream(); http.Response.Body = full;
        await export.ExecuteResultAsync(new ActionContext { HttpContext = http });
        full.Position = 0;
        using var fullReader = new StreamReader(full);
        var all = await fullReader.ReadToEndAsync();
        if (format == "json") {
            using var document = JsonDocument.Parse(all);
            Assert.Equal(1_004, document.RootElement.GetArrayLength());
        } else {
            Assert.Equal(1_005, all.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Contains("\"'=FORMULA\"", all);
        }
    }

    private async Task SeedAsync(int count)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO DecisionAudits (RunName,Ticker,StrategyName,Timestamp,Decision,RejectionReason,SignalJson) VALUES ('large',$ticker,'Swing','2026-10-05 09:00:00+00:00',$decision,$reason,'{}')";
        var ticker = command.Parameters.Add("$ticker", SqliteType.Text);
        var decision = command.Parameters.Add("$decision", SqliteType.Text);
        var reason = command.Parameters.Add("$reason", SqliteType.Text);
        command.Prepare();
        for (var i = 0; i < count; i++) {
            ticker.Value = i == 0 ? "=FORMULA" : $"T{i % 10}";
            decision.Value = i % 4 == 0 ? "Accepted" : i % 4 == 1 ? "NoSignal" : "Rejected";
            reason.Value = $"reason_{i % 25} (Actual: 1, Required: 2)";
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }

    private static DecisionAuditRecord Record(string ticker, string timestamp) => new()
    {
        RunName = "large", Ticker = ticker, StrategyName = "Swing", Timestamp = DateTimeOffset.Parse(timestamp),
        Decision = "Rejected", RejectionReason = "rvol (Actual: 0.1, Required: 1)", SignalJson = "{}"
    };
    private sealed class Factory(DbContextOptions<TradingFlowDbContext> options) : IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);
    }
    public void Dispose() => connection.Dispose();
}
