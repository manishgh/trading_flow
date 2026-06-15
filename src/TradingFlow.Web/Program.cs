using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Backtesting.StrategyEvaluation;
using TradingFlow.Data.Candles;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Web;
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
var repositoryRoot = ResolveRepositoryRoot(builder.Environment.ContentRootPath);
var dataRoot = ResolveRootFromEnvironment("TRADINGFLOW_DATA_ROOT", Path.Combine(repositoryRoot, "data"));
var cacheRoot = ResolveRootFromEnvironment("TRADINGFLOW_CACHE_ROOT", Path.Combine(dataRoot, "cache"));
builder.Services.AddSingleton(new ProjectPaths(repositoryRoot, dataRoot, cacheRoot));
builder.Services.AddSingleton<SimpleYamlReader>();
builder.Services.AddSingleton<IArtifactWriter>(AtomicFileArtifactWriter.Instance);
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
builder.Services.AddSingleton<NewsFeedService>();
builder.Services.AddSingleton<WarmupServiceClient>();
builder.Services.AddSingleton<StrategyEvaluationService>();

var dbPath = Path.Combine(dataRoot, "tradingflow.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
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
    EnsureOrderSchema(db);
}

app.Services.GetRequiredService<PaperJobService>().InitializeAsync().GetAwaiter().GetResult();
app.Services.GetRequiredService<MobileAutomationService>().InitializeAsync().GetAwaiter().GetResult();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.MapTradingFlowMobileApi();
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
app.MapPost("/api/strategies/intraday-top/evaluate", async (
    StrategyEvaluationService evaluator,
    CancellationToken cancellationToken) =>
{
    var result = await evaluator.EvaluateWithAlpacaAsync(
        new StrategyEvaluationRequest(
            ConfigPath: Path.Combine("configs", "paper", "alpaca-paper.yaml"),
            StrategyPath: Path.Combine("configs", "strategies", "intraday-ross-vwap-ema-cumulative-volume.v6-lite.yaml"),
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

static void EnsureOrderSchema(TradingFlow.Data.Context.TradingFlowDbContext db)
{
    var connection = db.Database.GetDbConnection();
    var shouldClose = connection.State == System.Data.ConnectionState.Closed;
    if (shouldClose)
    {
        connection.Open();
    }

    try
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(Orders);";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }

        ExecuteSqlIfMissing(columns, "RunName", "ALTER TABLE Orders ADD COLUMN RunName TEXT NOT NULL DEFAULT '';");
        ExecuteSqlIfMissing(columns, "ClientOrderId", "ALTER TABLE Orders ADD COLUMN ClientOrderId TEXT NOT NULL DEFAULT '';");
        ExecuteSql("CREATE INDEX IF NOT EXISTS IX_Orders_RunName ON Orders (RunName);");
        ExecuteSql("CREATE INDEX IF NOT EXISTS IX_Orders_ClientOrderId ON Orders (ClientOrderId);");
    }
    finally
    {
        if (shouldClose)
        {
            connection.Close();
        }
    }

    void ExecuteSqlIfMissing(HashSet<string> columns, string column, string sql)
    {
        if (!columns.Contains(column))
        {
            ExecuteSql(sql);
            columns.Add(column);
        }
    }

    void ExecuteSql(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
