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
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(2);

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
        var cutoff = DateTimeOffset.UtcNow.Subtract(RetentionWindow);
        return sessions is null
            ? Array.Empty<MobileAutomationSessionSnapshot>()
            : sessions
                .Where(session => IsRetained(session, cutoff))
                .OrderByDescending(x => x.CreatedAt)
                .ToArray();
    }

    public Task SaveAsync(IReadOnlyCollection<MobileAutomationSessionSnapshot> sessions, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(RetentionWindow);
        var trimmed = sessions
            .Where(session => IsRetained(session, cutoff))
            .OrderByDescending(x => x.CreatedAt)
            .Take(200)
            .ToArray();
        var json = JsonSerializer.Serialize(trimmed, JsonOptions);
        return artifactWriter.WriteTextAsync(filePath, json, cancellationToken);
    }

    public static bool IsRetained(MobileAutomationSessionSnapshot session, DateTimeOffset cutoff)
    {
        return !IsTerminal(session.Status) || ActivityTimestamp(session) >= cutoff;
    }

    private static DateTimeOffset ActivityTimestamp(MobileAutomationSessionSnapshot session)
    {
        return session.FinishedAt ?? session.StartedAt ?? session.CreatedAt;
    }

    private static bool IsTerminal(string status)
    {
        return status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("cancelled", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("interrupted", StringComparison.OrdinalIgnoreCase);
    }
}
