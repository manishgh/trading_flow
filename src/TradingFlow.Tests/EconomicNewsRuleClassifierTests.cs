using TradingFlow.Domain.News;
using TradingFlow.Domain.Research;

namespace TradingFlow.Tests;

public sealed class EconomicNewsRuleClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 14, 0, 0, TimeSpan.Zero);
    private static EconomicNewsRuleResult Classify(string headline, string? summary = null, string? body = null,
        string[]? symbols = null) => EconomicNewsRuleClassifier.Classify(new(headline, summary, body, symbols ?? ["ABC"], "alpaca"), Now);

    [Theory]
    [InlineData("ABC reports Q3 earnings", CatalystNewsCategory.Earnings)]
    [InlineData("ABC raises full-year guidance", CatalystNewsCategory.Guidance)]
    [InlineData("ABC agrees to acquire XYZ", CatalystNewsCategory.MergerAcquisition)]
    [InlineData("FDA approves ABC drug", CatalystNewsCategory.Regulatory)]
    [InlineData("ABC announces public offering", CatalystNewsCategory.CapitalStructure)]
    [InlineData("ABC wins government contract", CatalystNewsCategory.MajorCommercialEvent)]
    [InlineData("ABC names new chief executive", CatalystNewsCategory.Management)]
    [InlineData("Broker upgrades ABC to buy", CatalystNewsCategory.AnalystAction)]
    [InlineData("ABC settles lawsuit", CatalystNewsCategory.Legal)]
    [InlineData("ABC halts factory production", CatalystNewsCategory.Operational)]
    [InlineData("Federal Reserve cuts interest rates", CatalystNewsCategory.MacroSector)]
    [InlineData("Top stocks to buy this week", CatalystNewsCategory.PromotionalLowInformation)]
    [InlineData("ABC attends annual investor conference", CatalystNewsCategory.Unknown)]
    public void SpecificAssertedEventsMapToTheExistingThirteenCategoryVocabulary(string headline, CatalystNewsCategory expected)
    {
        var result = Classify(headline);
        Assert.Equal(expected, result.Category);
        Assert.Equal(EconomicNewsRuleClassifier.Version, result.Version);
        Assert.Equal(Now, result.ClassifiedAtUtc);
        Assert.Equal(EconomicNewsTextSupport.HeadlineOnly, result.TextSupport);
        Assert.True(result.ResearchOnly);
        Assert.False(result.HistoricalAvailabilityProven);
        Assert.False(result.AccuracyMeasured);
        Assert.False(result.TradingActivationAllowed);
        if (expected != CatalystNewsCategory.Unknown) Assert.NotEmpty(result.MatchedCues);
    }

    [Theory]
    [InlineData("Analysts expect strong ABC earnings next quarter")]
    [InlineData("ABC files routine Form 10-Q with SEC")]
    [InlineData("A trader recommends buying ABC shares")]
    [InlineData("ABC will report Q3 earnings next week")]
    [InlineData("Analysts expect ABC earnings to beat estimates")]
    [InlineData("ABC denies acquisition rumors")]
    [InlineData("ABC upgrades software to premium edition")]
    [InlineData("ABC names director for its new film")]
    public void ForecastsRoutineFilingsRecommendationsAndRumorsDoNotBecomeAssertedEvents(string headline)
    {
        var result = Classify(headline);
        Assert.Equal(CatalystNewsCategory.Unknown, result.Category);
        Assert.False(result.DirectionIsClear);
        Assert.Null(result.Materiality);
    }

    [Fact]
    public void GovernmentContractIsCompanyCommercialEvidenceRatherThanMacroPolicy()
    {
        var result = Classify("ABC awarded a government contract to supply satellites");
        Assert.Equal(CatalystNewsCategory.MajorCommercialEvent, result.Category);
        Assert.Equal(EconomicNewsScope.Company, result.Scope);
        Assert.Equal(new[] { "ABC" }, result.Targets);
        // Reading topics are separately inferred by the existing UI taxonomy.
        Assert.Contains("policy", result.DisplayTopics.Categories);
        Assert.Null(result.Materiality);
        Assert.Null(result.Adverse);
    }

    [Fact]
    public void EnforcementRequiresARegulatorActionAndLegalSolicitationDoesNotInventACase()
    {
        var enforcement = Classify("SEC charges ABC with accounting fraud");
        var ad = Classify("Law firm investigating ABC on behalf of investors");
        var formal = Classify("Investors filed class action against ABC");
        Assert.Equal(CatalystNewsCategory.Regulatory, enforcement.Category);
        Assert.Equal(CatalystNewsCategory.PromotionalLowInformation, ad.Category);
        Assert.Equal(CatalystNewsCategory.Legal, formal.Category);
        Assert.Contains(ad.MatchedCues, cue => cue.RuleId == "law-firm-solicitation");
    }

    [Fact]
    public void SummaryAndActualHtmlBodySupportAreVisibleAndConflictingEconomicEventsAbstain()
    {
        var headlineOnly = Classify("ABC update");
        var summary = Classify("ABC update", "ABC declares a dividend");
        var body = Classify("ABC update", body: "<p>ABC declares a dividend &amp; distribution.</p>");
        var conflict = Classify("ABC reports Q3 earnings", body: "ABC agrees to acquire XYZ.");
        Assert.Equal(CatalystNewsCategory.Unknown, headlineOnly.Category);
        Assert.Equal(CatalystNewsCategory.CapitalStructure, summary.Category);
        Assert.Equal(EconomicNewsTextSupport.SummarySupported, summary.TextSupport);
        Assert.Equal(CatalystNewsCategory.CapitalStructure, body.Category);
        Assert.Equal(EconomicNewsTextSupport.BodySupported, body.TextSupport);
        Assert.Contains(body.MatchedCues, cue => cue.Field == "body" && cue.EvidenceExcerpt.Contains("& distribution"));
        Assert.Equal(CatalystNewsCategory.Unknown, conflict.Category);
        Assert.Contains("conflicting_economic_event_categories", conflict.AmbiguityReasons);
        Assert.Contains(conflict.MatchedCues, cue => cue.Category == CatalystNewsCategory.Earnings);
        Assert.Contains(conflict.MatchedCues, cue => cue.Category == CatalystNewsCategory.MergerAcquisition);
        var unreadableBody = Classify("ABC update", body: "<p> </p>");
        Assert.Equal(EconomicNewsTextSupport.BodySupported, unreadableBody.TextSupport);
        Assert.Equal(CatalystNewsCategory.Unknown, unreadableBody.Category);
    }

    [Fact]
    public void HeadlinePriceActionCanBeExplainedByAssertedSupportButDoesNotSetDirection()
    {
        var result = Classify("ABC shares surge", "ABC receives FDA approval");
        Assert.Equal(CatalystNewsCategory.Regulatory, result.Category);
        Assert.Equal(CatalystDirection.Ambiguous, result.Direction);
        Assert.False(result.DirectionIsClear);
        Assert.Contains("economic_event_supported_only_by_summary_or_body", result.AmbiguityReasons);
    }

    [Fact]
    public void TickerMetadataDoesNotAloneBindIssuerScopeOrCertifyHistoricalSectorMembership()
    {
        var result = Classify("Acme raises guidance", symbols: ["ABC"]);
        Assert.Equal(CatalystNewsCategory.Guidance, result.Category);
        Assert.Equal(EconomicNewsScope.Unknown, result.Scope);
        Assert.Empty(result.Targets);
        Assert.False(result.DirectionIsClear);
        Assert.Null(result.Materiality);
        var macro = Classify("Federal Reserve cuts interest rates", symbols: ["SPY"]);
        Assert.Equal(EconomicNewsScope.Global, macro.Scope);
        Assert.Equal(new[] { "*" }, macro.Targets);
        Assert.False(macro.DirectionIsClear);
    }

    [Fact]
    public void DirectionAndMaterialityAreLimitedToExplicitSupportedRulesAndConflictsAreVisible()
    {
        var raise = Classify("ABC raises guidance");
        var mixed = Classify("ABC raises revenue forecast and cuts profit forecast");
        Assert.Equal(CatalystDirection.Positive, raise.Direction);
        Assert.True(raise.DirectionIsClear);
        Assert.Null(raise.Materiality);
        Assert.Equal(CatalystDirection.Ambiguous, mixed.Direction);
        Assert.False(mixed.DirectionIsClear);
        Assert.True(mixed.AmbiguityReasons.Contains("conflicting_headline_direction_cues") || mixed.Scope == EconomicNewsScope.Unknown);
        Assert.Null(Classify("ABC agrees to acquire XYZ").Materiality);
        Assert.Null(Classify("ABC agrees to acquire XYZ").Adverse);
    }

    [Fact]
    public void ActualClockIsRequiredAndIdenticalInputsAtTheSameClockAreDeterministic()
    {
        var input = new EconomicNewsRuleInput("ABC reports Q3 earnings", Symbols: ["ABC"]);
        Assert.Throws<ArgumentException>(() => EconomicNewsRuleClassifier.Classify(input, default));
        Assert.Throws<ArgumentException>(() => EconomicNewsRuleClassifier.Classify(input, Now.ToOffset(TimeSpan.FromHours(2))));
        var first = EconomicNewsRuleClassifier.Classify(input, Now);
        var second = EconomicNewsRuleClassifier.Classify(input, Now);
        Assert.Equal(first.Category, second.Category);
        Assert.Equal(first.MatchedCues.ToArray(), second.MatchedCues.ToArray());
        Assert.Equal(first.AmbiguityReasons.ToArray(), second.AmbiguityReasons.ToArray());
    }

    [Fact]
    public void MonthMayIsNotAForwardModalAndNegatedEventsCannotBorrowAnAffirmativeCue()
    {
        Assert.Equal(CatalystNewsCategory.Earnings, Classify("ABC reports quarterly revenue in May").Category);
        var negated = Classify("ABC does not raise guidance");
        Assert.Equal(CatalystNewsCategory.Unknown, negated.Category);
        Assert.Contains("negated_speculative_or_forward_scheduled_event_assertion", negated.AmbiguityReasons);
        Assert.Contains(negated.MatchedCues, cue => cue.AssertionUncertain);
    }

    [Theory]
    [InlineData("ABC cuts costs and reaffirms guidance")]
    [InlineData("ABC reports earnings after XYZ cuts jobs")]
    [InlineData("XYZ raises guidance; ABC is supplier")]
    [InlineData("ABC upgrades XYZ to buy")]
    public void UnrelatedHeadlineVerbsAndTickerMentionsCannotLendDirectionOrAdverseRisk(string headline)
    {
        var result = Classify(headline);
        Assert.Equal(CatalystDirection.Ambiguous, result.Direction);
        Assert.False(result.DirectionIsClear);
        Assert.Null(result.Adverse);
        Assert.Null(result.Materiality);
        if (headline.StartsWith("XYZ", StringComparison.Ordinal)) {
            Assert.Equal(EconomicNewsScope.Unknown, result.Scope);
            Assert.Empty(result.Targets);
        }
    }

    [Fact]
    public void ReaffirmedGuidanceHasNoMagnitudeOrDirectionAndAdverseUsesOnlyTheBoundEvent()
    {
        var unchanged = Classify("ABC reaffirms guidance");
        Assert.Equal(CatalystNewsCategory.Guidance, unchanged.Category);
        Assert.Equal(EconomicNewsScope.Company, unchanged.Scope);
        Assert.Equal(CatalystDirection.Ambiguous, unchanged.Direction);
        Assert.Null(unchanged.Materiality);
        Assert.Null(unchanged.Adverse);
        Assert.Null(Classify("ABC resumes factory production after XYZ data breach").Adverse);
    }

    [Fact]
    public void CommonWordTickersNeedExactCaseAndAssertionSubjectWhileAuthorityDenialIsAnActualDecision()
    {
        var onMonday = Classify("Acme raises guidance on Monday", symbols: ["ON"]);
        Assert.Equal(EconomicNewsScope.Unknown, onMonday.Scope);
        Assert.False(onMonday.DirectionIsClear);
        var lowercaseWord = Classify("on raises guidance", symbols: ["ON"]);
        Assert.Equal(EconomicNewsScope.Unknown, lowercaseWord.Scope);
        Assert.False(lowercaseWord.DirectionIsClear);
        var denial = Classify("FDA denies ABC drug approval");
        Assert.Equal(CatalystNewsCategory.Regulatory, denial.Category);
        Assert.DoesNotContain(denial.MatchedCues, cue => cue.AssertionUncertain);
        Assert.Null(denial.Materiality);
    }
}
