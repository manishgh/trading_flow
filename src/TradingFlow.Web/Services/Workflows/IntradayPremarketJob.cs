using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Finviz;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Services.Workflows;

public sealed class IntradayPremarketJob
{
    private readonly IDbContextFactory<TradingFlowDbContext> dbFactory;
    private readonly FinvizClient finvizClient;
    private readonly StockPulseReceiverService pulseReceiver;
    private readonly MarketPredictorHttpClient predictorClient;
    private readonly ILogger<IntradayPremarketJob> logger;

    public IntradayPremarketJob(
        IDbContextFactory<TradingFlowDbContext> dbFactory,
        FinvizClient finvizClient,
        StockPulseReceiverService pulseReceiver,
        MarketPredictorHttpClient predictorClient,
        ILogger<IntradayPremarketJob> logger)
    {
        this.dbFactory = dbFactory;
        this.finvizClient = finvizClient;
        this.pulseReceiver = pulseReceiver;
        this.predictorClient = predictorClient;
        this.logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting Intraday Premarket Sourcing Job...");
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Get mobile pulses
        var pulses = pulseReceiver.ConsumePulses();
        foreach (var p in pulses) candidates.Add(p);
        logger.LogInformation("Added {Count} candidates from Mobile Stock Pulse.", pulses.Count);

        // 2. Get from verified Intraday Screener Presets
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var presets = await db.ScreenerPresets
            .Where(p => p.Category.ToLower() == "intraday" && p.IsVerified)
            .ToListAsync(cancellationToken);

        foreach (var preset in presets)
        {
            var tickerSource = new FinvizScreenerTickerSource(finvizClient, preset.FilterQuery);
            var finvizTickers = await tickerSource.GetTickersAsync(cancellationToken);
            foreach (var t in finvizTickers) candidates.Add(t);
        }
        
        logger.LogInformation("Total candidates after Finviz presets: {Count}", candidates.Count);

        if (candidates.Count == 0)
        {
            logger.LogWarning("No candidates sourced. Exiting Intraday Job.");
            return;
        }

        // 3. Score using ML Engine
        var scoredCandidates = new List<(string Ticker, decimal Score)>();
        foreach (var ticker in candidates)
        {
            try
            {
                var result = await predictorClient.GetAsync(ticker, "intraday", "auto", cancellationToken);
                if (result.Intraday?.OpportunityProbability != null)
                {
                    scoredCandidates.Add((ticker, result.Intraday.OpportunityProbability.Value));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to score candidate {Ticker}", ticker);
            }
        }

        // 4. Select top candidates
        var topCandidates = scoredCandidates
            .OrderByDescending(x => x.Score)
            .Take(10)
            .ToList();
            
        logger.LogInformation("Top {Count} candidates selected for intraday monitoring.", topCandidates.Count);

        // 5. Update Intraday Watchlist
        var wishlist = await db.Wishlists
            .Include(w => w.Items)
            .FirstOrDefaultAsync(w => w.Name == "Active Intraday Candidates", cancellationToken);
            
        if (wishlist == null)
        {
            wishlist = new Wishlist
            {
                Id = Guid.NewGuid(),
                Name = "Active Intraday Candidates",
                IsDefault = false,
                IsObserved = true,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            db.Wishlists.Add(wishlist);
        }
        
        wishlist.Items.Clear();
        foreach (var tc in topCandidates)
        {
            wishlist.Items.Add(new WishlistItem
            {
                Id = Guid.NewGuid(),
                WishlistId = wishlist.Id,
                Ticker = tc.Ticker,
                Notes = $"Intraday ML Score: {tc.Score:F4}"
            });
        }
        
        wishlist.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        
        logger.LogInformation("Intraday Premarket Job completed.");
    }
}
