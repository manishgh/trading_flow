using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Backtesting.StrategyEvaluation;
using TradingFlow.Data.Backups;
using TradingFlow.Data.Candles;
using TradingFlow.Data.Context;
using TradingFlow.Data.News;
using TradingFlow.Data.Wishlists;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Engine.Execution;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Wishlists;

var cultureInfo = new CultureInfo("en-US");
CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;

var webContentRoot = ResolveWebContentRoot(Directory.GetCurrentDirectory());
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = webContentRoot,
    WebRootPath = Path.Combine(webContentRoot, "wwwroot")
});
var uiTestMode = String.Equals(
    Environment.GetEnvironmentVariable("TRADINGFLOW_UI_TEST_MODE"),
    "true",
    StringComparison.OrdinalIgnoreCase);
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);
}
Environment.SetEnvironmentVariable(
    "TRADINGFLOW_RESULT_OWNER",
    Environment.GetEnvironmentVariable("TRADINGFLOW_RESULT_OWNER") ?? "web");

builder.Services.AddRazorPages();
var repositoryRoot = ResolveRepositoryRoot(builder.Environment.ContentRootPath);
var dataRoot = ResolveRootFromEnvironment("TRADINGFLOW_DATA_ROOT", Path.Combine(repositoryRoot, "data"));
var cacheRoot = ResolveRootFromEnvironment("TRADINGFLOW_CACHE_ROOT", Path.Combine(dataRoot, "cache"));
var backupRoot = ResolveRootFromEnvironment("TRADINGFLOW_BACKUP_ROOT", Path.Combine(dataRoot, "backups"));
builder.Services.AddSingleton(new ProjectPaths(repositoryRoot, dataRoot, cacheRoot));
builder.Services.AddSingleton<SimpleYamlReader>();
builder.Services.AddSingleton<IArtifactWriter>(AtomicFileArtifactWriter.Instance);
builder.Services.AddSingleton(new RawArchiveOptions(Path.Combine(dataRoot, "raw")));
builder.Services.AddSingleton<IRawArchiveWriter, FileSystemRawArchiveWriter>();
builder.Services.AddSingleton<ICandleStore>(sp =>
{
    var paths = sp.GetRequiredService<ProjectPaths>();
    return new LocalFileCandleStore(Path.Combine(paths.CacheRoot, "candles"));
});
builder.Services.AddSingleton<ConfigCatalogService>();
builder.Services.AddSingleton<RunConfigWriter>();
builder.Services.AddSingleton<TradingFlow.Backtesting.BacktestRunner>();
builder.Services.AddSingleton<BacktestJobService>();
builder.Services.AddSingleton<OptimizationJobService>();
builder.Services.AddSingleton<AlpacaCredentialProvider>();
builder.Services.AddSingleton<PaperRuntimeFactory>();
builder.Services.AddSingleton<MobileAutomationSessionStore>();
builder.Services.AddSingleton<PaperEnvironmentService>();
builder.Services.AddSingleton<PaperJobService>();
builder.Services.AddSingleton<MobileAutomationService>();
builder.Services.AddSingleton<SqliteNewsFeedRepository>();
builder.Services.AddSingleton<TradingFlow.Domain.Wishlists.IWishlistRepository, SqliteWishlistRepository>();
builder.Services.AddSingleton<ArticleTextFetcher>();
builder.Services.AddSingleton<NewsFeedService>();
if (!uiTestMode)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<NewsFeedService>());
}
builder.Services.AddSingleton<WarmupServiceClient>();
builder.Services.AddSingleton<WishlistUniverseResolver>();
builder.Services.AddSingleton<WishlistBreakoutEvaluator>();
builder.Services.AddSingleton<WishlistMarketMonitor>();
builder.Services.AddSingleton<WishlistDeskService>();
builder.Services.AddSingleton<OperationalStatusService>();
var predictorBaseUrlText = builder.Configuration["MarketPredictor:BaseUrl"]
    ?? Environment.GetEnvironmentVariable("TRADINGFLOW_MARKET_PREDICTOR_URL");
