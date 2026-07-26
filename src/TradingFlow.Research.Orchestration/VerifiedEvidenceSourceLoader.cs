using System.Security.Cryptography;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Research.Orchestration;

internal sealed class VerifiedEvidenceSourceLoader(
    IEvidenceCatalog catalog,
    IImmutableArtifactStore artifactStore)
{
    public async Task<VerifiedSourceObservation> LoadAsync(
        EvidenceSourceObservation observation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var registered = await catalog.GetSourceObservationAsync(
            observation.ObservationId,
            cancellationToken);
        if (registered is null ||
            !registered.Artifact.Content.Sha256.Equals(
                observation.Artifact.Content.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Raw observation '{observation.ObservationId}' is not catalog committed with the expected hash.");
        }

        var verification = await artifactStore.VerifyAsync(
            registered.Artifact,
            cancellationToken);
        if (!verification.IsValid)
        {
            throw new InvalidDataException(
                $"Raw observation '{registered.ObservationId}' failed verification: " +
                $"{verification.FailureReason}");
        }

        if (registered.Artifact.Content.ByteLength > Int32.MaxValue)
        {
            throw new InvalidDataException(
                $"Raw observation '{registered.ObservationId}' exceeds the in-memory normalization boundary.");
        }

        var bytes = new byte[(int)registered.Artifact.Content.ByteLength];
        await using var stream = await artifactStore.OpenReadAsync(
            registered.Artifact,
            cancellationToken);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(
                bytes.AsMemory(offset, bytes.Length - offset),
                cancellationToken);
            if (read == 0)
            {
                throw new InvalidDataException(
                    $"Raw observation '{registered.ObservationId}' ended before its declared length.");
            }

            offset += read;
        }

        if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
        {
            throw new InvalidDataException(
                $"Raw observation '{registered.ObservationId}' exceeds its declared length.");
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!actualHash.Equals(
                registered.Artifact.Content.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Raw observation '{registered.ObservationId}' bytes do not match its SHA-256.");
        }

        return new VerifiedSourceObservation(registered, bytes);
    }

    public async Task<IReadOnlyList<EvidenceSourceReference>> ResolveAsync(
        IEnumerable<EvidenceRowSourceAddress> addresses,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var resolved = new List<EvidenceSourceReference>();
        foreach (var address in addresses
                     .Distinct()
                     .OrderBy(value => value.ObservationId, StringComparer.Ordinal)
                     .ThenBy(value => value.ObservationSha256, StringComparer.Ordinal))
        {
            var observation = await catalog.GetSourceObservationAsync(
                address.ObservationId,
                cancellationToken) ??
                throw new InvalidDataException(
                    $"Lineage observation '{address.ObservationId}' is not catalog committed.");
            if (!observation.Artifact.Content.Sha256.Equals(
                    address.ObservationSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Lineage observation '{address.ObservationId}' has a conflicting SHA-256.");
            }

            var verification = await artifactStore.VerifyAsync(
                observation.Artifact,
                cancellationToken);
            if (!verification.IsValid)
            {
                throw new InvalidDataException(
                    $"Lineage observation '{address.ObservationId}' failed verification: " +
                    $"{verification.FailureReason}");
            }

            resolved.Add(observation.ToReference());
        }

        return resolved;
    }
}

internal sealed record VerifiedSourceObservation(
    EvidenceSourceObservation Observation,
    byte[] Bytes);
