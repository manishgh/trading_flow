using TradingFlow.Contracts.Presentation;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Evidence.Sentiment;

namespace TradingFlow.Web.Services;

/// <summary>Reads published retrospective assessments on explicit request; never writes news or invokes inference.</summary>
public sealed class CompanyNewsAssessmentPreviewService(ProjectPaths paths, ILogger<CompanyNewsAssessmentPreviewService> logger)
{
    /// <summary>Returns the latest immutable publication, bounded for a review screen and distinct from the live news feed.</summary>
    public CompanyNewsAssessmentPreview Read(int page = 1, int pageSize = 25)
    {
        if (page < 1 || pageSize is < 1 or > 100 || page > Int32.MaxValue / pageSize)
            throw new ArgumentException("Choose a valid assessment page and 1–100 rows.");
        try
        {
            var root = Path.Combine(paths.DataRoot, "research", "evidence", "company-news-assessments");
            var publication = CompanyNewsAssessmentManifestStore.FindPublished(root)
                .OrderByDescending(item => File.GetLastWriteTimeUtc(item.Path)).ThenBy(item => item.Id, StringComparer.Ordinal).FirstOrDefault();
            if (publication is null)
                return new(null, null, null, [], "No company news assessments have been published. No backtest was started.");
            var manifest = CompanyNewsAssessmentManifestStore.ReadPublished(publication);
            var rows = manifest.Rows.OrderBy(item => item.Source.WorkItemId, StringComparer.Ordinal)
                .ThenBy(item => item.Symbol, StringComparer.Ordinal).Skip((page - 1) * pageSize).Take(pageSize).ToArray();
            var samples = rows.Select(row => new CompanyNewsAssessmentSample(row.Symbol, row.Source.Revision.Headline,
                row.Source.PublishedAtUtc, row.Assessment.Category.ToString(),
                CompanyNewsAssessmentPresentation.DirectionLabel(row.Assessment.State.ToString()),
                row.Assessment.ModelEvidence?.TopLabel.ToString(), row.Assessment.ModelEvidence?.Confidence,
                String.Join("; ", row.Assessment.Reasons.Select(reason => reason.Replace('_', ' '))),
                row.Assessment.Extraction.ModelInputText, row.Assessment.Extraction.RuleResult.TextSupport.ToString(),
                row.Assessment.AssessedAtUtc, row.Assessment.Revision.RevisionId, row.Assessment.RevisionContentSha256,
                row.Assessment.Extraction.IssuerMapping?.OfficialName,
                row.Assessment.Extraction.IssuerMapping?.SourceProvider,
                row.Assessment.Extraction.IssuerMapping?.ObservedAtUtc)).ToArray();
            return new(publication.Id, publication.Sha256, Summarize(manifest), samples,
                $"Archived assessment samples, page {page}; {samples.Length} of {manifest.Rows.Length} company assessments shown. " +
                "Retrospective research, not live news or a trading instruction. Human accuracy review is incomplete.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException or
            System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Published company news assessment verification failed");
            return new(null, null, null, [], "The published assessment could not be verified. Its data was not changed; no classification or backtest was started.");
        }
    }

    /// <summary>Projects immutable model and actual completion facts into the shared API/Web/Android summary.</summary>
    public static CompanyNewsAssessmentSummary Summarize(CompanyNewsAssessmentManifest manifest) => new(
        manifest.Policy.AssessmentMethodVersion, manifest.Model.Model, manifest.Model.Version, manifest.Model.ArtifactSha256,
        manifest.Policy.MappingVersion, manifest.Model.TrainingDataCutoffUtc,
        manifest.Model.TrainingDataProvenance, manifest.Rows.Min(row => row.Assessment.AssessedAtUtc),
        manifest.Rows.Max(row => row.Assessment.AssessedAtUtc), manifest.Rows.Length,
        manifest.Rows.GroupBy(row => row.Assessment.State.ToString()).ToDictionary(group => group.Key, group => group.Count()));
}