var predictorBaseUri = Uri.TryCreate(predictorBaseUrlText, UriKind.Absolute, out var configuredPredictorUri)
    ? configuredPredictorUri
    : null;
var predictorTimeoutSeconds = ParsePositiveInteger(
    builder.Configuration["MarketPredictor:RequestTimeoutSeconds"]
        ?? Environment.GetEnvironmentVariable("TRADINGFLOW_MARKET_PREDICTOR_TIMEOUT_SECONDS"),
    5);
var predictorMaximumAgeMinutes = ParsePositiveInteger(
    builder.Configuration["MarketPredictor:MaximumEvidenceAgeMinutes"]
        ?? Environment.GetEnvironmentVariable("TRADINGFLOW_MARKET_PREDICTOR_MAX_AGE_MINUTES"),
    15);
var predictorOptions = new MarketPredictorOptions(
    predictorBaseUri,
    TimeSpan.FromSeconds(predictorTimeoutSeconds),
    TimeSpan.FromMinutes(predictorMaximumAgeMinutes));
builder.Services.AddSingleton(predictorOptions);
builder.Services.AddHttpClient<MarketPredictorHttpClient>(client =>
{
    if (predictorOptions.BaseUri is not null)
    {
        client.BaseAddress = predictorOptions.BaseUri;
    }
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddTransient<SymbolIntelligenceService>();
builder.Services.AddSingleton<WishlistObserverService>();
if (!uiTestMode)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<WishlistObserverService>());
}
builder.Services.AddSingleton<AlpacaQuoteService>();
builder.Services.AddSingleton<AlpacaManualOrderService>();
builder.Services.AddSingleton<StrategyEvaluationService>();

