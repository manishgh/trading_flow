using TradingFlow.Domain.Strategies;

namespace TradingFlow.Tests;

internal sealed class StrategyAuthorizationTestRegistry :
    IStrategyAuthorizationPolicy,
    IStrategyExperimentAuthorizationCommands
{
    private readonly object gate = new();
    private readonly Dictionary<(StrategyArtifactIdentity Identity, StrategyExecutionAuthorization Authorization), StrategyAuthorizationGrant> grants = [];
    private readonly HashSet<StrategyArtifactIdentity> suspended = [];
    private readonly Dictionary<Guid, StrategyEntryAdmission> admissions = [];
    private long sequence;

    public void Seed(
        StrategyArtifactIdentity identity,
        StrategyExecutionAuthorization authorization,
        string decisionId = "test-grant")
    {
        lock (gate)
        {
            grants[(identity, authorization)] = new StrategyAuthorizationGrant(
                identity,
                authorization,
                decisionId,
                DateTimeOffset.UnixEpoch);
        }
    }

    public Task<IReadOnlyList<StrategyAuthorizationGrant>> GetActiveGrantsAsync(
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            IReadOnlyList<StrategyAuthorizationGrant> active = grants.Values
                .Where(grant => !suspended.Contains(grant.Identity))
                .Where(grant => grant.Authorization switch
                {
                    StrategyExecutionAuthorization.PaperExperiment => true,
                    StrategyExecutionAuthorization.PaperShadow =>
                        grants.ContainsKey((grant.Identity, StrategyExecutionAuthorization.PaperExperiment)),
                    StrategyExecutionAuthorization.Validated =>
                        grants.ContainsKey((grant.Identity, StrategyExecutionAuthorization.PaperExperiment)) &&
                        grants.ContainsKey((grant.Identity, StrategyExecutionAuthorization.PaperShadow)),
                    _ => false
                })
                .ToArray();
            return Task.FromResult(active);
        }
    }

    public Task<StrategyEntryAdmission> AdmitNewEntryAsync(
        StrategyArtifactIdentity identity,
        StrategySelectionMode selectionMode,
        Guid intentId,
        DateTimeOffset admittedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var authorization = selectionMode switch
        {
            StrategySelectionMode.RunPaperExperiment => StrategyExecutionAuthorization.PaperExperiment,
            StrategySelectionMode.RunPaperShadow => StrategyExecutionAuthorization.PaperShadow,
            StrategySelectionMode.RunLive => StrategyExecutionAuthorization.Validated,
            _ => throw new UnauthorizedAccessException("Test execution mode is not authorized.")
        };
        lock (gate)
        {
            if (admissions.TryGetValue(intentId, out var existing))
            {
                if (existing.Identity != identity || existing.SelectionMode != selectionMode)
                {
                    throw new InvalidDataException(
                        $"Entry intent '{intentId}' was reused with a different strategy authorization context.");
                }

                return Task.FromResult(existing);
            }

            var prerequisitesSatisfied = authorization switch
            {
                StrategyExecutionAuthorization.PaperExperiment => true,
                StrategyExecutionAuthorization.PaperShadow =>
                    grants.ContainsKey((identity, StrategyExecutionAuthorization.PaperExperiment)),
                StrategyExecutionAuthorization.Validated =>
                    grants.ContainsKey((identity, StrategyExecutionAuthorization.PaperExperiment)) &&
                    grants.ContainsKey((identity, StrategyExecutionAuthorization.PaperShadow)),
                _ => false
            };
            if (suspended.Contains(identity) ||
                !prerequisitesSatisfied ||
                !grants.TryGetValue((identity, authorization), out var grant))
            {
                throw new UnauthorizedAccessException("Test strategy is not authorized.");
            }

            var admission = new StrategyEntryAdmission(
                intentId,
                identity,
                selectionMode,
                grant.GrantDecisionId,
                ++sequence,
                admittedAtUtc);
            admissions.Add(intentId, admission);
            return Task.FromResult(admission);
        }
    }

    public Task GrantPaperExperimentAsync(
        StrategyArtifactIdentity identity,
        string decisionId,
        string actor,
        DateTimeOffset decidedAtUtc,
        string reason,
        CancellationToken cancellationToken = default)
    {
        Seed(identity, StrategyExecutionAuthorization.PaperExperiment, decisionId);
        return Task.CompletedTask;
    }

    public Task RevokePaperExperimentAsync(
        StrategyArtifactIdentity identity,
        string decisionId,
        string targetDecisionId,
        string actor,
        DateTimeOffset decidedAtUtc,
        string reason,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            grants.Remove((identity, StrategyExecutionAuthorization.PaperExperiment));
        }

        return Task.CompletedTask;
    }

    public Task SuspendPaperExperimentAsync(
        StrategyArtifactIdentity identity,
        string decisionId,
        string targetDecisionId,
        string actor,
        DateTimeOffset decidedAtUtc,
        string reason,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            suspended.Add(identity);
        }

        return Task.CompletedTask;
    }

    public Task ResumePaperExperimentAsync(
        StrategyArtifactIdentity identity,
        string decisionId,
        string targetDecisionId,
        string actor,
        DateTimeOffset decidedAtUtc,
        string reason,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            suspended.Remove(identity);
        }

        return Task.CompletedTask;
    }

    public Task SupersedePaperExperimentAsync(
        StrategyArtifactIdentity existingIdentity,
        string supersessionDecisionId,
        string targetDecisionId,
        StrategyArtifactIdentity replacementIdentity,
        string replacementDecisionId,
        string actor,
        DateTimeOffset decidedAtUtc,
        string reason,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            grants.Remove((existingIdentity, StrategyExecutionAuthorization.PaperExperiment));
            grants[(replacementIdentity, StrategyExecutionAuthorization.PaperExperiment)] =
                new StrategyAuthorizationGrant(
                    replacementIdentity,
                    StrategyExecutionAuthorization.PaperExperiment,
                    replacementDecisionId,
                    decidedAtUtc);
        }

        return Task.CompletedTask;
    }
}
