using System.Text.Json;
using System.Security.Cryptography;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Jobs;

namespace TradingFlow.Application.Jobs;

/// <summary>Host-owned budgets shared by singular and matrix admission; workers retain their own concurrency limits.</summary>
public sealed record RunQueueLimits(int Backtest, int Paper);

public sealed partial class RunWorkflowService
{
    /// <summary>Removes a losing attempt's files while retaining every input owned by the durable winner, including on cancellation.</summary>
    internal async Task DiscardUnacceptedInputsAsync(PersistedJob prepared)
    {
        var retained = await jobs.GetJobAsync(prepared.Id, CancellationToken.None);
        if (retained?.ConfigPath == prepared.ConfigPath) return;
        var retainedPaths = retained is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : DurableFileJobRequest.Deserialize(retained.RequestJson).Files.Select(file => Path.GetFullPath(file.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var request = DurableFileJobRequest.Deserialize(prepared.RequestJson);
        var comparisonRequest = request.PreparedMarketData?.Path;
        if (request.PreparedMarketData is { } reference)
        {
            var requestPath = Path.GetFullPath(reference.Path);
            var attemptDirectory = Path.GetDirectoryName(requestPath)!;
            var comparisonDirectory = Path.GetDirectoryName(attemptDirectory)!;
            var expectedRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(prepared.ConfigPath))!, "prepared-comparisons");
            if (Path.GetFileName(attemptDirectory).Equals(Path.GetFileNameWithoutExtension(prepared.ConfigPath), StringComparison.OrdinalIgnoreCase) &&
                Path.GetDirectoryName(comparisonDirectory)!.Equals(expectedRoot, StringComparison.OrdinalIgnoreCase))
                await PreparedRequestStagingCleanup.DiscardAsync(reference, Path.Combine(attemptDirectory, "request.json"),
                    jobs, prepared.Id);
        }
        var disposable = request.Files.Select(file => file.Path)
            .Where(path => comparisonRequest is null || !Path.GetFullPath(path).Equals(Path.GetFullPath(comparisonRequest), StringComparison.OrdinalIgnoreCase))
            .Where(path => !retainedPaths.Contains(Path.GetFullPath(path))).ToArray();
        await configuration.DiscardUnacceptedAsync(prepared.ConfigPath, disposable, CancellationToken.None);
    }

    /// <summary>Captures one comparison-owned data request in every unaccepted child; accepted retries retain their original binding.</summary>
    internal async Task AttachPreparedComparisonAsync(string comparisonId, IReadOnlyList<PersistedJob> books,
        DateTimeOffset cutoffUtc, CancellationToken token)
    {
        if (books.Count == 0) throw new InvalidOperationException("A prepared comparison needs at least one book.");
        var existing = await Task.WhenAll(books.Select(book => jobs.GetJobAsync(book.Id, token)));
        if (existing.Any(book => book is not null)) return; // Atomic admission/replay validation resolves this set next.
        var reference = await configuration.WritePreparedComparisonRequestAsync(comparisonId, books, cutoffUtc, token);
        if (reference is null) return; // Ordinary local CSV files keep their declared provenance without a prepared snapshot claim.
        try
        {
            foreach (var book in books)
            {
                var request = DurableFileJobRequest.Deserialize(book.RequestJson);
                using var originalInputs = request.AcquireVerifiedReadLease();
                var workflow = request.Workflow ?? throw new InvalidDataException("Comparison book has no immutable workflow identity.");
                var captured = DurableFileJobRequest.Capture(request.Files.Select(file => file.Path).Append(reference.Path));
                if (!captured.Files.Single(file => Path.GetFullPath(file.Path).Equals(Path.GetFullPath(reference.Path), StringComparison.OrdinalIgnoreCase))
                    .Sha256.Equals(reference.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Prepared input request changed before durable admission.");
                book.RequestJson = (captured with { CapturedAtUtc = request.CapturedAtUtc,
                    Workflow = workflow, PreparedMarketData = reference }).Serialize();
            }
        }
        catch
        {
            var expectedPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(books[0].ConfigPath))!,
                "prepared-comparisons", comparisonId, Path.GetFileNameWithoutExtension(books[0].ConfigPath), "request.json");
            await PreparedRequestStagingCleanup.DiscardAsync(reference, expectedPath, jobs, books[0].Id);
            throw;
        }
    }

    /// <summary>Prepares exactly one strategy/cohort book, preserving ordinary authorization and source-expiry checks.</summary>
    internal async Task<PersistedJob> PrepareCohortAsync(StartBacktestRequest request, BacktestCohortExecution cohort,
        UniversePreviewResponse source, CancellationToken token, IWorkflowEvidenceVerificationScope? evidenceScope = null)
    {
        ValidateBacktest(request, clock.GetUtcNow());
        if (request.Strategies.Count != 1 || request.Strategies[0] != cohort.Strategy || request.StartingCapital != cohort.StartingCapital)
            throw new InvalidOperationException("Each cohort execution requires its exact strategy and independent capital.");
        var json = JsonSerializer.Serialize(new { request, cohort }, Json);
        var evidence = await configuration.GetDecisionContextEvidenceAsync(request, token, evidenceScope);
        return await PrepareCoreAsync("backtest", request.IdempotencyKey, request.RunName, request.UniverseSnapshotId,
            request.Strategies, json, (universe, name) => configuration.WriteAsync(request, universe, name, token, evidenceScope),
            token, cohort, source, evidence, evidenceScope);
    }
}

/// <summary>Discards only a rejected attempt's captured parameter file after checking durable ownership and its exact bytes.</summary>
public static class PreparedRequestStagingCleanup
{
    /// <summary>Checks the unique expected path, all admitted references and link boundaries before removing one generated request.</summary>
    public static async Task DiscardAsync(PreparedMarketDataRequestReference reference, string expectedRequestPath,
        IDurableJobRepository repository, Guid attemptJobId)
    {
        var path = Path.GetFullPath(reference.Path);
        var expected = Path.GetFullPath(expectedRequestPath);
        if (!path.Equals(expected, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path) != "request.json") return;
        var retained = await repository.GetJobAsync(attemptJobId, CancellationToken.None);
        if (retained is not null && References(retained, path)) return;
        var admitted = await repository.GetAllJobsAsync(CancellationToken.None);
        if (admitted is not null && admitted.Any(job => References(job, path))) return;
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Rejected request cleanup cannot follow a directory link.");
        if (!File.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Rejected request cleanup cannot delete a linked file.");
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, CancellationToken.None))
                .Equals(reference.Sha256, StringComparison.OrdinalIgnoreCase)) return;
        File.Delete(path);
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    /// <summary>Protects both explicit prepared references and captured file evidence, including accepted sibling books.</summary>
    private static bool References(PersistedJob job, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(job.RequestJson);
            var root = document.RootElement;
            if (root.TryGetProperty("preparedMarketData", out var prepared) && prepared.ValueKind == JsonValueKind.Object &&
                prepared.TryGetProperty("path", out var reference) && reference.ValueKind == JsonValueKind.String &&
                Path.GetFullPath(reference.GetString()!).Equals(path, StringComparison.OrdinalIgnoreCase)) return true;
            return root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array &&
                files.EnumerateArray().Any(file => file.TryGetProperty("path", out var captured) && captured.ValueKind == JsonValueKind.String &&
                    Path.GetFullPath(captured.GetString()!).Equals(path, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            // Unreadable durable ownership is not proof that a generated parameter file is unaccepted.
            return true;
        }
    }
}
