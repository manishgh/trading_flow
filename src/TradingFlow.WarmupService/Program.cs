using TradingFlow.Alpaca;
using TradingFlow.Data.Candles;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Storage;
using TradingFlow.WarmupService.Models;
using TradingFlow.WarmupService.Services;
using TradingFlow.Contracts.V1;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment())
    builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);
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
builder.Services.AddSingleton<IRawArchiveWriter>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WarmupOptions>>().Value;
    return new FileSystemRawArchiveWriter(new RawArchiveOptions(Path.Combine(options.ArchiveRoot, "raw")));
});
builder.Services.AddSingleton<ICandleStore>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WarmupOptions>>().Value;
    return new LocalFileCandleStore(Path.Combine(options.CacheRoot, "candle-store"));
});
builder.Services.AddSingleton<IMarketDataProvider>(sp =>
{
    // Warm-up bars and calendar responses use the same persistent archive owner as news.
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WarmupOptions>>().Value;
    var credentials = sp.GetRequiredService<WarmupCredentialResolver>();
    return new AlpacaMarketDataProvider(
        new HttpClient(),
        AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
        {
            KeyId = credentials.Resolve("Alpaca", "KeyId", "ALPACA_KEY_ID"),
            SecretKey = credentials.Resolve("Alpaca", "SecretKey", "ALPACA_SECRET_KEY"),
            MarketDataFeed = options.MarketDataFeed
        },
        sp.GetRequiredService<IRawArchiveWriter>());
});
builder.Services.AddSingleton<ICatalystProvider>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WarmupOptions>>().Value;
    var credentials = sp.GetRequiredService<WarmupCredentialResolver>();
    return new AlpacaNewsProvider(
        new HttpClient(),
        AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
        {
            KeyId = credentials.Resolve("Alpaca", "KeyId", "ALPACA_KEY_ID"),
            SecretKey = credentials.Resolve("Alpaca", "SecretKey", "ALPACA_SECRET_KEY"),
            MarketDataFeed = options.MarketDataFeed
        },
        sp.GetRequiredService<IRawArchiveWriter>(),
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
app.UseWarmupServiceAccess(app.Configuration);

var credentialsForReadiness = app.Services.GetRequiredService<WarmupCredentialResolver>();
var missingCredentialNames = new List<string>();
if (String.IsNullOrWhiteSpace(credentialsForReadiness.Resolve("Alpaca", "KeyId", "ALPACA_KEY_ID"))) missingCredentialNames.Add("ALPACA_KEY_ID");
if (String.IsNullOrWhiteSpace(credentialsForReadiness.Resolve("Alpaca", "SecretKey", "ALPACA_SECRET_KEY"))) missingCredentialNames.Add("ALPACA_SECRET_KEY");
if (String.IsNullOrWhiteSpace(WarmupServiceAccess.ResolveKey(app.Configuration))) missingCredentialNames.Add("WarmupService:ApiKey");
if (app.Environment.IsProduction() && missingCredentialNames.Count > 0)
    throw new InvalidOperationException("Missing warm-up host configuration: " + String.Join(", ", missingCredentialNames));

app.MapGet("/health", () => missingCredentialNames.Count == 0
    ? (IResult)Results.Ok(new { status = "ok", service = "TradingFlow.WarmupService" })
    : Results.Json(new { status = "configuration_missing", missing = missingCredentialNames }, statusCode: 503));

app.MapGet("/api/warmup/watchlist", async (WarmupRequestStore store, CancellationToken cancellationToken) =>
{
    var items = await store.ReadAsync(cancellationToken);
    return Results.Ok(items.OrderBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase));
});

app.MapPost("/api/warmup/watchlist", async (
    WarmupWatchRequest request,
    WarmupJobQueue queue,
    CancellationToken cancellationToken) =>
{
    try { return Results.Accepted("/api/warmup/watchlist", await queue.AddWatchAsync(request, cancellationToken)); }
    catch (Exception error) when (error is InvalidOperationException or ArgumentException)
    { return Results.Json(new { message = error.Message }, statusCode: error is WarmupQueueFullException ? 429 : 400); }
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
    string runId;
    try { runId = WarmupJobQueue.RunIdForKey(request.IdempotencyKey); }
    catch (ArgumentException error) { return Results.BadRequest(new { message = error.Message }); }
    try { await queue.EnqueueAsync(new WarmupJobRequest(
        runId,
        String.IsNullOrWhiteSpace(request.Reason) ? "manual-run-now" : request.Reason!,
        request.Tickers?.Select(WarmupText.NormalizeTicker).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        DateTimeOffset.UtcNow), cancellationToken); }
    catch (Exception error) when (error is InvalidOperationException or ArgumentException)
    { return Results.Json(new { message = error.Message }, statusCode: error is WarmupQueueFullException ? 429 : 400); }
    return Results.Accepted($"/api/warmup/runs/{runId}", new { runId, queued = true });
});

app.MapGet("/api/warmup/jobs", async (WarmupJobQueue queue, CancellationToken token) =>
    Results.Ok((await queue.ListAsync(token)).OrderByDescending(job => job.Request.QueuedAtUtc).Select(QueueStatus)));

app.MapPost("/api/warmup/jobs/{runId}/cancel", async (string runId, WarmupJobQueue queue, CancellationToken token) =>
{
    if (!await queue.CancelAsync(runId, token)) return Results.NotFound(new { message = "Warmup job not found." });
    var job = (await queue.ListAsync(token)).Single(item => item.Request.RunId == runId);
    return Results.Ok(QueueStatus(job));
});

app.MapPost("/api/warmup/runs/{runId}/archive/retry", async (string runId, WarmupCoordinator coordinator, CancellationToken token) =>
{
    var run = await coordinator.RetryArchivesAsync(runId, token);
    return run is null ? Results.NotFound(new { message = "Warmup run not found." }) : Results.Ok(new WarmupArchiveRetryResponse(runId,
        run.Tickers.Select(ticker => new WarmupArchiveStatus(ticker.Ticker, ticker.CacheCompleted, ticker.ArchiveStatus, ticker.ArchiveError)).ToArray()));
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

/// <summary>Projects queue status without exposing the private frozen request payload or local file paths.</summary>
static WarmupQueueStatus QueueStatus(WarmupQueuedJob job) => new(job.Request.RunId, job.Request.Reason, job.Request.QueuedAtUtc,
    job.Status, job.Request.FrozenIntents?.Select(intent => intent.Ticker).ToArray() ?? job.Request.Tickers?.ToArray() ?? [], job.Error);
