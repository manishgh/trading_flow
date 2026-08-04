using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingFlow.Data.Catalysts;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Services.Workflows;

public sealed class IntradayCatalystExecutionService : BackgroundService
{
    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<IntradayCatalystExecutionService> logger;
    private readonly TimeSpan pollInterval = TimeSpan.FromSeconds(30);
    private readonly HashSet<string> processedEvents = new();

    public IntradayCatalystExecutionService(
        IServiceProvider serviceProvider,
        ILogger<IntradayCatalystExecutionService> logger)
    {
        this.serviceProvider = serviceProvider;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting Intraday Catalyst Execution Streamer...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunIterationAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during Catalyst Streamer iteration.");
            }

            await Task.Delay(pollInterval, stoppingToken);
        }
    }

    private async Task RunIterationAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TradingFlowDbContext>>();
        var catalystStreamer = scope.ServiceProvider.GetRequiredService<CatalystStreamer>();
        var orderSubmission = scope.ServiceProvider.GetRequiredService<IOrderSubmissionService>();
        var brokerClient = scope.ServiceProvider.GetRequiredService<IBrokerClient>();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        
        var wishlist = await db.Wishlists
            .Include(w => w.Items)
            .FirstOrDefaultAsync(w => w.Name == "Active Intraday Candidates", cancellationToken);

        if (wishlist == null || !wishlist.Items.Any())
        {
            return;
        }

        var windowStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var windowEnd = DateTimeOffset.UtcNow;

        foreach (var item in wishlist.Items)
        {
            var catalysts = await catalystStreamer.LoadTickerCatalystsAsync(item.Ticker, windowStart, windowEnd, cancellationToken);
            
            foreach (var catalyst in catalysts)
            {
                var eventKey = $"{catalyst.Ticker}_{catalyst.Timestamp.ToUnixTimeSeconds()}_{catalyst.Type}";
                
                if (processedEvents.Contains(eventKey))
                    continue;
                    
                processedEvents.Add(eventKey);

                if (IsTriggerEvent(catalyst))
                {
                    logger.LogInformation("Catalyst Execution Triggered for {Ticker}! Reason: {Type} - {Headline}", 
                        catalyst.Ticker, catalyst.Type, catalyst.Headline);

                    var finalizedOrder = new FinalizedOrder(
                        Ticker: catalyst.Ticker,
                        StrategyName: "IntradayCatalystExecution",
                        ShareQuantity: 10,
                        LimitPrice: 0m,
                        StopLossPrice: 0m,
                        TakeProfitPrice: 0m,
                        ExecutionTimestamp: DateTimeOffset.UtcNow
                    );

                    var runContext = ExecutionRunContextFactory.Create(
                        Guid.NewGuid(), 
                        "paper", 
                        new { Ticker = catalyst.Ticker, CatalystType = catalyst.Type }, 
                        DateTimeOffset.UtcNow
                    );

                    var submission = new BracketOrderSubmission(
                        IntentId: OrderIntentIdFactory.Create(Guid.NewGuid(), "IntradayCatalystExecution", "buy", catalyst.Ticker, DateTimeOffset.UtcNow),
                        Candidate: new ValidatedEntryCandidate(Guid.NewGuid(), "IntradayWatchlist", "1d", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "{}"),
                        RunContext: runContext,
                        StrategyId: "IntradayCatalystExecution",
                        Side: "buy",
                        OrderType: "market",
                        TimeInForce: "day",
                        SessionDate: DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime),
                        CreatedAtUtc: DateTimeOffset.UtcNow,
                        Order: finalizedOrder,
                        AllowExtendedHoursTrading: false
                    );

                    await orderSubmission.SubmitBracketOrderAsync(submission, brokerClient, cancellationToken);
                }
            }
        }
    }

    private static bool IsTriggerEvent(CatalystEvent catalyst)
    {
        if (catalyst.SentimentScore <= 0)
            return false;

        return catalyst.Type switch
        {
            CatalystType.NewsReport => true,
            CatalystType.EarningsRelease => true,
            CatalystType.RegulatoryFiling => true,
            _ => false
        };
    }
}
