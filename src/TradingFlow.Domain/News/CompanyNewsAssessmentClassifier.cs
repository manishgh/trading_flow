using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TradingFlow.Domain.Research;

namespace TradingFlow.Domain.News;

/// <summary>
/// Conservative literal-subject adapter around the existing economic rules. It keeps all
/// supplied context and abstains when that context cannot be attributed to the issuer.
/// Agreement with an unevaluated model is a diagnostic hypothesis, not ground truth.
/// </summary>
public static class CompanyNewsAssessmentClassifier
{
    public const string ExtractionVersion = "company-news-extraction/v1";
    public const string MappingVersion = "literal-ticker-subject/v1";
    public const string MappedExtractionVersion = "company-news-extraction/v2";
    public const string MappedMappingVersion = "frozen-provider-official-name/v2";
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    // Supporting fields are inspected as headline assertions only within the reused classifier's headline bound.
    private const int MaximumSupportingAssertionCharacters = 32_000;

    /// <summary>Retains exact original fields and exposes only supported, unambiguous issuer assertions to inference.</summary>
    public static CompanyNewsExtraction Extract(CompanyNewsRevisionInput revision, string issuerSymbol,
        DateTimeOffset extractedAtUtc)
    {
        ValidateInput(revision, issuerSymbol, extractedAtUtc);
        var rules = EconomicNewsRuleClassifier.Classify(new(revision.Headline, revision.Summary,
            revision.Body, [issuerSymbol], revision.Provider), extractedAtUtc);
        var spans = OriginalFields(revision);
        var modelText = String.Join("\n\n", spans.Select(span => span.Text));
        var reasons = ImmutableArray.CreateBuilder<string>();
        if (!IsSupportedLiteralSymbol(issuerSymbol))
            reasons.Add("unsupported_symbol_format");
        if (!ContainsTicker(revision.Headline, issuerSymbol))
            reasons.Add("literal_issuer_subject_missing_from_headline");
        if (rules.Scope != EconomicNewsScope.Company)
            reasons.Add("issuer_not_bound_to_headline_assertion");
        if (rules.Category is not (CatalystNewsCategory.Earnings or CatalystNewsCategory.Guidance or CatalystNewsCategory.AnalystAction))
            reasons.Add("unsupported_company_direction_category");
        if (!rules.DirectionIsClear && !IsExplicitReaffirmation(rules))
            reasons.Add("no_supported_explicit_direction_or_neutral_assertion");
        if (!IsSingleLiteralAssertion(revision.Headline, issuerSymbol, rules))
            reasons.Add("headline_contains_unsupported_clause_or_subject_relation");
        ValidateContext(spans, issuerSymbol, extractedAtUtc, rules, reasons);
        return new(revision, issuerSymbol, ComputeRevisionContentSha256(revision), ExtractionVersion,
            MappingVersion, rules, spans, modelText, ComputeTextSha256(modelText), reasons.Count == 0,
            reasons.Distinct(StringComparer.Ordinal).ToImmutableArray());
    }

    /// <summary>Uses a frozen official-name binding while retaining the original text for model inference and review.</summary>
    public static CompanyNewsExtraction Extract(CompanyNewsRevisionInput revision, string issuerSymbol,
        DateTimeOffset extractedAtUtc, CompanyNewsIssuerMapping mapping) =>
        CompanyNewsMappedAssessmentClassifier.Extract(revision, issuerSymbol, extractedAtUtc, mapping);

    /// <summary>Returns only the official name and deterministic trailing legal-suffix trim for mapping-collision checks.</summary>
    public static ImmutableArray<string> GetIssuerAliases(CompanyNewsIssuerMapping mapping) =>
        CompanyNewsMappedAssessmentClassifier.GetIssuerAliases(mapping);

