using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Orders;
using TradingFlow.Engine.Execution;
using TradingFlow.Finviz;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Services.Workflows;

public sealed class SwingDailyJob
{
    private readonly FinvizClient finvizClient;
    private readonly MarketPredictorHttpClient predictorClient;
    private readonly IOrderSubmissionService orderSubmissionService;
    private readonly IBrokerClient brokerClient;
    private readonly ILogger<SwingDailyJob> logger;
    private readonly TimeProvider timeProvider;

    public SwingDailyJob(
        FinvizClient finvizClient,
        MarketPredictorHttpClient predictorClient,
        IOrderSubmissionService orderSubmissionService,
        IBrokerClient brokerClient,
        TimeProvider timeProvider,
        ILogger<SwingDailyJob> logger)
    {
        this.finvizClient = finvizClient;
        this.predictorClient = predictorClient;
        this.orderSubmissionService = orderSubmissionService;
        this.brokerClient = brokerClient;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task RunDailySwingPipelineAsync(string finvizQuery, CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting Daily Swing Pipeline with query: {Query}", finvizQuery);

        // 1. Fetch baseline candidates
        var tickerSource = new FinvizScreenerTickerSource(finvizClient, finvizQuery);
        var candidates = await tickerSource.GetTickersAsync(cancellationToken);
        
        logger.LogInformation("Finviz returned {Count} candidates.", candidates.Count);
        if (candidates.Count == 0) return;

        // 2. Query ML predictions
        var scoredCandidates = new List<(string Ticker, MarketPredictorResult Result)>();
        foreach (var ticker in candidates)
        {
            try
            {
                var result = await predictorClient.GetAsync(ticker, "swing", "auto", cancellationToken);
                if (result.IsValidPaperEvidence && result.Swing != null && result.Swing.Probability.HasValue)
                {
                    scoredCandidates.Add((ticker, result));
                }
                else
                {
                    logger.LogDebug("Ticker {Ticker} skipped: Not valid evidence.", ticker);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch prediction for {Ticker}", ticker);
            }
        }

        // 3. Rank and take Top 4
        var top4 = scoredCandidates
            .OrderByDescending(c => c.Result.Swing!.Probability!.Value)
            .Take(4)
            .ToList();

        logger.LogInformation("Selected Top {Count} candidates for swing execution.", top4.Count);

        // 4. Submit to Execution Engine
        foreach (var selection in top4)
        {
            var ticker = selection.Ticker;
            var swingData = selection.Result.Swing!;

            logger.LogInformation("Submitting swing order for {Ticker} with ML Score {Score}", ticker, swingData.Probability);

            var price = 100m; 
            var atr = 2.0m; 
            
            var limitPrice = price; 
            var stopPrice = price - (atr * 1.5m);
            var targetPrice = price + (atr * 3.0m);
            var quantity = 10; 

            var finalizedOrder = new FinalizedOrder(
                Ticker: ticker,
                StrategyName: "AutomatedSwing",
                ShareQuantity: quantity,
                LimitPrice: limitPrice,
                StopLossPrice: stopPrice,
                TakeProfitPrice: targetPrice,
                ExecutionTimestamp: timeProvider.GetUtcNow()
            );

            var candidateId = Guid.NewGuid();
            var runContext = ExecutionRunContextFactory.Create(
                Guid.NewGuid(), 
                "paper", 
                new { Job = "SwingDailyJob" }, 
                timeProvider.GetUtcNow());

            var submission = new BracketOrderSubmission(
                IntentId: OrderIntentIdFactory.Create(Guid.NewGuid(), "SwingAutomated", "buy", ticker, timeProvider.GetUtcNow()),
                Candidate: new ValidatedEntryCandidate(candidateId, "Finviz", "10d", timeProvider.GetUtcNow(), timeProvider.GetUtcNow(), "{}"),
                RunContext: runContext,
                StrategyId: "AutomatedSwing",
                Side: "buy",
                OrderType: "limit",
                TimeInForce: "day",
                SessionDate: DateOnly.FromDateTime(timeProvider.GetUtcNow().DateTime),
                CreatedAtUtc: timeProvider.GetUtcNow(),
                Order: finalizedOrder,
                AllowExtendedHoursTrading: false
            );

            try
            {
                var receipt = await orderSubmissionService.SubmitBracketOrderAsync(submission, brokerClient, cancellationToken);
                logger.LogInformation("Order submitted for {Ticker}. Broker ID: {BrokerId}", ticker, receipt.BrokerOrderId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to submit order for {Ticker}", ticker);
            }
        }
    }
}
