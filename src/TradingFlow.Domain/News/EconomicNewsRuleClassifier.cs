using System.Collections.Immutable;
using System.Net;
using System.Text.RegularExpressions;
using TradingFlow.Domain.Research;

namespace TradingFlow.Domain.News;

public enum EconomicNewsTextSupport { HeadlineOnly = 1, SummarySupported = 2, BodySupported = 3 }
public enum EconomicNewsScope { Unknown = 0, Company = 1, Sector = 2, Global = 3 }
public enum EconomicNewsContext { Unknown = 0, Company = 1, Sector = 2, Global = 3, Financial = 4, Policy = 5 }

public sealed record EconomicNewsRuleInput(string Headline, string? Summary = null, string? Body = null,
    IReadOnlyList<string>? Symbols = null, string Provider = "");
public sealed record EconomicNewsRuleCue(string RuleId, CatalystNewsCategory Category,
    string Field, string MatchedText, string EvidenceExcerpt, bool AssertionUncertain, string AssertionPrefix);
public sealed record EconomicNewsRuleResult(string Version, DateTimeOffset ClassifiedAtUtc,
    CatalystNewsCategory Category, CatalystDirection Direction, bool DirectionIsClear,
    CatalystMateriality? Materiality, EconomicNewsTextSupport TextSupport,
    EconomicNewsScope Scope, EconomicNewsContext ContextCategory, ImmutableArray<string> Targets,
    bool? Adverse, ImmutableArray<EconomicNewsRuleCue> MatchedCues, ImmutableArray<string> AmbiguityReasons,
    NewsDisplayTopics DisplayTopics, bool ResearchOnly = true, bool HistoricalAvailabilityProven = false,
    bool AccuracyMeasured = false, bool TradingActivationAllowed = false);

/// <summary>
/// Interpretable research screening rules, not human annotations or a certified economic model.
/// The caller binds the exact input revision and persists the actual classification time.
/// Missing materiality/adverse evidence stays null; it must not be filled with a default for admission.
/// </summary>
public static class EconomicNewsRuleClassifier
{
    public const string Version = "economic-news-research-rules-v1";
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static Regex Pattern(string value) => new(value, Options, NewsPatterns.MatchTimeout);
    private sealed record Rule(string Id, CatalystNewsCategory Category, Regex Pattern);
    private static Rule R(string id, CatalystNewsCategory category, string pattern) => new(id, category, Pattern(pattern));

