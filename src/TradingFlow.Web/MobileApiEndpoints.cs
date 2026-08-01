using TradingFlow.Domain.Strategies;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Web.Services.Wishlists;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Web;

public static class MobileApiEndpoints
{
    public static IEndpointRouteBuilder MapTradingFlowMobileApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/mobile");

        group.MapGet("/catalog", (ConfigCatalogService catalog) =>
        {
            return Results.Ok(new MobileCatalogResponse(
                catalog.GetBacktestConfigs().Select(ToMobileRunConfig).ToArray(),
                catalog.GetPaperConfigs().Select(ToMobileRunConfig).ToArray(),
                catalog.GetStrategies().Select(ToMobileStrategy).ToArray()));
        });


        group.MapGet("/wishlists", async (
            IWishlistRepository wishlists,
            CancellationToken cancellationToken) =>
        {
            var items = await wishlists.ListAsync(cancellationToken);
            return Results.Ok(items.Select(ToMobileWishlist).ToArray());
        });

        group.MapGet("/wishlists/{wishlistId:guid}", async (
            Guid wishlistId,
            IWishlistRepository wishlists,
            CancellationToken cancellationToken) =>
        {
            var wishlist = await wishlists.GetByIdAsync(wishlistId, cancellationToken);
            return wishlist is null ? Results.NotFound() : Results.Ok(ToMobileWishlist(wishlist));
        });

        group.MapGet("/wishlists/{wishlistId:guid}/desk", async (
            Guid wishlistId,
            string? feed,
            int? signalMinutes,
            int? newsHours,
            IWishlistRepository wishlists,
            WishlistDeskService desk,
            CancellationToken cancellationToken) =>
        {
            var wishlist = await wishlists.GetByIdAsync(wishlistId, cancellationToken);
            if (wishlist is null)
            {
                return Results.NotFound();
            }

            var snapshot = await desk.BuildAsync(
                wishlist,
                String.IsNullOrWhiteSpace(feed) ? "sip" : feed,
                TimeSpan.FromMinutes(Math.Clamp(signalMinutes ?? 20, 5, 240)),
                TimeSpan.FromHours(Math.Clamp(newsHours ?? 4, 1, 24)),
                cancellationToken);

            return Results.Ok(ToMobileWishlistDesk(wishlist, snapshot));
        });

        group.MapGet("/wishlists/{wishlistId:guid}/symbols/{ticker}/intelligence", async (
            Guid wishlistId,
            string ticker,
            string? mode,
            string? horizon,
            string? feed,
            IWishlistRepository wishlists,
            WishlistDeskService desk,
            SymbolIntelligenceService intelligence,
            CancellationToken cancellationToken) =>
        {
            var wishlist = await wishlists.GetByIdAsync(wishlistId, cancellationToken);
            if (wishlist is null)
            {
                return Results.NotFound();
            }

            string predictionMode;
            try
            {
                predictionMode = MarketPredictorHttpClient.NormalizeMode(mode ?? "unified");
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return Results.BadRequest(exception.Message);
            }

            var normalizedTicker = ticker.Trim().ToUpperInvariant();
            var snapshot = await desk.BuildAsync(
                wishlist,
                String.IsNullOrWhiteSpace(feed) ? "sip" : feed,
                TimeSpan.FromMinutes(20),
                TimeSpan.FromHours(4),
                cancellationToken);
            var row = snapshot.Rows.FirstOrDefault(candidate =>
                candidate.Ticker.Equals(normalizedTicker, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                return Results.NotFound($"Ticker {normalizedTicker} is not active in wishlist {wishlist.Name}.");
            }

            var response = await intelligence.BuildAsync(
                row,
                predictionMode,
                String.IsNullOrWhiteSpace(horizon) ? "auto" : horizon,
                cancellationToken);
            return Results.Ok(response);
        });

        group.MapPost("/orders/preview", async (
            MobileOrderPreviewRequest request,
            ManualOrderTicketService tickets,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var preview = await tickets.PreviewAsync(
                    new ManualOrderDraft(
                        request.Ticker,
                        "buy",
                        request.Quantity,
                        request.LimitPrice,
                        request.StopLossPrice,
                        request.TakeProfitPrice,
                        request.Horizon,
                        request.AllowExtendedHoursTrading),
                    cancellationToken);
                return Results.Ok(preview);
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(exception.Message);
            }
        });

