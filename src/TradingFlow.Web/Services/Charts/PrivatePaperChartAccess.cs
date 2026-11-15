using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingFlow.Application.Jobs;
using TradingFlow.Data.Context;
using TradingFlow.Data.Identity;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Web.Services.Charts;

public sealed class PrivatePaperChartOptions
{
    public const string Section = "TradingFlow:PrivatePaperChart";
    public List<PrivatePaperChartGrant> Grants { get; init; } = [];
}

public sealed class PrivatePaperChartGrant
{
    public Guid UserId { get; init; }
    public string PaperAccountId { get; init; } = "";
    public List<Guid> PaperJobIds { get; init; } = [];
    public List<Guid> ManualOrderIntentIds { get; init; } = [];
}

internal sealed record PaperChartScope(Guid UserId, string AccountId, Guid RunId, Guid? PaperJobId,
    Guid IntentId, string ClientOrderId, string JournalSymbol, string Symbol, string Profile, long? GenerationId,
    Guid DecisionRunId, string ConfigHash);

internal sealed record PaperChartSelection(string Token, string Symbol, string Profile, string RunLabel,
    string ClientOrderId, long? GenerationId, DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc);

internal sealed record PaperChartWindow(DateTimeOffset FromUtc, DateTimeOffset ToUtc);
internal sealed record PaperChartContext(PaperChartScope Scope, DateTimeOffset ExpiresAtUtc);
internal sealed record PaperChartCursor(PaperChartContext Context, PaperChartWindow Window, DateTimeOffset AtUtc, string Id);
internal sealed record PaperChartBinding(PaperChartScope Scope, PaperChartIntent Intent, string RunLabel, PositionEventRecord? Opening = null);

internal sealed class PrivatePaperChartTokens(IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector selection = protection.CreateProtector("TradingFlow.PrivatePaperChart.Selection.v1");
    private readonly IDataProtector paging = protection.CreateProtector("TradingFlow.PrivatePaperChart.Page.v1");

    // Keep private chart capabilities short-lived even if the application cookie remains valid.
    public PaperChartSelection Issue(PaperChartBinding binding)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(5);
        var scope = binding.Scope;
        return new(selection.Protect(JsonSerializer.Serialize(new PaperChartContext(scope, expires))),
            scope.Symbol, scope.Profile, binding.RunLabel, scope.ClientOrderId, scope.GenerationId, now, expires);
    }

    // Bind the token to the live operator and exact chart symbol before any durable journal selections.
    public PaperChartContext? Read(string? token, Guid userId, string symbol)
    {
        var context = Open<PaperChartContext>(selection, token);
        return context is not null && context.Scope.UserId == userId && context.Scope.Symbol == symbol &&
            context.ExpiresAtUtc > clock.GetUtcNow() ? context : null;
    }

    // Paging cannot extend the original context's identity, UTC window, or lifetime.
    public string CreatePage(PaperChartCursor cursor) => paging.Protect(JsonSerializer.Serialize(cursor));

    // Reject a cursor from another exact context before acquiring the bounded journal snapshot.
    public PaperChartCursor? ReadPage(string token, PaperChartContext context, PaperChartWindow window)
    {
        var cursor = Open<PaperChartCursor>(paging, token);
        return cursor is not null && cursor.Context == context && cursor.Window == window ? cursor : null;
    }

    // Never pass token decoding errors or protected contents to the browser.
    private static T? Open<T>(IDataProtector protector, string? token) where T : class
    {
        if (token is not { Length: > 0 and <= 4096 }) return null;
        try { return JsonSerializer.Deserialize<T>(protector.Unprotect(token)); }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException) { return null; }
    }
}

