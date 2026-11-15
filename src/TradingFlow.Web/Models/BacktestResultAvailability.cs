using TradingFlow.Application.Jobs;

namespace TradingFlow.Web.Models;

/// <summary>Separates execution completion from the presence of retained summary and original result evidence.</summary>
public sealed record BacktestResultAvailability(string State, string Label, string Detail, bool HasRetainedSummary)
{
    public bool RequiresAttention => State == "unavailable";

    // Inspect only the recorded location; never relocate, rewrite, publish or infer an artifact from a completed job status.
    public static BacktestResultAvailability Observe(BacktestJobSnapshot job)
    {
        if (!job.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            return new("pending", job.Status, "Result availability is assessed after execution completes.", job.Result is not null);

        if (job.Result is null)
            return Missing("The completed job has no retained result payload. Locate and verify its original artifact before interpreting the run.", false);

        var summary = "Retained summary and monthly records, where present, remain visible below. ";
        var reference = job.Result.ResultPath;
        if (string.IsNullOrWhiteSpace(reference))
            return Missing(summary + "The saved result has no original artifact reference. Locate and verify the original artifact before repair.", true);

        // File.Exists also returns false for inaccessible paths; do not misreport every false result as a confirmed deletion.
        if (!Path.IsPathFullyQualified(reference) || !File.Exists(reference))
            return Missing(summary + "The original result file is not accessible at its recorded absolute location. Ask the operator to locate and verify it using this job ID; this page has not repaired or rerun the job.", true);

        var attemptSegment = $"{Path.DirectorySeparatorChar}.durable-attempts{Path.DirectorySeparatorChar}";
        if (Path.GetFullPath(reference).Contains(attemptSegment, StringComparison.OrdinalIgnoreCase) &&
            !DurableJobResultPublication.IsPublished(reference))
            return Missing(summary + "The recorded attempt file exists but its publication marker is unavailable. An attempt file alone is not a published result; the operator must verify publication.", true);

        return new("present", "Result file present", "The recorded file is present. This view does not revalidate its contents or grant research/paper authorization.", true);
    }

    // Keep unavailable wording identical for recent-run badges, selected results and progress responses.
    private static BacktestResultAvailability Missing(string detail, bool summary) =>
        new("unavailable", "Result unavailable", detail, summary);
}
