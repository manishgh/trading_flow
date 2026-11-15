using System.Runtime.CompilerServices;
using System.Text.Json;
using TradingFlow.Data.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Persistence;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace TradingFlow.Backtesting.Artifacts;

/// <summary>Reads original inline publications or the explicitly declared reference encoding; never guesses from payload shape.</summary>
public static class CandidateAuditReader
{
    public sealed record Observation(CandidateRecord Candidate, CandidateTransitionRecord? Transition);
    /// <summary>Requires exactly one sidecar for the new encoding and rejects ambiguous or unsupported declarations.</summary>
    public static void ValidateEncoding(BacktestArtifactManifest manifest)
    {
        var sidecars = manifest.Artifacts.Count(item => item.Kind == "candidate_evidence");
        if (manifest.CandidateAuditEncoding is null && sidecars == 0) return;
        if (manifest.CandidateAuditEncoding != CandidateEvidenceCodec.EncodingId || sidecars != 1)
            throw new InvalidDataException("Candidate audit encoding requires its exact evidence sidecar.");
    }

    /// <summary>Verifies sidecar bytes before opening its bounded disk index and checking artifact-set ownership.</summary>
    public static async Task<CandidateEvidenceIndex?> OpenEvidenceAsync(string root, BacktestArtifactManifest manifest, CancellationToken token)
    {
        ValidateEncoding(manifest);
        if (manifest.CandidateAuditEncoding is null) return null;
        var reference = manifest.Artifacts.Single(item => item.Kind == "candidate_evidence");
        try
        {
            await BacktestArtifactIntegrity.VerifyAsync(root, reference, token);
            return new CandidateEvidenceIndex(BacktestArtifactIntegrity.ResolveArtifactPath(root, reference.Path), manifest.ArtifactSetId, create: false);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("Candidate evidence sidecar is missing, corrupt or has an unsupported schema.", exception);
        }
    }

    /// <summary>Streams one hydrated transition at a time; null marks a discovered candidate without any transition.</summary>
    public static async IAsyncEnumerable<Observation> ReadAsync(string root, BacktestArtifactManifest manifest,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        using var evidence = await OpenEvidenceAsync(root, manifest, token);
        var reference = manifest.Artifacts.Single(item => item.Kind == "candidate_decisions");
        await BacktestArtifactIntegrity.VerifyAsync(root, reference, token);
        await using var stream = File.OpenRead(BacktestArtifactIntegrity.ResolveArtifactPath(root, reference.Path));
        await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<BacktestCandidateDecisionAudit>(stream, cancellationToken: token))
        {
            if (item is null) throw new InvalidDataException("Candidate audit contains an empty record.");
            if (item.Transitions.Count == 0) yield return new(item.Candidate, null);
            foreach (var transition in item.Transitions)
            {
                token.ThrowIfCancellationRequested();
                yield return new(item.Candidate, evidence is null ? transition : Hydrate(item.Candidate, transition, evidence));
            }
        }
    }

    /// <summary>Checks candidate/run ownership and preserves semantic hashes after reconstructing exact decision JSON.</summary>
    internal static CandidateTransitionRecord Hydrate(CandidateRecord candidate, CandidateTransitionRecord transition, CandidateEvidenceIndex evidence)
    {
        if (transition.RunId != candidate.RunId || transition.CandidateId != candidate.CandidateId)
            throw new InvalidDataException("Candidate evidence transition belongs to another candidate or run.");
        var hydrated = evidence.Hydrate(transition);
        if (hydrated.Source == "strategy_decision_kernel" && String.IsNullOrWhiteSpace(hydrated.EvidenceJson))
            throw new InvalidDataException("Kernel transition is missing its semantic decision evidence.");
        if (hydrated.EvidenceJson.Length > 0)
        {
            using var document = JsonDocument.Parse(hydrated.EvidenceJson);
            var hasSemantic = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("semantic", out _);
            if (hydrated.Source == "strategy_decision_kernel" && !hasSemantic)
                throw new InvalidDataException("Kernel transition is missing its semantic decision evidence.");
            // Hash the original canonical bytes: reserializing JsonElement can escape a typed date's '+' differently.
            if (hasSemantic && Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                    document.RootElement.GetProperty("semantic").GetRawText()))) != hydrated.SemanticDecisionSha256)
                throw new InvalidDataException("Hydrated candidate evidence differs from its semantic decision hash.");
        }
        return hydrated;
    }

    /// <summary>Verifies each referenced observation independently without retaining the candidate's expanded history.</summary>
    internal static void Verify(BacktestCandidateDecisionAudit audit, CandidateEvidenceIndex evidence)
    {
        foreach (var transition in audit.Transitions) _ = Hydrate(audit.Candidate, transition, evidence);
    }
}