var dbPath = Path.Combine(dataRoot, "tradingflow.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var backupLocalTime = ResolveBackupLocalTime(builder.Configuration["DatabaseBackup:LocalTime"]);
var backupTimeZone = ResolveMarketTimeZone(builder.Configuration["DatabaseBackup:MarketTimeZone"]);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new DatabaseBackupSchedule(backupLocalTime, backupTimeZone));
builder.Services.AddSingleton(serviceProvider => new SqliteDatabaseBackupService(
    dbPath,
    backupRoot,
    serviceProvider.GetRequiredService<IArtifactWriter>(),
    serviceProvider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<SqliteDatabaseRestoreService>();
if (!uiTestMode)
{
    builder.Services.AddHostedService<DatabaseBackupHostedService>();
}
builder.Services.AddSingleton<SqliteConnectionDurabilityInterceptor>();
builder.Services.AddDbContextFactory<TradingFlowDbContext>((serviceProvider, options) =>
    options
        .UseSqlite($"Data Source={dbPath}")
        .AddInterceptors(serviceProvider.GetRequiredService<SqliteConnectionDurabilityInterceptor>()));
builder.Services.AddSingleton<TradingFlowDatabaseInitializer>();

builder.Services.AddSingleton<TradingFlow.Domain.Locking.ITickerLockService, TradingFlow.Data.Locking.SqliteTickerLockService>();
builder.Services.AddSingleton<TradingFlow.Domain.Orders.IOrderStateRepository, TradingFlow.Data.Orders.SqliteOrderStateRepository>();
builder.Services.AddSingleton<TradingFlow.Domain.Persistence.IOrderIntentRepository, TradingFlow.Data.Orders.SqliteOrderIntentRepository>();
builder.Services.AddSingleton<TradingFlow.Domain.Persistence.IOrderEventRepository, TradingFlow.Data.Orders.SqliteOrderEventRepository>();
builder.Services.AddSingleton<TradingFlow.Domain.Persistence.IPositionLedgerRepository, TradingFlow.Data.Orders.SqlitePositionLedgerRepository>();
builder.Services.AddSingleton<TradingFlow.Domain.Persistence.IReconciliationRepository, TradingFlow.Data.Orders.SqliteReconciliationRepository>();
builder.Services.AddSingleton<TradingFlow.Domain.Persistence.ICandidateRepository, TradingFlow.Data.Orders.SqliteCandidateRepository>();
builder.Services.AddSingleton<TradingFlow.Domain.Persistence.IGateEvaluationRepository, TradingFlow.Data.Orders.SqliteGateEvaluationRepository>();
builder.Services.AddSingleton<TradingFlow.Engine.Execution.IOrderLifecycleService, TradingFlow.Engine.Execution.OrderLifecycleService>();
builder.Services.AddSingleton<IEntryAdmissionControl, EntryAdmissionControl>();
var productionConfiguration = new ProductionConfigurationLoader();
T ResolveProductionParameter<T>(string name, string environmentVariable) =>
    productionConfiguration.ResolveParameter<T>(
        ProductionProfile.Paper,
        name,
        builder.Configuration[$"TradingFlow:Production:{name}"]
            ?? Environment.GetEnvironmentVariable(environmentVariable));
builder.Services.AddSingleton(new EntryGateOptions(
    ResolveProductionParameter<int>("setup_max_age_s", "TRADINGFLOW_SETUP_MAX_AGE_S"),
    ResolveProductionParameter<int>("quote_max_age_ms", "TRADINGFLOW_QUOTE_MAX_AGE_MS"),
    ResolveProductionParameter<decimal>("max_spread_bps", "TRADINGFLOW_MAX_SPREAD_BPS"),
    ResolveProductionParameter<decimal>("max_expected_slippage_bps", "TRADINGFLOW_MAX_EXPECTED_SLIPPAGE_BPS"),
    ResolveProductionParameter<decimal>("max_notional_per_trade_pct", "TRADINGFLOW_MAX_NOTIONAL_PER_TRADE_PCT"),
    ResolveProductionParameter<decimal>("max_gross_exposure_intraday_pct", "TRADINGFLOW_MAX_GROSS_EXPOSURE_INTRADAY_PCT"),
    ResolveProductionParameter<decimal>("max_gross_exposure_overnight_pct", "TRADINGFLOW_MAX_GROSS_EXPOSURE_OVERNIGHT_PCT"),
    ResolveProductionParameter<int>("max_positions_day", "TRADINGFLOW_MAX_POSITIONS_DAY"),
    ResolveProductionParameter<int>("max_positions_swing", "TRADINGFLOW_MAX_POSITIONS_SWING")));
builder.Services.AddSingleton(ManualEntryOptions.Parse(
    ResolveProductionParameter<string>("manual_entry_policy", "TRADINGFLOW_MANUAL_ENTRY_POLICY")));
builder.Services.AddSingleton<AlpacaSecurityTradingStatusService>();
builder.Services.AddSingleton<ISecurityTradingStatusProvider>(serviceProvider =>
    serviceProvider.GetRequiredService<AlpacaSecurityTradingStatusService>());
if (!uiTestMode)
{
    builder.Services.AddHostedService(serviceProvider =>
        serviceProvider.GetRequiredService<AlpacaSecurityTradingStatusService>());
}
builder.Services.AddSingleton<IEntryGateChain, EntryGateChain>();
var orderPollIntervalSeconds = productionConfiguration.ResolveParameter<int>(
    ProductionProfile.Paper,
    "order_poll_interval_s",
    builder.Configuration["TradingFlow:Production:order_poll_interval_s"]
        ?? Environment.GetEnvironmentVariable("TRADINGFLOW_ORDER_POLL_INTERVAL_S"));
builder.Services.AddSingleton(new OrderSynchronizationOptions(orderPollIntervalSeconds));
var reconcileIntervalSeconds = productionConfiguration.ResolveParameter<int>(
    ProductionProfile.Paper,
    "reconcile_interval_s",
    builder.Configuration["TradingFlow:Production:reconcile_interval_s"]
        ?? Environment.GetEnvironmentVariable("TRADINGFLOW_RECONCILE_INTERVAL_S"));
var orphanTimeoutSeconds = productionConfiguration.ResolveParameter<int>(
    ProductionProfile.Paper,
    "order_orphan_timeout_s",
    builder.Configuration["TradingFlow:Production:order_orphan_timeout_s"]
        ?? Environment.GetEnvironmentVariable("TRADINGFLOW_ORDER_ORPHAN_TIMEOUT_S"));
var reconciliationOptions = new AccountReconciliationOptions(
    reconcileIntervalSeconds,
    orphanTimeoutSeconds);
builder.Services.AddSingleton(reconciliationOptions);
var backstopAtrMultiple = productionConfiguration.ResolveParameter<decimal>(
    ProductionProfile.Paper,
    "backstop_atr_mult",
    builder.Configuration["TradingFlow:Production:backstop_atr_mult"]
        ?? Environment.GetEnvironmentVariable("TRADINGFLOW_BACKSTOP_ATR_MULT"));
builder.Services.AddSingleton(new ProtectiveOrderOptions(backstopAtrMultiple));
var allowMultiStrategySameSymbol = productionConfiguration.ResolveParameter<bool>(
    ProductionProfile.Paper,
    "allow_multi_strategy_same_symbol",
    builder.Configuration["TradingFlow:Production:allow_multi_strategy_same_symbol"]
        ?? Environment.GetEnvironmentVariable("TRADINGFLOW_ALLOW_MULTI_STRATEGY_SAME_SYMBOL"));
builder.Services.AddSingleton(new PositionConflictOptions(allowMultiStrategySameSymbol));
builder.Services.AddSingleton<IPositionConflictGuard, PositionConflictGuard>();
builder.Services.AddSingleton<TradingFlow.Engine.Indicators.IndicatorEngine>();
builder.Services.AddSingleton(serviceProvider => new Lazy<IMarketDataProvider>(() =>
{
    var credentials = serviceProvider.GetRequiredService<AlpacaCredentialProvider>();
    var options = TradingFlow.Alpaca.AlpacaOptions.Create(ProductionProfile.Paper) with
    {
        KeyId = credentials.KeyId,
        SecretKey = credentials.SecretKey
    };
    return new TradingFlow.Alpaca.AlpacaMarketDataProvider(
        new HttpClient(),
        options,
        serviceProvider.GetRequiredService<ILogger<TradingFlow.Alpaca.AlpacaMarketDataProvider>>());
}));
var synchronizationStartedAt = DateTimeOffset.UtcNow;
var synchronizationRunContext = ExecutionRunContextFactory.Create(
    Guid.NewGuid(),
    "paper",
    new
    {
        orderPollIntervalSeconds,
        reconcileIntervalSeconds,
        orphanTimeoutSeconds
    },
    synchronizationStartedAt);
builder.Services.AddSingleton(new ReconciliationRunContext(new TradingFlow.Domain.Persistence.ProductionRun
{
    RunId = synchronizationRunContext.RunId,
    Profile = synchronizationRunContext.Profile,
    Status = "running",
    StartedAtUtc = synchronizationRunContext.StartedAtUtc,
    ConfigHash = synchronizationRunContext.ConfigHash,
    CodeVersion = synchronizationRunContext.CodeVersion
}));
builder.Services.AddSingleton<IProtectiveOrderInvariantService, ProtectiveOrderInvariantService>();
builder.Services.AddSingleton<IAccountReconciliationService, AccountReconciliationService>();
builder.Services.AddSingleton<IOrderSynchronizationCoordinator, OrderSynchronizationCoordinator>();
builder.Services.AddSingleton<IOrderSubmissionService, OrderSubmissionService>();
if (!uiTestMode)
{
    builder.Services.AddHostedService<AlpacaOrderSynchronizationHostedService>();
}
builder.Services.AddSingleton<TradingFlow.Domain.Audit.IDecisionAuditRepository, TradingFlow.Data.Audit.SqliteDecisionAuditRepository>();
builder.Services.AddSingleton<TradingFlow.Domain.Jobs.IJobRepository, TradingFlow.Data.Jobs.SqliteJobRepository>();

var app = builder.Build();

var apiProfilerLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TradingFlow.ApiProfiler");
TradingFlow.Domain.Logging.ApiProfiler.ConfigureMetricSink(metric =>
{
    var level = metric.IsSuccess ? LogLevel.Information : LogLevel.Warning;
    apiProfilerLogger.Log(
        level,
        "External API request {Service} {Method} {Endpoint} completed in {DurationMs} ms with success={IsSuccess}. Error={ErrorMessage}",
        metric.Service,
        metric.Method,
        metric.Endpoint,
        Math.Round(metric.DurationMs, 2),
        metric.IsSuccess,
        metric.ErrorMessage);
});

using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TradingFlowDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await scope.ServiceProvider.GetRequiredService<TradingFlowDatabaseInitializer>().InitializeAsync(db);
}

if (!uiTestMode)
{
    // Resolve before any resumable job starts so entry submission is fail-closed until
    // the account stream and initial REST cross-check have both completed.
    _ = app.Services.GetRequiredService<IOrderSynchronizationCoordinator>();
    _ = app.Services.GetRequiredService<IAccountReconciliationService>();

    app.Services.GetRequiredService<PaperJobService>().InitializeAsync().GetAwaiter().GetResult();
    app.Services.GetRequiredService<MobileAutomationService>().InitializeAsync().GetAwaiter().GetResult();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.MapTradingFlowMobileApi();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "TradingFlow.Web" }));