    /// <summary>Requires the exact pinned model and input hash; absent, failed or disagreeing inference remains unknown.</summary>
    public static CompanyNewsAssessment Complete(CompanyNewsExtraction extraction, CompanyNewsModelEvidence? modelEvidence,
        CompanyNewsPinnedModel pinnedModel, DateTimeOffset assessedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ValidateModel(pinnedModel);
        RequireUtc(assessedAtUtc, nameof(assessedAtUtc));
        // Recompute the pure extraction so callers cannot alter flags, text or rule results to bypass subject checks.
        var verified = RecreateExtraction(extraction);
        if (!SameExtraction(extraction, verified))
            throw new ArgumentException("Extraction does not match its exact revision, issuer and rule inputs.", nameof(extraction));
        if (assessedAtUtc < extraction.RuleResult.ClassifiedAtUtc)
            throw new ArgumentException("Assessment cannot precede extraction.", nameof(assessedAtUtc));
        var reasons = extraction.Reasons.ToBuilder();
        var state = CompanyNewsAssessmentState.Unknown;
        if (IsGlobalContext(extraction))
        {
            state = CompanyNewsAssessmentState.Irrelevant;
            reasons.Add("global_context_is_not_company_direction_evidence");
        }
        else if (extraction.CanAssess && ValidateEvidence(modelEvidence, pinnedModel, extraction, assessedAtUtc, reasons))
        {
            state = Agree(extraction.RuleResult, modelEvidence!.TopLabel);
            if (state == CompanyNewsAssessmentState.Unknown)
                reasons.Add("model_polarity_disagrees_with_supported_assertion");
        }
        return new(extraction, state, modelEvidence, reasons.Distinct(StringComparer.Ordinal).ToImmutableArray(), assessedAtUtc);
    }

    /// <summary>Replays the exact frozen extractor; a mapping cannot silently upgrade a prior v1 publication.</summary>
    private static CompanyNewsExtraction RecreateExtraction(CompanyNewsExtraction extraction) => extraction.ExtractionVersion switch {
        ExtractionVersion when extraction.IssuerMapping is null && extraction.RuleInputBinding is null =>
            Extract(extraction.Revision, extraction.IssuerSymbol, extraction.RuleResult.ClassifiedAtUtc),
        MappedExtractionVersion when extraction.IssuerMapping is not null =>
            Extract(extraction.Revision, extraction.IssuerSymbol, extraction.RuleResult.ClassifiedAtUtc, extraction.IssuerMapping),
        _ => throw new ArgumentException("Unsupported or inconsistent frozen company-news extraction version.", nameof(extraction))
    };

    /// <summary>Hashes a collision-safe envelope of all original fields and provider/article/revision identifiers.</summary>
    public static string ComputeRevisionContentSha256(CompanyNewsRevisionInput revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return ComputeTextSha256(JsonSerializer.Serialize(new[] { "company-news-revision/v1", revision.Provider,
            revision.ProviderArticleId, revision.RevisionId, revision.Headline, revision.Summary, revision.Body }));
    }

    /// <summary>Uses UTF-8 SHA-256 without changing whitespace, HTML, punctuation or case.</summary>
    public static string ComputeTextSha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Rejects malformed identities and bounds text consistently with the reused classifier.</summary>
    private static void ValidateInput(CompanyNewsRevisionInput revision, string symbol, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.ProviderArticleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.RevisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (symbol.Length > 64 || symbol != symbol.Trim() || symbol != symbol.ToUpperInvariant())
            throw new ArgumentException("Preserve a nonempty canonical uppercase provider symbol of at most 64 characters.", nameof(symbol));
        RequireUtc(atUtc, nameof(atUtc));
    }

    /// <summary>Recognizes the supported literal ticker syntax without rewriting unsupported provider symbol identities.</summary>
    private static bool IsSupportedLiteralSymbol(string symbol) =>
        Regex.IsMatch(symbol, @"^[A-Z][A-Z0-9.-]{0,15}$", Options, TimeSpan.FromMilliseconds(100));

    /// <summary>Keeps every supplied field intact, making original offsets and contradictory context reviewable.</summary>
    internal static ImmutableArray<CompanyNewsEvidenceSpan> OriginalFields(CompanyNewsRevisionInput revision)
    {
        var spans = ImmutableArray.CreateBuilder<CompanyNewsEvidenceSpan>();
        foreach (var (field, text) in new[] { ("headline", revision.Headline), ("summary", revision.Summary), ("body", revision.Body) })
        {
            if (!String.IsNullOrWhiteSpace(text)) spans.Add(new(field, 0, text.Length, text, text));
        }
        return spans.ToImmutable();
    }

