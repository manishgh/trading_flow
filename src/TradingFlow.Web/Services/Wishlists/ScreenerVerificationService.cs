using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Finviz;

namespace TradingFlow.Web.Services.Wishlists;

public sealed class ScreenerVerificationService
{
    private readonly IDbContextFactory<TradingFlowDbContext> dbFactory;
    private readonly FinvizClient finvizClient;
    private readonly MarketPredictorHttpClient predictorClient;
    private readonly ILogger<ScreenerVerificationService> logger;

    public ScreenerVerificationService(
        IDbContextFactory<TradingFlowDbContext> dbFactory,
        FinvizClient finvizClient,
        MarketPredictorHttpClient predictorClient,
        ILogger<ScreenerVerificationService> logger)
    {
        this.dbFactory = dbFactory;
        this.finvizClient = finvizClient;
        this.predictorClient = predictorClient;
        this.logger = logger;
    }

    public async Task<bool> VerifyPresetAsync(Guid presetId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var preset = await db.ScreenerPresets.FindAsync(new object[] { presetId }, cancellationToken);
        
        if (preset == null)
        {
            logger.LogWarning("Preset {PresetId} not found.", presetId);
            return false;
        }

        logger.LogInformation("Verifying Screener Preset: {Name} [{Category}]", preset.Name, preset.Category);

        var tickerSource = new FinvizScreenerTickerSource(finvizClient, preset.FilterQuery);
        var candidates = await tickerSource.GetTickersAsync(cancellationToken);
        
        if (candidates.Count == 0)
        {
            logger.LogWarning("Preset {Name} returned 0 candidates. Verification failed.", preset.Name);
            preset.IsVerified = false;
            preset.AverageMlScore = 0;
            preset.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        var scores = new List<decimal>();
        foreach (var ticker in candidates.Take(20)) // Verify against top 20 max to save time
        {
            try
            {
                var result = await predictorClient.GetAsync(ticker, preset.Category.ToLowerInvariant(), "auto", cancellationToken);
                
                var probability = preset.Category.Equals("swing", StringComparison.OrdinalIgnoreCase) 
                    ? result.Swing?.Probability 
                    : result.Intraday?.OpportunityProbability;

                if (probability.HasValue)
                {
                    scores.Add(probability.Value);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to get ML score for {Ticker} during verification.", ticker);
            }
        }

        if (scores.Count == 0)
        {
            logger.LogWarning("No ML scores available for Preset {Name}. Verification failed.", preset.Name);
            preset.IsVerified = false;
            preset.AverageMlScore = 0;
            preset.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        var averageScore = scores.Average();
        preset.AverageMlScore = averageScore;
        
        // Let us define a threshold for a "good filter" (e.g. > 0.55 Average Probability)
        preset.IsVerified = averageScore >= 0.55m;
        preset.UpdatedAtUtc = DateTimeOffset.UtcNow;
        
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Preset {Name} verification completed. IsVerified: {Verified}, Avg Score: {Score}", preset.Name, preset.IsVerified, averageScore);
        return preset.IsVerified;
    }
}
