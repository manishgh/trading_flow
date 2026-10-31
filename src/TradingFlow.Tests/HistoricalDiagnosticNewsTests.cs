using System.Globalization;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting;
using TradingFlow.Cli;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Evidence.Historical;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Collection;
using TradingFlow.Data.Evidence.Normalization;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Data.Evidence.Sentiment;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class HistoricalDiagnosticNewsTests
{
    /// <summary>Publishes a small synthetic real assessment graph for runner and worker lifetime tests without inference.</summary>
    internal static async Task<HistoricalDiagnosticNewsPublication> PublishAssessmentDiagnosticAsync(Archive fixture)
    {
        fixture.News("scope-company", [Story("scope-one", "2026-08-17T10:00:00Z", "2026-08-17T10:00:00Z", ["ABC"], "ABC raises full-year guidance")]);
        var original = fixture.Provider();
        var selectionPublication = HistoricalDiagnosticNewsManifestStore.PublishExclusive(Path.Combine(fixture.Root, "published"), original.Manifest);
        var selection = CompanyNewsAssessmentBuilder.ReadDiagnostic(fixture.Root, selectionPublication.Path, selectionPublication.Sha256);
        var at = Archive.Capture.AddDays(3);
        var model = new CompanyNewsPinnedModel("finbert-http", "ProsusAI/finbert", new string('a', 40), new string('b', 64),
            "Synthetic fixture; exact training data unknown.", null);
        var assessments = await CompanyNewsAssessmentBuilder.BuildAsync(fixture.Root, "synthetic-scope-fixture",
            new("diagnostic", Path.GetRelativePath(fixture.Root, selectionPublication.Path), selectionPublication.Sha256), selection.Sources,
            selection.Dependencies, model, (extraction, _) => Task.FromResult(new CompanyNewsModelEvidence(model,
                extraction.ModelInputSha256, CompanyNewsPolarity.Positive, .9m, at)), new AssessmentClock(at));
        var publication = CompanyNewsAssessmentManifestStore.PublishExclusive(Path.Combine(fixture.Root, "assessments"), assessments);
        return HistoricalDiagnosticNewsManifestStore.PublishExclusive(Path.Combine(fixture.Root, "published"), original.Manifest with {
            CompanyAssessmentManifest = new(Path.GetRelativePath(fixture.Root, publication.Path), publication.Sha256) });
    }

    /// <summary>Assessment publication enriches the existing provider while preserving revision selection and actual source clocks.</summary>
    [Fact]
    public async Task CompanyAssessmentsOverlayExactArchivedRevisionsAndVerifyDependencyGraph()
    {
        using var fixture = new Archive();
        fixture.News("company", [Story("one", "2026-08-17T10:00:00Z", "2026-08-17T10:00:00Z", ["ABC"], "ABC raises full-year guidance")]);
        var original = fixture.Provider();
        var originalPublication = HistoricalDiagnosticNewsManifestStore.PublishExclusive(Path.Combine(fixture.Root, "published"), original.Manifest);
        var selection = CompanyNewsAssessmentBuilder.ReadDiagnostic(fixture.Root, originalPublication.Path, originalPublication.Sha256);
        var at = Archive.Capture.AddDays(3);
        var model = new CompanyNewsPinnedModel("finbert-http", "ProsusAI/finbert", new string('a', 40), new string('b', 64),
            "Synthetic fixture; exact training data unknown.", null);
        var assessments = await CompanyNewsAssessmentBuilder.BuildAsync(fixture.Root, "synthetic-fixture",
            new("diagnostic", Path.GetRelativePath(fixture.Root, originalPublication.Path), originalPublication.Sha256), selection.Sources,
            selection.Dependencies, model, (extraction, _) => Task.FromResult(new CompanyNewsModelEvidence(model,
                extraction.ModelInputSha256, CompanyNewsPolarity.Positive, .9m, at)), new AssessmentClock(at));
        var published = CompanyNewsAssessmentManifestStore.PublishExclusive(Path.Combine(fixture.Root, "assessments"), assessments);
        var enriched = HistoricalDiagnosticNewsManifestStore.PublishExclusive(Path.Combine(fixture.Root, "published"), original.Manifest with {
            CompanyAssessmentManifest = new(Path.GetRelativePath(fixture.Root, published.Path), published.Sha256) });
        var provider = HistoricalDiagnosticNewsProvider.LoadVerified(fixture.Root, enriched.Path, enriched.Sha256);
        var context = await provider.ResolveAsync("ABC", Utc("2026-08-19T13:30:00Z"));
        var selected = Assert.Single(context.EligibleRevisions);
        Assert.Equal(CompanyNewsAssessmentState.Positive, selected.Assessment!.State);
        Assert.Equal(Archive.Capture, selected.ActualReceivedAtUtc);
        Assert.Equal(at, selected.Assessment.AssessedAtUtc);
        Assert.Equal(original.Manifest.PolicySha256, context.PolicySha256);
        Assert.NotNull(context.AssessmentPolicy);
        Assert.Equal(HistoricalDiagnosticNewsManifestStore.DeclaredDependencyGraphSha256(enriched, provider.Manifest), provider.DependencyGraphSha256);
        // Replacing only an assessment's archived text must fail before a context can be resolved.
        var row = Assert.Single(assessments.Rows);
        Assert.Throws<InvalidDataException>(() => CompanyNewsAssessmentManifestStore.Validate(assessments with { Rows = [row with {
            Source = row.Source with { Revision = row.Source.Revision with { Headline = "ABC cuts profit forecast" } } }] }));
        VerifyOperationScope(fixture, enriched, provider, context);
    }

    /// <summary>Uses the full production factory to prove one operation reuses its provider while independent attempts verify afresh.</summary>
    private static void VerifyOperationScope(Archive fixture, HistoricalDiagnosticNewsPublication publication,
        HistoricalDiagnosticNewsProvider original, HistoricalNewsPresenceContext expected)
    {
        var owner = new object();
        var config = new EngineConfig("batch", 1, 1, 0, 1, true, EvidenceResearchRoot: fixture.Root,
            EvidenceManifestPath: publication.Path, EvidenceManifestSha256: publication.Sha256,
            EvidenceDependencyGraphSha256: original.DependencyGraphSha256);
        var scope = Assert.IsType<WorkflowEvidenceVerificationScope>(WorkflowEvidenceVerificationScope.Open(owner, config));
        FrozenBacktestEvidence? retained = null;
        try
        {
            var verified = scope.Resolve(config);
            retained = verified;
            Assert.NotNull(scope.AssessmentSummary);
            Assert.Same(verified, FrozenBacktestEvidence.FromConfig(config, verified, scope.Guard));
            Assert.Same(verified, scope.Resolve(config));
            Assert.Throws<ArgumentNullException>(() => FrozenBacktestEvidence.FromConfig(config, verified, null!));
            Assert.Throws<InvalidOperationException>(() => FrozenBacktestEvidence.FromConfig(config, verified, _ => { }));
            var freshWithoutScope = FrozenBacktestEvidence.FromConfig(config)!;
            Assert.Throws<InvalidOperationException>(() => FrozenBacktestEvidence.FromConfig(config, freshWithoutScope, _ => { }));
            Assert.Throws<InvalidOperationException>(() => FrozenBacktestEvidence.FromConfig(config, freshWithoutScope, scope.Guard));
            Assert.Throws<InvalidOperationException>(() => verified.BindVerificationScope(scope.Guard));
            Assert.Empty(typeof(FrozenBacktestEvidence).GetConstructors());
            Assert.Throws<InvalidOperationException>(() => scope.RequireOwner(new object()));
            Assert.Throws<InvalidDataException>(() => scope.Resolve(config with { EvidenceManifestSha256 = new string('0', 64) }));
            Assert.Throws<InvalidDataException>(() => scope.Resolve(config with { EvidenceDependencyGraphSha256 = new string('0', 64) }));
            Assert.Throws<InvalidDataException>(() => scope.Resolve(config with { EvidenceResearchRoot = Path.GetDirectoryName(fixture.Root) }));
            Assert.Throws<InvalidDataException>(() => scope.Resolve(config with { EvidenceManifestPath = Path.Combine(fixture.Root, "other.json") }));
            var resolved = verified.HistoricalNewsProvider!.ResolveAsync("ABC", expected.DecisionAtUtc).AsTask().GetAwaiter().GetResult();
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(resolved));
            using var independent = Assert.IsType<WorkflowEvidenceVerificationScope>(WorkflowEvidenceVerificationScope.Open(new object(), config));
            Assert.NotSame(verified, independent.Resolve(config));
            Assert.NotSame(verified.HistoricalNewsProvider, independent.Resolve(config).HistoricalNewsProvider);
            if (OperatingSystem.IsWindows())
                Assert.ThrowsAny<IOException>(() => File.AppendAllText(Path.Combine(fixture.Root, "datasets", "company.json"), " "));
        }
        finally
        {
            scope.Dispose();
        }
        Assert.Throws<ObjectDisposedException>(() => scope.Resolve(config));
        Assert.Throws<ObjectDisposedException>(() => scope.Guard(config));
        Assert.Throws<ObjectDisposedException>(() => FrozenBacktestEvidence.FromConfig(config, retained!, scope.Guard));
        using (var recovery = WorkflowEvidenceVerificationScope.Open(owner, config)) Assert.NotNull(recovery);
        File.AppendAllText(Path.Combine(fixture.Root, "datasets", "company.json"), " ");
        Assert.Throws<InvalidDataException>(() => WorkflowEvidenceVerificationScope.Open(owner, config));
    }

    private sealed class AssessmentClock(DateTimeOffset now) : TimeProvider
    {
        /// <summary>Provides an observed October measurement clock for the synthetic retrospective overlay.</summary>
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    // The diagnostic policy must remain byte-identical to the already reviewed historical resolver policy.
    public void DomainAndHistoricalResolverFreezeTheSamePolicyBytes() => Assert.Equal(
        HistoricalEvidenceResolver.PolicySha256(HistoricalReconstructionPolicy.ConservativeV1), HistoricalNewsDiagnosticPolicy.PolicySha256);

    [Fact]
    // Equality with the delayed session open requires waiting another session; actual receipt remains October.
    public async Task ExactOpenBoundaryWaitsForFollowingCapturedOpenAndKeepsActualReceipt()
    {
        using var fixture = new Archive();
        fixture.News("a", [Story("one", "2026-08-17T13:30:00Z", "2026-08-17T13:30:00Z", ["AAA"])]);
        var provider = fixture.Provider();
        Assert.Empty((await provider.ResolveAsync("AAA", Utc("2026-08-18T13:30:00Z"))).EligibleRevisions);
        var revision = Assert.Single((await provider.ResolveAsync("AAA", Utc("2026-08-19T13:30:00Z"))).EligibleRevisions);
        Assert.Equal(Utc("2026-08-19T13:30:00Z"), revision.AssumedAvailableAtUtc);
        Assert.Equal(Archive.Capture, revision.ActualReceivedAtUtc);
    }

    [Fact]
    // Lookup may remember both tags, but decision membership always belongs to the latest arrived revision.
    public async Task LaterRevisionRemovesMembershipWithoutChangingEarlierDecision()
    {
        using var fixture = new Archive();
        fixture.News("a", [Story("one", "2026-08-17T10:00:00Z", "2026-08-17T10:00:00Z", ["AAA"]),
            Story("one", "2026-08-17T10:00:00Z", "2026-08-18T10:00:00Z", ["BBB"])]);
        var provider = fixture.Provider();
        Assert.Single((await provider.ResolveAsync("AAA", Utc("2026-08-18T13:30:00Z"))).EligibleRevisions);
        Assert.Empty((await provider.ResolveAsync("BBB", Utc("2026-08-18T13:30:00Z"))).EligibleRevisions);
        Assert.Empty((await provider.ResolveAsync("AAA", Utc("2026-08-19T13:30:00Z"))).EligibleRevisions);
        Assert.Single((await provider.ResolveAsync("BBB", Utc("2026-08-19T13:30:00Z"))).EligibleRevisions);
    }

    [Fact]
    // Every selected article is searched and provider updates never renew original publication freshness.
    public async Task SearchesAllFiveDaysAndDoesNotResetPublicationAge()
    {
        using var fixture = new Archive();
        fixture.News("a", [Story("old-updated", "2026-08-01T10:00:00Z", "2026-08-17T10:00:00Z", ["AAA"]),
            Story("four-days", "2026-08-14T14:00:00Z", "2026-08-14T14:00:00Z", ["AAA"]),
            Story("recent", "2026-08-17T10:00:00Z", "2026-08-17T10:00:00Z", ["AAA"])]);
        var context = await fixture.Provider().ResolveAsync("AAA", Utc("2026-08-19T13:30:00Z"));
        Assert.True(context.QueryEvidenceComplete);
        Assert.Equal(new[] { "four-days", "recent" }, context.EligibleRevisions.Select(item => item.ArticleId).ToArray());
    }

    [Fact]
    // The frozen freshness interval includes exactly 120 hours and excludes the following instant.
    public async Task PublicationFreshnessBoundaryIsInclusive()
    {
        using var fixture = new Archive();
        fixture.News("a", [Story("one", "2026-08-14T13:30:00Z", "2026-08-14T13:30:00Z", ["AAA"])]);
        var provider = fixture.Provider();
        var boundary = Utc("2026-08-19T13:30:00Z");
        Assert.Single((await provider.ResolveAsync("AAA", boundary)).EligibleRevisions);
        Assert.Empty((await provider.ResolveAsync("AAA", boundary.AddTicks(1))).EligibleRevisions);
    }

    [Fact]
    // An unclocked captured revision freezes its known symbol scope unavailable without contaminating other symbols.
    public async Task MissingRevisionClockRemainsScopedUnknown()
    {
        using var fixture = new Archive();
        fixture.News("a", [Story("one", "unverified", "2026-08-17T10:00:00Z", ["AAA"])]);
        var provider = fixture.Provider();
        var affected = await provider.ResolveAsync("AAA", Utc("2026-08-19T13:30:00Z"));
        Assert.False(affected.QueryEvidenceComplete);
        Assert.Equal("news_timestamp_unverified", affected.UnknownReason);
        Assert.True((await provider.ResolveAsync("BBB", Utc("2026-08-19T13:30:00Z"))).QueryEvidenceComplete);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    // A nonterminal capped query and an undocumented filter clock both remain unknown even with zero returned stories.
    public async Task IncompleteQueryAndUnprovedClockNeverBecomeNoNews(bool capped, bool missingSemantics)
    {
        using var fixture = new Archive();
        fixture.News("a", [], capped ? "uncollected-next-page" : null);
        var context = await fixture.Provider(withSemantics: !missingSemantics).ResolveAsync("AAA", Utc("2026-08-19T13:30:00Z"));
        Assert.False(context.QueryEvidenceComplete);
        Assert.NotNull(context.UnknownReason);
    }

    [Fact]
    // Conflicting later revisions cannot invalidate a decision before their assumed arrival.
    public async Task SameUpdateConflictBecomesUnknownOnlyAfterArrival()
    {
        using var fixture = new Archive();
        fixture.News("a", [Story("one", "2026-08-17T10:00:00Z", "2026-08-17T10:00:00Z", ["AAA"]),
            Story("one", "2026-08-17T10:00:00Z", "2026-08-18T10:00:00Z", ["AAA"], "version-a"),
            Story("one", "2026-08-17T10:00:00Z", "2026-08-18T10:00:00Z", ["AAA"], "version-b")]);
        var provider = fixture.Provider();
        Assert.True((await provider.ResolveAsync("AAA", Utc("2026-08-18T13:30:00Z"))).QueryEvidenceComplete);
        var later = await provider.ResolveAsync("AAA", Utc("2026-08-19T13:30:00Z"));
        Assert.False(later.QueryEvidenceComplete);
        Assert.Equal("news_same_update_revision_conflict", later.UnknownReason);
    }

    [Fact]
    // Exact re-downloads collapse by article/content identity while retaining their physical evidence hashes.
    public async Task DuplicatePagesCountArticleOnce()
    {
        using var fixture = new Archive();
        var story = Story("one", "2026-08-17T10:00:00Z", "2026-08-17T10:00:00Z", ["AAA"]);
        fixture.News("a", [story]);
        fixture.News("duplicate", [story]);
        var revision = Assert.Single((await fixture.Provider().ResolveAsync("AAA", Utc("2026-08-19T13:30:00Z"))).EligibleRevisions);
        Assert.True(revision.SourceSha256.Length >= 2);
    }

    [Fact]
    // Scope pruning retains revised-away membership and excludes unrelated malformed issuer news.
    public async Task ScopedPackageKeepsRemovalAndDoesNotQuarantineUnrelatedSymbols()
    {
        using var fixture = new Archive();
        fixture.News("a", [Story("one", "2026-08-17T10:00:00Z", "2026-08-17T10:00:00Z", ["AAA"]),
            Story("one", "2026-08-17T10:00:00Z", "2026-08-18T10:00:00Z", []),
            Story("bad-other", "2026-08-18T10:00:00Z", "2026-08-17T10:00:00Z", ["OTHER"])]);
        var provider = fixture.Provider(selected: ["AAA"]);
        Assert.Equal(2, provider.Manifest.Revisions.Length);
        var context = await provider.ResolveAsync("AAA", Utc("2026-08-19T13:30:00Z"));
        Assert.True(context.QueryEvidenceComplete);
        Assert.Empty(context.EligibleRevisions);
        Assert.False((await provider.ResolveAsync("OTHER", Utc("2026-08-19T13:30:00Z"))).QueryEvidenceComplete);
    }

    [Fact]
    // Scope pruning and decision membership normalize whitespace identically, preserving a provider-linked selected stock.
    public async Task ScopedWhitespaceSymbolMatchesDecisionMembership()
    {
        using var fixture = new Archive();
        fixture.News("a", [Story("one", "2026-08-17T10:00:00Z", "2026-08-17T10:00:00Z", [" AAA "])]);
        var provider = fixture.Provider(selected: ["AAA"]);
        Assert.Single(provider.Manifest.Revisions);
        var context = await provider.ResolveAsync("AAA", Utc("2026-08-19T13:30:00Z"));
        Assert.True(context.QueryEvidenceComplete);
        Assert.Equal(new[] { "AAA" }, Assert.Single(context.EligibleRevisions).Symbols.ToArray());
    }

    [Theory]
    [InlineData("blank_symbol")]
    [InlineData("null_identity")]
    // Unsupported identity or blank association is retained as unknown; a content-hash fallback must never certify complete empty news.
    public async Task ScopedMalformedMembershipAndNullIdentityRemainUnknown(string defect)
    {
        using var fixture = new Archive();
        object story = defect == "blank_symbol" ? Story("one", "2026-08-17T10:00:00Z", "2026-08-17T10:00:00Z", [" "])
            : new { id = (string?)null, created_at = "2026-08-17T10:00:00Z", updated_at = "2026-08-17T10:00:00Z", symbols = new[] { "AAA" }, headline = "headline" };
        fixture.News("a", [story]);
        var provider = fixture.Provider(selected: ["AAA"]);
        Assert.Single(provider.Manifest.Revisions);
        var context = await provider.ResolveAsync("AAA", Utc("2026-08-19T13:30:00Z"));
        Assert.False(context.QueryEvidenceComplete);
        Assert.Equal("news_identity_or_symbols_malformed", context.UnknownReason);
        Assert.Empty(context.EligibleRevisions);
    }

    [Fact]
    // Manifest hashes and original payload dependencies are verified independently before a frozen request can use them.
    public void PublishedManifestAndRawDependencyTamperingAreRejected()
    {
        using var fixture = new Archive();
        fixture.News("a", []);
        var provider = fixture.Provider();
        var publication = Assert.Single(HistoricalDiagnosticNewsManifestStore.FindPublished(Path.Combine(fixture.Root, "published")));
        Assert.Equal(provider.ManifestSha256, publication.Sha256);
        var displayed = HistoricalDiagnosticNewsManifestStore.ReadPublished(publication);
        Assert.Equal(provider.Manifest.SchemaVersion, displayed.SchemaVersion);
        Assert.Equal(provider.DependencyGraphSha256, HistoricalDiagnosticNewsManifestStore.DeclaredDependencyGraphSha256(publication, displayed));
        File.AppendAllText(Path.Combine(fixture.Root, "datasets", "a.json"), " ");
        Assert.Throws<InvalidDataException>(() => HistoricalDiagnosticNewsProvider.LoadVerified(fixture.Root, publication.Path, publication.Sha256));
        File.AppendAllText(publication.Path, " ");
        Assert.Throws<InvalidDataException>(() => HistoricalDiagnosticNewsManifestStore.ReadPublished(publication));
    }

    [Fact]
    // A frozen receipt selection is unaffected by a later capture appended under the original shared archive root.
    public async Task LaterUnrelatedCaptureDoesNotChangeFrozenPackage()
    {
        using var fixture = new Archive();
        fixture.News("a", []);
        var original = fixture.Provider(selected: ["AAA"]);
        var publication = Assert.Single(HistoricalDiagnosticNewsManifestStore.FindPublished(Path.Combine(fixture.Root, "published")));
        fixture.News("later-capture", [Story("later", "2026-08-17T10:00:00Z", "2026-08-17T10:00:00Z", ["AAA"])]);
        var reloaded = HistoricalDiagnosticNewsProvider.LoadVerified(fixture.Root, publication.Path, publication.Sha256);
        Assert.Equal(original.DependencyGraphSha256, reloaded.DependencyGraphSha256);
        var context = await reloaded.ResolveAsync("AAA", Utc("2026-08-19T13:30:00Z"));
        Assert.True(context.QueryEvidenceComplete);
        Assert.Empty(context.EligibleRevisions);
    }

    [Fact]
    // Program supplies the option tail; the CLI derives selected symbols from serialized saved cohorts and packages only synthetic local evidence.
    public async Task PackagingCommandAcceptsOptionTailFromProgramRegistration()
    {
        using var fixture = new Archive(DateTimeOffset.UtcNow.AddSeconds(-1));
        fixture.News("a", []);
        var planPath = await WriteSavedPlanAsync(fixture.Root);
        var artifacts = Path.Combine(fixture.Root, "evidence");
        await HistoricalDiagnosticNewsPackagingCommand.ExecuteAsync(["--saved-plan", planPath,
            "--research-root", fixture.Root, "--evidence-root", artifacts, "--legacy-root", fixture.Root,
            "--query-semantics-evidence", Path.Combine(fixture.Root, "query-semantics.txt"), "--query-timestamp-field", "updated_at"]);
        var publication = Assert.Single(HistoricalDiagnosticNewsManifestStore.FindPublished(Path.Combine(artifacts, "historical-news-diagnostics")));
        Assert.Equal(new[] { "AAA" }, HistoricalDiagnosticNewsManifestStore.ReadPublished(publication).SelectedSymbols.ToArray());
    }

    [Fact]
    // The real catalog-backed command must load manifests and raw pages under evidence/objects and publish a sibling selectable package.
    public async Task CatalogPackagingUsesTheServersEvidenceBaseLayout()
    {
        using var fixture = new Archive(DateTimeOffset.UtcNow.AddSeconds(-1));
        try
        {
            var evidenceRoot = Path.Combine(fixture.Root, "evidence");
            var catalogPath = Path.Combine(evidenceRoot, "catalog.sqlite");
            var datasetId = await CreateCatalogNewsDatasetAsync(evidenceRoot, catalogPath);
            var planPath = await WriteSavedPlanAsync(fixture.Root);
            await HistoricalDiagnosticNewsPackagingCommand.ExecuteAsync(["--saved-plan", planPath,
                "--research-root", fixture.Root, "--evidence-root", evidenceRoot, "--legacy-root", fixture.Root,
                "--catalog", catalogPath, "--dataset", datasetId, "--derived-root", Path.Combine(fixture.Root, "derived"),
                "--query-semantics-evidence", Path.Combine(fixture.Root, "query-semantics.txt"), "--query-timestamp-field", "updated_at"]);
            var publication = Assert.Single(HistoricalDiagnosticNewsManifestStore.FindPublished(Path.Combine(evidenceRoot, "historical-news-diagnostics")));
            var manifest = HistoricalDiagnosticNewsManifestStore.ReadPublished(publication);
            Assert.Single(manifest.Revisions);
            Assert.Contains(manifest.QueryCoverage, item => item.Complete);
            Assert.False(Directory.Exists(Path.Combine(evidenceRoot, "objects", "historical-news-diagnostics")));
        }
        finally
        {
            // Release fixture-owned SQLite handles before the fixture removes its isolated root on Windows.
            SqliteConnection.ClearAllPools();
        }
    }

    // Produces a genuine normalized news dataset through the existing catalog, immutable store and normalizer without network calls.
    private static async Task<string> CreateCatalogNewsDatasetAsync(string evidenceRoot, string catalogPath)
    {
        var now = Utc("2026-08-25T12:00:00Z");
        var store = new FileSystemImmutableArtifactStore(new ImmutableArtifactStoreOptions(Path.Combine(evidenceRoot, "objects")));
        var catalog = new SqliteEvidenceCatalog(new EvidenceCatalogOptions(catalogPath, EvidenceCatalogOpenMode.BootstrapNew), store);
        var request = new EvidenceCollectionRequest("news", "alpaca", "https://data.alpaca.markets/v1beta1/news", ["AAA"],
            now.AddDays(-10), now.AddDays(-1), "news", "raw", "USD", DateOnly.FromDateTime(now.UtcDateTime),
            new Dictionary<string, string> { ["exclude_contentless"] = "false" });
        var plan = new EvidenceCollectionPlan(now.AddDays(-1), "synthetic-package-layout", new string('a', 64),
            "synthetic-code", "synthetic-collector-v1", "synthetic-partition-v1", [request]);
        await catalog.RegisterCollectionPlanAsync(plan);
        await catalog.SaveCollectionCheckpointAsync(new(plan.JobId, EvidenceCollectionState.Planned, now.AddDays(-1)));
        await catalog.SaveCollectionCheckpointAsync(new(plan.JobId, EvidenceCollectionState.Collecting, now.AddHours(-1)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { news = new[] { new { id = 1,
            created_at = now.AddDays(-3), updated_at = now.AddDays(-3), symbols = new[] { "AAA" },
            headline = "Synthetic company story", url = "https://example.test/story/1" } }, next_page_token = (string?)null });
        var artifact = new EvidenceArtifactReference(new EvidenceContentAddress(Convert.ToHexStringLower(SHA256.HashData(bytes)),
            bytes.Length, "application/json"), new EvidenceObjectNamespace("raw/alpaca"));
        await store.PutIfAbsentAsync(new(artifact), bytes);
        using var message = new AlpacaNewsEvidenceRequestAdapter(new Uri("https://data.alpaca.markets")).CreateRequest(request, null);
        var uriHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(message.RequestUri!.AbsoluteUri)));
        var observation = new EvidenceSourceObservation("synthetic-news-page", plan.JobId, plan.LogicalPlanHash,
            plan.CollectionAttemptHash, request.RequestId, "page-1", "alpaca", request.Endpoint, EvidenceTransportKind.Http,
            200, request.Symbols, request.RequestedStartUtc, request.RequestedEndUtc, request.DataFeed, request.Adjustment,
            request.Currency, request.AsOfDate, null, null, null, now, artifact, plan.RunId, plan.ConfigHash, plan.CodeVersion,
            requestAttributes: new Dictionary<string, string> { ["page_ordinal"] = "1", ["attempt"] = "1", ["request_uri_sha256"] = uriHash });
        await catalog.RegisterSourceObservationAsync(observation);
        await catalog.SaveRequestCursorCheckpointAsync(new(plan.JobId, request.RequestId, 1, null, [], observation.ObservationId, 1, true, now));
        var normalizer = new EvidenceNormalizationOrchestrator(catalog, store,
            new EvidenceParquetPartitionPublisher(new EvidenceParquetCodec(), store));
        var result = await normalizer.NormalizeAsync(new EvidenceNormalizationJob(plan.JobId, EvidenceDatasetKind.NewsRevisions,
            1, "synthetic-news-v1", [new EvidenceSecurityIdentity("synthetic-aaa", "AAA")],
            new EvidenceObjectNamespace("normalized/alpaca-news"), NewsAvailabilityEvidence.ProviderUpdatedTimestampOnly));
        return result.DatasetId;
    }

    // Serializes the ordinary saved-plan contract so both CLI tests select stocks through the same source as production packaging.
    private static async Task<string> WriteSavedPlanAsync(string root)
    {
        var snapshot = Guid.NewGuid();
        var hash = new string('a', 64);
        var request = new StartBacktestRequest("1.0", "synthetic-package-plan", snapshot,
            [new("synthetic-recipe", "1.0.0", hash)], Utc("2026-08-01T00:00:00Z"), Utc("2026-09-01T00:00:00Z"),
            10000m, 1m, 25m, 4, "prefer_cache", "retain", "synthetic-package-key");
        var source = new UniversePreviewResponse("1.0", snapshot, "swing", Archive.Capture,
            Archive.Capture.AddDays(1), hash, [new("AAA", "diagnostic", [])], []);
        var saved = new BacktestPlanResponse(7, Archive.Capture, request, [new("group-1", ["AAA"], 10000m)],
            1, 15, "Synthetic saved source selection", source, hash);
        var planPath = Path.Combine(root, "saved-plan.json");
        await File.WriteAllBytesAsync(planPath, JsonSerializer.SerializeToUtf8Bytes(saved, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return planPath;
    }

    [Fact]
    // Saved-plan schema dispatch captures the complete raw graph, and restart verification refuses changed original bytes.
    public void DiagnosticDispatchAndDurableInputCapturePreserveExactDependencyGraph()
    {
        using var fixture = new Archive();
        fixture.News("a", []);
        var provider = fixture.Provider();
        var publication = Assert.Single(HistoricalDiagnosticNewsManifestStore.FindPublished(Path.Combine(fixture.Root, "published")));
        var config = Path.Combine(fixture.Root, "run.yaml");
        File.WriteAllText(config, "synthetic immutable request input");
        var run = new SimpleYamlReader().ReadBacktestRun(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "swing-backtest-profile.yaml"));
        run = run with { Strategies = [], Engine = run.Engine with { EvidenceResearchRoot = fixture.Root,
            EvidenceManifestPath = publication.Path, EvidenceManifestSha256 = publication.Sha256,
            EvidenceDependencyGraphSha256 = provider.DependencyGraphSha256 } };
        var dispatched = FrozenBacktestEvidence.FromConfig(run.Engine)!;
        Assert.Null(dispatched.StrictContextProvider);
        Assert.NotNull(dispatched.HistoricalNewsProvider);
        Assert.False(dispatched.Provenance.StrictCatalystProviderUsed);
        Assert.Equal(HistoricalNewsDiagnosticPolicy.PolicyId, dispatched.Provenance.DiagnosticPolicyId);
        Assert.Throws<InvalidDataException>(() => FrozenBacktestEvidence.FromConfig(run.Engine with { EvidenceDependencyGraphSha256 = new string('0', 64) }));
        var inputs = BacktestRunInputFiles.Resolve(config, run);
        Assert.Equal(provider.Dependencies.Count + 1, inputs.Count);
        var durable = DurableFileJobRequest.Deserialize(DurableFileJobRequest.Capture(inputs).Serialize());
        durable.Verify();
        File.AppendAllText(Path.Combine(fixture.Root, "datasets", "a.json"), " ");
        Assert.Throws<InvalidDataException>(() => durable.Verify());
        Assert.Throws<InvalidDataException>(() => FrozenBacktestEvidence.FromConfig(run.Engine));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    // Recomputing an internally consistent manifest cannot hide a whole story or the later revision that removed a stock tag.
    public void CompleteQueryCannotCertifyOmittedArticleOrRevision(bool omitOnlyLatest)
    {
        using var fixture = new Archive();
        fixture.News("a", [Story("one", "2026-08-17T10:00:00Z", "2026-08-17T10:00:00Z", ["AAA"]),
            Story("one", "2026-08-17T10:00:00Z", "2026-08-18T10:00:00Z", [])]);
        var historical = HistoricalEvidenceManifestBuilder.BuildFromCollectionRoot(fixture.Root, Archive.Capture.AddDays(1));
        var hidden = historical with { Facts = historical.Facts.Where(item => item.Kind != HistoricalFactKind.NewsVersion ||
            omitOnlyLatest && item.UpdatedAtUtc == Utc("2026-08-17T10:00:00Z")).ToImmutableArray() };
        var manifest = HistoricalDiagnosticNewsManifestBuilder.Build(fixture.Root, [hidden], builtAtUtc: Archive.Capture.AddDays(2), selectedSymbols: ["AAA"]);
        Assert.Throws<InvalidDataException>(() => HistoricalDiagnosticNewsManifestStore.PublishExclusive(Path.Combine(fixture.Root, "bad-publication"), manifest));
    }

    [Fact]
    // Even recomputed availability clocks cannot legitimize an earlier open that is absent from the captured exchange calendar.
    public void CalendarSessionsMustMatchExactCapturedProviderPayload()
    {
        using var fixture = new Archive();
        fixture.News("a", [Story("one", "2026-08-17T10:00:00Z", "2026-08-17T10:00:00Z", ["AAA"])]);
        var historical = HistoricalEvidenceManifestBuilder.BuildFromCollectionRoot(fixture.Root, Archive.Capture.AddDays(1));
        var changed = historical with { Sessions = historical.Sessions.SetItem(1, historical.Sessions[1] with {
            OpenUtc = historical.Sessions[1].OpenUtc.AddHours(-1) }) };
        var manifest = HistoricalDiagnosticNewsManifestBuilder.Build(fixture.Root, [changed], builtAtUtc: Archive.Capture.AddDays(2), selectedSymbols: ["AAA"]);
        Assert.Throws<InvalidDataException>(() => HistoricalDiagnosticNewsManifestStore.PublishExclusive(Path.Combine(fixture.Root, "bad-calendar"), manifest));
    }

    // Synthetic stories make revision and symbol changes explicit without using live data.
    private static object Story(string id, string published, string updated, string[] symbols, string headline = "headline") =>
        new { id, created_at = published, updated_at = updated, symbols, headline };

    // Test clocks are always literal UTC; no operator-local conversion participates in expected values.
    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    internal sealed class Archive : IDisposable
    {
        public static readonly DateTimeOffset Capture = Utc("2026-10-19T12:00:00Z");
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tf-diagnostic-news-tests-" + Guid.NewGuid().ToString("N"));
        private readonly DateTimeOffset actualCapture;

        // The fixture owns all of its files and supplies real calendar rows rather than next-weekday assumptions.
        public Archive(DateTimeOffset? actualCapture = null)
        {
            this.actualCapture = actualCapture ?? Capture;
            CapturePage("calendar", "/v2/calendar", Enumerable.Range(17, 12).Select(day => new DateOnly(2026, 8, day))
                .Where(date => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                .Select(date => new { date = date.ToString("yyyy-MM-dd"), open = "09:30", close = "16:00" }).ToArray(), new { });
            File.WriteAllText(Path.Combine(Root, "query-semantics.txt"), "Synthetic retained observed updated_at query behavior, not an official guarantee.");
        }

        // A complete query explicitly requests contentless stories and ends only on a null response cursor.
        public void News(string name, object[] stories, string? next = null) => CapturePage(name, "/v1beta1/news",
            new { news = stories, next_page_token = next }, new { start = "2026-08-01T00:00:00Z", end = "2026-09-01T00:00:00Z", exclude_contentless = "false" });

        // Supplies synthetic raw issuer names without adding an export universe or widening normalized earnings facts.
        public void Earnings(string name, object[] items) => CapturePage(name, "/api/calendar/earnings",
            new { items, page = 1, totalPages = 1 }, new { dateFrom = "2026-08-17", page = 1 }, "finviz");

        // Packages and reloads through public production interfaces so hashes, membership and clock checks are exercised together.
        public HistoricalDiagnosticNewsProvider Provider(bool withSemantics = true, string[]? selected = null)
        {
            var historical = HistoricalEvidenceManifestBuilder.BuildFromCollectionRoot(Root, Capture.AddDays(1));
            var semantics = withSemantics ? new HistoricalDiagnosticQuerySemantics("updated_at", "query-semantics.txt",
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Root, "query-semantics.txt"))))) : null;
            var manifest = HistoricalDiagnosticNewsManifestBuilder.Build(Root, [historical], semantics, Capture.AddDays(2), selected);
            var published = HistoricalDiagnosticNewsManifestStore.PublishExclusive(Path.Combine(Root, "published"), manifest);
            return HistoricalDiagnosticNewsProvider.LoadVerified(Root, published.Path, published.Sha256);
        }

        // Writes exact raw bytes and a distinct actual receipt; the diagnostic never edits that receipt date.
        private void CapturePage(string name, string resource, object payload, object parameters, string provider = "alpaca-data")
        {
            Directory.CreateDirectory(Path.Combine(Root, "datasets"));
            Directory.CreateDirectory(Path.Combine(Root, "receipts"));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            var relative = Path.Combine("datasets", name + ".json");
            File.WriteAllBytes(Path.Combine(Root, relative), bytes);
            File.WriteAllText(Path.Combine(Root, "receipts", name + ".json"), JsonSerializer.Serialize(new {
                Provider = provider, Resource = resource, Parameters = parameters, HttpStatus = 200,
                CapturedUtc = actualCapture, Payload = relative, PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), HistoricalFirstReceiptClaim = false }));
        }

        // Deletes only the exact synthetic fixture root after checking both its parent and owned-name marker.
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(full).StartsWith("tf-diagnostic-news-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the owned synthetic fixture root.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