/// <summary>Resolves a chart capability from current Identity membership, protected grants, and durable paper provenance.</summary>
internal sealed class PrivatePaperChartAccess(IDbContextFactory<TradingFlowDbContext> contexts,
    IOptionsMonitor<PrivatePaperChartOptions> options, BrokerAccountBindingOptions account)
{
    // Never use a browser-supplied role claim as the current authority.
    public static Guid OperatorId(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true && Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id : Guid.Empty;

    // The regular page can hide unavailable selectors without reading any order, position, or broker state.
    public async Task<bool> CanSelectAsync(ClaimsPrincipal user, Guid? paperJobId, CancellationToken token)
    {
        var id = OperatorId(user);
        return Grants(id).Any(grant => paperJobId is { } jobId
            ? grant.PaperJobIds.Contains(jobId) : grant.PaperJobIds.Count > 0 || grant.ManualOrderIntentIds.Count > 0) &&
            await IsAdministratorAsync(id, token);
    }

    // Re-read the grant and role on every request, including cursored requests.
    public async Task<bool> CanReadAsync(PaperChartScope scope, CancellationToken token) =>
        scope.AccountId == account.ExpectedAccountId && IsGranted(scope.UserId, scope.PaperJobId, scope.IntentId) &&
        await IsAdministratorAsync(scope.UserId, token);

    // Select a durable order or the currently open local generation without the regular page's broker refresh.
    public async Task<PaperChartBinding?> SelectAsync(ClaimsPrincipal user, Guid? paperJobId, string? clientOrderId,
        string journalSymbol, CancellationToken token)
    {
        var symbol = CanonicalSymbol(journalSymbol);
        if (symbol is null || clientOrderId is { Length: 0 or > 100 } || (paperJobId.HasValue && clientOrderId is null) ||
            !await CanSelectAsync(user, paperJobId, token)) return null;
        var userId = OperatorId(user);
        await using var db = await contexts.CreateDbContextAsync(token);
        if (clientOrderId is not null)
        {
            var order = await PrivatePaperChartJournal.Intents(db.OrderIntents.Where(item => item.ClientOrderId == clientOrderId))
                .SingleOrDefaultAsync(token);
            return order is null || order.Symbol != journalSymbol
                ? null : await ResolveAsync(db, userId, order, paperJobId, null, token);
        }

        var latest = await db.PositionEvents.AsNoTracking()
            .Where(item => item.AccountId == account.ExpectedAccountId && item.Symbol == journalSymbol)
            .OrderByDescending(item => item.PositionEventId)
            .Select(item => new { item.QuantityAfter, item.PositionGenerationEventId, item.PositionGenerationClientOrderId, item.Source })
            .FirstOrDefaultAsync(token);
        if (latest is not { QuantityAfter: not 0m, PositionGenerationEventId: > 0 }) return null;
        if (latest.Source is not ("broker_stream" or "broker_rest")) return null;
        var opening = await OpeningAsync(db, journalSymbol, latest.PositionGenerationEventId, token);
        if (opening is null || opening.ClientOrderId != latest.PositionGenerationClientOrderId) return null;
        var origin = await PrivatePaperChartJournal.Intents(db.OrderIntents.Where(item => item.ClientOrderId == opening.ClientOrderId))
            .SingleOrDefaultAsync(token);
        return await ProveGenerationAsync(db, userId, origin, opening, token);
    }

    // Verify again inside the read snapshot, using the stored intent ID and generation from the protected capability.
    public async Task<PaperChartBinding?> ResolveBoundAsync(TradingFlowDbContext db, PaperChartScope scope, CancellationToken token)
    {
        var intent = await PrivatePaperChartJournal.Intents(db.OrderIntents.Where(item => item.IntentId == scope.IntentId))
            .SingleOrDefaultAsync(token);
        PaperChartBinding? binding;
        if (scope.GenerationId is { } generation)
        {
            var opening = await OpeningAsync(db, scope.JournalSymbol, generation, token);
            binding = opening is null ? null : await ProveGenerationAsync(db, scope.UserId, intent, opening, token);
        }
        else binding = intent is null ? null : await ResolveAsync(db, scope.UserId, intent, scope.PaperJobId, null, token);
        return binding?.Scope == scope ? binding : null;
    }

    // Look up only the exact event whose durable primary key proves the generation opener.
    private Task<PositionEventRecord?> OpeningAsync(TradingFlowDbContext db, string symbol, long generation, CancellationToken token) =>
        PrivatePaperChartJournal.PositionFields(db.PositionEvents.Where(row => row.PositionEventId == generation &&
            row.PositionGenerationEventId == generation && row.AccountId == account.ExpectedAccountId && row.Symbol == symbol &&
            row.PositionGenerationClientOrderId == row.ClientOrderId && row.RunId != Guid.Empty &&
            (row.Source == "broker_stream" || row.Source == "broker_rest") && row.BrokerOrderId != "" && row.ExecutionId != ""))
            .SingleOrDefaultAsync(token);

    // A generation exposes evidence only when the opening ledger row and entry intent have the same durable provenance.
    private Task<PaperChartBinding?> ProveGenerationAsync(TradingFlowDbContext db, Guid userId, PaperChartIntent? intent,
        PositionEventRecord opening, CancellationToken token) =>
        intent is not null && IsEntry(intent.Kind) && opening.QuantityAfter != 0m && opening.FillQuantity > 0m &&
        opening.RunId == intent.RunId && opening.ClientOrderId == intent.ClientOrderId && opening.AccountId == intent.AccountId &&
        opening.Symbol == intent.Symbol &&
        opening.ConfigHash == intent.ConfigHash && opening.CodeVersion == intent.CodeVersion
            ? ResolveAsync(db, userId, intent, null, opening, token)
            : Task.FromResult<PaperChartBinding?>(null);

    // Run/account ownership comes from the retained job, execution run, and intent rather than from an environment query.
    private async Task<PaperChartBinding?> ResolveAsync(TradingFlowDbContext db, Guid userId, PaperChartIntent intent,
        Guid? requestedJobId, PositionEventRecord? opening, CancellationToken token)
    {
        var symbol = CanonicalSymbol(intent.Symbol);
        if (symbol is null || !account.EnforcementEnabled || intent.AccountId != account.ExpectedAccountId ||
            intent.IntentId == Guid.Empty || intent.RunId == Guid.Empty || String.IsNullOrWhiteSpace(intent.ClientOrderId) ||
            intent.Kind is not (OrderIntentKind.StrategyEntry or OrderIntentKind.OperatorEntry or OrderIntentKind.PositionExit or OrderIntentKind.ProtectiveStop) ||
            (requestedJobId is { } requested && requested != intent.RunId)) return null;
        var job = await db.Jobs.AsNoTracking().Where(row => row.Id == intent.RunId)
            .Select(row => new { row.Id, row.JobType, row.RunName, Request = row.RequestJson.Length <= 65536 ? row.RequestJson : "" })
            .SingleOrDefaultAsync(token);
        if (!IsGranted(userId, job?.Id, intent.IntentId) || (requestedJobId.HasValue && job is null)) return null;
        var run = await PrivatePaperChartJournal.RunFields(db.ProductionRuns.Where(row => row.RunId == intent.RunId))
            .SingleOrDefaultAsync(token);
        if (run is null || !Agrees(intent, run)) return null;
        if (job is null ? run.Profile != "paper" || !IsStandaloneManual(intent) :
            job.JobType != "paper" || !AgreesWithJob(run, job.Request)) return null;
        return new(new(userId, intent.AccountId, intent.RunId, job?.Id, intent.IntentId, intent.ClientOrderId,
            intent.Symbol, symbol, run.Profile, opening?.PositionEventId, run.DecisionRunId ?? run.RunId, run.ConfigHash),
            intent, job?.RunName ?? intent.RunId.ToString("D"), opening);
    }

    // The original producer enumerates paper execution profiles separately from backtest, diagnostic, and live runs.
    internal static bool Agrees(PaperChartIntent intent, ProductionRun run) =>
        run.Profile is "paper" or "paper_shadow" or "paper_experiment" && intent.RunId == run.RunId &&
        HasRecordedProvenance(run.ConfigHash, run.CodeVersion) && intent.ConfigHash == run.ConfigHash &&
        intent.CodeVersion == run.CodeVersion && run.StartedAtUtc != default;

    // Unknown legacy fingerprints cannot establish an order's paper execution lineage.
    internal static bool HasRecordedProvenance(string configHash, string codeVersion) =>
        configHash.Length == 64 && configHash.All(Uri.IsHexDigit) && !String.IsNullOrWhiteSpace(codeVersion);

    // Legacy paper jobs must still have a valid frozen request. Workflow jobs carry explicit paper and lineage bindings.
    private static bool AgreesWithJob(ProductionRun run, string requestJson)
    {
        try
        {
            var request = DurableFileJobRequest.Deserialize(requestJson);
            return request.Workflow is { } workflow
                ? workflow.Provenance.Mode == run.Profile && workflow.Provenance.UniverseSnapshotId == run.UniverseSnapshotId &&
                    workflow.Provenance.DecisionRunId == run.DecisionRunId
                : run.Profile == "paper" && run.DecisionRunId is null && run.UniverseSnapshotId == Guid.Empty;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or JsonException or NullReferenceException)
        { return false; }
    }

    // Match the retained direct manual ticket and explicit manual exit producers. Automation and legacy-unknown rows fail closed.
    private static bool IsStandaloneManual(PaperChartIntent order)
    {
        using var json = PrivatePaperChartProjection.ReadObject(order.RequestJson);
        if (json is null) return false;
        var root = json.RootElement;
        return order.Kind switch
        {
            OrderIntentKind.OperatorEntry => order.StrategyId == "MANUAL-OPERATOR-DIRECT" && order.CandidateId is null &&
                root.TryGetProperty("operatorOverride", out var flag) && flag.ValueKind == JsonValueKind.True &&
                PrivatePaperChartProjection.Text(root, "operatorOverrideActor") == "wishlist_operator" &&
                PrivatePaperChartProjection.Text(root, "operatorOverrideReason") is not null &&
                PrivatePaperChartProjection.Stamp(root, "operatorOverrideIssuedAtUtc") is not null,
            OrderIntentKind.PositionExit => order.GenerationId > 0 &&
                PrivatePaperChartProjection.Text(root, "reason") == "operator_exit",
            _ => false
        };
    }

    // Membership queries the live local Identity join instead of the possibly stale application cookie.
    private async Task<bool> IsAdministratorAsync(Guid userId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.UserRoles.AsNoTracking().AnyAsync(link => link.UserId == userId &&
            db.Users.Any(user => user.Id == link.UserId) &&
            db.Roles.Any(role => role.Id == link.RoleId && role.Name == TradingFlowRoles.Administrator), token);
    }

    // An exact job grant and an exact standalone intent grant cover only their own type of context.
    private bool IsGranted(Guid userId, Guid? jobId, Guid intentId) => Grants(userId).Any(grant =>
        jobId is { } id ? grant.PaperJobIds.Contains(id) : grant.ManualOrderIntentIds.Contains(intentId));

    // An absent or unbound account has no grants, irrespective of the role or private token.
    private IEnumerable<PrivatePaperChartGrant> Grants(Guid userId) =>
        userId == Guid.Empty || !account.EnforcementEnabled || String.IsNullOrEmpty(account.ExpectedAccountId)
            ? [] : options.CurrentValue.Grants.Where(grant => grant.UserId == userId && grant.PaperAccountId == account.ExpectedAccountId);

    // Use the chart's canonical symbol without widening the durable journal symbol used for exact joins.
    internal static string? CanonicalSymbol(string? symbol)
    {
        if (symbol is not { Length: > 0 and <= 20 }) return null;
        try { return ChartSeriesService.Normalize(new(symbol, "1d", "regular", "swing")).Symbol; }
        catch (ArgumentException) { return null; }
    }

    // Only entry kinds can originate a position generation.
    internal static bool IsEntry(OrderIntentKind kind) => kind is OrderIntentKind.StrategyEntry or OrderIntentKind.OperatorEntry;
}
