using Microsoft.Extensions.Options;

namespace TradingFlow.Web.Services.Charts;

/// <summary>Controls how many concurrent chart tabs each operator may keep open.</summary>
public sealed class ChartTransportOptions
{
    public const int DefaultMaximumConnectionsPerUser = 4;
    public int MaximumConnectionsPerUser { get; init; } = DefaultMaximumConnectionsPerUser;
}

/// <summary>Bounds active browser connections and selection/history requests before any expensive read.</summary>
public sealed class ChartTransportLimits(TimeProvider clock, IOptions<ChartTransportOptions>? options = null)
{
    private const int MaximumConnections = 128;
    private readonly int maximumConnectionsPerUser =
        options?.Value.MaximumConnectionsPerUser ?? ChartTransportOptions.DefaultMaximumConnectionsPerUser;
    private readonly object sync = new();
    private readonly Dictionary<string, string> connections = new();
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> requests = new();
    // Permit several chart tabs while bounding connections per operator and across the process.
    public bool Connect(string connection, string user)
    {
        lock (sync)
        {
            if (connections.Count >= MaximumConnections || connections.Values.Count(x => x == user) >= maximumConnectionsPerUser) return false;
            connections[connection] = user;
            return true;
        }
    }
    // Release only the connection that is closing.
    public void Disconnect(string connection) { lock (sync) connections.Remove(connection); }
    // Share a short selection/history budget across the operator's tabs.
    public bool Request(string user)
    {
        lock (sync)
        {
            var now = clock.GetUtcNow();
            foreach (var key in requests.Where(x => now - x.Value.Start >= TimeSpan.FromMinutes(1)).Select(x => x.Key).ToArray())
                requests.Remove(key);
            if (!requests.TryGetValue(user, out var current))
            {
                if (requests.Count >= 512) return false;
                requests[user] = (now, 1);
                return true;
            }
            if (current.Count >= 20) return false;
            requests[user] = (current.Start, current.Count + 1);
            return true;
        }
    }
}