        group.MapPost("/orders/confirm", async (
            MobileOrderConfirmRequest request,
            ManualOrderTicketService tickets,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await tickets.ConfirmAsync(request.TicketToken, cancellationToken));
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(exception.Message);
            }
        });

        group.MapPost("/wishlists", async (
            MobileWishlistSaveRequest request,
            IWishlistRepository wishlists,
            CancellationToken cancellationToken) =>
        {
            if (String.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest("Wishlist name is required.");
            }

            var saved = await wishlists.SaveAsync(new Wishlist
            {
                Id = request.Id ?? Guid.Empty,
                Name = request.Name,
                Description = request.Description,
                IsDefault = request.IsDefault ?? false,
                IncludeExtendedHours = request.IncludeExtendedHours ?? true,
                IsObserved = request.IsObserved ?? false
            }, cancellationToken);
            var loaded = await wishlists.GetByIdAsync(saved.Id, cancellationToken) ?? saved;
            return Results.Ok(ToMobileWishlist(loaded));
        });

        group.MapPost("/wishlists/{wishlistId:guid}/observe", async (
            Guid wishlistId,
            MobileWishlistObserveRequest request,
            IWishlistRepository wishlists,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await wishlists.SetObservedAsync(wishlistId, request.IsObserved, cancellationToken);
                var updated = await wishlists.GetByIdAsync(wishlistId, cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(ToMobileWishlist(updated));
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(exception.Message);
            }
        });

        group.MapDelete("/wishlists/{wishlistId:guid}", async (
            Guid wishlistId,
            IWishlistRepository wishlists,
            CancellationToken cancellationToken) =>
        {
            await wishlists.DeleteAsync(wishlistId, cancellationToken);
            return Results.NoContent();
        });

        group.MapPost("/wishlists/{wishlistId:guid}/items", async (
            Guid wishlistId,
            MobileWishlistItemRequest request,
            IWishlistRepository wishlists,
            CancellationToken cancellationToken) =>
        {
            if (String.IsNullOrWhiteSpace(request.Ticker))
            {
                return Results.BadRequest("Ticker is required.");
            }

            try
            {
                var item = await wishlists.AddOrUpdateItemAsync(wishlistId, request.Ticker, request.DisplayName, request.Notes, cancellationToken);
                return Results.Ok(ToMobileWishlistItem(item));
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(ex.Message);
            }
        });

        group.MapDelete("/wishlists/{wishlistId:guid}/items/{ticker}", async (
            Guid wishlistId,
            string ticker,
            IWishlistRepository wishlists,
            CancellationToken cancellationToken) =>
        {
            await wishlists.RemoveItemAsync(wishlistId, ticker, cancellationToken);
            return Results.NoContent();
        });


        group.MapPost("/wishlists/{wishlistId:guid}/monitor/evaluate", async (
            Guid wishlistId,
            MobileWishlistMonitorRequest request,
            WishlistMarketMonitor monitor,
            CancellationToken cancellationToken) =>
        {
            var snapshots = request.Snapshots
                .Where(snapshot => !String.IsNullOrWhiteSpace(snapshot.Ticker))
                .Select(ToWishlistMarketSnapshot)
                .ToDictionary(snapshot => snapshot.Ticker, StringComparer.OrdinalIgnoreCase);
            var result = await monitor.EvaluateAndPersistAlertsAsync(wishlistId, snapshots, cancellationToken);
            return Results.Ok(new MobileWishlistMonitorResponse(
                result.Evaluations.Select(ToMobileWishlistEvaluation).ToArray(),
                result.PersistedSignals.Select(ToMobileWishlistSignal).ToArray()));
        });
        group.MapGet("/wishlists/signals", async (
            Guid? wishlistId,
            string? ticker,
            int? hours,
            int? limit,
            IWishlistRepository wishlists,
            CancellationToken cancellationToken) =>
        {
            var since = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(Math.Clamp(hours ?? 24, 1, 168)));
            var signals = await wishlists.GetSignalsAsync(wishlistId, ticker, since, limit ?? 100, cancellationToken);
            return Results.Ok(signals.Select(ToMobileWishlistSignal).ToArray());
        });

        group.MapPost("/wishlists/signals/{signalId:guid}/ack", async (
            Guid signalId,
            IWishlistRepository wishlists,
            CancellationToken cancellationToken) =>
        {
            await wishlists.AcknowledgeSignalAsync(signalId, cancellationToken);
            return Results.Accepted($"/api/mobile/wishlists/signals/{signalId}");
        });
        group.MapGet("/paper/jobs", (PaperJobService paperJobs) =>
            Results.Ok(paperJobs.List()));

        group.MapGet("/paper/jobs/{jobId:guid}", (Guid jobId, PaperJobService paperJobs) =>
        {
            var job = paperJobs.Get(jobId);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        group.MapPost("/paper/runs", async (
            MobilePaperRunRequest request,
            RunConfigWriter configWriter,
            PaperJobService paperJobs,
            WishlistUniverseResolver universeResolver,
            IWishlistRepository wishlists,
            CancellationToken cancellationToken) =>
        {
            var wishlist = request.WishlistId is { } wishlistId
                ? await wishlists.GetByIdAsync(wishlistId, cancellationToken)
                : null;
            if (request.WishlistId is not null && wishlist is null)
            {
                return Results.NotFound($"Wishlist {request.WishlistId.Value} does not exist.");
            }

            IReadOnlyList<string> tickers;
            try
            {
                tickers = await universeResolver.ResolveAsync(request.Tickers, request.WishlistId, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(ex.Message);
            }

            if (tickers.Count == 0 && String.IsNullOrWhiteSpace(request.ScreenerFilter))
            {
                return Results.BadRequest("Provide at least one ticker, wishlist, or Finviz screener filter.");
            }

            var runName = String.IsNullOrWhiteSpace(request.RunName)
                ? $"paper_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}"
                : request.RunName.Trim();
            string configPath;
            try
            {
                configPath = configWriter.SaveTempConfig(
                    request.BaseConfigPath,
                    tickers,
                    request.StrategyPath,
                    request.OrderExpiration,
                    request.EntryOrderType,
                    request.AllowExtendedHoursTrading,
                    request.ScreenerFilter,
                    runName,
                    request.NewsEnabled,
                    wishlist?.Id,
                    wishlist?.Name,
                    wishlist is null ? "ephemeral" : "wishlist");
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(exception.Message);
            }

            return Results.Ok(paperJobs.Start(runName, configPath));
        });

        group.MapPost("/paper/jobs/{jobId:guid}/cancel", (Guid jobId, PaperJobService paperJobs) =>
        {
            paperJobs.CancelJob(jobId);
            return Results.Accepted($"/api/mobile/paper/jobs/{jobId}");
        });

        group.MapPost("/paper/jobs/{jobId:guid}/cancel-broker-orders", async (Guid jobId, PaperJobService paperJobs) =>
        {
            var cancelled = await paperJobs.CancelAllBrokerOrdersAsync(jobId);
            return cancelled ? Results.Ok() : Results.BadRequest("No matching broker orders were cancelled.");
        });

        group.MapGet("/paper/jobs/{jobId:guid}/positions", async (Guid jobId, PaperJobService paperJobs) =>
        {
            var positions = await paperJobs.GetOpenPositionsAsync(jobId);
            return Results.Ok(positions.Select(ToMobilePaperPosition).ToArray());
        });

        group.MapPost("/paper/jobs/{jobId:guid}/positions/{ticker}/close", async (Guid jobId, string ticker, PaperJobService paperJobs) =>
        {
            var closed = await paperJobs.ClosePositionAsync(jobId, ticker);
            return closed ? Results.Ok() : Results.BadRequest($"Could not close {ticker} for this run.");
        });

        group.MapGet("/automation/sessions", (MobileAutomationService automation) =>
            Results.Ok(automation.List()));

        group.MapGet("/automation/sessions/{sessionId:guid}", (Guid sessionId, MobileAutomationService automation) =>
        {
            var session = automation.Get(sessionId);
            return session is null ? Results.NotFound() : Results.Ok(session);
        });

        group.MapPost("/automation/entry", async (
            MobileAutomationStartRequest request,
            RunConfigWriter configWriter,
            MobileAutomationService automation,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Ticker))
            {
                return Results.BadRequest("Ticker is required.");
            }

            string generatedConfigPath;
            try
            {
                generatedConfigPath = configWriter.SaveTempConfig(
                    request.ConfigPath,
                    [request.Ticker.Trim().ToUpperInvariant()],
                    request.StrategyPath,
                    orderExpiration: "day",
                    entryOrderType: request.EntryOrderType,
                    allowExtendedHoursTrading: request.AllowExtendedHoursTrading,
                    screenerFilter: string.Empty,
                    runName: string.IsNullOrWhiteSpace(request.RunName)
                        ? $"mobile_auto_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}"
                        : request.RunName.Trim(),
                    newsEnabled: false);
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(exception.Message);
            }

            var started = await automation.StartAsync(
                request with { ConfigPath = generatedConfigPath },
                cancellationToken);
            return Results.Ok(started);
        });

        group.MapPost("/automation/sessions/{sessionId:guid}/cancel", (Guid sessionId, MobileAutomationService automation) =>
        {
            automation.Cancel(sessionId);
            return Results.Accepted($"/api/mobile/automation/sessions/{sessionId}");
        });

        group.MapPost("/automation/sessions/{sessionId:guid}/close", async (Guid sessionId, MobileAutomationService automation) =>
        {
            var closed = await automation.CloseAsync(sessionId);
            return closed ? Results.Ok() : Results.BadRequest("Could not close this automation position.");
        });

        group.MapGet("/running-trades", async (
            string? source,
            PaperJobService paperJobs,
            MobileAutomationService automation) =>
        {
            var trades = await RunningTradesBuilder.BuildAsync(paperJobs, automation);
            var filtered = RunningTradesBuilder.Filter(trades, source);
            return Results.Ok(new MobileRunningTradesResponse(
                filtered,
                filtered.Sum(trade => trade.UnrealizedPl),
                filtered.Count));
        });

        group.MapGet("/orders", async (
            string? state,
            int? limit,
            TradingFlow.Domain.Persistence.IOrderActivityQuery orders,
            CancellationToken cancellationToken) =>
        {
            TradingFlow.Domain.Orders.OrderState? selectedState = null;
            if (!String.IsNullOrWhiteSpace(state))
            {
                if (!Enum.TryParse<TradingFlow.Domain.Orders.OrderState>(state, true, out var parsedState))
                {
                    return Results.BadRequest($"Unknown order state '{state}'.");
                }

                selectedState = parsedState;
            }

            var snapshots = await orders.ListRecentAsync(selectedState, limit ?? 200, cancellationToken);
            return Results.Ok(snapshots.Select(snapshot => new MobileOrderActivityResponse(
                snapshot.RunId,
                snapshot.ClientOrderId,
                snapshot.BrokerOrderId,
                snapshot.StrategyId,
                snapshot.Symbol,
                snapshot.Side,
                snapshot.OrderType,
                snapshot.TimeInForce,
                snapshot.RequestedQuantity,
                snapshot.LimitPrice,
                snapshot.StopPrice,
                snapshot.State.ToString(),
                snapshot.CreatedAtUtc,
                snapshot.UpdatedAtUtc,
                snapshot.FilledQuantity,
                snapshot.FillPrice,
                snapshot.EventSource)));
        });

        group.MapGet("/backtests/jobs", (BacktestJobService backtestJobs) =>
            Results.Ok(backtestJobs.List()));

        group.MapGet("/backtests/jobs/{jobId:guid}", (Guid jobId, BacktestJobService backtestJobs) =>
        {
            var job = backtestJobs.Get(jobId);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        group.MapPost("/backtests/runs", async (
            MobileBacktestRunRequest request,
            RunConfigWriter configWriter,
            BacktestJobService backtestJobs,
            IWishlistRepository wishlists,
            CancellationToken cancellationToken) =>
        {
            var strategyPaths = request.StrategyPaths
                .Where(path => !String.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (strategyPaths.Length == 0)
            {
                return Results.BadRequest("Select at least one strategy.");
            }

            var runName = String.IsNullOrWhiteSpace(request.RunName)
                ? $"backtest_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}"
                : request.RunName.Trim();
            if (request.WishlistId is null)
            {
                return Results.BadRequest("Select a wishlist for the backtest universe.");
            }

            var wishlist = await wishlists.GetByIdAsync(request.WishlistId.Value, cancellationToken);
            if (wishlist is null)
            {
                return Results.NotFound($"Wishlist {request.WishlistId.Value} does not exist.");
            }

            var tickers = wishlist.Items
                .Where(item => item.Active)
                .Select(item => item.Ticker.Trim().ToUpperInvariant())
                .Where(ticker => ticker.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (tickers.Length == 0)
            {
                return Results.BadRequest($"Wishlist {wishlist.Name} has no active tickers.");
            }

            var configPath = configWriter.WriteBacktestConfig(new BacktestRunRequest(
                request.BaseConfigPath,
                runName,
                request.LookbackDays,
                wishlist.Id,
                wishlist.Name,
                tickers,
                strategyPaths,
                request.StartingCapital,
                request.AccountRiskBudgetPct,
                request.MaxPositionNotionalPct,
                request.MaxConcurrentPositions,
                request.CachePolicy,
                Array.Empty<StrategyParameterOverride>()));
            return Results.Ok(backtestJobs.Start(runName, configPath));
        });

        group.MapPost("/backtests/jobs/{jobId:guid}/cancel", (Guid jobId, BacktestJobService backtestJobs) =>
        {
            return backtestJobs.CancelJob(jobId) switch
            {
                BacktestCancellationOutcome.Accepted => Results.Accepted($"/api/mobile/backtests/jobs/{jobId}"),
                BacktestCancellationOutcome.NotFound => Results.NotFound(),
                _ => Results.Conflict("Backtest is already finished.")
            };
        });

        group.MapGet("/notifications", (
            PaperJobService paperJobs,
            BacktestJobService backtestJobs,
            MobileAutomationService automation) =>
        {
            var cutoff = DateTimeOffset.UtcNow.Subtract(MobileAutomationSessionStore.RetentionWindow);
            var notifications = paperJobs.List()
                .Concat(backtestJobs.List())
                .SelectMany(ToNotifications)
                .Concat(automation.List().SelectMany(ToNotifications))
                .Where(item => item.Timestamp >= cutoff)
                .OrderByDescending(item => item.Timestamp)
                .Take(80)
                .ToArray();
            return Results.Ok(notifications);
        });

        group.MapGet("/news/latest", async (
            string configPath,
            string? tickers,
            int? hours,
            NewsFeedService newsFeed,
            CancellationToken cancellationToken) =>
        {
            var symbols = (tickers ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ticker => ticker.ToUpperInvariant())
                .ToArray();
            var feed = await newsFeed.GetLatestAsync(configPath, symbols, hours ?? 24, cancellationToken);
            return Results.Ok(feed);
        });

        group.MapGet("/news/feed", async (
            string? ticker,
            int? hours,
            NewsFeedService newsFeed,
            CancellationToken cancellationToken) =>
        {
            var feed = await newsFeed.GetRollingAsync(hours ?? 4, ticker, cancellationToken);
            return Results.Ok(feed);
        });

        group.MapPost("/news/refresh", async (
            NewsFeedService newsFeed,
            CancellationToken cancellationToken) =>
        {
            await newsFeed.RefreshOnceAsync(cancellationToken);
            return Results.Accepted("/api/mobile/news/feed");
        });

        group.MapGet("/warmup/watchlist", async (
            WarmupServiceClient warmup,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await warmup.GetWatchlistAsync(cancellationToken) ?? Array.Empty<WarmupTickerIntentDto>());
        });

        group.MapPost("/warmup/watchlist", async (
            WarmupWatchRequestDto request,
            WarmupServiceClient warmup,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await warmup.AddWatchlistAsync(request, cancellationToken));
        });

        group.MapDelete("/warmup/watchlist/{ticker}", async (
            string ticker,
            WarmupServiceClient warmup,
            CancellationToken cancellationToken) =>
        {
            await warmup.RemoveAsync(ticker, cancellationToken);
            return Results.NoContent();
        });

        group.MapPost("/warmup/run-now", async (
            WarmupRunNowRequestDto request,
            WarmupServiceClient warmup,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await warmup.RunNowAsync(request, cancellationToken));
        });

        group.MapGet("/warmup/runs", async (
            WarmupServiceClient warmup,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await warmup.GetRunsAsync(cancellationToken) ?? Array.Empty<WarmupRunRecordDto>());
        });

        return endpoints;
    }



    private static WishlistMarketSnapshot ToWishlistMarketSnapshot(MobileWishlistMonitorSnapshotRequest request)
    {
        var ticker = request.Ticker.Trim().ToUpperInvariant();
        var timestamp = request.Timestamp ?? DateTimeOffset.UtcNow;
        var current = new IndicatorSnapshot(
            ticker,
            timestamp,
            String.IsNullOrWhiteSpace(request.Timeframe) ? "1m" : request.Timeframe.Trim(),
            request.CurrentPrice,
            request.CurrentVolume,
            request.Vwap,
            Rsi: null,
            request.Atr,
            request.Ema20,
            Ema50: null,
            Ema200: null,
            BollingerMiddle: null,
            BollingerUpper: null,
            BollingerLower: null,
            RelativeVolume: request.SessionRelativeVolume,
            MacdLine: request.MacdHistogram,
            MacdSignal: 0m,
            MacdHistogram: request.MacdHistogram,
            Catalyst: BuildRequestCatalyst(request, ticker, timestamp),
            Ema10: request.Ema10,
            SessionRelativeVolume: request.SessionRelativeVolume);
        IndicatorSnapshot? previous = null;
        if (request.PreviousMacdHistogram is not null || request.PreviousVolume is not null)
        {
            previous = current with
            {
                Timestamp = timestamp.AddMinutes(-1),
                CurrentVolume = request.PreviousVolume ?? request.CurrentVolume,
                MacdHistogram = request.PreviousMacdHistogram
            };
        }

        return new WishlistMarketSnapshot(ticker, current, previous, request.RecentHigh, request.SessionOpen);
    }

    private static MobileWishlistMonitorEvaluationResponse ToMobileWishlistEvaluation(WishlistBreakoutEvaluation evaluation)
    {
        return new MobileWishlistMonitorEvaluationResponse(
            evaluation.Ticker,
            evaluation.ShouldAlert,
            evaluation.SignalType,
            evaluation.Severity,
            evaluation.Price,
            evaluation.Reason,
            evaluation.Score,
            evaluation.SessionGainPct,
            evaluation.SessionRelativeVolume,
            evaluation.VwapExtensionAtr,
            evaluation.NewsHeadline,
            evaluation.NewsUrl,
            evaluation.NewsProvider);
    }

    private static CatalystEvent? BuildRequestCatalyst(
        MobileWishlistMonitorSnapshotRequest request,
        string ticker,
        DateTimeOffset timestamp)
    {
        if (String.IsNullOrWhiteSpace(request.NewsHeadline) && String.IsNullOrWhiteSpace(request.NewsUrl))
        {
            return null;
        }

        return new CatalystEvent(
            ticker,
            timestamp,
            CatalystType.NewsReport,
            String.IsNullOrWhiteSpace(request.NewsHeadline) ? "Matched news catalyst" : request.NewsHeadline.Trim(),
            request.NewsSentiment ?? 0m,
            Provider: String.IsNullOrWhiteSpace(request.NewsProvider) ? "news" : request.NewsProvider.Trim(),
            Url: String.IsNullOrWhiteSpace(request.NewsUrl) ? null : request.NewsUrl.Trim());
    }
    private static MobileWishlistResponse ToMobileWishlist(Wishlist wishlist)
    {
        return new MobileWishlistResponse(
            wishlist.Id,
            wishlist.Name,
            wishlist.Description,
            wishlist.IsDefault,
            wishlist.IncludeExtendedHours,
            wishlist.IsObserved,
            wishlist.CreatedAtUtc,
            wishlist.UpdatedAtUtc,
            wishlist.Items
                .OrderByDescending(item => item.Active)
                .ThenBy(item => item.Ticker)
                .Select(ToMobileWishlistItem)
                .ToArray());
    }

    private static MobileWishlistItemResponse ToMobileWishlistItem(WishlistItem item)
    {
        return new MobileWishlistItemResponse(
            item.Id,
            item.WishlistId,
            item.Ticker,
            item.DisplayName,
            item.Notes,
            item.Active,
            item.AddedAtUtc);
    }

    private static MobileWishlistSignalResponse ToMobileWishlistSignal(WishlistSignal signal)
    {
        return new MobileWishlistSignalResponse(
            signal.Id,
            signal.WishlistId,
            signal.Ticker,
            signal.SignalType,
            signal.Severity,
            signal.DetectedAtUtc,
            signal.Price,
            signal.Reason,
            signal.SnapshotJson,
            signal.NewsHeadline,
            signal.NewsUrl,
            signal.NewsProvider,
            signal.Acknowledged);
    }

    private static MobileWishlistDeskResponse ToMobileWishlistDesk(Wishlist wishlist, WishlistDeskSnapshot snapshot)
    {
        return new MobileWishlistDeskResponse(
            ToMobileWishlist(wishlist),
            snapshot.Rows.Select(ToMobileWishlistDeskRow).ToArray(),
            snapshot.RecentSignals.Select(ToMobileWishlistSignal).ToArray(),
            snapshot.RelatedNews,
            snapshot.RunningTrades,
            snapshot.TotalPl);
    }

    private static MobileWishlistDeskRowResponse ToMobileWishlistDeskRow(WishlistDeskRow row)
    {
        return new MobileWishlistDeskRowResponse(
            ToMobileWishlistItem(row.Item),
            row.Ticker,
            row.DisplayName,
            row.Quote.BidPrice,
            row.Quote.AskPrice,
            row.Quote.MidPrice,
            row.Quote.DisplayBid,
            row.Quote.DisplayAsk,
            row.Quote.DisplayPrice,
            row.Quote.BuyCaption,
            row.Quote.SellCaption,
            row.Quote.Timestamp,
            row.HasQuote,
            row.HasTrade,
            row.HasSignal,
            row.HasNews,
            row.EligibilityLabel,
            row.EligibilityReason,
            row.LatestSignal is null ? null : ToMobileWishlistSignal(row.LatestSignal),
            row.LatestNews,
            row.Trade);
    }

    private static MobilePaperPositionResponse ToMobilePaperPosition(BrokerPosition position)
    {
        return new MobilePaperPositionResponse(
            position.Ticker,
            position.Side,
            position.Qty,
            position.EntryPrice,
            position.CurrentPrice,
            position.UnrealizedPl);
    }

    private static MobileRunConfigOption ToMobileRunConfig(RunConfigSummary summary)
    {
        return new MobileRunConfigOption(
            summary.Path,
            summary.FileName,
            summary.Config.RunName,
            summary.Config.Mode,
            summary.Config.Provider,
            summary.Config.Tickers,
            summary.Config.Intervals);
    }

    private static MobileStrategyOption ToMobileStrategy(StrategyOption option)
    {
        var strategy = option.Definition;
        return new MobileStrategyOption(
            option.Path,
            option.FileName,
            strategy.StrategyId,
            strategy.StrategyName,
            strategy.Version,
            strategy.Direction,
            strategy.Timeframe,
            strategy.Execution.Timeframe,
            strategy.EntryRules.SetupType,
            UsesNews(strategy),
            option.Audit?.ReturnPct,
            option.Audit?.MaxDrawdownPct,
            option.Audit?.Trades,
            option.Audit?.WinRatePct,
            option.Audit?.AverageHold,
            option.Audit?.ResultPath);
    }

    private static bool UsesNews(StrategyDefinition strategy)
    {
        return strategy.EntryRules.RequirePositiveNews ||
            strategy.EntryRules.MinNewsSentiment is not null ||
            strategy.EntryRules.VetoNewsSentimentBelow is not null ||
            strategy.EntryRules.SetupType.Contains("news", StringComparison.OrdinalIgnoreCase) ||
            strategy.EntryRules.SetupType.Contains("catalyst", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<MobileNotificationItem> ToNotifications(BacktestJobSnapshot job)
    {
        if (job.Status is "failed")
        {
            yield return new MobileNotificationItem(
                job.FinishedAt ?? DateTimeOffset.UtcNow,
                "error",
                $"{job.RunName} failed",
                job.ErrorMessage ?? "The run failed.",
                job.RunName,
                job.JobId);
        }
        else if (job.Status is "completed")
        {
            yield return new MobileNotificationItem(
                job.FinishedAt ?? DateTimeOffset.UtcNow,
                "success",
                $"{job.RunName} completed",
                job.Result is null ? "Run completed." : $"Net P/L {job.Result.NetProfit:C2}, return {job.Result.TotalReturnPct:F2}%.",
                job.RunName,
                job.JobId);
        }
        else if (job.Status is "running")
        {
            yield return new MobileNotificationItem(
                job.StartedAt ?? job.CreatedAt,
                "info",
                $"{job.RunName} running",
                $"{job.CurrentStage}: {job.CompletedTickerCount}/{job.TotalTickerCount} ticker pipelines.",
                job.RunName,
                job.JobId);
        }
    }

    private static IEnumerable<MobileNotificationItem> ToNotifications(MobileAutomationSessionSnapshot session)
    {
        if (session.Status is "failed")
        {
            yield return new MobileNotificationItem(
                session.FinishedAt ?? DateTimeOffset.UtcNow,
                "error",
                $"{session.Ticker} automation failed",
                session.ErrorMessage ?? "Automation session failed.",
                session.RunName,
                session.SessionId);
            yield break;
        }

        if (session.Status is "completed")
        {
            yield return new MobileNotificationItem(
                session.FinishedAt ?? DateTimeOffset.UtcNow,
                "success",
                $"{session.Ticker} automation completed",
                session.ExitReason is null
                    ? "Position closed."
                    : $"Exited on {session.ExitReason}.",
                session.RunName,
                session.SessionId);
            yield break;
        }

        if (session.Status is "running" or "starting" or "queued")
        {
            yield return new MobileNotificationItem(
                session.StartedAt ?? session.CreatedAt,
                "info",
                $"{session.Ticker} automation {session.Status}",
                session.Events.LastOrDefault() ?? session.CurrentStage,
                session.RunName,
                session.SessionId);
        }
    }
}
