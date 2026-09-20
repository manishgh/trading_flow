using System.Text.Json;
using System.Text;
using TradingFlow.Domain.Jobs;

namespace TradingFlow.Application.Jobs;

public sealed record DurableJobPublication(
    Guid JobId,
    string JobType,
    string ResultReference,
    DateTimeOffset PublishedAtUtc);

/// <summary>
/// Creates the filesystem publication fence only after the durable database row is
/// terminal. Attempt files without this marker are staging data, never a result.
/// </summary>
public static class DurableJobResultPublication
{
    public const string MarkerFileName = ".durable-job-published.json";

    public static async Task PublishIfCompletedAsync(
        PersistedJob job,
        CancellationToken cancellationToken)
    {
        if (!job.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
            String.IsNullOrWhiteSpace(job.ResultReference))
        {
            return;
        }

        var resultReference = Path.GetFullPath(job.ResultReference);
        var durableAttemptSegment = $"{Path.DirectorySeparatorChar}.durable-attempts{Path.DirectorySeparatorChar}";
        if (!resultReference.Contains(durableAttemptSegment, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = File.Exists(resultReference)
            ? Path.GetDirectoryName(resultReference)!
            : Directory.Exists(resultReference)
                ? resultReference
                : throw new InvalidDataException(
                    $"Completed durable job result does not exist: {resultReference}");
        var markerPath = Path.Combine(directory, MarkerFileName);
        if (File.Exists(markerPath))
        {
            return;
        }

        var temporaryPath = Path.Combine(directory, $".{MarkerFileName}.{Guid.NewGuid():N}.tmp");
        var publication = new DurableJobPublication(
            job.Id,
            job.JobType,
            resultReference,
            job.FinishedAt?.ToUniversalTime() ?? DateTimeOffset.UtcNow);
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                await writer.WriteAsync(
                    JsonSerializer.Serialize(publication, new JsonSerializerOptions { WriteIndented = true })
                        .AsMemory(),
                    cancellationToken);
                await writer.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, markerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static bool IsPublished(string resultReference)
    {
        var fullPath = Path.GetFullPath(resultReference);
        var directory = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath)! : fullPath;
        return File.Exists(Path.Combine(directory, MarkerFileName));
    }
}
