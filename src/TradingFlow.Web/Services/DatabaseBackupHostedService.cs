using TradingFlow.Data.Backups;

namespace TradingFlow.Web.Services;

internal sealed class DatabaseBackupSchedule
{
    public DatabaseBackupSchedule(TimeOnly localRunTime, TimeZoneInfo marketTimeZone)
    {
        LocalRunTime = localRunTime;
        MarketTimeZone = marketTimeZone ?? throw new ArgumentNullException(nameof(marketTimeZone));
    }

    public TimeOnly LocalRunTime { get; }

    public TimeZoneInfo MarketTimeZone { get; }

    public DateOnly GetOperationalDate(DateTimeOffset utcNow)
    {
        var marketNow = TimeZoneInfo.ConvertTime(utcNow, MarketTimeZone);
        var marketDate = DateOnly.FromDateTime(marketNow.DateTime);
        return utcNow >= ToUtc(marketDate) ? marketDate : marketDate.AddDays(-1);
    }

    public DateTimeOffset GetNextRun(DateTimeOffset utcNow)
    {
        var marketNow = TimeZoneInfo.ConvertTime(utcNow, MarketTimeZone);
        var marketDate = DateOnly.FromDateTime(marketNow.DateTime);
        var todayRun = ToUtc(marketDate);
        return todayRun > utcNow ? todayRun : ToUtc(marketDate.AddDays(1));
    }

    private DateTimeOffset ToUtc(DateOnly marketDate)
    {
        var local = DateTime.SpecifyKind(marketDate.ToDateTime(LocalRunTime), DateTimeKind.Unspecified);
        while (MarketTimeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        var offset = MarketTimeZone.IsAmbiguousTime(local)
            ? MarketTimeZone.GetAmbiguousTimeOffsets(local).Min()
            : MarketTimeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}

/// <summary>
/// Produces one retained backup after each New York extended-hours session.
/// </summary>
internal sealed class DatabaseBackupHostedService(
    SqliteDatabaseBackupService backupService,
    DatabaseBackupSchedule schedule,
    TimeProvider timeProvider,
    ILogger<DatabaseBackupHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            TimeSpan delay;
            try
            {
                var operationalDate = schedule.GetOperationalDate(now);
                var result = await backupService.CreateDailyBackupAsync(operationalDate, stoppingToken);
                logger.LogInformation(
                    "SQLite backup {BackupState} for operational date {OperationalDate} at {BackupPath}; sha256={Sha256}, size={SizeBytes}.",
                    result.Created ? "created" : "verified",
                    result.OperationalDate,
                    result.BackupPath,
                    result.Sha256,
                    result.SizeBytes);
                var completedAt = timeProvider.GetUtcNow();
                delay = schedule.GetNextRun(completedAt) - completedAt;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogCritical(exception, "SQLite daily backup failed; retrying in {RetryDelay}.", RetryDelay);
                delay = RetryDelay;
            }

            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
        }
    }
}
