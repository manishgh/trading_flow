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

    /// <summary>Runs the existing nightly producer and one durable local queue consumer under host cancellation.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scheduled = options.RunNightlyScheduler
            ? RunNightlyLoopAsync(stoppingToken)
            : Task.CompletedTask;
        var queued = RunQueueLoopAsync(stoppingToken);
        await Task.WhenAll(scheduled, queued);
    }

    /// <summary>Uses persisted claim/outcome state so restart resumes accepted work and cancellation drains providers.</summary>
    private async Task RunQueueLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await queue.ExecuteAsync(job, async token => { await coordinator.RunAsync(job, token); }, stoppingToken);
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

    /// <summary>Enqueues nightly requests through the same bounded acceptance path as manual work.</summary>
    private async Task RunNightlyLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = ResolveDelayToNextNightlyRun();
            logger.LogInformation("Next nightly warmup in {Delay}.", delay);
            await Task.Delay(delay, stoppingToken);
            await EnqueueNightlyAsync(stoppingToken);
        }
    }

    /// <summary>Logs expected work/capacity rejections and returns to the existing loop so the next nightly attempt still runs.</summary>
    public async Task<bool> EnqueueNightlyAsync(CancellationToken token)
    {
        try
        {
            await queue.EnqueueAsync(new WarmupJobRequest(Guid.NewGuid().ToString("N"), "nightly-scheduled-warmup", null,
                DateTimeOffset.UtcNow), token);
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(error, "Nightly warmup was not accepted; queue capacity, stock selection or configured work limits must be reviewed.");
            return false;
        }
    }

    /// <summary>Calculates the next configured local schedule rather than introducing an additional scheduler.</summary>
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

    /// <summary>Resolves the configured exchange timezone across Windows/IANA installations.</summary>
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
