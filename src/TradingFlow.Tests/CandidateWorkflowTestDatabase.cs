using Microsoft.EntityFrameworkCore;
using TradingFlow.Application.Candidates;
using TradingFlow.Data.Application;
using TradingFlow.Data.Context;
using TradingFlow.Data.Discovery;
using TradingFlow.Data.Orders;

namespace TradingFlow.Tests;

internal sealed class CandidateWorkflowTestDatabase : IAsyncDisposable, IDbContextFactory<TradingFlowDbContext>
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tradingflow-candidates-{Guid.NewGuid():N}");
    private readonly DbContextOptions<TradingFlowDbContext> options;
    public CandidateWorkflowTestDatabase()
    {
        Directory.CreateDirectory(root);
        options = new DbContextOptionsBuilder<TradingFlowDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "workflow.db")};Pooling=False;Default Timeout=15")
            .Options;
    }
    public TradingFlowDbContext CreateDbContext() => new(options);
    public async Task InitializeAsync()
    {
        await using var context = CreateDbContext();
        await context.Database.EnsureCreatedAsync();
    }
    public SqliteApplicationWorkflowRepository Workflows => new(this);
    public SqliteCandidateRepository Candidates => new(this);
    public SqliteDiscoveryRepository Discovery => new(this);
    public CandidateApplicationService Queries(TimeProvider clock) => new(
        Candidates, new SqliteGateEvaluationRepository(this), new SqliteOrderIntentRepository(this),
        new SqliteCandidateAuditEvidenceRepository(this), Workflows, clock, Workflows);
    public ValueTask DisposeAsync()
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(root));
        if (!String.Equals(parent, Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test database cleanup escaped the temporary directory.");
        Directory.Delete(root, recursive: true);
        return ValueTask.CompletedTask;
    }
}

internal sealed class CandidateWorkflowTestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
