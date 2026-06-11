using System.Globalization;
using TradingFlow.Backtesting.StrategyEvaluation;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Services;

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
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);
Environment.SetEnvironmentVariable(
    "TRADINGFLOW_RESULT_OWNER",
    Environment.GetEnvironmentVariable("TRADINGFLOW_RESULT_OWNER") ?? "web");

builder.Services.AddRazorPages();
builder.Services.AddSingleton(new ProjectPaths(ResolveRepositoryRoot(builder.Environment.ContentRootPath)));
builder.Services.AddSingleton<SimpleYamlReader>();
builder.Services.AddSingleton<IArtifactWriter>(AtomicFileArtifactWriter.Instance);
builder.Services.AddSingleton<ConfigCatalogService>();
builder.Services.AddSingleton<RunConfigWriter>();
builder.Services.AddSingleton<TradingFlow.Backtesting.BacktestRunner>();
builder.Services.AddSingleton<BacktestJobService>();
builder.Services.AddSingleton<OptimizationJobService>();
builder.Services.AddSingleton<AlpacaCredentialProvider>();
builder.Services.AddSingleton<PaperEnvironmentService>();
builder.Services.AddSingleton<PaperJobService>();
builder.Services.AddSingleton<StrategyEvaluationService>();

var dbPath = Path.Combine(ResolveRepositoryRoot(builder.Environment.ContentRootPath), "tradingflow.db");
builder.Services.AddDbContextFactory<TradingFlow.Data.Context.TradingFlowDbContext>(options =>
    Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(options, $"Data Source={dbPath}"));

builder.Services.AddSingleton<TradingFlow.Domain.Locking.ITickerLockService, TradingFlow.Data.Locking.SqliteTickerLockService>();
builder.Services.AddSingleton<TradingFlow.Domain.Orders.IOrderStateRepository, TradingFlow.Data.Orders.SqliteOrderStateRepository>();
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
    var dbFactory = scope.ServiceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<TradingFlow.Data.Context.TradingFlowDbContext>>();
    using var db = dbFactory.CreateDbContext();
    db.Database.EnsureCreated();
}

app.Services.GetRequiredService<PaperJobService>().InitializeAsync().GetAwaiter().GetResult();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "TradingFlow.Web" }));
app.MapGet("/api/profiler/alpaca", () => Results.Ok(TradingFlow.Domain.Logging.ApiProfiler.GetSummary("Alpaca")));
app.MapPost("/api/strategies/evaluate", async (
    StrategyEvaluationRequest request,
    StrategyEvaluationService evaluator,
    CancellationToken cancellationToken) =>
{
    var result = await evaluator.EvaluateWithAlpacaAsync(request, cancellationToken);
    return Results.Ok(result);
});
app.MapPost("/api/strategies/ross-gapgo-bullflag/evaluate", async (
    StrategyEvaluationService evaluator,
    CancellationToken cancellationToken) =>
{
    var result = await evaluator.EvaluateWithAlpacaAsync(
        new StrategyEvaluationRequest(
            ConfigPath: Path.Combine("configs", "paper", "alpaca-paper.yaml"),
            StrategyPath: Path.Combine("configs", "strategies", "intraday-ross-gapgo-bullflag.v2-confirmed-entry.yaml"),
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
