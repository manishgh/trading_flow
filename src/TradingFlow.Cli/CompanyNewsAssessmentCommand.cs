using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Data.Evidence.Historical;
using TradingFlow.Data.Evidence.Sentiment;
using TradingFlow.Domain.News;

namespace TradingFlow.Cli;

/// <summary>Reusable model preview over existing archive selections; this command never executes a portfolio.</summary>
public static class CompanyNewsAssessmentCommand
{
    /// <summary>Assesses the existing pilot or diagnostic manifest and publishes immutable model outputs and blind review inputs.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken = default)
    {
        var allowed = new HashSet<string>(["--research-root", "--pilot-selection", "--selection-sha256", "--diagnostic-manifest",
            "--manifest-sha256", "--model-policy", "--endpoint", "--output", "--diagnostic-output",
            "--issuer-manifest", "--issuer-manifest-sha256"], StringComparer.Ordinal);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
            if (index + 1 >= args.Length || !allowed.Contains(args[index]) || !options.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException("assess-company requires unique named option/value pairs.");
        // Explicit paths and hashes make all source/model selections reviewable before inference begins.
        string Require(string name) => options.TryGetValue(name, out var value) && !String.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException("Required company assessment option: " + name);
        var researchRoot = Path.GetFullPath(Require("--research-root"));
        var pilot = options.ContainsKey("--pilot-selection");
        if (pilot == options.ContainsKey("--diagnostic-manifest"))
            throw new ArgumentException("Select exactly one retained pilot or historical diagnostic manifest.");
        // Validate every destination before reading/classifying the archive so a missing path cannot cause a wasted inference or partial publication.
        var publicationRoot = CompanyNewsAssessmentManifestStore.ResolvePublicationRoot(researchRoot, Require("--output"));
        var diagnosticOutput = pilot ? null : CompanyNewsAssessmentManifestStore.ResolvePublicationRoot(researchRoot, Require("--diagnostic-output"));
        if (options.ContainsKey("--issuer-manifest") != options.ContainsKey("--issuer-manifest-sha256"))
            throw new ArgumentException("Issuer mapping requires both --issuer-manifest and --issuer-manifest-sha256.");
        var modelPath = Path.GetFullPath(Require("--model-policy"));
        var model = JsonSerializer.Deserialize<CompanyNewsPinnedModel>(File.ReadAllBytes(modelPath), CompanyNewsAssessmentManifestStore.JsonOptions)
            ?? throw new InvalidDataException("Frozen model policy is empty.");
        var endpoint = new Uri(Require("--endpoint"), UriKind.Absolute);
        if (!endpoint.IsLoopback || endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("Company evidence requires the local pinned FinBERT sidecar.");
        var selection = pilot
            ? CompanyNewsAssessmentBuilder.ReadPilot(researchRoot, Path.GetFullPath(Require("--pilot-selection")), Require("--selection-sha256"))
            : CompanyNewsAssessmentBuilder.ReadDiagnostic(researchRoot, Path.GetFullPath(Require("--diagnostic-manifest")), Require("--manifest-sha256"));
        var issuerMappings = options.ContainsKey("--issuer-manifest")
            ? CompanyNewsIssuerMappingReader.ReadVerified(researchRoot, Path.GetFullPath(Require("--issuer-manifest")), Require("--issuer-manifest-sha256"),
                selection.Sources.SelectMany(source => source.Symbols)) : null;
        var modelRelativePath = Path.GetRelativePath(researchRoot, modelPath);
        if (modelRelativePath.StartsWith("..", StringComparison.Ordinal)) throw new InvalidDataException("Model policy must remain inside the frozen research root.");
        var dependencies = selection.Dependencies.Add(new(modelRelativePath,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(modelPath)))));
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var assessor = new FinbertCompanyNewsAssessor(client, endpoint, model, TimeProvider.System);
        var reference = new CompanyNewsAssessmentSelection(pilot ? "pilot" : "diagnostic",
            Path.GetRelativePath(researchRoot, Path.GetFullPath(Require(pilot ? "--pilot-selection" : "--diagnostic-manifest"))),
            Require(pilot ? "--selection-sha256" : "--manifest-sha256"));
        var manifest = await CompanyNewsAssessmentBuilder.BuildAsync(researchRoot, pilot ? "retained-development-pilot" : "all-frozen-diagnostic-revisions",
            reference, selection.Sources, dependencies, model, assessor.AssessAsync, TimeProvider.System, cancellationToken, issuerMappings);
        var publication = await CompanyNewsAssessmentManifestStore.PublishExclusiveAsync(publicationRoot, manifest, cancellationToken);
        var directory = Path.GetDirectoryName(publication.Path)!;
        await CompanyNewsAssessmentManifestStore.WriteStreamExclusiveAsync(Path.Combine(directory, "SAMPLES.md"),
            (stream, token) => WriteSamplesAsync(stream, manifest, token), cancellationToken);
        await CompanyNewsAssessmentManifestStore.WriteStreamExclusiveAsync(Path.Combine(directory, "blind-company-review.json"),
            (stream, token) => WriteBlindReviewAsync(stream, publication.Sha256, selection.Sources, token), cancellationToken);
        HistoricalDiagnosticNewsPublication? enriched = null;
        if (!pilot)
        {
            var original = HistoricalDiagnosticNewsProvider.LoadVerified(researchRoot, Path.GetFullPath(Require("--diagnostic-manifest")), Require("--manifest-sha256"));
            enriched = HistoricalDiagnosticNewsManifestStore.PublishExclusive(diagnosticOutput!,
                original.Manifest with { CompanyAssessmentManifest = new(Path.GetRelativePath(researchRoot, publication.Path), publication.Sha256) });
        }
        output.WriteLine(JsonSerializer.Serialize(new { Publication = publication, EnrichedDiagnostic = enriched,
            StoryCount = selection.Sources.Length, IssuerAssessments = manifest.Rows.Length,
            Inferences = manifest.Rows.Count(row => row.Assessment.ModelEvidence is { Succeeded: true }),
            States = manifest.Rows.GroupBy(row => row.Assessment.State).ToDictionary(group => group.Key.ToString(), group => group.Count()),
            IssuerMappingCount = issuerMappings?.Mappings.Count, AmbiguousIssuerMappingCount = issuerMappings?.UnavailableSymbols.Length,
            UnsupportedIssuerMappingRows = issuerMappings?.Exclusions.Length,
            HumanLabelsCompleted = 0, ClassifierAccuracyEvaluated = false, EconomicExecutionPerformed = false },
            new JsonSerializerOptions(CompanyNewsAssessmentManifestStore.JsonOptions) { WriteIndented = true }));
        return 0;
    }