app.MapGet("/health/trading-readiness", (
    IOrderSynchronizationCoordinator synchronization,
    IAccountReconciliationService reconciliation,
    IEntryAdmissionControl admission) =>
{
    var synchronizationHealth = synchronization.GetHealth();
    var admissionSnapshot = admission.GetSnapshot();
    var response = new
    {
        status = admissionSnapshot.EntriesAllowed ? "ready" : "blocked",
        entriesAllowed = admissionSnapshot.EntriesAllowed,
        orderSynchronization = synchronizationHealth,
        accountReconciliation = reconciliation.GetHealth(),
        blocks = admissionSnapshot.Blocks
    };
    return admissionSnapshot.EntriesAllowed
        ? Results.Ok(response)
        : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
});
app.MapGet("/api/profiler/alpaca", () => Results.Ok(TradingFlow.Domain.Logging.ApiProfiler.GetSummary("Alpaca")));
app.MapPost("/api/operations/reconciliations/{reconciliationId:guid}/ack", async (
    Guid reconciliationId,
    ReconciliationAcknowledgementRequest request,
    IAccountReconciliationService reconciliation,
    CancellationToken cancellationToken) =>
{
    await reconciliation.AcknowledgeAsync(
        reconciliationId,
        request.Actor,
        request.Reason,
        cancellationToken);
    return Results.Ok(reconciliation.GetHealth());
});
app.MapGet("/api/wishlists/{wishlistId:guid}/quotes/stream", async (
    Guid wishlistId,
    IWishlistRepository wishlists,
    AlpacaQuoteService quoteService,
    ConfigCatalogService catalog,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var wishlist = await wishlists.GetByIdAsync(wishlistId, cancellationToken);
    if (wishlist is null)
    {
        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var tickers = wishlist.Items
        .Where(item => item.Active)
        .Select(item => item.Ticker)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers.Connection = "keep-alive";
    httpContext.Response.Headers["X-Accel-Buffering"] = "no";
    httpContext.Response.ContentType = "text/event-stream";

    var feed = ResolveDefaultQuoteFeed(catalog);
    while (!cancellationToken.IsCancellationRequested)
    {
        var quotes = await GetLiveQuotesWithExtendedFallbackAsync(quoteService, tickers, feed, cancellationToken);
        var payload = quotes.Values
            .OrderBy(quote => quote.Ticker)
            .Select(quote => new
            {
                ticker = quote.Ticker,
                bidPrice = quote.BidPrice,
                askPrice = quote.AskPrice,
                midPrice = quote.MidPrice,
                bidText = quote.DisplayBid,
                askText = quote.DisplayAsk,
                midText = quote.DisplayPrice,
                buyCaption = quote.BuyCaption,
                sellCaption = quote.SellCaption,
                timestamp = quote.Timestamp
            });

        await httpContext.Response.WriteAsync("event: quotes\n", cancellationToken);
        await httpContext.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
    }
});
app.MapGet("/api/wishlists/{wishlistId:guid}/activity/stream", async (
    Guid wishlistId,
    IWishlistRepository wishlists,
    NewsFeedService newsFeed,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var wishlist = await wishlists.GetByIdAsync(wishlistId, cancellationToken);
    if (wishlist is null)
    {
        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var tickerSet = wishlist.Items
        .Where(item => item.Active)
        .Select(item => item.Ticker.Trim().ToUpperInvariant())
        .Where(ticker => ticker.Length > 0)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers.Connection = "keep-alive";
    httpContext.Response.Headers["X-Accel-Buffering"] = "no";
    httpContext.Response.ContentType = "text/event-stream";

    while (!cancellationToken.IsCancellationRequested)
    {
        var signalSince = DateTimeOffset.UtcNow.AddMinutes(-20);
        var newsSince = DateTimeOffset.UtcNow.AddHours(-4);
        var signals = (await wishlists.GetSignalsAsync(wishlistId, null, signalSince, 30, cancellationToken))
            .Select(signal => new
            {
                id = signal.Id,
                ticker = signal.Ticker,
                signalType = signal.SignalType,
                reason = signal.Reason,
                detectedAt = signal.DetectedAtUtc,
                detectedAtText = signal.DetectedAtUtc.ToLocalTime().ToString("dd/MM HH:mm")
            });
        var rollingNews = await newsFeed.GetRollingAsync(4, null, cancellationToken);
        var news = rollingNews.Items
            .Where(item => SplitTickerDisplay(item.Ticker).Any(tickerSet.Contains))
            .Where(item => item.Timestamp >= newsSince)
            .OrderByDescending(item => item.Timestamp)
            .Take(80)
            .Select(item => new
            {
                ticker = item.Ticker,
                headline = item.Headline,
                summary = item.Summary,
                provider = item.Provider,
                source = item.Source,
                url = item.Url,
                timestamp = item.Timestamp,
                timestampText = item.Timestamp.ToLocalTime().ToString("dd/MM HH:mm")
            });

        await httpContext.Response.WriteAsync("event: activity\n", cancellationToken);
        await httpContext.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { signals, news })}\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
    }
});
app.MapPost("/api/strategies/evaluate", async (
    StrategyEvaluationRequest request,
    StrategyEvaluationService evaluator,
    CancellationToken cancellationToken) =>
{
    var result = await evaluator.EvaluateWithAlpacaAsync(request, cancellationToken);
    return Results.Ok(result);
});
app.MapPost("/api/strategies/intraday-top/evaluate", async (
    StrategyEvaluationService evaluator,
    CancellationToken cancellationToken) =>
{
    var result = await evaluator.EvaluateWithAlpacaAsync(
        new StrategyEvaluationRequest(
            ConfigPath: Path.Combine("configs", "paper", "alpaca-paper.yaml"),
            StrategyPath: Path.Combine("configs", "strategies", "intraday-ema10-ema20-macd-volume.v1.yaml"),
            Tickers: new[] { "RGTI", "POET", "NVTS" },
            LookbackDays: 30,
            Start: null,
            End: null),
        cancellationToken);
    return Results.Ok(result);
});
app.Run();

