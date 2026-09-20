using System.Security.Cryptography;
using System.Text.Json;

namespace TradingFlow.Application.Jobs;

public sealed record DurableFileEvidence(string Path, long Length, string Sha256);

public sealed record DurableFileJobRequest(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<DurableFileEvidence> Files)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static DurableFileJobRequest Capture(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var fullPaths = paths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (fullPaths.Length == 0)
        {
            throw new ArgumentException("A durable file job requires at least one input file.", nameof(paths));
        }

        using var lease = DurableFileReadLease.Open(fullPaths, expected: null);
        return new DurableFileJobRequest(1, DateTimeOffset.UtcNow, lease.Evidence);
    }

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public static DurableFileJobRequest Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var request = JsonSerializer.Deserialize<DurableFileJobRequest>(json, JsonOptions)
            ?? throw new InvalidDataException("The durable file request is empty.");
        if (request.SchemaVersion != 1 || request.Files.Count == 0)
        {
            throw new InvalidDataException("The durable file request schema is unsupported or incomplete.");
        }

        return request;
    }

    public void Verify()
    {
        using var lease = AcquireVerifiedReadLease();
    }

    public DurableFileReadLease AcquireVerifiedReadLease() =>
        DurableFileReadLease.Open(Files.Select(file => file.Path), Files);

    public void EnsureContains(IEnumerable<string> requiredPaths)
    {
        ArgumentNullException.ThrowIfNull(requiredPaths);
        var captured = Files
            .Select(file => Path.GetFullPath(file.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = requiredPaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !captured.Contains(path))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"Durable job config references uncaptured input(s): {String.Join(", ", missing)}");
        }
    }
}

/// <summary>
/// Holds verified input files open with read-only sharing. Configuration readers may
/// reopen them, while ordinary writers and deletes are rejected until execution ends.
/// </summary>
public sealed class DurableFileReadLease : IDisposable
{
    private readonly IReadOnlyList<FileStream> streams;

    private DurableFileReadLease(IReadOnlyList<FileStream> streams, IReadOnlyList<DurableFileEvidence> evidence)
    {
        this.streams = streams;
        Evidence = evidence;
    }

    public IReadOnlyList<DurableFileEvidence> Evidence { get; }

    internal static DurableFileReadLease Open(
        IEnumerable<string> paths,
        IReadOnlyList<DurableFileEvidence>? expected)
    {
        var fullPaths = paths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expectedByPath = expected?.ToDictionary(
            item => Path.GetFullPath(item.Path),
            StringComparer.OrdinalIgnoreCase);
        var opened = new List<FileStream>(fullPaths.Length);
        try
        {
            foreach (var path in fullPaths)
            {
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("A durable job input file does not exist.", path);
                }

                opened.Add(new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.SequentialScan));
            }

            var evidence = opened.Select(stream =>
            {
                stream.Position = 0;
                var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                stream.Position = 0;
                var path = Path.GetFullPath(stream.Name);
                var actual = new DurableFileEvidence(path, stream.Length, hash);
                if (expectedByPath is not null &&
                    (!expectedByPath.TryGetValue(path, out var expectedFile) ||
                     expectedFile.Length != actual.Length ||
                     !expectedFile.Sha256.Equals(actual.Sha256, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException($"Durable job input changed after enqueue: {path}");
                }

                return actual;
            }).ToArray();
            if (expectedByPath is not null && evidence.Length != expectedByPath.Count)
            {
                throw new InvalidDataException("Durable job input set changed after enqueue.");
            }

            return new DurableFileReadLease(opened, evidence);
        }
        catch
        {
            foreach (var stream in opened)
            {
                stream.Dispose();
            }

            throw;
        }
    }

    public void Dispose()
    {
        foreach (var stream in streams)
        {
            stream.Dispose();
        }
    }
}
