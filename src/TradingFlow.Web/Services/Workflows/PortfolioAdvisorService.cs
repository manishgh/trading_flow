using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Portfolio;

namespace TradingFlow.Web.Services.Workflows;

public sealed class PortfolioAdvisorService : BackgroundService
{
    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<PortfolioAdvisorService> logger;
    private readonly TimeSpan pollInterval = TimeSpan.FromHours(1);

    public PortfolioAdvisorService(
        IServiceProvider serviceProvider,
        ILogger<PortfolioAdvisorService> logger)
    {
        this.serviceProvider = serviceProvider;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting Portfolio Advisor Service...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunIterationAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during Portfolio Advisor iteration.");
            }

            await Task.Delay(pollInterval, stoppingToken);
        }
    }

    private async Task RunIterationAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TradingFlowDbContext>>();
        var predictorClient = scope.ServiceProvider.GetRequiredService<MarketPredictorHttpClient>();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        
        var positions = await db.AdvisoryPositions.ToListAsync(cancellationToken);

        if (!positions.Any())
        {
            logger.LogInformation("No Advisory Positions found to analyze.");
            return;
        }

        foreach (var position in positions)
        {
            logger.LogInformation("Analyzing Long-Term Position: {Ticker} ({Shares} shares @ {Price})", 
                position.Ticker, position.Shares, position.PurchasePrice);

            try
            {
                var result = await predictorClient.GetAsync(position.Ticker, "swing", "auto", cancellationToken);
                var probability = result.Swing?.Probability;

                if (probability.HasValue)
                {
                    position.LastMlScore = probability.Value;
                    position.LastScoredAtUtc = DateTimeOffset.UtcNow;
                    
                    if (probability.Value < 0.45m)
                    {
                        position.LastRecommendation = "Sell";
                        logger.LogWarning("ADVISORY ALERT: Position {Ticker} has a weak swing ML score of {Score}. Recommendation: SELL.", 
                            position.Ticker, probability.Value);
                    }
                    else
                    {
                        position.LastRecommendation = "Hold";
                        logger.LogInformation("Position {Ticker} ML Score is {Score}. Recommendation: HOLD.", 
                            position.Ticker, probability.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to analyze position {Ticker}", position.Ticker);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
