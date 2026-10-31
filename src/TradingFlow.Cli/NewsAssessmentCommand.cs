using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TradingFlow.Data.Evidence.Labeling;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Research;

namespace TradingFlow.Cli;

/// <summary>Offline file exchange only: no provider, database, runtime activation or inferred human labels.</summary>
public static class NewsAssessmentCommand
{
    public const string Name = "news-assessment";
    public static int Run(string[] args, TextWriter output)
    {
        if (args.Length > 0 && args[0] == "assess-company")
            return CompanyNewsAssessmentCommand.RunAsync(args[1..], output).GetAwaiter().GetResult();
        if (args.Length < 1) throw new ArgumentException("news-assessment export|import|predict-baseline|evaluate with explicit file paths.");
        var mode = args[0];
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2) {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException("Assessment options require unique --name value pairs.");
        }
        string Required(string name) => options.TryGetValue(name, out var value) && !String.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException("Required assessment option: " + name);
        string Full(string name) => Path.GetFullPath(Required(name));
        DateTimeOffset Clock(string name) {
            var value = DateTimeOffset.Parse(Required(name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (value == default || value.Offset != TimeSpan.Zero) throw new ArgumentException(name + " must be actual UTC.");
            return value;
        }
        void CheckOptions(params string[] allowed) {
            var unknown = options.Keys.Except(allowed, StringComparer.Ordinal).ToArray();
            if (unknown.Length > 0) throw new ArgumentException("Unknown assessment options: " + String.Join(',', unknown));
        }
        if (mode == "export") {
            CheckOptions("--collection-root", "--summary", "--output", "--availability", "--delay-seconds", "--sample-size");
            var basis = Required("--availability") switch {
                "observed-receipt" => NewsAssessmentAvailabilityBasis.ObservedReceipt,
                "conservative-provider-updated" => NewsAssessmentAvailabilityBasis.ConservativeProviderUpdated,
                _ => throw new ArgumentException("Availability must name observed-receipt or conservative-provider-updated.") };
            var delay = basis == NewsAssessmentAvailabilityBasis.ConservativeProviderUpdated
                ? Int32.Parse(Required("--delay-seconds"), CultureInfo.InvariantCulture) : 0;
            if (basis == NewsAssessmentAvailabilityBasis.ObservedReceipt && options.ContainsKey("--delay-seconds"))
                throw new ArgumentException("Observed receipt must not be combined with a reconstruction delay.");
            var sample = options.TryGetValue("--sample-size", out var size) ? Int32.Parse(size, CultureInfo.InvariantCulture) : 500;
            if (sample < 500) throw new ArgumentException("Production assessment packets require at least 500 work items; human protocol thresholds cannot be weakened.");
            var packet = NewsAssessmentWorkflow.Export(Full("--collection-root"), Required("--summary"), new(basis, delay), sample);
            var bytes = NewsAssessmentWorkflow.Bytes(packet); var sha = NewsAssessmentWorkflow.Hash(bytes);
            var destination = Full("--output");
            Write(Path.Combine(destination, "assessment-packet.json"), bytes);
            var template = NewsAssessmentWorkflow.Bytes(NewsAssessmentWorkflow.EmptyHumanDocument(sha));
            foreach (var reviewer in new[] { "reviewer-a", "reviewer-b" }) {
                var assigned = reviewer == "reviewer-a" ? packet.WorkItems : packet.WorkItems.Where(item => item.IndependentSecondLabelRequired).ToImmutableArray();
                Write(Path.Combine(destination, reviewer, "work-items.json"), NewsAssessmentWorkflow.Bytes(new {
                    PacketSha256 = sha, Instructions = "Independent human review only. Do not inspect another reviewer's labels or baseline predictions.", WorkItems = assigned }));
                Write(Path.Combine(destination, reviewer, "REVIEW-QUEUE.md"), Encoding.UTF8.GetBytes(ReviewQueue(assigned)));
                Write(Path.Combine(destination, reviewer, "human-labels-template.json"), NewsAssessmentWorkflow.Bytes(new {
                    FormatVersion = NewsAssessmentWorkflow.HumanFormat, PacketSha256 = sha, HumanProvenanceDeclaration = "HUMAN_COMPLETION_REQUIRED",
                    Annotations = assigned.Select(item => new { item.Item.WorkItemId, item.Item.NewsRevisionContentSha256,
                        AnnotatorId = (string?)null, LabeledAtUtc = (string?)null,
                        Label = new { Category = (string?)null, Direction = (string?)null, DirectionIsClear = (bool?)null,
                            Materiality = (string?)null, Scope = (string?)null, ContextCategory = (string?)null,
                            Targets = (string[]?)null, Adverse = (bool?)null, EvidenceSupport = (string?)null,
                            ReadingCategories = (string[]?)null, ReadingSectors = (string[]?)null } }).ToArray(),
                    Adjudications = Array.Empty<object>() }));
                Write(Path.Combine(destination, reviewer, "RUBRIC.md"), Encoding.UTF8.GetBytes(Rubric));
            }
            Write(Path.Combine(destination, "adjudications-template.json"), template);
            // Carry the binding guide into each blind packet; reviewers need no repository access.
            var guidePath = Path.Combine(AppContext.BaseDirectory, "catalyst-human-labeling-v1.md");
            if (!File.Exists(guidePath)) throw new FileNotFoundException("The binding human labeling guide must accompany the CLI build.", guidePath);
            foreach (var reviewer in new[] { "reviewer-a", "reviewer-b" })
                Write(Path.Combine(destination, reviewer, "catalyst-human-labeling-v1.md"), File.ReadAllBytes(guidePath));
            Write(Path.Combine(destination, "human-label-fields.json"), NewsAssessmentWorkflow.Bytes(new {
                FormatVersion = NewsAssessmentWorkflow.HumanFormat, PacketSha256 = sha,
                DeclarationRequiredForCompletedLabels = "independent_human_no_future_data",
                AnnotationFields = new[] { "work_item_id", "news_revision_content_sha256", "annotator_id", "labeled_at_utc", "label" },
                LabelFields = new[] { "category", "direction", "direction_is_clear", "materiality", "scope", "context_category", "targets", "adverse", "evidence_support", "reading_categories", "reading_sectors" },
                EconomicCategories = Enum.GetValues<CatalystNewsCategory>(), Directions = Enum.GetValues<CatalystDirection>(),
                Materialities = Enum.GetValues<CatalystMateriality>(), Scopes = Enum.GetValues<NewsAssessmentScope>(),
                ContextCategories = Enum.GetValues<NewsAssessmentContextCategory>(), EvidenceSupport = Enum.GetValues<NewsAssessmentTextSupport>(),
                ReadingCategories = NewsDisplayTaxonomy.Categories.Where(item => item.Key != "all").Select(item => item.Key).ToArray(),
                ReadingSectors = NewsDisplayTaxonomy.Sectors.Select(item => item.Key).ToArray(),
                AdjudicationFields = new[] { "work_item_id", "news_revision_content_sha256", "adjudicator_id", "adjudicated_at_utc", "label", "rationale" } }));
            var manifest = new { FormatVersion = "tradingflow.news-assessment-package.v1", PacketSha256 = sha,
                PacketPath = Path.Combine(destination, "assessment-packet.json"), WorkItemCount = packet.WorkItems.Length,
                HeldOutCount = packet.WorkItems.Count(item => item.HeldOut), IndependentSecondLabelCount = packet.WorkItems.Count(item => item.IndependentSecondLabelRequired),
                packet.EligibleRevisionCount, ExcludedRevisions = packet.ExcludedRevisions,
                TextSupportCounts = packet.WorkItems.GroupBy(item => item.TextSupport).ToDictionary(group => group.Key.ToString(), group => group.Count()),
                packet.AvailabilityContract, HistoricalFirstReceiptProven = false, HumanLabelsCompleted = 0,
                ClassifierAccuracyMeasured = false, TradingActivationAllowed = false,
                RequiredHumanEvidence = "At least 500 resolved human labels, 100 independent double labels, every original economic category and required scope/content coverage; no invented labels or passes.",
                CatalogGroundTruthPublished = false,
                Files = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
                    .Where(path => Path.GetFileName(path) != "assessment-package.json")
                    .Order(StringComparer.Ordinal).ToDictionary(path => Path.GetRelativePath(destination, path), NewsAssessmentWorkflow.FileHash) };
            Write(Path.Combine(destination, "assessment-package.json"), NewsAssessmentWorkflow.Bytes(manifest));
            output.WriteLine(JsonSerializer.Serialize(manifest, NewsAssessmentWorkflow.JsonOptions)); return 0;
        }
        CheckOptions("--packet", "--packet-sha256", "--output", "--labels-a", "--labels-b", "--adjudications", "--predictions", "--at-utc");
        var packetBytes = File.ReadAllBytes(Full("--packet")); var expectedSha = Required("--packet-sha256");
        if (NewsAssessmentWorkflow.Hash(packetBytes) != expectedSha) throw new InvalidDataException("Packet file does not match its frozen expected SHA256.");
        var source = NewsAssessmentWorkflow.Read<NewsAssessmentPacket>(packetBytes);
        NewsAssessmentWorkflow.ValidatePacket(source, expectedSha);
        var now = Clock("--at-utc");
        var resultPath = Full("--output");
        if (mode == "predict-baseline") {
            if (options.Keys.Any(key => key is "--labels-a" or "--labels-b" or "--adjudications" or "--predictions"))
                throw new ArgumentException("Blind baseline prediction accepts no human label or other prediction inputs.");
            var baseline = NewsAssessmentWorkflow.PredictDisplayBaseline(source, expectedSha, now);
            var baselineBytes = NewsAssessmentWorkflow.Bytes(baseline); Write(resultPath, baselineBytes);
            output.WriteLine(JsonSerializer.Serialize(new { Output = resultPath, Sha256 = NewsAssessmentWorkflow.Hash(baselineBytes), baseline.ClassifierVersion,
                baseline.Task, EconomicPredictions = "explicit_abstention", Nws12Acceptance = false }, NewsAssessmentWorkflow.JsonOptions)); return 0;
        }
        if (mode is not ("import" or "evaluate")) throw new ArgumentException("Unknown news-assessment mode.");
        if (mode == "import" && options.ContainsKey("--predictions")) throw new ArgumentException("Import accepts human documents only.");
        var humanPaths = new[] { "--labels-a", "--labels-b", "--adjudications" }.Where(options.ContainsKey).Select(Full).ToArray();
        if (humanPaths.Length == 0) throw new ArgumentException("Import/evaluation requires actual human label document paths.");
        var documents = humanPaths.Select(path => NewsAssessmentWorkflow.Read<NewsAssessmentHumanDocument>(File.ReadAllBytes(path))).ToArray();
        if (documents.Any(document => document.FormatVersion != NewsAssessmentWorkflow.HumanFormat || document.PacketSha256 != expectedSha))
            throw new InvalidDataException("Human label inputs belong to a different immutable packet.");
        if (documents.Any(document => document.Annotations.IsDefault || document.Adjudications.IsDefault)) throw new InvalidDataException("Human label arrays are required.");
        var nonempty = documents.Where(document => document.Annotations.Length + document.Adjudications.Length > 0).ToArray();
        if (nonempty.Any(document => document.HumanProvenanceDeclaration != "independent_human_no_future_data"))
            throw new InvalidDataException("Completed documents must explicitly identify independent human/no-future-data labeling.");
        var humans = new NewsAssessmentHumanDocument(NewsAssessmentWorkflow.HumanFormat, expectedSha,
            nonempty.Length > 0 ? "independent_human_no_future_data" : "HUMAN_COMPLETION_REQUIRED",
            documents.SelectMany(document => document.Annotations).ToImmutableArray(), documents.SelectMany(document => document.Adjudications).ToImmutableArray());
        object result = mode == "import" ? NewsAssessmentWorkflow.Import(source, expectedSha, humans, now) :
            NewsAssessmentWorkflow.Evaluate(source, expectedSha, humans,
                NewsAssessmentWorkflow.Read<NewsAssessmentPredictionDocument>(File.ReadAllBytes(Full("--predictions"))), now);
        var resultBytes = NewsAssessmentWorkflow.Bytes(result); Write(resultPath, resultBytes);
        Write(resultPath + ".inputs.json", NewsAssessmentWorkflow.Bytes(new { PacketSha256 = expectedSha,
            HumanInputHashes = humanPaths.ToDictionary(path => path, NewsAssessmentWorkflow.FileHash),
            PredictionInputSha256 = mode == "evaluate" ? NewsAssessmentWorkflow.FileHash(Full("--predictions")) : null,
            ResultSha256 = NewsAssessmentWorkflow.Hash(resultBytes), DatabaseWrites = 0, TradingActivationAllowed = false }));
        output.WriteLine(Encoding.UTF8.GetString(resultBytes)); return 0;
    }

    private static void Write(string path, byte[] bytes) {
        if (File.Exists(path)) {
            if (!File.ReadAllBytes(path).SequenceEqual(bytes)) throw new IOException("Refusing to overwrite immutable assessment artifact: " + path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(true);
    }

    private static string ReviewQueue(IEnumerable<NewsAssessmentWorkItem> items)
    {
        var text = new StringBuilder("# Blind human review queue\n\nRead RUBRIC.md and catalyst-human-labeling-v1.md first. Complete a copy of human-labels-template.json outside this immutable packet. No labels or model predictions are supplied.\n");
        void Block(string title, string? value) {
            text.Append("\n### ").Append(title).Append('\n');
            if (String.IsNullOrWhiteSpace(value)) { text.Append("\nNot supplied in this archived response.\n"); return; }
            var fence = "```";
            while (value.Contains(fence, StringComparison.Ordinal)) fence += "`";
            text.Append('\n').Append(fence).Append("text\n").Append(value).Append('\n').Append(fence).Append('\n');
        }
        foreach (var item in items) {
            text.Append("\n## ").Append(item.Item.WorkItemId).Append("\n\nRevision SHA256: ").Append(item.Item.NewsRevisionContentSha256)
                .Append("\n\nProvider symbols: ").Append(String.Join(", ", item.Item.Symbols))
                .Append("\n\nPublished UTC: ").Append(item.Item.PublishedAtUtc.ToString("O", CultureInfo.InvariantCulture))
                .Append("; provider updated UTC: ").Append(item.Item.ProviderUpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture))
                .Append("; actual archive receipt UTC: ").Append(item.ObservedReceiptAtUtc.ToString("O", CultureInfo.InvariantCulture))
                .Append("\n\nText supplied: ").Append(item.TextSupport).Append(". ")
                .Append(item.ReconstructedArticleAvailableAtUtc is { } reconstructed
                    ? "Diagnostic provider-update reconstruction UTC (not original receipt): " + reconstructed.ToString("O", CultureInfo.InvariantCulture)
                    : "No historical receipt reconstruction is asserted.").Append('\n');
            Block("Headline", item.Item.Headline); Block("Summary", item.Item.Summary); Block("Body (raw archived text)", item.Body);
        }
        return text.ToString();
    }

    public const string Rubric = """
        # Independent human news assessment

        Copy your blank label template to a new completion file. Do not edit this
        immutable packet, inspect the other reviewer's labels or inspect baseline
        predictions. A language model must not complete human annotations.

        Use only the exact archived revision shown. Do not open today's article
        page, later revisions, prices, returns or commentary. Record stable human
        annotator IDs and actual UTC completion times after the observed download.
        Set human_provenance_declaration to independent_human_no_future_data only
        when you actually submit independently completed human labels.

        Two distinct tasks share each article. For reading_categories and
        reading_sectors, judge headline, summary and provider/ticker metadata only,
        matching the UI's reading view. Categories are multi-label company, sector,
        global, financial and policy, with market only when none applies. These are
        reading topics, not stock impact, historical company-sector membership or
        the thirteen economic catalyst classes.

        For category, direction, direction_is_clear, materiality, scope,
        context_category, targets and adverse, use all text actually supplied,
        including body where available. evidence_support must exactly match the
        work item's text_support. Headline-only means this response supplied no
        body/summary; it does not prove that no original body ever existed. Use
        economic category unknown and ambiguous direction when evidence is weak.
        Clear direction requires positive or negative, and must concern information
        available in this revision, never a later price reaction. Adverse records
        observed adverse-event risk, not a prediction of permanent repricing.

        The thirteen economic categories and direction/materiality definitions in
        catalyst-human-labeling-v1.md remain binding. Minimum 500
        resolved labels, 100 independently double-labeled articles, every economic
        category and zero unresolved conflicts are unchanged. New scope/context
        fields need coverage too; missing coverage is unknown rather than a pass.

        Company scope targets must be exact provider symbols from this item. Sector
        scope targets name the article's sector subject and do not certify a stock's
        historical sector membership. Global scope targets exactly ["*"] and may
        have global, financial or policy context. Company/sector financial or policy
        articles retain that scope. Unknown scope has empty targets and unknown
        context_category. Record all required fields listed in human-label-fields.json.

        Reviewer A receives all items; reviewer B independently labels the assigned
        100. Do not coordinate. Disagreements in ANY economic, context, support or
        reading field require explicit human adjudication with actual UTC time
        after both labels and a substantive revision-based rationale. Consensus
        items must not be adjudicated. Unresolved conflicts stay unavailable.

        Actual receipt and conservative provider-update reconstruction are separate
        clocks. The latter is a frozen diagnostic assumption, not reconstructed
        original delivery or classifier completion. Timing-conflicted revisions
        are quarantined from this queue. Today's human/classifier completion must
        never be backdated into a historical decision. The packet and measured
        reading accuracy do not activate a strategy or satisfy economic NWS12.
        This raw-archive assessment is a supplemental queue, not a publication of
        committed classifier_ground_truth rows into the evidence catalog. Completing
        it does not bypass the existing immutable catalog/export provenance gate.
        """;
}
