using System.Globalization;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;

var cultureInfo = new CultureInfo("en-US");
CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);
Environment.SetEnvironmentVariable(
    "TRADINGFLOW_RESULT_OWNER",
    Environment.GetEnvironmentVariable("TRADINGFLOW_RESULT_OWNER") ?? "web");

builder.Services.AddRazorPages();
builder.Services.AddSingleton(new ProjectPaths(ResolveRepositoryRoot(builder.Environment.ContentRootPath)));
builder.Services.AddSingleton<SimpleYamlReader>();
builder.Services.AddSingleton<ConfigCatalogService>();
builder.Services.AddSingleton<RunConfigWriter>();
builder.Services.AddSingleton<TradingFlow.Backtesting.BacktestRunner>();
builder.Services.AddSingleton<BacktestJobService>();
builder.Services.AddSingleton<OptimizationJobService>();
builder.Services.AddSingleton<AlpacaCredentialProvider>();
builder.Services.AddSingleton<PaperEnvironmentService>();
builder.Services.AddSingleton<PaperJobService>();

var dbPath = Path.Combine(ResolveRepositoryRoot(builder.Environment.ContentRootPath), "tradingflow.db");
builder.Services.AddDbContextFactory<TradingFlow.Data.Context.TradingFlowDbContext>(options =>
    Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(options, $"Data Source={dbPath}"));

builder.Services.AddSingleton<TradingFlow.Domain.Locking.ITickerLockService, TradingFlow.Data.Locking.SqliteTickerLockService>();
builder.Services.AddSingleton<TradingFlow.Domain.Orders.IOrderStateRepository, TradingFlow.Data.Orders.SqliteOrderStateRepository>();
builder.Services.AddSingleton<TradingFlow.Domain.Audit.IDecisionAuditRepository, TradingFlow.Data.Audit.SqliteDecisionAuditRepository>();
builder.Services.AddSingleton<TradingFlow.Domain.Jobs.IJobRepository, TradingFlow.Data.Jobs.SqliteJobRepository>();

var app = builder.Build();

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
