using System.Text.Json;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Web.Services;

/// <summary>
/// State a subsystem can be in. <see cref="NotApplicable"/> is a first-class
/// state, not a soft failure: an optional sidecar that is deliberately not
/// deployed is healthy operation, and colouring it as a fault trains the
/// operator to ignore the column.
/// </summary>
public enum SubsystemState
{
    Healthy,
    Failing,
    NotApplicable
}

/// <summary>
/// One row of the operations health table.
/// </summary>
/// <param name="Name">Display name.</param>
/// <param name="Owner">
/// The type that produces this line. An operator filing a bug should not have to
/// guess which service to look in.
/// </param>
/// <param name="State">Health state.</param>
/// <param name="StateLabel">Short label shown in the state tag.</param>
/// <param name="Evidence">
/// What was actually observed. Never a paraphrase of the state - the state is
/// derived from this, so the text has to carry the reading.
/// </param>
/// <param name="CheckedAtUtc">When the reading was taken, or null when it was not.</param>
public sealed record SubsystemHealth(
    string Name,
    string Owner,
    SubsystemState State,
    string StateLabel,
    string Evidence,
    DateTimeOffset? CheckedAtUtc);

/// <summary>Warm-up cache facts read from the manifest the warmer writes.</summary>
public sealed record WarmupCacheReport(
    string CachePath,
    DateTimeOffset? ManifestWrittenAtUtc,
    DateTimeOffset? WindowStartUtc,
    DateTimeOffset? WindowEndUtc,
    int TickerCount,
    int TimeframeCount,
    int BarCount,
    string Detail);

