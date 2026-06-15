using System.Text.Json;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

/// <summary>
/// Persists mobile automation session summaries so Android-triggered paper
/// executions survive backend restarts with an auditable trail.
/// </summary>
public sealed class MobileAutomationSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string filePath;
    private readonly IArtifactWriter artifactWriter;

    public MobileAutomationSessionStore(ProjectPaths paths, IArtifactWriter artifactWriter)
    {
        filePath = Path.Combine(paths.RepositoryRoot, "data", "runtime", "mobile", "automation-sessions.json");
        this.artifactWriter = artifactWriter;
    }

    public async Task<IReadOnlyList<MobileAutomationSessionSnapshot>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return Array.Empty<MobileAutomationSessionSnapshot>();
        }

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var sessions = await JsonSerializer.DeserializeAsync<List<MobileAutomationSessionSnapshot>>(stream, JsonOptions, cancellationToken);
        return sessions is null
            ? Array.Empty<MobileAutomationSessionSnapshot>()
            : sessions;
    }

    public Task SaveAsync(IReadOnlyCollection<MobileAutomationSessionSnapshot> sessions, CancellationToken cancellationToken = default)
    {
        var trimmed = sessions
            .OrderByDescending(x => x.CreatedAt)
            .Take(200)
            .ToArray();
        var json = JsonSerializer.Serialize(trimmed, JsonOptions);
        return artifactWriter.WriteTextAsync(filePath, json, cancellationToken);
    }
}