    /// <summary>Streams unchanged Markdown using UTF-8 without a BOM; cancellation leaves only operation-owned staging.</summary>
    private static async Task WriteSamplesAsync(Stream stream, CompanyNewsAssessmentManifest manifest, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write("# Company news assessment samples\n\nRetrospective development measurements. Human relevance/direction labels and classifier accuracy remain unmeasured. No economic attempt was executed. Confidence describes the model text label, not recovery probability.\n\n");
        writer.Write("Model: ");
        writer.Write(manifest.Model.Model);
        writer.Write('@');
        writer.Write(manifest.Model.Version);
        writer.Write("\n\nTraining provenance: ");
        writer.Write(manifest.Model.TrainingDataProvenance);
        writer.Write('\n');
        foreach (var row in manifest.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteSampleRow(writer, row);
        }
        await writer.FlushAsync(cancellationToken);
    }

    /// <summary>Writes each issuer row in the original field order, retaining clocks, rejection reasons and mapping notices.</summary>
    private static void WriteSampleRow(TextWriter writer, CompanyNewsAssessmentRow row)
    {
        writer.Write("\n## ");
        writer.Write(row.Source.WorkItemId);
        writer.Write(" / ");
        writer.Write(row.Symbol);
        writer.Write("\n\nHeadline: ");
        writer.Write(row.Source.Revision.Headline.Replace('\n', ' '));
        writer.Write("\n\nState: ");
        writer.Write(row.Assessment.State);
        writer.Write("; category: ");
        writer.Write(row.Assessment.Category);
        writer.Write("; asserted direction: ");
        writer.Write(row.Assessment.AssertedDirection);
        writer.Write("; textual polarity: ");
        writer.Write(row.Assessment.ModelEvidence?.TopLabel.ToString() ?? "not inferred");
        writer.Write("; confidence: ");
        writer.Write(row.Assessment.ModelEvidence?.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown");
        writer.Write("\n\nReasons: ");
        writer.Write(String.Join(", ", row.Assessment.Reasons));
        writer.Write("\n\nActual archive receipt UTC: ");
        writer.Write(row.Source.ActualReceivedAtUtc.ToString("O"));
        writer.Write("; actual assessment UTC: ");
        writer.Write(row.Assessment.AssessedAtUtc.ToString("O"));
        writer.Write("; separately assumed historical availability UTC: ");
        writer.Write(row.Source.AssumedAvailableAtUtc?.ToString("O") ?? "none");
        writer.Write("\n\nInference failure: ");
        writer.Write(row.Assessment.ModelEvidence?.FailureReason ?? "none");
        writer.Write('\n');
        if (row.Assessment.Extraction.IssuerMapping is { } mapping)
        {
            writer.Write("\n\nCompany mapping: ");
            writer.Write(mapping.OfficialName);
            writer.Write(" / ");
            writer.Write(mapping.Symbol);
            writer.Write("; source: ");
            writer.Write(mapping.SourceProvider);
            writer.Write("; actual mapping capture UTC: ");
            writer.Write(mapping.ObservedAtUtc.ToString("O"));
            writer.Write(". Present-day captured name; original historical name availability is unproved.\n");
        }
    }

    /// <summary>Streams the original indented envelope with stable story order and first-ten independent-review flags.</summary>
    private static Task WriteBlindReviewAsync(Stream stream, string manifestSha256,
        ImmutableArray<CompanyNewsAssessmentSource> sources, CancellationToken cancellationToken) =>
        JsonSerializer.SerializeAsync(stream, new { ManifestSha256 = manifestSha256, HumanLabelsCompleted = 0,
            Instructions = "Independent humans only; judge exact archived company relevance/direction without model predictions or later prices. Prior category labels are not sentiment labels.",
            Stories = sources.Select((source, index) => new { source.WorkItemId, source.RawRevisionContentSha256,
                source.Revision, source.Symbols, source.ActualReceivedAtUtc,
                ReviewerA = new { CompanyRelevance = (string?)null, Direction = (string?)null, Notes = (string?)null },
                IndependentReviewerBRequired = index < 10,
                ReviewerB = new { CompanyRelevance = (string?)null, Direction = (string?)null, Notes = (string?)null } }) },
            new JsonSerializerOptions(CompanyNewsAssessmentManifestStore.JsonOptions) { WriteIndented = true }, cancellationToken);
}
