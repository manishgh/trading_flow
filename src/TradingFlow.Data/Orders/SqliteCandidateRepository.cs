using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Data.Orders;

/// <summary>
/// Persists discovery evidence and applies append-only candidate transitions with
/// optimistic version checks. This repository records decisions; it never creates one.
/// </summary>
public sealed class SqliteCandidateRepository(
    IDbContextFactory<TradingFlowDbContext> contextFactory) : ICandidateRepository
{
    public async Task<CandidateRecord> UpsertDiscoveryAsync(
        ProductionRun run,
        CandidateRecord candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateDiscovery(candidate);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await ProductionRunPersistence.EnsureAsync(context, run, cancellationToken);
        var existing = await context.Candidates.SingleOrDefaultAsync(
            item => item.CandidateId == candidate.CandidateId,
            cancellationToken);
        if (existing is null)
        {
            candidate.Version = 0;
            context.Candidates.Add(candidate);
        }
        else
        {
            if (existing.State is not (StrategyCandidateState.Discovered or
                StrategyCandidateState.DataWarming or
                StrategyCandidateState.Qualified or
                StrategyCandidateState.Armed or
                StrategyCandidateState.Disarmed or
                StrategyCandidateState.DataError))
            {
                throw new InvalidOperationException(
                    $"Candidate {candidate.CandidateId} in terminal state {existing.State} cannot be revalidated.");
            }

            RequireSameIdentity(candidate, existing);
            RequireSameDiscoveryEvidence(candidate, existing);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return existing ?? candidate;
    }

    public async Task<CandidateRecord> ApplyDecisionAsync(
        ProductionRun run,
        Guid candidateId,
        int expectedVersion,
        string semanticDecisionSha256,
        DateTimeOffset expiresAtUtc,
        IReadOnlyList<CandidateTransitionAppendRequest> transitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(transitions);
        ValidateHash(semanticDecisionSha256);
        RequireUtc(expiresAtUtc, nameof(expiresAtUtc));
        if (candidateId == Guid.Empty || expectedVersion < 0 || transitions.Count == 0)
        {
            throw new InvalidOperationException(
                "A candidate decision requires identity, expected version, and at least one transition/evaluation event.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await ProductionRunPersistence.EnsureAsync(context, run, cancellationToken);
        var candidate = await context.Candidates.SingleOrDefaultAsync(
            item => item.CandidateId == candidateId,
            cancellationToken)
            ?? throw new InvalidOperationException($"Candidate {candidateId} was not persisted before evaluation.");
        if (candidate.RunId != run.RunId || candidate.Version != expectedVersion)
        {
            throw new DbUpdateConcurrencyException(
                $"Candidate {candidateId} expected version {expectedVersion}, actual {candidate.Version}.");
        }

        var current = candidate.State;
        var sequence = candidate.Version;
        foreach (var transition in transitions)
        {
            ValidateTransition(transition);
            if (transition.PreviousState != current)
            {
                throw new InvalidOperationException(
                    $"Candidate transition expected {current} but supplied {transition.PreviousState}.");
            }

            StrategyCandidateStateMachine.RequireTransition(current, transition.NewState);
            sequence++;
            context.CandidateTransitions.Add(new CandidateTransitionRecord
            {
                CandidateId = candidateId,
                Sequence = sequence,
                PreviousState = current,
                NewState = transition.NewState,
                OccurredAtUtc = transition.OccurredAtUtc,
                ReasonCode = transition.ReasonCode.Trim(),
                Source = transition.Source.Trim(),
                SemanticDecisionSha256 = semanticDecisionSha256,
                EvidenceJson = transition.EvidenceJson,
                RunId = run.RunId,
                SchemaVersion = run.SchemaVersion,
                ConfigHash = run.ConfigHash,
                CodeVersion = run.CodeVersion
            });
            current = transition.NewState;
        }

        candidate.State = current;
        candidate.Version = sequence;
        candidate.ExpiresAtUtc = expiresAtUtc;
        candidate.SemanticDecisionSha256 = semanticDecisionSha256;
        candidate.RevalidatedAtUtc = transitions[^1].OccurredAtUtc;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return candidate;
    }

    public async Task<CandidateRecord?> GetAsync(
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        if (candidateId == Guid.Empty)
        {
            return null;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Candidates
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.CandidateId == candidateId, cancellationToken);
    }

    public async Task<IReadOnlyList<CandidateTransitionRecord>> GetTransitionsAsync(
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        if (candidateId == Guid.Empty)
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.CandidateTransitions
            .AsNoTracking()
            .Where(item => item.CandidateId == candidateId)
            .OrderBy(item => item.Sequence)
            .ToArrayAsync(cancellationToken);
    }

    private static void ValidateDiscovery(CandidateRecord candidate)
    {
        if (candidate.CandidateId == Guid.Empty || candidate.RunId == Guid.Empty ||
            String.IsNullOrWhiteSpace(candidate.Symbol) ||
            String.IsNullOrWhiteSpace(candidate.SelectedStrategy) ||
            String.IsNullOrWhiteSpace(candidate.DiscoverySource) ||
            candidate.State is not (StrategyCandidateState.Discovered or StrategyCandidateState.DataWarming))
        {
            throw new InvalidOperationException(
                "Discovery persistence requires identity, run provenance, symbol, strategy, source, and a pre-admission state.");
        }

        ValidateHash(candidate.StrategyContentSha256);
        if (String.IsNullOrWhiteSpace(candidate.AdmissionProfileId) ||
            String.IsNullOrWhiteSpace(candidate.AdmissionProfileVersion) ||
            String.IsNullOrWhiteSpace(candidate.SetupKey))
        {
            throw new InvalidOperationException(
                "Candidate admission profile and immutable setup identity are required.");
        }

        RequireUtc(candidate.DiscoveredAtUtc, nameof(candidate.DiscoveredAtUtc));
        RequireUtc(candidate.RevalidatedAtUtc, nameof(candidate.RevalidatedAtUtc));
        RequireUtc(candidate.ExpiresAtUtc, nameof(candidate.ExpiresAtUtc));
        RequireUtc(candidate.DiscoveryWindowStartUtc, nameof(candidate.DiscoveryWindowStartUtc));
        RequireUtc(candidate.DiscoveryWindowEndUtc, nameof(candidate.DiscoveryWindowEndUtc));
        if (candidate.ExpiresAtUtc <= candidate.DiscoveredAtUtc)
        {
            throw new InvalidOperationException("Candidate expiry must be later than discovery.");
        }
    }

    private static void ValidateTransition(CandidateTransitionAppendRequest transition)
    {
        RequireUtc(transition.OccurredAtUtc, nameof(transition.OccurredAtUtc));
        if (String.IsNullOrWhiteSpace(transition.ReasonCode) ||
            String.IsNullOrWhiteSpace(transition.Source) ||
            String.IsNullOrWhiteSpace(transition.EvidenceJson))
        {
            throw new InvalidOperationException(
                "Candidate transition reason, source, and complete decision evidence are required.");
        }
    }

    private static void RequireSameIdentity(CandidateRecord source, CandidateRecord target)
    {
        if (source.RunId != target.RunId ||
            !source.Symbol.Equals(target.Symbol, StringComparison.OrdinalIgnoreCase) ||
            !source.SelectedStrategy!.Equals(target.SelectedStrategy, StringComparison.Ordinal) ||
            !source.StrategyContentSha256.Equals(target.StrategyContentSha256, StringComparison.Ordinal) ||
            !source.AdmissionProfileId.Equals(target.AdmissionProfileId, StringComparison.Ordinal) ||
            !source.AdmissionProfileVersion.Equals(target.AdmissionProfileVersion, StringComparison.Ordinal) ||
            !source.SetupKey.Equals(target.SetupKey, StringComparison.Ordinal) ||
            source.DiscoveryWindowStartUtc != target.DiscoveryWindowStartUtc ||
            source.DiscoveryWindowEndUtc != target.DiscoveryWindowEndUtc)
        {
            throw new InvalidOperationException("Candidate revalidation cannot change immutable setup identity.");
        }
    }

    private static void RequireSameDiscoveryEvidence(CandidateRecord source, CandidateRecord target)
    {
        if (!source.DiscoverySource.Equals(target.DiscoverySource, StringComparison.Ordinal) ||
            source.DiscoveredAtUtc != target.DiscoveredAtUtc ||
            source.ExpiresAtUtc != target.ExpiresAtUtc ||
            !source.SetupScoresJson.Equals(target.SetupScoresJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Persisted discovery evidence is immutable; changed evidence requires a new candidate identity.");
        }
    }

    private static void ValidateHash(string value)
    {
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("A 64-character SHA-256 hash is required.");
        }
    }

    private static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{name} must be UTC.");
        }
    }
}