    // Verbs and economic objects must occur in the same sentence. A channel, a provider
    // tag, a ticker, an SEC filing or a lone word such as "buy" is never an event rule.
    private static readonly Rule[] Rules = [
        R("reported-period-results", CatalystNewsCategory.Earnings,
            @"\b(?:reports?|reported|posts?|posted|announces?|announced)\b[^.!?]{0,90}\b(?:earnings|financial results|quarterly results|quarter results|full.year results|fiscal.year results|quarterly revenue|quarter revenue|quarterly profit|quarter profit|q[1-4] results|q[1-4] earnings|q[1-4] revenue|q[1-4] eps)\b"),
        R("reported-beat-miss", CatalystNewsCategory.Earnings,
            @"\b(?:earnings|revenue|eps|quarterly profit|q[1-4] profit)\b[^.!?]{0,35}\b(?:beats?|beat|misses?|missed)\b[^.!?]{0,35}\b(?:estimates?|expectations?|consensus)\b"),
        R("issued-forward-guidance", CatalystNewsCategory.Guidance,
            @"\b(?:raises?|raised|cuts?|cut|lowers?|lowered|boosts?|boosted|reaffirms?|reaffirmed|withdraws?|withdrew|issues?|issued|updates?|updated)\s+(?:(?:its|the|full.year|annual|quarterly|q[1-4]|20\d{2})\s+){0,5}(?:guidance|outlook|full.year forecast|revenue forecast|profit forecast|earnings forecast)\b"),
        R("announced-transaction", CatalystNewsCategory.MergerAcquisition,
            @"\b(?:agrees?|agreed|agreement|announces?|announced|completes?|completed|terminates?|terminated|rejects?|rejected)\b[^.!?]{0,65}\b(?:acquisition|merger|takeover|buyout|divestiture|to acquire|to sell its business|to buy (?:rival|competitor|company|business))\b|\b(?:acquires?|acquired|merges? with|merged with|divests?|divested)\b"),
        R("regulator-decision", CatalystNewsCategory.Regulatory,
            @"\b(?:fda|ftc|sec|doj|epa|european commission|regulator|regulators)\b[^.!?]{0,65}\b(?:approves?|approved|rejects?|rejected|clears?|cleared|denies|denied|charges?|charged|fines?|fined|investigates?|investigated|opens? (?:a |an )?(?:probe|investigation)|issues? (?:a )?(?:warning|complete response letter))\b|\b(?:receives?|received|obtains?|obtained)\b[^.!?]{0,45}\b(?:fda|regulatory) approval\b"),
        R("capital-action", CatalystNewsCategory.CapitalStructure,
            @"\b(?:announces?|announced|launches?|launched|prices?|priced|completes?|completed|declares?|declared|authorizes?|authorized|suspends?|suspended|cuts?|cut|raises?|raised|increases?|increased)\b[^.!?]{0,65}\b(?:public offering|stock offering|share offering|equity offering|secondary offering|convertible notes|debt offering|debt refinancing|dividend|share repurchase|stock repurchase|share buyback|stock buyback|buyback program|stock split|reverse split)\b|\b(?:files?|filed) (?:for )?(?:chapter 11|bankruptcy)\b"),
        R("commercial-agreement", CatalystNewsCategory.MajorCommercialEvent,
            @"\b(?:wins?|won|awarded|secures?|secured|signs?|signed|lands?|landed|enters?|entered)\b[^.!?]{0,65}\b(?:contract|commercial agreement|supply agreement|licensing agreement|strategic partnership|customer agreement)\b|\b(?:launches?|launched|unveils?|unveiled)\b[^.!?]{0,55}\b(?:product|platform|service|drug|satellite|commercial launch)\b"),
        R("executive-governance-event", CatalystNewsCategory.Management,
            @"\b(?:appoints?|appointed|names?|named|elects?|elected|fires?|fired|dismisses?|dismissed)\b[^.!?]{0,65}\b(?:ceo|cfo|chief executive|chief financial|chairman|chairwoman|board member|board director)\b|\b(?:ceo|cfo|chief executive|chief financial|chairman|chairwoman)\b[^.!?]{0,45}\b(?:resigns?|resigned|retires?|retired|steps? down|dies|died|departs?|departed)\b"),
        R("analyst-rating-action", CatalystNewsCategory.AnalystAction,
            @"\b(?:upgrades?|upgraded|downgrades?|downgraded)\b[^.!?]{0,65}\b(?:to|from)\s+(?:buy|sell|hold|neutral|outperform|underperform|overweight|underweight|equal.weight|market.perform)\b|\b(?:raises?|raised|cuts?|cut|lowers?|lowered|lifts?|lifted|boosts?|boosted|maintains?|maintained)\b[^.!?]{0,45}\bprice target\b|\b(?:initiates?|initiated|resumes?|resumed)\b[^.!?]{0,35}\bcoverage\b"),
        R("formal-legal-action", CatalystNewsCategory.Legal,
            @"\b(?:files?|filed|faces?|faced|settles?|settled|dismisses?|dismissed)\b[^.!?]{0,60}\b(?:lawsuit|class action|litigation|legal claim)\b|\b(?:court|judge|jury)\b[^.!?]{0,60}\b(?:rules?|ruled|orders?|ordered|awards?|awarded|verdict|judgment|convicts?|convicted)\b|\b(?:lawsuit|class action)\b[^.!?]{0,30}\b(?:filed|settled|dismissed)\b"),
        R("operating-disruption", CatalystNewsCategory.Operational,
            @"\b(?:halts?|halted|suspends?|suspended|stops?|stopped|resumes?|resumed|restarts?|restarted|shuts?|shut|closes?|closed)\b[^.!?]{0,45}\b(?:production|plant|factory|operations|manufacturing|facility)\b|\b(?:factory fire|plant fire|production outage|supply disruption|cyberattack|data breach|product recall|safety recall)\b|\b(?:recalls?|recalled)\b[^.!?]{0,40}\b(?:vehicles|cars|products|devices)\b"),
        R("macro-policy-action", CatalystNewsCategory.MacroSector,
            @"\b(?:federal reserve|fed|ecb|bank of england|bank of japan|central bank)\b[^.!?]{0,65}\b(?:interest rates?|rate cut|rate hike|rates?|monetary policy|quantitative easing)\b|\b(?:inflation|consumer price index|cpi|payrolls|unemployment|gdp)\b[^.!?]{0,55}\b(?:rises?|rose|falls?|fell|slows?|slowed|accelerates?|accelerated|report|data|growth)\b|\b(?:government|president|congress|administration)\b[^.!?]{0,65}\b(?:imposes?|imposed|announces?|announced|enacts?|enacted|approves?|approved)\b[^.!?]{0,45}\b(?:tariffs?|sanctions?|export controls?|tax law|legislation)\b|\b(?:war|invasion|ceasefire|military conflict|missile attack|opec production cut)\b"),
        R("promotional-or-price-only", CatalystNewsCategory.PromotionalLowInformation,
            @"\b(?:stocks? to buy|stocks? to watch|why .{0,45} (?:stock|shares) (?:is|are) (?:up|down)|top stocks|next big stock|hot stocks|price action|shares (?:rise|rises|fall|falls|surge|surges|plunge|plunges)|stock (?:rises|falls|surges|plunges))\b"),
        R("law-firm-solicitation", CatalystNewsCategory.PromotionalLowInformation,
            @"\b(?:law firm|shareholder alert|investor alert|investor notice|shareholder notice|reminds investors|encourages investors|contact (?:the |a )?law firm)\b[^.!?]{0,130}\b(?:investigat|deadline|losses|rights|contact|reminds|potential claims)\w*|\b(?:investigating|investigation)\b[^.!?]{0,85}\b(?:on behalf of investors|on behalf of shareholders|potential claims|securities fraud claims)\b")
    ];
    private static readonly Regex Uncertain = Pattern(@"\b(?:not|never|no plans|rumou?rs?|speculat\w*|may (?:announce|acquire|buy|sell|report|raise|cut|face|receive|win)|might|could|considering|reportedly|expected to|expects? to|analysts? expect|will report|to report|preview|estimates? ahead|sources say)\b");
    private static readonly Regex Denial = Pattern(@"\b(?:denies|denied)\b");
    private static readonly Regex AuthorityDenial = Pattern(@"^(?:fda|ftc|sec|doj|epa|european commission|regulator|regulators)\s+(?:denies|denied)\b");
    private static readonly Regex Financial = Pattern(@"\b(?:rates?|inflation|cpi|gdp|payrolls|unemployment|earnings|revenue|profit|debt|offering|dividend|buyback)\b");
    private static readonly Regex Policy = Pattern(@"\b(?:tariffs?|sanctions?|legislation|government|congress|federal reserve|central bank|export controls?|war|invasion|ceasefire)\b");
    private static readonly Regex Positive = Pattern(@"\b(?:raises?|raised|boosts?|boosted|upgrades?|upgraded|beats?|beat)\b");
    private static readonly Regex Negative = Pattern(@"\b(?:cuts?|cut|lowers?|lowered|withdraws?|withdrew|downgrades?|downgraded|misses?|missed)\b");

