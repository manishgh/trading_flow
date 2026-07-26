using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace TradingFlow.Research.Momentum;

/// <summary>
/// Creates deterministic, defensively copied payloads for the canonical swing
/// research outputs. The evidence packager remains responsible for durable,
/// content-addressed storage.
/// </summary>
public static class MomentumCanonicalArtifactBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static IReadOnlyList<MomentumCanonicalArtifact> Build(
        CrossSectionalMomentumReport report,
        MomentumResearchAuditReport audit)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(audit);

        return new ReadOnlyCollection<MomentumCanonicalArtifact>(
        [
            Create(MomentumCanonicalArtifactName.Report, report),
            Create(
                MomentumCanonicalArtifactName.FormationLedger,
                report.FormationLedger),
            Create(MomentumCanonicalArtifactName.AuditReport, audit)
        ]);
    }

    private static MomentumCanonicalArtifact Create<T>(string name, T payload)
    {
        var utf8Json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return new MomentumCanonicalArtifact(
            name,
            Convert.ToHexString(SHA256.HashData(utf8Json)).ToLowerInvariant(),
            utf8Json);
    }
}

public sealed class MomentumCanonicalArtifact
{
    private readonly byte[] utf8Json;

    internal MomentumCanonicalArtifact(
        string name,
        string sha256,
        ReadOnlySpan<byte> utf8Json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (utf8Json.IsEmpty)
        {
            throw new ArgumentException(
                "A canonical momentum artifact cannot be empty.",
                nameof(utf8Json));
        }

        Name = name;
        Sha256 = sha256;
        this.utf8Json = utf8Json.ToArray();
    }

    public string Name { get; }

    public string Sha256 { get; }

    public byte[] GetUtf8Json() => utf8Json.ToArray();
}

public static class MomentumCanonicalArtifactName
{
    public const string Report = "momentum-report";
    public const string FormationLedger = "momentum-formation-ledger";
    public const string AuditReport = "momentum-audit-report";
}
