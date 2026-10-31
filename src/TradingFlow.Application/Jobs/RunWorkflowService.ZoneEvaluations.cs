using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Jobs;

namespace TradingFlow.Application.Jobs;

public sealed partial class RunWorkflowService
{
    // Read the committed artifact, not the bounded progress snapshot; all pages share its published hash.
    public async Task<RunZoneEvaluationPage?> ZoneEvaluationsAsync(Guid id, long offset = 0, int limit = 50,
        CancellationToken token = default)
    {
        if (offset < 0 || limit is < 1 or > 200) throw new InvalidOperationException("Zone evidence offset must be nonnegative and page size between 1 and 200.");
        var job = await jobs.GetJobAsync(id, token);
        if (job is null || job.JobType != "backtest") return null;
        if (job.Status != "completed" || String.IsNullOrWhiteSpace(job.ResultReference))
            return new(0, offset, limit, null, [], "Complete zone evidence is available after result publication.");
        var resultPath = Path.GetFullPath(job.ResultReference);
        var manifestPath = Path.ChangeExtension(resultPath, ".manifest.json");
        if (!File.Exists(manifestPath)) return new(0, offset, limit, null, [], "This result has no published zone evidence manifest.");
        RejectLinkedEvidencePath(manifestPath);
        await using var manifestStream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (manifestStream.Length > 1_000_000) throw new InvalidDataException("Zone evidence manifest exceeds the supported size.");
        var manifest = await JsonSerializer.DeserializeAsync<BacktestArtifactManifest>(manifestStream, Json, token)
            ?? throw new InvalidDataException("Zone evidence manifest is empty.");
        if (manifest.SchemaVersion != 1 || !manifest.PublicationComplete)
            throw new InvalidDataException("Zone evidence manifest is not a complete supported publication.");
        var reference = manifest.Artifacts.SingleOrDefault(item => item.Kind == "zone_evaluations");
        if (reference is null) return new(0, offset, limit, null, [], "This older result did not retain per-candle zone evidence.");
        await VerifyZonePublicationAsync(job, resultPath, manifest, token);
        var path = Path.ChangeExtension(resultPath, ".zone-evaluations.ndjson");
        var referencedPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(resultPath)!, reference.Path));
        if (reference.SchemaVersion != 1 || !reference.Complete || reference.RecordCount < 0 ||
            Path.IsPathRooted(reference.Path) || !String.Equals(path, referencedPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Zone evidence reference differs from its exact result sibling.");
        RejectLinkedEvidencePath(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != reference.ByteLength || !Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token))
            .Equals(reference.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Zone evidence differs from its published length or hash.");
        input.Position = 0;
        using var reader = new StreamReader(input);
        var rows = new List<RunZoneEvaluationDetail>(limit);
        long count = 0;
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (count >= offset && rows.Count < limit)
                rows.Add(JsonSerializer.Deserialize<RunZoneEvaluationDetail>(line, Json)
                    ?? throw new InvalidDataException("Zone evidence row is empty."));
            count++;
        }
        if (count != reference.RecordCount) throw new InvalidDataException("Zone evidence row count differs from its manifest.");
        var next = rows.Count > 0 && count - offset > rows.Count ? offset + rows.Count : (long?)null;
        return new(count, offset, limit, next, rows);
    }

    // The job's display name differs from its generated config name; bind through the committed job marker and hashed result.
    private static async Task VerifyZonePublicationAsync(PersistedJob job, string resultPath,
        BacktestArtifactManifest manifest, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(resultPath)!;
        var markerPath = Path.Combine(directory, DurableJobResultPublication.MarkerFileName);
        RejectLinkedEvidencePath(markerPath);
        await using var marker = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (marker.Length > 16_384) throw new InvalidDataException("Zone evidence job publication marker exceeds the supported size.");
        var publication = await JsonSerializer.DeserializeAsync<DurableJobPublication>(marker, Json, token)
            ?? throw new InvalidDataException("Zone evidence job publication marker is empty.");
        if (publication.JobId != job.Id || publication.JobType != job.JobType ||
            String.IsNullOrWhiteSpace(publication.ResultReference) ||
            !String.Equals(Path.GetFullPath(publication.ResultReference), resultPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Zone evidence publication marker differs from the completed job.");

        var reference = manifest.Artifacts.SingleOrDefault(item => item.Kind == "backtest_result")
            ?? throw new InvalidDataException("Zone evidence manifest has no hashed result identity.");
        var path = Path.GetFullPath(Path.Combine(directory, reference.Path));
        if (reference.SchemaVersion != 1 || !reference.Complete || reference.RecordCount != 1 ||
            Path.IsPathRooted(reference.Path) || !String.Equals(path, resultPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Zone evidence result reference differs from the completed job.");
        RejectLinkedEvidencePath(path);
        await using var result = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (result.Length != reference.ByteLength || !Convert.ToHexStringLower(await SHA256.HashDataAsync(result, token))
            .Equals(reference.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Zone evidence result differs from its published length or hash.");
        result.Position = 0;
        var identity = await JsonSerializer.DeserializeAsync<ZoneResultIdentity>(result, Json, token)
            ?? throw new InvalidDataException("Zone evidence result identity is empty.");
        if (String.IsNullOrWhiteSpace(identity.RunName) || identity.RunName != manifest.RunName ||
            String.IsNullOrWhiteSpace(identity.ResultPath) ||
            !String.Equals(Path.GetFullPath(identity.ResultPath), resultPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Zone evidence manifest identity differs from its published result.");
    }

    private sealed record ZoneResultIdentity(string RunName, string ResultPath);

    // A retained file cannot redirect evidence reads through a filesystem link.
    private static void RejectLinkedEvidencePath(string path)
    {
        for (string? current = Path.GetFullPath(path); !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked zone evidence paths are unavailable.");
    }
}
