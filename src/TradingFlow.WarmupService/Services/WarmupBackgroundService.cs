using Microsoft.Extensions.Options;
using TradingFlow.WarmupService.Models;

namespace TradingFlow.WarmupService.Services;

public sealed class WarmupBackgroundService(
    WarmupJobQueue queue,
    WarmupCoordinator coordinator,
    IOptions<WarmupOptions> options,
    ILogger<WarmupBackgroundService> logger) : BackgroundService
{
    private readonly WarmupOptions options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scheduled = options.RunNightlyScheduler
            ? RunNightlyLoopAsync(stoppingToken)
            : Task.CompletedTask;
        var queued = RunQueueLoopAsync(stoppingToken);
        await Task.WhenAll(scheduled, queued);
    }

    private async Task RunQueueLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await coordinator.RunAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Warmup job {RunId} failed at service level.", job.RunId);
            }
        }
    }

    private async Task RunNightlyLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = ResolveDelayToNextNightlyRun();
            logger.LogInformation("Next nightly warmup in {Delay}.", delay);
            await Task.Delay(delay, stoppingToken);
            await queue.EnqueueAsync(
                new WarmupJobRequest(
                    Guid.NewGuid().ToString("N"),
                    "nightly-scheduled-warmup",
                    null,
                    DateTimeOffset.UtcNow),
                stoppingToken);
        }
    }

    private TimeSpan ResolveDelayToNextNightlyRun()
    {
        var zone = ResolveTimeZone(options.MarketTimeZone);
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var targetTime = TimeOnly.Parse(options.NightlyRunLocalTime, System.Globalization.CultureInfo.InvariantCulture);
        var targetLocal = new DateTimeOffset(
            localNow.Year,
            localNow.Month,
            localNow.Day,
            targetTime.Hour,
            targetTime.Minute,
            0,
            localNow.Offset);
        if (targetLocal <= localNow)
        {
            targetLocal = targetLocal.AddDays(1);
        }

        return targetLocal.ToUniversalTime() - DateTimeOffset.UtcNow;
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        foreach (var candidate in new[] { id, "America/New_York", "Eastern Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(candidate);
            }
            catch
            {
            }
        }

        return TimeZoneInfo.Utc;
    }
}
