using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Backups;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class SqliteBackupRestoreTests
{
    [Fact]
    public async Task BackupThenRestore_PreservesPointInTimeSnapshotAndManifest()
    {
        await using var fixture = new BackupFixture();
        await fixture.InitializeAsync();
        await fixture.AddRunAsync("before-backup");
        var operationalDate = new DateOnly(2026, 7, 21);

        var first = await fixture.BackupService.CreateDailyBackupAsync(operationalDate);
        await fixture.AddRunAsync("after-backup");
        var second = await fixture.BackupService.CreateDailyBackupAsync(operationalDate);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.BackupPath, second.BackupPath);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.True(File.Exists(first.ManifestPath));

        var restoredPath = Path.Combine(fixture.Root, "restore", "tradingflow.db");
        var restore = await new SqliteDatabaseRestoreService()
            .RestoreToEmptyAsync(first.BackupPath, restoredPath);
        await using var restoredContext = new TradingFlowDbContext(
            BackupFixture.CreateOptions(restoredPath, withDurabilityInterceptor: false));
        var profiles = await restoredContext.ProductionRuns
            .AsNoTracking()
            .Select(run => run.Profile)
            .ToArrayAsync();

        Assert.Equal(first.Sha256, restore.Sha256);
        Assert.Equal(["before-backup"], profiles);
    }

    [Fact]
    public async Task RestoreToEmptyAsync_ExistingDestination_IsRejectedWithoutModification()
    {
        await using var fixture = new BackupFixture();
        await fixture.InitializeAsync();
        var backup = await fixture.BackupService.CreateDailyBackupAsync(new DateOnly(2026, 7, 21));
        var destination = Path.Combine(fixture.Root, "occupied.db");
        await File.WriteAllTextAsync(destination, "operator-data");

        await Assert.ThrowsAsync<IOException>(
            () => new SqliteDatabaseRestoreService().RestoreToEmptyAsync(backup.BackupPath, destination));

        Assert.Equal("operator-data", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task RestoreToEmptyAsync_MissingManifest_FailsClosed()
    {
        await using var fixture = new BackupFixture();
        await fixture.InitializeAsync();
        var backup = await fixture.BackupService.CreateDailyBackupAsync(new DateOnly(2026, 7, 21));
        File.Delete(backup.ManifestPath);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new SqliteDatabaseRestoreService().RestoreToEmptyAsync(
                backup.BackupPath,
                Path.Combine(fixture.Root, "restore.db")));
    }

    [Fact]
    public async Task CreateDailyBackupAsync_ExistingMismatchedManifest_FailsClosedWithoutRewriting()
    {
        await using var fixture = new BackupFixture();
        await fixture.InitializeAsync();
        var operationalDate = new DateOnly(2026, 7, 21);
        var backup = await fixture.BackupService.CreateDailyBackupAsync(operationalDate);
        var mismatchedManifest = (await File.ReadAllTextAsync(backup.ManifestPath))
            .Replace(backup.Sha256, new string('0', 64), StringComparison.Ordinal);
        await File.WriteAllTextAsync(backup.ManifestPath, mismatchedManifest);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.BackupService.CreateDailyBackupAsync(operationalDate));

        Assert.Equal(mismatchedManifest, await File.ReadAllTextAsync(backup.ManifestPath));
    }

    [Fact]
    public void BackupSchedule_UsesCompletedNewYorkOperationalDate()
    {
        var schedule = new DatabaseBackupSchedule(new TimeOnly(20, 30), ResolveNewYork());

        Assert.Equal(
            new DateOnly(2026, 7, 20),
            schedule.GetOperationalDate(new DateTimeOffset(2026, 7, 21, 23, 0, 0, TimeSpan.Zero)));
        Assert.Equal(
            new DateOnly(2026, 7, 21),
            schedule.GetOperationalDate(new DateTimeOffset(2026, 7, 22, 0, 31, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void BackupSchedule_RecomputesUtcOffsetAcrossDst()
    {
        var schedule = new DatabaseBackupSchedule(new TimeOnly(20, 30), ResolveNewYork());

        Assert.Equal(
            new DateTimeOffset(2026, 7, 22, 0, 30, 0, TimeSpan.Zero),
            schedule.GetNextRun(new DateTimeOffset(2026, 7, 21, 22, 0, 0, TimeSpan.Zero)));
        Assert.Equal(
            new DateTimeOffset(2026, 1, 16, 1, 30, 0, TimeSpan.Zero),
            schedule.GetNextRun(new DateTimeOffset(2026, 1, 15, 22, 0, 0, TimeSpan.Zero)));
    }

    private static TimeZoneInfo ResolveNewYork()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
        }

        throw new TimeZoneNotFoundException("New York timezone is unavailable on the test host.");
    }

    private sealed class BackupFixture : IAsyncDisposable
    {
        private readonly string databasePath;

        public BackupFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"tradingflow-backup-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            databasePath = Path.Combine(Root, "source", "tradingflow.db");
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            BackupService = new SqliteDatabaseBackupService(
                databasePath,
                Path.Combine(Root, "backups"),
                AtomicFileArtifactWriter.Instance,
                new FixedTimeProvider(new DateTimeOffset(2026, 7, 22, 0, 45, 0, TimeSpan.Zero)));
        }

        public string Root { get; }

        public SqliteDatabaseBackupService BackupService { get; }

        public async Task InitializeAsync()
        {
            await using var context = new TradingFlowDbContext(
                CreateOptions(databasePath, withDurabilityInterceptor: true));
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        public async Task AddRunAsync(string profile)
        {
            await using var context = new TradingFlowDbContext(
                CreateOptions(databasePath, withDurabilityInterceptor: true));
            context.ProductionRuns.Add(new ProductionRun
            {
                RunId = Guid.NewGuid(),
                SchemaVersion = 1,
                ConfigHash = new string('c', 64),
                CodeVersion = "backup-test",
                Profile = profile,
                Status = "complete",
                StartedAtUtc = DateTimeOffset.UtcNow,
                FinishedAtUtc = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
        }

        public static DbContextOptions<TradingFlowDbContext> CreateOptions(
            string path,
            bool withDurabilityInterceptor)
        {
            var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false
            }.ToString();
            var builder = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(connectionString);
            if (withDurabilityInterceptor)
            {
                builder.AddInterceptors(new SqliteConnectionDurabilityInterceptor());
            }

            return builder.Options;
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
