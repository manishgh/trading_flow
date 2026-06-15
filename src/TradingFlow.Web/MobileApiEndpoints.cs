using TradingFlow.Domain.Strategies;
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

        group.MapGet("/paper/jobs", (PaperJobService paperJobs) =>
            Results.Ok(paperJobs.List()));

        group.MapGet("/paper/jobs/{jobId:guid}", (Guid jobId, PaperJobService paperJobs) =>
        {
            var job = paperJobs.Get(jobId);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        group.MapPost("/paper/runs", (
            MobilePaperRunRequest request,
            RunConfigWriter configWriter,
            PaperJobService paperJobs) =>
        {
            var tickers = request.Tickers
                .Select(ticker => ticker.Trim().ToUpperInvariant())
                .Where(ticker => ticker.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (tickers.Length == 0 && String.IsNullOrWhiteSpace(request.ScreenerFilter))
            {
                return Results.BadRequest("Provide at least one ticker or a Finviz screener filter.");
            }

            var runName = String.IsNullOrWhiteSpace(request.RunName)
                ? $"paper_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}"
                : request.RunName.Trim();
            var configPath = configWriter.SaveTempConfig(
                request.BaseConfigPath,
                tickers,
                request.StrategyPath,
                request.OrderExpiration,
                request.EntryOrderType,
                request.ExtendedHours,
                request.ScreenerFilter,
                runName,
                request.NewsEnabled);
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

            var generatedConfigPath = configWriter.SaveTempConfig(
                request.ConfigPath,
                [request.Ticker.Trim().ToUpperInvariant()],
                request.StrategyPath,
                orderExpiration: "day",
                entryOrderType: "market",
                extendedHours: true,
                screenerFilter: string.Empty,
                runName: string.IsNullOrWhiteSpace(request.RunName)
                    ? $"mobile_auto_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}"
                    : request.RunName.Trim(),
                newsEnabled: false);

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

        group.MapGet("/backtests/jobs", (BacktestJobService backtestJobs) =>
            Results.Ok(backtestJobs.List()));

        group.MapGet("/backtests/jobs/{jobId:guid}", (Guid jobId, BacktestJobService backtestJobs) =>
        {
            var job = backtestJobs.Get(jobId);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        group.MapPost("/backtests/runs", (
            MobileBacktestRunRequest request,
            RunConfigWriter configWriter,
            BacktestJobService backtestJobs) =>
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
            var configPath = configWriter.WriteBacktestConfig(new BacktestRunRequest(
                request.BaseConfigPath,
                runName,
                request.LookbackDays,
                request.Tickers,
                strategyPaths,
                request.StartingCapital,
                request.RiskPerTradePct,
                request.MaxPositionValuePct,
                request.MaxConcurrentPositions,
                request.CachePolicy,
                Array.Empty<StrategyParameterOverride>()));
            return Results.Ok(backtestJobs.Start(runName, configPath));
        });

        group.MapPost("/backtests/jobs/{jobId:guid}/cancel", (Guid jobId, BacktestJobService backtestJobs) =>
        {
            backtestJobs.CancelJob(jobId);
            return Results.Accepted($"/api/mobile/backtests/jobs/{jobId}");
        });

        group.MapGet("/notifications", (
            PaperJobService paperJobs,
            BacktestJobService backtestJobs,
            MobileAutomationService automation) =>
        {
            var notifications = paperJobs.List()
                .Concat(backtestJobs.List())
                .SelectMany(ToNotifications)
                .Concat(automation.List().SelectMany(ToNotifications))
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
            UsesNews(strategy));
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