/// <summary>Everything the operations screen renders.</summary>
public sealed record OperationsReport(
    OperationalStatusSnapshot Status,
    IReadOnlyList<SubsystemHealth> Subsystems,
    EntryAdmissionSnapshot Admission,
    WarmupCacheReport Warmup)
{
    public int AttentionCount => Subsystems.Count(item => item.State == SubsystemState.Failing);

    /// <summary>
    /// One-line summary for the header tag. Blocks come first because an open
    /// entry block is the state that changes what the operator may do next.
    /// </summary>
    public string Headline
    {
        get
        {
            var blocks = Admission.Blocks.Count;
            var parts = new List<string>(2);
            parts.Add(blocks == 1 ? "1 block open" : $"{blocks} blocks open");
            if (AttentionCount > 0)
            {
                parts.Add(AttentionCount == 1
                    ? "1 subsystem needs attention"
                    : $"{AttentionCount} subsystems need attention");
            }
            else
            {
                parts.Add("all subsystems nominal");
            }
            return String.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Assembles the operator health view from authoritative server state.
///
/// Every reading here is a read. Nothing on this screen starts, stops or
/// reconfigures a subsystem, so opening it can never change what the system is
/// doing. Anything unreadable is reported as unknown rather than guessed.
/// </summary>
public sealed class OperationsHealthService
{
    private readonly OperationalStatusService operationalStatus;
    private readonly AlpacaCredentialProvider credentials;
    private readonly IOrderSynchronizationCoordinator synchronization;
    private readonly IAccountReconciliationService reconciliation;
    private readonly IOrderDispatchRecoveryHealth dispatchRecovery;
    private readonly IEntryAdmissionControl admission;
    private readonly MarketPredictorHttpClient marketPredictor;
    private readonly RawArchiveOptions rawArchiveOptions;
    private readonly WarmupServiceClient warmup;
    private readonly ProjectPaths paths;
    private readonly IConfiguration configuration;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<OperationsHealthService> logger;

    public OperationsHealthService(
        OperationalStatusService operationalStatus,
        AlpacaCredentialProvider credentials,
        IOrderSynchronizationCoordinator synchronization,
        IAccountReconciliationService reconciliation,
        IOrderDispatchRecoveryHealth dispatchRecovery,
        IEntryAdmissionControl admission,
        MarketPredictorHttpClient marketPredictor,
        RawArchiveOptions rawArchiveOptions,
        WarmupServiceClient warmup,
        ProjectPaths paths,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<OperationsHealthService> logger)
    {
        this.operationalStatus = operationalStatus;
        this.credentials = credentials;
        this.synchronization = synchronization;
        this.reconciliation = reconciliation;
        this.dispatchRecovery = dispatchRecovery;
        this.admission = admission;
        this.marketPredictor = marketPredictor;
        this.rawArchiveOptions = rawArchiveOptions;
        this.warmup = warmup;
        this.paths = paths;
        this.configuration = configuration;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<OperationsReport> BuildAsync(
        string quoteFeed,
        IEnumerable<DateTimeOffset?> quoteTimestamps,
        CancellationToken cancellationToken)
    {
        var observations = quoteTimestamps as IReadOnlyList<DateTimeOffset?> ?? quoteTimestamps.ToArray();
        var now = timeProvider.GetUtcNow();

        var statusTask = operationalStatus.GetAsync(quoteFeed, observations, cancellationToken);
        var accountTask = operationalStatus.GetAccountAsync(cancellationToken);
        var sessionTask = operationalStatus.GetTradingSessionAsync(cancellationToken);
        var modelTask = marketPredictor.GetHealthAsync(cancellationToken);
        await Task.WhenAll(statusTask, accountTask, sessionTask, modelTask);

        var status = await statusTask;
        var account = await accountTask;
        var session = await sessionTask;
        var model = await modelTask;
        var admissionSnapshot = admission.GetSnapshot();
        var warmupReport = ReadWarmupManifest();

        var subsystems = new List<SubsystemHealth>
        {
            BuildCredentialRow(account, now),
            BuildCalendarRow(session, now),
            BuildQuoteRow(status, now),
            BuildOrderStreamRow(now),
            BuildDispatchRecoveryRow(),
            BuildReconciliationRow(),
            BuildEntryGateRow(admissionSnapshot, now),
            BuildPredictorRow(model, now),
            BuildSentimentRow(now),
            BuildWarmupRow(warmupReport, now),
            BuildRawArchiveRow(now),
            BuildBackupRow(now)
        };

        return new OperationsReport(status, subsystems, admissionSnapshot, warmupReport);
    }

    private static SubsystemHealth BuildCredentialRow(
        (BrokerAccountSnapshot? Snapshot, string Detail) account,
        DateTimeOffset now)
    {
        var healthy = account.Snapshot is not null &&
            !account.Snapshot.AccountBlocked &&
            !account.Snapshot.TradingBlocked;
        return new SubsystemHealth(
            "Alpaca credentials",
            "AlpacaCredentialProvider",
            healthy ? SubsystemState.Healthy : SubsystemState.Failing,
            healthy ? "Authorised" : "Unreadable",
            account.Snapshot is null
                ? account.Detail
                : $"{account.Detail} Blocked: account {Yes(account.Snapshot.AccountBlocked)}, trading {Yes(account.Snapshot.TradingBlocked)}.",
            now);
    }

    private static SubsystemHealth BuildCalendarRow(TradingSessionSnapshot? session, DateTimeOffset now)
    {
        return new SubsystemHealth(
            "Market calendar",
            "AlpacaBrokerClient.GetSessionAsync",
            session is null ? SubsystemState.Failing : SubsystemState.Healthy,
            session is null ? "Unavailable" : "Current",
            session is null
                ? "Calendar unavailable; execution remains fail-closed and no session may be assumed."
                : $"Trade date {session.TradeDate:yyyy-MM-dd}, session {session.Session.ToString().ToLowerInvariant()}.",
            now);
    }

    private static SubsystemHealth BuildQuoteRow(OperationalStatusSnapshot status, DateTimeOffset now)
    {
        var healthy = status.QuoteStatus is "connected" or "delayed";
        return new SubsystemHealth(
            "Quote stream",
            "OperationalStatusService",
            healthy ? SubsystemState.Healthy : SubsystemState.Failing,
            Capitalise(status.QuoteStatus),
            $"{status.QuoteDetail} Fresh at or under {Describe(OperationalStatusService.FreshQuoteThreshold)}, " +
            $"delayed at or under {Describe(OperationalStatusService.StaleQuoteThreshold)}, stale beyond it. " +
            $"Feed {status.QuoteFeed}.",
            now);
    }

    private SubsystemHealth BuildOrderStreamRow(DateTimeOffset now)
    {
        var health = synchronization.GetHealth();
        var divergent = health.DivergenceCycles.Count;
        var evidence = health.StreamConnected
            ? $"Stream connected. Last event {Describe(health.LastStreamUpdateUtc)}; last REST cross-check {Describe(health.LastRestPollUtc)}."
            : $"Stream disconnected. Last event {Describe(health.LastStreamUpdateUtc)}; orders fall back to REST cross-check.";
        if (divergent > 0)
        {
            evidence += $" {divergent} client order id(s) diverging between stream and broker.";
        }

        return new SubsystemHealth(
            "Order stream",
            "IOrderSynchronizationCoordinator",
            health.StreamConnected && divergent == 0 ? SubsystemState.Healthy : SubsystemState.Failing,
            health.StreamConnected ? "Connected" : "Disconnected",
            evidence,
            now);
    }

    private SubsystemHealth BuildReconciliationRow()
    {
        var health = reconciliation.GetHealth();
        var clean = health.InitialReconciliationCompleted &&
            String.Equals(health.Status, "clean", StringComparison.OrdinalIgnoreCase);
        var evidence = health.InitialReconciliationCompleted
            ? $"Status {health.Status}; {health.DifferenceCount} difference(s)."
            : "Initial reconciliation has not completed; the local book is not yet proven against the broker.";
        if (health.OutstandingReconciliationId is { } outstanding)
        {
            evidence += $" Outstanding reconciliation {outstanding}.";
        }

        return new SubsystemHealth(
            "Account reconciliation",
            "IAccountReconciliationService",
            clean ? SubsystemState.Healthy : SubsystemState.Failing,
            clean ? "Clean" : Capitalise(health.Status),
            evidence,
            health.LastCompletedAtUtc);
    }

    private SubsystemHealth BuildDispatchRecoveryRow()
    {
        var recovery = dispatchRecovery.GetHealth();
        var healthy = recovery.InitialCycleCompleted && recovery.Healthy;
        return new SubsystemHealth(
            "Order recovery",
            "OrderDispatchRecoveryHostedService",
            healthy ? SubsystemState.Healthy : SubsystemState.Failing,
            healthy ? "Current" : recovery.InitialCycleCompleted ? "Failing" : "Starting",
            recovery.Detail,
            recovery.LastCompletedAtUtc);
    }

    private static SubsystemHealth BuildEntryGateRow(EntryAdmissionSnapshot snapshot, DateTimeOffset now)
    {
        return new SubsystemHealth(
            "Entry gate",
            "IEntryAdmissionControl",
            snapshot.EntriesAllowed ? SubsystemState.Healthy : SubsystemState.Failing,
            snapshot.EntriesAllowed ? "Open" : "Blocked",
            snapshot.EntriesAllowed
                ? "New entries are allowed by the server risk gate."
                : $"{snapshot.Blocks.Count} block(s) open: {String.Join(", ", snapshot.Blocks.Select(block => block.Code))}.",
            now);
    }

    private static SubsystemHealth BuildPredictorRow(MarketPredictorHealth model, DateTimeOffset now)
    {
        var ready = String.Equals(model.Status, "ready", StringComparison.OrdinalIgnoreCase);
        var notConfigured = String.Equals(model.Status, "not configured", StringComparison.OrdinalIgnoreCase);
        return new SubsystemHealth(
            "Market predictor",
            "MarketPredictorHttpClient",
            notConfigured ? SubsystemState.NotApplicable : ready ? SubsystemState.Healthy : SubsystemState.Failing,
            notConfigured ? "Not configured" : ready ? "Ready" : "Attention",
            $"{model.Detail} Contract market_predictor.prediction.v1; swing evidence only. " +
            "Model output is read-only evidence and cannot authorise an entry.",
            now);
    }

    private SubsystemHealth BuildSentimentRow(DateTimeOffset now)
    {
        // An unset endpoint is the documented VADER-fallback configuration, not a
        // fault. It renders as not-applicable so a real FinBERT outage stays
        // visible against it.
        var endpoint = Environment.GetEnvironmentVariable("FINBERT_SENTIMENT_URL");
        var configured = !String.IsNullOrWhiteSpace(endpoint);
        return new SubsystemHealth(
            "FinBERT sentiment",
            "sidecars/finbert-sentiment",
            configured ? SubsystemState.Healthy : SubsystemState.NotApplicable,
            configured ? "Configured" : "Not applicable",
            configured
                ? $"FINBERT_SENTIMENT_URL is set; the sidecar scores headlines in place of the VADER fallback."
                : "FINBERT_SENTIMENT_URL is unset, so sentiment uses the built-in VADER fallback. This is a supported configuration, not a failure.",
            now);
    }

    private static SubsystemHealth BuildWarmupRow(WarmupCacheReport warmupReport, DateTimeOffset now)
    {
        var healthy = warmupReport.ManifestWrittenAtUtc is not null;
        return new SubsystemHealth(
            "Warm-up service",
            "WarmupServiceClient",
            healthy ? SubsystemState.Healthy : SubsystemState.Failing,
            healthy ? "Written" : "No manifest",
            warmupReport.Detail,
            warmupReport.ManifestWrittenAtUtc);
    }

    private SubsystemHealth BuildRawArchiveRow(DateTimeOffset now)
    {
        var root = rawArchiveOptions.RootPath;
        try
        {
            if (!Directory.Exists(root))
            {
                return new SubsystemHealth(
                    "Raw archive",
                    "IRawArchiveWriter",
                    SubsystemState.Failing,
                    "Missing",
                    $"Archive root {root} does not exist, so provider payloads cannot be archived before parse.",
                    now);
            }

            var documents = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Take(100_001).Count();
            var newest = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(File.GetLastWriteTimeUtc)
                .DefaultIfEmpty()
                .Max();
            return new SubsystemHealth(
                "Raw archive",
                "IRawArchiveWriter",
                SubsystemState.Healthy,
                "Archiving",
                $"Payloads are archived before parse under {root}. " +
                $"{(documents > 100_000 ? "over 100,000" : documents.ToString())} manifest(s); " +
                $"newest write {(newest == default ? "none yet" : $"{newest:yyyy-MM-dd HH:mm} UTC")}. " +
                $"Retention {rawArchiveOptions.RetentionDays} days.",
                now);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Unable to read the raw archive root for the operations screen.");
            return new SubsystemHealth(
                "Raw archive",
                "IRawArchiveWriter",
                SubsystemState.Failing,
                "Unreadable",
                $"Archive root {root} could not be read.",
                now);
        }
    }

    private SubsystemHealth BuildBackupRow(DateTimeOffset now)
    {
        var root = Environment.GetEnvironmentVariable("TRADINGFLOW_BACKUP_ROOT");
        if (String.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(paths.DataRoot, "backups");
        }

        try
        {
            var newest = Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*.db", SearchOption.AllDirectories)
                    .Select(File.GetLastWriteTimeUtc)
                    .DefaultIfEmpty()
                    .Max()
                : default;
            if (newest == default)
            {
                return new SubsystemHealth(
                    "Database backup",
                    "DatabaseBackupHostedService",
                    SubsystemState.Failing,
                    "None on file",
                    $"No SQLite backup found under {root}. The scheduled backup has not produced a file.",
                    now);
            }

            // A backup older than 48h means at least one scheduled run was
            // missed, whatever the schedule; that is attention, not nominal.
            var age = now - new DateTimeOffset(newest, TimeSpan.Zero);
            var missed = age > TimeSpan.FromHours(48);
            var configured = configuration["DatabaseBackup:LocalTime"] ?? "not configured";
            return new SubsystemHealth(
                "Database backup",
                "DatabaseBackupHostedService",
                missed ? SubsystemState.Failing : SubsystemState.Healthy,
                missed ? "Behind schedule" : "Current",
                $"Last successful backup {newest:yyyy-MM-dd HH:mm} UTC ({(int)age.TotalHours}h ago) under {root}. " +
                $"Scheduled daily at {configured} market time." +
                (missed ? " More than 48 hours old, so at least one scheduled run did not complete." : String.Empty),
                new DateTimeOffset(newest, TimeSpan.Zero));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Unable to read the backup root for the operations screen.");
            return new SubsystemHealth(
                "Database backup",
                "DatabaseBackupHostedService",
                SubsystemState.Failing,
                "Unreadable",
                $"Backup root {root} could not be read.",
                now);
        }
    }

    private WarmupCacheReport ReadWarmupManifest()
    {
        var cachePath = Path.Combine(paths.CacheRoot, "warmup");
        var manifestPath = Path.Combine(cachePath, "_warmup_manifest.json");
        if (!File.Exists(manifestPath))
        {
            return new WarmupCacheReport(
                cachePath,
                null,
                null,
                null,
                0,
                0,
                0,
                $"No manifest at {manifestPath}. The warm-up service has not written a cache for this root.");
        }

        try
        {
            using var stream = File.OpenRead(manifestPath);
            var manifest = JsonSerializer.Deserialize<WarmupManifest>(
                stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var written = new DateTimeOffset(File.GetLastWriteTimeUtc(manifestPath), TimeSpan.Zero);
            return new WarmupCacheReport(
                manifest?.OutputRoot ?? cachePath,
                written,
                manifest?.Start,
                manifest?.End,
                manifest?.TickerCount ?? 0,
                manifest?.TimeframeCount ?? 0,
                manifest?.BarCount ?? 0,
                $"Manifest written {written:yyyy-MM-dd HH:mm} UTC covering {manifest?.LookbackDays ?? 0} days, " +
                $"{manifest?.TickerCount ?? 0} ticker(s) across {manifest?.TimeframeCount ?? 0} timeframe(s), " +
                $"{manifest?.BarCount ?? 0} bar(s).");
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            logger.LogWarning(exception, "Unable to read the warm-up manifest for the operations screen.");
            return new WarmupCacheReport(
                cachePath,
                null,
                null,
                null,
                0,
                0,
                0,
                $"Manifest at {manifestPath} could not be read.");
        }
    }

    /// <summary>Warm-up manifest projection. Only the fields this screen states.</summary>
    private sealed record WarmupManifest(
        string? OutputRoot,
        DateTimeOffset? Start,
        DateTimeOffset? End,
        int LookbackDays,
        int TickerCount,
        int TimeframeCount,
        int BarCount);

    private static string Yes(bool value) => value ? "yes" : "no";

    private static string Capitalise(string value) =>
        String.IsNullOrEmpty(value) ? value : Char.ToUpperInvariant(value[0]) + value[1..];

    private static string Describe(TimeSpan span) =>
        span.TotalSeconds < 60 ? $"{(int)span.TotalSeconds}s" : $"{(int)span.TotalMinutes}m";

    private static string Describe(DateTimeOffset? timestamp) =>
        timestamp is null ? "never" : $"{timestamp.Value.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC";
}
