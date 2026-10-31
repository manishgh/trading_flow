using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TradingFlow.Application.Candidates;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Context;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Web.Services.Desk;

/// <summary>Reads the shared engine's persisted decisions without inventing market or eligibility evidence.</summary>
public sealed class DeskStrategyAssessmentService(
    IDbContextFactory<TradingFlowDbContext> databases,
    ICandidateQueryService candidates,
    IDecisionAuditQuery audits,
    StrategyArtifactCatalog catalog,
    IMemoryCache cache,
    TimeProvider clock)
{
    /// <summary>Lists catalog strategies and the most recent recorded strategy/run combinations.</summary>
    public async Task<IReadOnlyList<StockStrategyChoice>> ChoicesAsync(CancellationToken token)
    {
        await using var db = await databases.CreateDbContextAsync(token);
        // Transition IDs provide a bounded database-side ordering supported by SQLite;
        // sorting DateTimeOffset columns directly is not supported by its EF provider.
        var recorded = await (from candidate in db.Candidates.AsNoTracking()
            join run in db.ProductionRuns.AsNoTracking() on candidate.RunId equals run.RunId
            join transition in db.CandidateTransitions.AsNoTracking() on candidate.CandidateId equals transition.CandidateId
            where transition.Source == "strategy_decision_kernel" && candidate.SelectedStrategy != null &&
                transition.Sequence <= candidate.Version && transition.SemanticDecisionSha256 == candidate.SemanticDecisionSha256
            group transition by new { candidate.RunId, candidate.SelectedStrategy,
                candidate.StrategySemanticVersion, candidate.StrategyContentSha256, run.Profile, run.StartedAtUtc } into groupRows
            orderby groupRows.Max(item => item.TransitionId) descending
            select new {
                groupRows.Key.RunId, groupRows.Key.SelectedStrategy, groupRows.Key.StrategySemanticVersion,
                groupRows.Key.StrategyContentSha256, groupRows.Key.Profile, groupRows.Key.StartedAtUtc
            }).Take(100).ToArrayAsync(token);
        var artifacts = catalog.GetSnapshot().ExecutableArtifacts;
        var choices = recorded.Select(item => {
            var identity = new StrategyReference(item.SelectedStrategy!, item.StrategySemanticVersion, item.StrategyContentSha256);
            var artifact = artifacts.FirstOrDefault(artifact => artifact.Identity.StrategyId == identity.StrategyId &&
                artifact.Identity.SemanticVersion == identity.SemanticVersion && artifact.Identity.ContentSha256 == identity.ContentSha256);
            return new StockStrategyChoice(item.RunId, identity,
                artifact?.ResolvedStrategy.StrategyName ?? identity.StrategyId, item.Profile, item.StartedAtUtc);
        }).ToList();
        foreach (var artifact in artifacts)
        {
            var identity = new StrategyReference(artifact.Identity.StrategyId, artifact.Identity.SemanticVersion, artifact.Identity.ContentSha256);
            choices.Insert(0, new(null, identity, artifact.ResolvedStrategy.StrategyName, "current", null));
        }
        return choices;
    }

    /// <summary>Reads checks only from the requested exact run/strategy; a missing stock never borrows another run's decision.</summary>
    public async Task<StockStrategyAssessmentResponse> ReadAsync(
        Guid? runId, StrategyReference strategy, IReadOnlyList<string> requestedSymbols, CancellationToken token)
    {
        Validate(strategy, requestedSymbols);
        var symbols = requestedSymbols.Select(item => item.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        var now = clock.GetUtcNow();
        var run = runId is { } id ? await candidates.GetRunAsync(id, token) : null;
        if (runId is not null && (run is null || !run.Provenance.Strategies.Contains(strategy)))
            throw new ArgumentException("The selected run does not contain this exact strategy version.");
        var results = new List<StockStrategyAssessment>(symbols.Length);
        foreach (var symbol in symbols)
        {
            var candidate = run?.Candidates.Where(item => item.Strategy == strategy && item.Symbol == symbol)
                .OrderByDescending(item => item.RevalidatedAtUtc).ThenBy(item => item.CandidateId).FirstOrDefault();
            var audit = candidate is null ? null : await cache.GetOrCreateAsync(
                $"desk-strategy:{candidate.CandidateId:N}:{candidate.Version}", async entry => {
                    // Candidate versions advance when a decision changes; frequent quote
                    // updates do not need to reread every immutable audit record.
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
                    return await audits.GetAsync(candidate.CandidateId, token);
                });
            if (audit is not null && audit.Candidate.CandidateId != candidate!.CandidateId)
                throw new InvalidDataException("Recorded strategy evaluation belongs to another candidate.");
            results.Add(Project(symbol, strategy, runId, audit, now));
        }
        return new(ContractVersions.VersionOne, runId, strategy, now, results);
    }

    /// <summary>Projects original engine checks and their recorded values; never recalculates pass/fail in a client or adapter.</summary>
    public static StockStrategyAssessment Project(string symbol, StrategyReference strategy, Guid? runId,
        CandidateAuditResponse? audit, DateTimeOffset now)
    {
        if (audit is null)
            return Missing(symbol, strategy, runId);
        if (audit.Candidate.Symbol != symbol || audit.Candidate.Strategy != strategy)
            throw new InvalidDataException("Recorded strategy evaluation does not match the selected stock and strategy.");
        var checks = audit.GateEvaluations.Where(item => item.GateName.StartsWith("strategy:", StringComparison.Ordinal))
            .OrderBy(item => item.Order).ToArray();
        if (checks.Length == 0 || String.IsNullOrWhiteSpace(audit.SemanticDecisionSha256))
            return Missing(symbol, strategy, runId);
        var expired = audit.Candidate.ExpiresAtUtc <= now;
        return new(symbol, strategy, runId, audit.Candidate.CandidateId, "recorded",
            "Recorded strategy evaluation; not a current order recommendation. " +
            (expired ? "Its candidate has expired. " : "") +
            "Checks not reached by the engine are not marked as passed.",
            checks.Max(item => item.EvaluatedAtUtc), audit.Candidate.LatestCompletedCandleAtUtc,
            expired, checks, audit.Candidate.ExpiresAtUtc);
    }

    /// <summary>Identifies missing evaluation explicitly rather than falling back to the former generic daily score.</summary>
    private static StockStrategyAssessment Missing(string symbol, StrategyReference strategy, Guid? runId) =>
        new(symbol, strategy, runId, null, "missing", "No strategy evaluation for this stock. Select the current strategy and request checks.",
            null, null, false, []);

    /// <summary>Bounds read work and rejects ambiguous identities or invalid symbols before any repository call.</summary>
    private static void Validate(StrategyReference strategy, IReadOnlyList<string> symbols)
    {
        if (String.IsNullOrWhiteSpace(strategy.StrategyId) || String.IsNullOrWhiteSpace(strategy.SemanticVersion) ||
            String.IsNullOrWhiteSpace(strategy.ContentSha256) || strategy.ContentSha256.Length != 64 ||
            strategy.ContentSha256.Any(character => !Char.IsAsciiHexDigit(character)))
            throw new ArgumentException("An exact strategy ID, version and SHA-256 are required.");
        if (symbols.Count is < 1 or > DeskSources.DisplayLimit || symbols.Any(item => String.IsNullOrWhiteSpace(item) ||
            item.Trim().Length > 20 || item.Trim().Any(character => !Char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-')))
            throw new ArgumentException($"Request between 1 and {DeskSources.DisplayLimit} valid stock symbols.");
    }
}