static string ResolveRepositoryRoot(string contentRoot)
{
    var directory = new DirectoryInfo(contentRoot);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TradingFlow.slnx")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? contentRoot;
}

static string ResolveRootFromEnvironment(string variableName, string fallback)
{
    var value = Environment.GetEnvironmentVariable(variableName);
    return String.IsNullOrWhiteSpace(value)
        ? Path.GetFullPath(fallback)
        : Path.GetFullPath(value);
}

static TimeOnly ResolveBackupLocalTime(string? value)
{
    const string fallback = "20:30";
    var configured = String.IsNullOrWhiteSpace(value) ? fallback : value;
    if (!TimeOnly.TryParseExact(
            configured,
            "HH:mm",
            CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var localTime))
    {
        throw new InvalidOperationException(
            $"DatabaseBackup:LocalTime must use 24-hour HH:mm format; received '{configured}'.");
    }

    return localTime;
}

static TimeZoneInfo ResolveMarketTimeZone(string? value)
{
    var configured = String.IsNullOrWhiteSpace(value) ? "America/New_York" : value;
    var candidates = configured.Equals("America/New_York", StringComparison.OrdinalIgnoreCase)
        || configured.Equals("Eastern Standard Time", StringComparison.OrdinalIgnoreCase)
        ? new[] { configured, "America/New_York", "Eastern Standard Time" }.Distinct()
        : [configured];
    foreach (var candidate in candidates)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(candidate);
        }
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }
    }

    throw new InvalidOperationException(
        $"DatabaseBackup:MarketTimeZone '{configured}' could not be resolved on this host.");
}