    public static EconomicNewsRuleResult Classify(EconomicNewsRuleInput input, DateTimeOffset classifiedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (classifiedAtUtc == default || classifiedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Persist the actual nondefault UTC classification time.", nameof(classifiedAtUtc));
        if (input.Headline is null || input.Headline.Length > 32_000 || (input.Summary?.Length ?? 0) > 100_000 || (input.Body?.Length ?? 0) > 1_000_000)
            throw new ArgumentException("News text exceeds the bounded research input contract.", nameof(input));
        var headline = Text(input.Headline); var summary = Text(input.Summary); var body = Text(input.Body);
        // Supplied-text provenance follows the archive contract, independently of
        // whether HTML normalization leaves enough readable evidence to classify.
        var support = !String.IsNullOrWhiteSpace(input.Body) ? EconomicNewsTextSupport.BodySupported : !String.IsNullOrWhiteSpace(input.Summary)
            ? EconomicNewsTextSupport.SummarySupported : EconomicNewsTextSupport.HeadlineOnly;
        var symbols = (input.Symbols ?? []).Where(value => !String.IsNullOrWhiteSpace(value)).Select(value => value.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        if (symbols.Length > 256 || symbols.Any(value => value.Length > 64))
            throw new ArgumentException("News symbol metadata exceeds the bounded research input contract.", nameof(input));
        var topics = NewsDisplayTaxonomy.Describe(new PersistedNewsItem { Headline = input.Headline,
            Summary = input.Summary, Provider = input.Provider, Ticker = symbols.IsEmpty ? "MARKET" : String.Join(',', symbols) });
        var cues = ImmutableArray.CreateBuilder<EconomicNewsRuleCue>();
        Collect("headline", headline, cues); Collect("summary", summary, cues); Collect("body", body, cues);
        var reasons = ImmutableArray.CreateBuilder<string>();
        var economic = cues.Where(cue => cue.Category != CatalystNewsCategory.PromotionalLowInformation).ToArray();
        var headlineEconomic = economic.Where(cue => cue.Field == "headline").ToArray();
        var candidates = economic.Select(cue => cue.Category).Distinct().ToArray();
        CatalystNewsCategory category;
        if (candidates.Length > 1) { category = CatalystNewsCategory.Unknown; reasons.Add("conflicting_economic_event_categories"); }
        else if (economic.Any(cue => cue.AssertionUncertain)) { category = CatalystNewsCategory.Unknown; reasons.Add("negated_speculative_or_forward_scheduled_event_assertion"); }
        else if (candidates.Length == 1) {
            category = candidates[0];
            if (headlineEconomic.Length == 0) reasons.Add("economic_event_supported_only_by_summary_or_body");
        }
        else if (cues.Any(cue => cue.Category == CatalystNewsCategory.PromotionalLowInformation)) category = CatalystNewsCategory.PromotionalLowInformation;
        else { category = CatalystNewsCategory.Unknown; reasons.Add("no_specific_asserted_economic_event_cue"); }
        var scope = EconomicNewsScope.Unknown; var context = EconomicNewsContext.Unknown; var targets = ImmutableArray<string>.Empty;
        if (category == CatalystNewsCategory.MacroSector) {
            scope = EconomicNewsScope.Global; targets = ["*"];
            context = Policy.IsMatch(headline) ? EconomicNewsContext.Policy : Financial.IsMatch(headline) ? EconomicNewsContext.Financial : EconomicNewsContext.Global;
        }
        else if (category is not (CatalystNewsCategory.Unknown or CatalystNewsCategory.PromotionalLowInformation) && symbols.Length == 1 &&
            headlineEconomic.Length > 0 && headlineEconomic.All(cue => SubjectBound(cue, symbols[0]))) {
            scope = EconomicNewsScope.Company; context = EconomicNewsContext.Company; targets = symbols;
        }
        else if (category is not (CatalystNewsCategory.Unknown or CatalystNewsCategory.PromotionalLowInformation))
            reasons.Add("issuer_scope_not_bound_to_the_economic_assertion_subject");
        var direction = CatalystDirection.Ambiguous; var clear = false;
        // Do not derive direction from subsequent prices, provider sentiment or body background.
        // Even these narrowly stated headline changes remain model predictions, not facts.
        if (scope == EconomicNewsScope.Company && category is (CatalystNewsCategory.Guidance or CatalystNewsCategory.AnalystAction or CatalystNewsCategory.Earnings)) {
            var positive = headlineEconomic.Any(cue => Positive.IsMatch(cue.MatchedText));
            var negative = headlineEconomic.Any(cue => Negative.IsMatch(cue.MatchedText));
            if (positive != negative) { direction = positive ? CatalystDirection.Positive : CatalystDirection.Negative; clear = true; }
            else if (positive && negative) reasons.Add("conflicting_headline_direction_cues");
        }
        // Event type, including guidance, does not establish issuer-relative magnitude.
        // No materiality bucket is fabricated to complete the legacy label schema.
        CatalystMateriality? materiality = null;
        reasons.Add("materiality_not_established_by_rules");
        bool? adverse = category == CatalystNewsCategory.Operational && scope == EconomicNewsScope.Company &&
            headlineEconomic.Any(cue => Pattern(@"\b(?:outage|disruption|cyberattack|breach|recall|fire)\b").IsMatch(cue.MatchedText)) ? true :
            category == CatalystNewsCategory.Guidance && direction == CatalystDirection.Negative ? true : null;
        return new(Version, classifiedAtUtc, category, direction, clear, materiality, support, scope, context, targets,
            adverse, cues.ToImmutable(), reasons.Distinct(StringComparer.Ordinal).ToImmutableArray(), topics);
    }

    private static void Collect(string field, string text, ImmutableArray<EconomicNewsRuleCue>.Builder cues)
    {
        // Sentence windows keep unrelated historical/background paragraphs from lending
        // verbs to a bare headline noun. All matches remain visible to the reviewer.
        foreach (var sentence in Regex.Split(text, @"(?<=[.!?])\s+", RegexOptions.CultureInvariant, NewsPatterns.MatchTimeout)) {
            if (sentence.Length == 0) continue;
            foreach (var rule in Rules) {
                foreach (Match match in rule.Pattern.Matches(sentence)) cues.Add(new(rule.Id, rule.Category, field, match.Value,
                    sentence.Length <= 600 ? sentence : sentence.Substring(Math.Max(0, match.Index - 100), Math.Min(600, sentence.Length - Math.Max(0, match.Index - 100))),
                    Uncertain.IsMatch(sentence) || Denial.IsMatch(sentence) &&
                        !(rule.Category == CatalystNewsCategory.Regulatory && AuthorityDenial.IsMatch(match.Value)), sentence[..match.Index]));
            }
        }
    }

    private static bool SubjectBound(EconomicNewsRuleCue cue, string symbol) =>
        Regex.IsMatch(cue.AssertionPrefix, @"(?:^|[\s(,$])" + Regex.Escape(symbol) + @"\s*$",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, NewsPatterns.MatchTimeout) &&
        // In an active analyst sentence the ticker before "upgrades" can identify
        // the bank making the recommendation, not the security being rated.
        (cue.Category != CatalystNewsCategory.AnalystAction ||
            Pattern(@"^(?:upgraded|downgraded)\s+(?:to|from)\b").IsMatch(cue.MatchedText));

    private static string Text(string? raw) => String.IsNullOrWhiteSpace(raw) ? "" :
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(raw, "<[^>]*>", " ", RegexOptions.CultureInvariant, NewsPatterns.MatchTimeout)),
            @"\s+", " ", RegexOptions.CultureInvariant, NewsPatterns.MatchTimeout).Trim();
}
