using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TradingFlow.Contracts.Presentation;
using TradingFlow.Contracts.Transport;
using TradingFlow.Contracts.V1;

namespace TradingFlow.Tests;

public sealed class DecisionContextEvidenceClientTests
{
    /// <summary>The client displays every returned fact, including unknown inputs, without shortening immutable hashes.</summary>
    [Fact]
    public void SummaryPresentation_RetainsServerFactsAndUnknownReasons()
    {
        var summary = Summary();
        Assert.Equal(new[] {
            "Evidence: approved-profile", "Manifest SHA-256: " + new string('a', 64),
            "Provider: fixture · Version: v1", "Taxonomy versions: taxonomy-2", "Classifier versions: classifier-3",
            "Evaluation SHA-256: " + new string('b', 64), "Availability basis: actual_receipt",
            "Coverage scopes: company, sector", "Event lookback days: 5", "Maximum coverage age hours: 24",
            "Dependency graph SHA-256: " + new string('c', 64), "Unknown reasons: global coverage absent"
        }, DecisionContextEvidencePresentation.Lines(summary));
        Assert.Empty(DecisionContextEvidencePresentation.Lines(null));
    }

    /// <summary>Shows the resolved fees, slippage, recipe parent and admission policy without supplying client defaults.</summary>
    [Fact]
    public void SettingsPresentation_RetainsResolvedCostsAndOriginalLineage()
    {
        var identity = new StrategyReference("recipe", "2.0.0", new string('d', 64));
        var parent = new StrategyReference("original", "1.0.0", new string('f', 64));
        var settings = new BacktestResolvedSettingsResponse("profile.yaml", new string('e', 64), 1, 2, 0.00001m,
            0.0002m, 8, 5, 3, "native-open-cutoff", null,
            [new(identity, "Recovery fixture", 10, parent, new("reviewed-native", "1.0.0", "existing-native", "shared-risk"))]);
        Assert.Equal(new[] {
            "Profile: profile.yaml", "Configuration SHA-256: " + new string('e', 64),
            "Fixed buy fee USD: 1 · Fixed sell fee USD: 2",
            "SEC fee rate: 0.00001 · FINRA TAF per share USD: 0.0002 · FINRA TAF cap USD: 8",
            "Maximum bar participation percent: 5 · Impact bps at maximum participation: 3",
            "Execution replay policy: native-open-cutoff", "Strategy horizon: swing", "Cutoff policy: Not specified", "Precommitted liquidation source UTC: Not specified",
            "Recipe: Recovery fixture · recipe 2.0.0 (" + new string('d', 64) + ")", "Adverse slippage bps: 10",
            "Parent: original 1.0.0 (" + new string('f', 64) + ")", "Admission: reviewed-native 1.0.0",
            "Execution timing: existing-native · Risk policy: shared-risk"
        }, BacktestSettingsPresentation.Lines(settings));
        Assert.Empty(BacktestSettingsPresentation.Lines(null));
    }

    /// <summary>Save transport retains the expected manifest reference and the server's frozen summary after reopening.</summary>
    [Fact]
    public async Task PlanTransport_RoundTripsOnlyImmutableReferenceAndServerSummary()
    {
        var summary = Summary();
        var run = new StartBacktestRequest("1.0", "evidence-client-test", Guid.NewGuid(),
            [new("fixture", "1.0.0", new string('d', 64))], DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddDays(10), 10000, 1, 25, 4, "use_cache", "full", "stable-key",
            DecisionContextEvidence: summary.Reference);
        var plan = new BacktestPlanResponse(1, DateTimeOffset.UnixEpoch, run,
            [new("Group 1", ["MSFT"], 10000)], 1, 10, "fixture", null!, new string('e', 64),
            EvidenceSummary: summary);
        using var handler = new EvidenceTransport(plan);
        using var http = new HttpClient(handler);
        var client = new TradingWorkflowClient(http, () => "https://fixture.example");
        var saved = await client.SaveBacktestPlanAsync(new(run, 5));
        var reopened = Assert.Single(await client.BacktestPlansAsync());
        Assert.Equal(summary.Reference, saved.Run.DecisionContextEvidence);
        Assert.Equal(summary.Reference, reopened.Run.DecisionContextEvidence);
        Assert.Equal(DecisionContextEvidencePresentation.Lines(summary), DecisionContextEvidencePresentation.Lines(reopened.EvidenceSummary));
        using var payload = JsonDocument.Parse(handler.RequestBody!);
        var reference = payload.RootElement.GetProperty("run").GetProperty("decisionContextEvidence");
        Assert.Equal(2, reference.EnumerateObject().Count());
        Assert.Equal(summary.Reference.Id, reference.GetProperty("id").GetString());
        Assert.Equal(summary.Reference.ManifestSha256, reference.GetProperty("manifestSha256").GetString());
    }

    /// <summary>Creates synthetic summary facts without claiming the absent global coverage passed.</summary>
    private static DecisionContextEvidenceSummary Summary() => new(new("approved-profile", new string('a', 64)),
        "fixture", "v1", ["taxonomy-2"], ["classifier-3"], [new string('b', 64)], ["actual_receipt"],
        ["company", "sector"], 5, 24, ["global coverage absent"], new string('c', 64));

    private sealed class EvidenceTransport(BacktestPlanResponse plan) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        /// <summary>Emulates save/reopen responses locally; it never creates a host, job or provider request.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Content is not null) RequestBody = await request.Content.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK) { Content = request.Method == HttpMethod.Get
                ? JsonContent.Create(new[] { plan }) : JsonContent.Create(plan) };
        }
    }
}