    /// <summary>Detects unsupported context and withholds overlong supporting assertions without truncating archived fields.</summary>
    private static void ValidateContext(ImmutableArray<CompanyNewsEvidenceSpan> spans, string symbol, DateTimeOffset atUtc,
        EconomicNewsRuleResult original, ImmutableArray<string>.Builder reasons)
    {
        foreach (var span in spans)
        {
            if (span.Field != "headline" && span.Length > MaximumSupportingAssertionCharacters)
            {
                reasons.Add($"supporting_{span.Field}_exceeds_32000_character_assertion_limit");
                continue;
            }
            if (Regex.IsMatch(span.Text, @"<[^>]*>|&(?:#\w+|[A-Za-z]+);", Options, TimeSpan.FromMilliseconds(100)))
                reasons.Add("markup_requires_reviewed_original_span_mapping");
            if (Regex.IsMatch(span.Text, @"\b(?:rival|competitor|versus|vs\.?|compared|whereas|while|unlike)\b",
                Options | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                reasons.Add("multi_company_or_comparative_context_requires_review");
            if (Regex.IsMatch(span.Text, @"\b(?:not|never|denies|denied|rumou?rs?|might|could|reportedly|speculat\w*)\b",
                Options | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                reasons.Add("negated_or_speculative_surrounding_context");
            if (span.Field == "headline") continue;
            if (!ContainsTicker(span.Text, symbol)) reasons.Add("supporting_field_issuer_not_explicit");
            // Supporting text is inspected as its own assertion only to veto conflict. It never supplies headline direction.
            var support = EconomicNewsRuleClassifier.Classify(new(span.Text, Symbols: [symbol]), atUtc);
            var economic = support.MatchedCues.Where(cue => cue.Category != CatalystNewsCategory.PromotionalLowInformation).ToArray();
            if (!IsSingleLiteralAssertion(span.Text, symbol, support))
                reasons.Add("supporting_field_contains_unsupported_clause_or_subject_relation");
            if (economic.Length == 0) continue;
            if (support.Scope != EconomicNewsScope.Company || economic.Any(cue => cue.AssertionUncertain))
                reasons.Add("supporting_assertion_subject_or_negation_uncertain");
            if (support.Category != original.Category || support.DirectionIsClear && support.Direction != original.Direction)
                reasons.Add("conflicting_supporting_company_assertions");
        }
    }

    /// <summary>
    /// Accepts one literal subject and a separately bounded assertion grammar, with optional numeric change.
    /// The display classifier's wildcard matches never define this grammar: they can span another subject.
    /// Extra clauses and beneficiary relations remain unknown; decimal financial values remain supported.
    /// </summary>
    private static bool IsSingleLiteralAssertion(string text, string symbol, EconomicNewsRuleResult rules)
    {
        var cues = rules.MatchedCues.Where(cue => cue.Field == "headline" &&
            cue.Category != CatalystNewsCategory.PromotionalLowInformation).ToArray();
        if (cues.Length != 1) return false;
        var assertion = SupportedAssertionPattern(cues[0].Category);
        if (assertion is null) return false;
        var pattern = @"^\s*\$?" + Regex.Escape(symbol) + @"\s+" + assertion +
            @"(?:\s+(?:to|by)\s+\$?\d+(?:\.\d+)?(?:%|\s+per\s+share)?)?\s*\.?\s*$";
        return Regex.IsMatch(text, pattern, Options | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// Defines the narrow subject-safe subset of the reused event categories without free-text wildcards.
    /// Every token after the issuer must belong to the asserted financial event; arbitrary text and names
    /// cannot lend another company's beat, miss or rating direction to the supplied issuer.
    /// </summary>
    internal static string? SupportedAssertionPattern(CatalystNewsCategory category) => category switch {
        CatalystNewsCategory.Earnings =>
            @"(?:earnings|revenue|eps|quarterly\s+profit|q[1-4]\s+profit)\s+(?:beats?|beat|misses?|missed)\s+(?:estimates?|expectations?|consensus)",
        CatalystNewsCategory.Guidance =>
            @"(?:raises?|raised|cuts?|cut|lowers?|lowered|boosts?|boosted|reaffirms?|reaffirmed|withdraws?|withdrew)\s+" +
            @"(?:(?:its|the|full(?:-|\s+)year|annual|quarterly|q[1-4]|20\d{2})\s+){0,5}" +
            @"(?:guidance|outlook|full(?:-|\s+)year\s+forecast|revenue\s+forecast|profit\s+forecast|earnings\s+forecast)",
        CatalystNewsCategory.AnalystAction =>
            @"(?:upgraded|downgraded)\s+(?:to|from)\s+" +
            @"(?:buy|sell|hold|neutral|outperform|underperform|overweight|underweight|equal(?:-|\s+)weight|market(?:-|\s+)perform)",
        _ => null
    };

    /// <summary>Matches a literal uppercase ticker token, never a provider tag or an inferred company-name alias.</summary>
    private static bool ContainsTicker(string text, string symbol) => Regex.IsMatch(text,
        @"(?:^|[^A-Za-z0-9.-])\$?" + Regex.Escape(symbol) + @"(?:$|[^A-Za-z0-9.-])", Options, TimeSpan.FromMilliseconds(100));

    /// <summary>Only an explicit unchanged guidance assertion can provide neutral economic direction in this version.</summary>
    private static bool IsExplicitReaffirmation(EconomicNewsRuleResult rules) => rules.Scope == EconomicNewsScope.Company &&
        rules.Category == CatalystNewsCategory.Guidance && !rules.DirectionIsClear &&
        rules.MatchedCues.Where(cue => cue.Field == "headline").All(cue => !cue.AssertionUncertain &&
            Regex.IsMatch(cue.MatchedText, @"^reaffirm(?:s|ed)?\b", Options | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))) &&
        rules.MatchedCues.Any(cue => cue.Field == "headline");

    /// <summary>Confirms explicit macro context is outside issuer direction; ambiguous company text never becomes irrelevant.</summary>
    private static bool IsGlobalContext(CompanyNewsExtraction extraction) => IsSupportedLiteralSymbol(extraction.IssuerSymbol) &&
        extraction.EvidenceSpans.All(span => span.Field == "headline" || span.Length <= MaximumSupportingAssertionCharacters) &&
        extraction.RuleResult.Scope == EconomicNewsScope.Global &&
        !extraction.EvidenceSpans.Any(span => ContainsTicker(span.Text, extraction.IssuerSymbol));

    /// <summary>Checks every frozen model identity field, the exact text hash and real inference completion clock.</summary>
    private static bool ValidateEvidence(CompanyNewsModelEvidence? evidence, CompanyNewsPinnedModel pinned,
        CompanyNewsExtraction extraction, DateTimeOffset atUtc, ImmutableArray<string>.Builder reasons)
    {
        if (evidence is null) { reasons.Add("required_model_assessment_missing"); return false; }
        if (!evidence.Succeeded) { reasons.Add("required_model_assessment_failed"); return false; }
        if (evidence.Identity != pinned) { reasons.Add("pinned_model_identity_mismatch_or_fallback"); return false; }
        if (evidence.InputContentSha256 != extraction.ModelInputSha256) { reasons.Add("model_input_hash_mismatch"); return false; }
        if (evidence.Confidence is < 0m or > 1m || !Enum.IsDefined(evidence.TopLabel) || evidence.TopLabel == CompanyNewsPolarity.Unknown)
        { reasons.Add("invalid_model_polarity_or_confidence"); return false; }
        if (evidence.CompletedAtUtc == default || evidence.CompletedAtUtc.Offset != TimeSpan.Zero ||
            evidence.CompletedAtUtc < extraction.RuleResult.ClassifiedAtUtc || evidence.CompletedAtUtc > atUtc ||
            pinned.TrainingDataCutoffUtc > evidence.CompletedAtUtc)
        { reasons.Add("model_completion_clock_invalid"); return false; }
        return true;
    }

    /// <summary>Preserves unknown training provenance explicitly while requiring a usable immutable model identity.</summary>
    private static void ValidateModel(CompanyNewsPinnedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.TrainingDataProvenance);
        if (model.ArtifactSha256 is null || !Regex.IsMatch(model.ArtifactSha256, "^[a-f0-9]{64}$", Options, TimeSpan.FromMilliseconds(100)))
            throw new ArgumentException("Pin a canonical SHA-256 model artifact.", nameof(model));
        if (model.TrainingDataCutoffUtc.HasValue) RequireUtc(model.TrainingDataCutoffUtc.Value, nameof(model));
    }

    /// <summary>Maps only agreement between the explicit economic assertion and the model's top polarity.</summary>
    private static CompanyNewsAssessmentState Agree(EconomicNewsRuleResult rules, CompanyNewsPolarity polarity) =>
        (rules.Direction, polarity) switch {
            (CatalystDirection.Positive, CompanyNewsPolarity.Positive) when rules.DirectionIsClear => CompanyNewsAssessmentState.Positive,
            (CatalystDirection.Negative, CompanyNewsPolarity.Negative) when rules.DirectionIsClear => CompanyNewsAssessmentState.Negative,
            (_, CompanyNewsPolarity.Neutral) when IsExplicitReaffirmation(rules) => CompanyNewsAssessmentState.Neutral,
            _ => CompanyNewsAssessmentState.Unknown
        };

    /// <summary>Compares every replayed cue and original span using the original default serializer options.</summary>
    private static bool SameExtraction(CompanyNewsExtraction left, CompanyNewsExtraction right) =>
        CompanyNewsAssessmentContentComparer.SameContent(left, right);

    /// <summary>Rejects inferred/default/local clocks; callers supply their observed UTC measurement time.</summary>
    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
            throw new ArgumentException("Supply a non-default actual UTC measurement time.", parameterName);
    }
}
