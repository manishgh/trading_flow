using TradingFlow.Alpaca;
using TradingFlow.Data.Candles;
using TradingFlow.Engine.Abstractions;
using TradingFlow.WarmupService.Models;
using TradingFlow.WarmupService.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile(Path.Combine("src", "TradingFlow.Web", "appsettings.local.json"), optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fff zzz ";
});

builder.Services.Configure<WarmupOptions>(builder.Configuration.GetSection("Warmup"));
builder.Services.AddSingleton<WarmupCredentialResolver>();
builder.Services.AddSingleton<WarmupRequestStore>();
builder.Services.AddSingleton<WarmupRunStore>();
builder.Services.AddSingleton<WarmupArtifactWriter>();
builder.Services.AddSingleton<WarmupJobQueue>();
builder.Services.AddSingleton<ICandleStore>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WarmupOptions>>().Value;
    return new LocalFileCandleStore(Path.Combine(options.CacheRoot, "candle-store"));
});
builder.Services.AddSingleton<IMarketDataProvider>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WarmupOptions>>().Value;
    var credentials = sp.GetRequiredService<WarmupCredentialResolver>();
    return new AlpacaMarketDataProvider(
        new HttpClient(),
        AlpacaOptions.CreateDefault() with
        {
            KeyId = credentials.Resolve("Alpaca", "KeyId", "ALPACA_KEY_ID"),
            SecretKey = credentials.Resolve("Alpaca", "SecretKey", "ALPACA_SECRET_KEY"),
            MarketDataFeed = options.MarketDataFeed,
            ExtendedHours = true
        });
});
builder.Services.AddSingleton<ICatalystProvider>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WarmupOptions>>().Value;
    var credentials = sp.GetRequiredService<WarmupCredentialResolver>();
    return new AlpacaNewsProvider(
        new HttpClient(),
        AlpacaOptions.CreateDefault() with
        {
            KeyId = credentials.Resolve("Alpaca", "KeyId", "ALPACA_KEY_ID"),
            SecretKey = credentials.Resolve("Alpaca", "SecretKey", "ALPACA_SECRET_KEY"),
            MarketDataFeed = options.MarketDataFeed,
            ExtendedHours = true
        },
        sp.GetService<ILogger<AlpacaNewsProvider>>());
});
builder.Services.AddSingleton<IWarmupArchiveSink>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WarmupOptions>>().Value;
    var logger = sp.GetRequiredService<ILogger<SasAzureBlobWarmupArchiveSink>>();
    return new CompositeWarmupArchiveSink(
        new LocalWarmupArchiveSink(options.ArchiveRoot),
        new SasAzureBlobWarmupArchiveSink(
            new HttpClient(),
            options.BlobContainerSasUrl,
            options.BlobPrefix,
            logger));
});
builder.Services.AddSingleton<WarmupCoordinator>();
builder.Services.AddHostedService<WarmupBackgroundService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "TradingFlow.WarmupService" }));

app.MapGet("/api/warmup/watchlist", async (WarmupRequestStore store, CancellationToken cancellationToken) =>
{
    var items = await store.ReadAsync(cancellationToken);
    return Results.Ok(items.OrderBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase));
});

app.MapPost("/api/warmup/watchlist", async (
    WarmupWatchRequest request,
    WarmupRequestStore store,
    WarmupJobQueue queue,
    CancellationToken cancellationToken) =>
{
    var intents = await store.UpsertAsync(request, cancellationToken);
    if (request.RunNow)
    {
        await queue.EnqueueAsync(new WarmupJobRequest(
            Guid.NewGuid().ToString("N"),
            "manual-watchlist-request",
            intents.Select(x => x.Ticker).ToArray(),
            DateTimeOffset.UtcNow), cancellationToken);
    }

    return Results.Accepted($"/api/warmup/watchlist", new
    {
        accepted = intents.Count,
        tickers = intents.Select(x => x.Ticker).OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
        runQueued = request.RunNow
    });
});

app.MapDelete("/api/warmup/watchlist/{ticker}", async (
    string ticker,
    WarmupRequestStore store,
    CancellationToken cancellationToken) =>
{
    var removed = await store.RemoveAsync(ticker, cancellationToken);
    return removed ? Results.NoContent() : Results.NotFound(new { ticker });
});

app.MapPost("/api/warmup/run-now", async (
    WarmupRunNowRequest request,
    WarmupJobQueue queue,
    CancellationToken cancellationToken) =>
{
    var runId = Guid.NewGuid().ToString("N");
    await queue.EnqueueAsync(new WarmupJobRequest(
        runId,
        String.IsNullOrWhiteSpace(request.Reason) ? "manual-run-now" : request.Reason!,
        request.Tickers?.Select(WarmupText.NormalizeTicker).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        DateTimeOffset.UtcNow), cancellationToken);
    return Results.Accepted($"/api/warmup/runs/{runId}", new { runId, queued = true });
});

app.MapGet("/api/warmup/runs", async (WarmupRunStore store, CancellationToken cancellationToken) =>
{
    var runs = await store.ReadAsync(cancellationToken);
    return Results.Ok(runs.OrderByDescending(x => x.StartedAtUtc).Take(100));
});

app.MapGet("/api/warmup/runs/{runId}", async (string runId, WarmupRunStore store, CancellationToken cancellationToken) =>
{
    var runs = await store.ReadAsync(cancellationToken);
    var run = runs.FirstOrDefault(x => x.RunId.Equals(runId, StringComparison.OrdinalIgnoreCase));
    return run is null ? Results.NotFound(new { runId }) : Results.Ok(run);
});

app.Run();