static string ResolveWebContentRoot(string currentDirectory)
{
    if (File.Exists(Path.Combine(currentDirectory, "TradingFlow.Web.csproj")) &&
        Directory.Exists(Path.Combine(currentDirectory, "wwwroot")))
    {
        return currentDirectory;
    }

    var nestedWebRoot = Path.Combine(currentDirectory, "src", "TradingFlow.Web");
    if (File.Exists(Path.Combine(nestedWebRoot, "TradingFlow.Web.csproj")) &&
        Directory.Exists(Path.Combine(nestedWebRoot, "wwwroot")))
    {
        return nestedWebRoot;
    }

    return currentDirectory;
}


static async Task<IReadOnlyDictionary<string, AlpacaLatestQuote>> GetLiveQuotesWithExtendedFallbackAsync(
    AlpacaQuoteService quoteService,
    IReadOnlyCollection<string> tickers,
    string feed,
    CancellationToken cancellationToken)
{
    var primary = await quoteService.GetLatestQuotesAsync(tickers, feed, cancellationToken);
    var staleCutoff = DateTimeOffset.UtcNow.AddMinutes(-15);
    var needsFallback = primary.Values.Any(quote => quote.MidPrice is null || quote.Timestamp is null || quote.Timestamp < staleCutoff);
    if (!needsFallback || feed.Equals("overnight", StringComparison.OrdinalIgnoreCase))
    {
        return primary;
    }

    var overnight = await quoteService.GetLatestQuotesAsync(tickers, "overnight", cancellationToken);
    return primary.ToDictionary(
        pair => pair.Key,
        pair =>
        {
            if (!overnight.TryGetValue(pair.Key, out var fallback))
            {
                return pair.Value;
            }

            var primaryTimestamp = pair.Value.Timestamp ?? DateTimeOffset.MinValue;
            var fallbackTimestamp = fallback.Timestamp ?? DateTimeOffset.MinValue;
            return (pair.Value.MidPrice is null && fallback.MidPrice is not null) || fallbackTimestamp > primaryTimestamp
                ? fallback
                : pair.Value;
        },
        StringComparer.OrdinalIgnoreCase);
}

static string ResolveDefaultQuoteFeed(ConfigCatalogService catalog)
{
    var selected = catalog.GetPaperConfigs()
        .FirstOrDefault(config => config.FileName.Equals("alpaca-paper.yaml", StringComparison.OrdinalIgnoreCase))
        ?? catalog.GetPaperConfigs().FirstOrDefault();
    return selected?.Config.Providers.Alpaca.DataFeed ?? "sip";
}

static IEnumerable<string> SplitTickerDisplay(string tickerDisplay)
{
    return tickerDisplay
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(ticker => ticker.Trim().ToUpperInvariant())
        .Where(ticker => ticker.Length > 0);
}

static int ParsePositiveInteger(string? value, int defaultValue)
{
    return Int32.TryParse(value, out var parsed) && parsed > 0 ? parsed : defaultValue;
}
