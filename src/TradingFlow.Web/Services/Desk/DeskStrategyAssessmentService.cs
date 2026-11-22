using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TradingFlow.Application.Candidates;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Strategies;
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
    // How many recorded runs of one older version are read to find its direction.
    private const int MaximumDirectionLookups = 3;

    /// <summary>Determines whether a recorded run's strategy may appear in the Desk picker without deleting persisted audit rows.</summary>
    private bool IsPickerEligibleRecordedStrategy(
        StrategyReference identity,
        IReadOnlyList<StrategyArtifact> artifacts,
        out StrategyArtifact? matchedArtifact,
        out StrategyArtifact? enabledFamilyArtifact)
    {
        matchedArtifact = artifacts.FirstOrDefault(artifact =>
            artifact.Identity.StrategyId == identity.StrategyId &&
            artifact.Identity.SemanticVersion == identity.SemanticVersion &&
            artifact.Identity.ContentSha256 == identity.ContentSha256);
        if (matchedArtifact is not null)
        {
            enabledFamilyArtifact = catalog.GetDisabledReason(matchedArtifact) is null ? matchedArtifact : null;
            return enabledFamilyArtifact is not null;
        }

        // Retired strategies absent from the catalog (such as swing.connors-rsi2) and disabled strategy families stay hidden in the picker.
        enabledFamilyArtifact = artifacts.FirstOrDefault(artifact =>
            artifact.Identity.StrategyId == identity.StrategyId &&
            catalog.GetDisabledReason(artifact) is null);
        return enabledFamilyArtifact is not null;
    }

    /// <summary>
    /// Resolves the normalized trade direction ("long" or "short") from the exact catalog artifact, or for an older version
    /// from the direction its own run recorded. Null when neither exists: another version's direction is never borrowed.
    /// </summary>
    private static string? ResolveStrategyDirection(StrategyArtifact? exactArtifact, string? recordedDirection)
    {
        var raw = exactArtifact?.ResolvedStrategy.Direction ?? recordedDirection;
        return String.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Formats a clean display name for a recorded older version of an enabled strategy family without copying the
    /// newer version's parenthetical gate prose, appending <c>· older version</c> so Desk picker labels stay readable.
    /// </summary>
    private static string FormatOlderVersionName(StrategyReference identity, StrategyArtifact? enabledFamilyArtifact)
    {
        var baseName = enabledFamilyArtifact?.ResolvedStrategy.StrategyName;
        if (String.IsNullOrWhiteSpace(baseName))
        {
            return $"{identity.StrategyId} · older version";
        }

        // Strip trailing parenthetical gate prose belonging to the newer catalog artifact before appending the older-version tag.
        var trimmed = baseName.Trim();
        var openParen = trimmed.LastIndexOf('(');
        if (openParen > 0 && trimmed.EndsWith(')'))
        {
            var withoutParen = trimmed[..openParen].TrimEnd();
            if (withoutParen.Length > 0)
            {
                trimmed = withoutParen;
            }
        }

        return $"{trimmed} · older version";
    }

    /// <summary>
    /// Extracts the normalized trade direction (<c>"long"</c> or <c>"short"</c>) recorded in a
    /// <c>strategy_decision_kernel</c> transition's canonical JSON audit (<c>semantic.direction</c>,
    /// <c>semantic.orderPlan.direction</c>, or the <c>strategy_entry_rules</c> rule's nested <c>evidenceJson</c> string
    /// payload). Returns null when the recorded evidence failed before entry rules or contains malformed JSON.
    /// </summary>
    internal static string? TryExtractRecordedDirection(string? evidenceJson)
    {
        if (String.IsNullOrWhiteSpace(evidenceJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(evidenceJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("semantic", out var semantic) && semantic.ValueKind == JsonValueKind.Object)
            {
                if (TryReadNormalizedDirection(semantic, "direction", out var semanticDirection))
                {
                    return semanticDirection;
                }

                if (semantic.TryGetProperty("orderPlan", out var orderPlan) &&
                    orderPlan.ValueKind == JsonValueKind.Object &&
                    TryReadNormalizedDirection(orderPlan, "direction", out var planDirection))
                {
                    return planDirection;
                }
            }

            if (root.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
            {
                foreach (var rule in rules.EnumerateArray())
                {
                    if (rule.ValueKind != JsonValueKind.Object ||
                        !rule.TryGetProperty("source", out var source) ||
                        source.ValueKind != JsonValueKind.String ||
                        !String.Equals(source.GetString(), "strategy_entry_rules", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // StrategyDecisionKernel.AddRule serializes rule details into a nested JSON string property "evidenceJson".
                    if (rule.TryGetProperty("evidenceJson", out var nestedEvidence) &&
                        nestedEvidence.ValueKind == JsonValueKind.String)
                    {
                        var nestedJson = nestedEvidence.GetString();
                        if (!String.IsNullOrWhiteSpace(nestedJson))
                        {
                            try
                            {
                                using var nestedDoc = JsonDocument.Parse(nestedJson);
                                if (TryExtractDirectionFromRulePayload(nestedDoc.RootElement) is { } nestedDirection)
                                {
                                    return nestedDirection;
                                }
                            }
                            catch (JsonException)
                            {
                                // Malformed nested rule payload must not fail the picker.
                            }
                        }
                    }

                    if (rule.TryGetProperty("inputs", out var inputs) &&
                        TryExtractDirectionFromRulePayload(inputs) is { } inputDirection)
                    {
                        return inputDirection;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    // Reads recorded trade direction from a parsed strategy_entry_rules evidence object (admitted direction or directional rejection).
    private static string? TryExtractDirectionFromRulePayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryReadNormalizedDirection(payload, "direction", out var ruleDirection))
        {
            return ruleDirection;
        }

        var longRej = payload.TryGetProperty("longRejection", out var lr) && lr.ValueKind == JsonValueKind.String
            ? lr.GetString() : null;
        var shortRej = payload.TryGetProperty("shortRejection", out var sr) && sr.ValueKind == JsonValueKind.String
            ? sr.GetString() : null;
        if (String.Equals(longRej, "direction_not_long", StringComparison.Ordinal) &&
            !String.Equals(shortRej, "direction_not_short", StringComparison.Ordinal))
        {
            return "short";
        }

        if (String.Equals(shortRej, "direction_not_short", StringComparison.Ordinal) &&
            !String.Equals(longRej, "direction_not_long", StringComparison.Ordinal))
        {
            return "long";
        }

        return null;
    }

    // Reads a JSON string property and normalizes valid "long" / "short" trade directions.
    private static bool TryReadNormalizedDirection(JsonElement container, string propertyName, out string direction)
    {
        direction = String.Empty;
        if (!container.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = prop.GetString()?.Trim().ToLowerInvariant();
        if (value is "long" or "short")
        {
            direction = value;
            return true;
        }

        return false;
    }

    // Streams at most 5 persisted kernel transitions for an older-version run (filtering by transition.RunId and prioritizing
    // triggered/armed rows first), stopping at the first resolved direction instead of buffering large audit blobs.
    private static async Task<string?> ResolveOlderVersionRecordedDirectionAsync(
        TradingFlowDbContext db,
        Guid runId,
        StrategyReference identity,
        CancellationToken token)
    {
        var evidenceStream = (from transition in db.CandidateTransitions.AsNoTracking()
            join candidate in db.Candidates.AsNoTracking() on transition.CandidateId equals candidate.CandidateId
            where transition.RunId == runId &&
                candidate.RunId == runId &&
                candidate.SelectedStrategy == identity.StrategyId &&
                candidate.StrategySemanticVersion == identity.SemanticVersion &&
                candidate.StrategyContentSha256 == identity.ContentSha256 &&
                transition.Source == "strategy_decision_kernel" &&
                transition.Sequence <= candidate.Version &&
                transition.SemanticDecisionSha256 == candidate.SemanticDecisionSha256
            orderby (transition.NewState == StrategyCandidateState.Triggered || transition.NewState == StrategyCandidateState.Armed ? 1 : 0) descending,
                transition.TransitionId descending
            select transition.EvidenceJson).Take(5).AsAsyncEnumerable();

        await foreach (var json in evidenceStream.WithCancellation(token))
        {
            if (TryExtractRecordedDirection(json) is { } direction)
            {
                return direction;
            }
        }

        return null;
    }

    /// <summary>Lists enabled choices for current checks while retaining only non-retired, enabled recorded strategy/run choices.</summary>
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
        var choices = new List<StockStrategyChoice>();
        // Memoize immutable older-version directions by exact (StrategyId, SemanticVersion, ContentSha256) across rows and requests.
        var memoizedOlderDirections = new Dictionary<StrategyReference, string?>();
        var lookups = new Dictionary<StrategyReference, int>();
        foreach (var item in recorded)
        {
            var identity = new StrategyReference(item.SelectedStrategy!, item.StrategySemanticVersion, item.StrategyContentSha256);
            if (!IsPickerEligibleRecordedStrategy(identity, artifacts, out var exactArtifact, out var familyArtifact))
                continue;
            string? recordedDirection = null;
            if (exactArtifact is null)
            {
                var directionCacheKey = $"desk-strategy-direction:{identity.StrategyId}:{identity.SemanticVersion}:{identity.ContentSha256}";
                if (memoizedOlderDirections.TryGetValue(identity, out var memoized))
                {
                    recordedDirection = memoized;
                }
                else if (cache.TryGetValue(directionCacheKey, out string? cachedDirection) && cachedDirection is not null)
                {
                    recordedDirection = cachedDirection.Length == 0 ? null : cachedDirection;
                    memoizedOlderDirections[identity] = recordedDirection;
                }
                else
                {
                    // A run whose candidates all failed early records no direction, so a few later runs are tried before the
                    // answer "none recorded" (kept as an empty text) is remembered for a short while.
                    recordedDirection = await ResolveOlderVersionRecordedDirectionAsync(db, item.RunId, identity, token);
                    lookups[identity] = lookups.GetValueOrDefault(identity) + 1;
                    if (recordedDirection is not null || lookups[identity] >= MaximumDirectionLookups)
                    {
                        memoizedOlderDirections[identity] = recordedDirection;
                        cache.Set(directionCacheKey, recordedDirection ?? String.Empty,
                            recordedDirection is null ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(1));
                    }
                }
            }
            var direction = ResolveStrategyDirection(exactArtifact, recordedDirection);
            var displayName = exactArtifact is not null
                ? exactArtifact.ResolvedStrategy.StrategyName
                : FormatOlderVersionName(identity, familyArtifact);
            choices.Add(new StockStrategyChoice(item.RunId, identity, displayName, item.Profile, item.StartedAtUtc, direction));
        }
        foreach (var artifact in artifacts.Where(item => catalog.GetDisabledReason(item) is null))
        {
            var identity = new StrategyReference(artifact.Identity.StrategyId, artifact.Identity.SemanticVersion, artifact.Identity.ContentSha256);
            var direction = ResolveStrategyDirection(artifact, null);
            choices.Insert(0, new(null, identity, artifact.ResolvedStrategy.StrategyName, "current", null, direction));
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
