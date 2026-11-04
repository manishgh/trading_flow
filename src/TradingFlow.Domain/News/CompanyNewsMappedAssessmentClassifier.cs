using System.Collections.Immutable;
using System.Text.RegularExpressions;
using TradingFlow.Domain.Research;

namespace TradingFlow.Domain.News;

/// <summary>Versioned official-name subject binding and a bounded analyst-first assertion grammar.</summary>
internal static class CompanyNewsMappedAssessmentClassifier
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking;
    private const int MaximumAssertionCharacters = 32_000;
    private const string Ratings = @"(?:buy|sell|hold|neutral|outperform|underperform|overweight|underweight|equal(?:-|\s+)weight|market(?:-|\s+)perform)";
    private const string NumericSuffix = @"(?:\s+(?:to|by)\s+\$?\d+(?:\.\d+)?(?:%|\s+per\s+share)?)?";

    /// <summary>Canonicalizes only complete supported assertions; all other original context is retained and withholds entry.</summary>
    public static CompanyNewsExtraction Extract(CompanyNewsRevisionInput revision, string symbol, DateTimeOffset atUtc,
        CompanyNewsIssuerMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ValidateMapping(mapping);
        if (mapping.Symbol != symbol)
            throw new ArgumentException("Issuer mapping must bind the exact requested provider symbol.", nameof(mapping));
        if (mapping.ObservedAtUtc > atUtc)
            throw new ArgumentException("Mapping observation cannot follow extraction completion.", nameof(mapping));
        var aliases = GetIssuerAliases(mapping);
        var spans = ImmutableArray.CreateBuilder<CompanyNewsEvidenceSpan>();
        var bindingReasons = ImmutableArray.CreateBuilder<string>();
        var headline = BindField("headline", revision.Headline, symbol, aliases, atUtc, spans, bindingReasons)!;
        var summary = BindField("summary", revision.Summary, symbol, aliases, atUtc, spans, bindingReasons);
        var body = BindField("body", revision.Body, symbol, aliases, atUtc, spans, bindingReasons);
        var canonical = revision with { Headline = headline, Summary = summary, Body = body };
        var assessed = CompanyNewsAssessmentClassifier.Extract(canonical, symbol, atUtc);
        var originals = CompanyNewsAssessmentClassifier.OriginalFields(revision);
        var modelText = String.Join("\n\n", originals.Select(span => span.Text));
        return assessed with {
            Revision = revision,
            RevisionContentSha256 = CompanyNewsAssessmentClassifier.ComputeRevisionContentSha256(revision),
            ExtractionVersion = CompanyNewsAssessmentClassifier.MappedExtractionVersion,
            MappingVersion = CompanyNewsAssessmentClassifier.MappedMappingVersion,
            EvidenceSpans = originals,
            ModelInputText = modelText,
            ModelInputSha256 = CompanyNewsAssessmentClassifier.ComputeTextSha256(modelText),
            CanAssess = assessed.CanAssess && bindingReasons.Count == 0,
            Reasons = assessed.Reasons.Concat(bindingReasons).Distinct(StringComparer.Ordinal).ToImmutableArray(),
            IssuerMapping = mapping,
            RuleInputBinding = new(headline, summary, body, spans.ToImmutable())
        };
    }

    /// <summary>Derives only an anchored legal suffix removal; brand prefixes and arbitrary aliases are never inferred.</summary>
    public static ImmutableArray<string> GetIssuerAliases(CompanyNewsIssuerMapping mapping)
    {
        ValidateMapping(mapping);
        var official = mapping.OfficialName;
        var trimmed = Regex.Replace(official,
            @"(?:,\s*|\s+)(?:Inc\.?|Incorporated|Corp\.?|Corporation|Ltd\.?|Limited|PLC)\s*$", "", Options,
            NewsPatterns.MatchTimeout).TrimEnd();
        return new[] { official, trimmed }.Where(value => !String.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(value => value.Length).ToImmutableArray();
    }

    /// <summary>Checks immutable mapping metadata; source-row truth and cross-issuer collisions are checked by Data.</summary>
    private static void ValidateMapping(CompanyNewsIssuerMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapping.Symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapping.OfficialName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapping.SourceProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapping.SourceIdentity);
        if (mapping.Symbol.Length > 64 || mapping.Symbol != mapping.Symbol.Trim() || mapping.Symbol != mapping.Symbol.ToUpperInvariant())
            throw new ArgumentException("Mapping symbol must retain a canonical uppercase provider identity.", nameof(mapping));
        if (mapping.OfficialName.Length > 256 || mapping.OfficialName != mapping.OfficialName.Trim() ||
            mapping.OfficialName.Any(Char.IsControl))
            throw new ArgumentException("Preserve a bounded canonical official name from the verified source row.", nameof(mapping));
        if (mapping.SourceContentSha256 is null || !Regex.IsMatch(mapping.SourceContentSha256, "^[a-f0-9]{64}$", Options,
            NewsPatterns.MatchTimeout) || mapping.SourceContentSha256 != mapping.SourceContentSha256.ToLowerInvariant())
            throw new ArgumentException("Mapping source requires its canonical payload SHA-256.", nameof(mapping));
        if (mapping.ObservedAtUtc == default || mapping.ObservedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Mapping observation must retain the actual non-default UTC capture time.", nameof(mapping));
    }

    /// <summary>Withholds reported/refuted prose before replacing complete assertions, retaining every original field and span.</summary>
    private static string? BindField(string field, string? original, string symbol, ImmutableArray<string> aliases,
        DateTimeOffset atUtc, ImmutableArray<CompanyNewsEvidenceSpan>.Builder spans, ImmutableArray<string>.Builder reasons)
    {
        if (String.IsNullOrWhiteSpace(original) || original.Length > MaximumAssertionCharacters) return original;
        if (HasReportedOrRefutedContext(original))
        {
            reasons.Add($"{field}_reported_or_refuted_assertion_requires_review");
            return original;
        }
        var subjects = String.Join('|', new[] { symbol }.Concat(aliases).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length).Select(Regex.Escape));
        var canonical = BindSubjectFirst(original, symbol, subjects) ?? BindAnalystFirst(original, symbol, subjects, atUtc);
        if (canonical is null) return original;
        spans.Add(new(field, 0, original.Length, original, original));
        return canonical;
    }

    /// <summary>Accepts exact official-name or ticker subjects only when all following tokens form one supported assertion.</summary>
    private static string? BindSubjectFirst(string original, string symbol, string subjects)
    {
        var assertions = new[] { CatalystNewsCategory.Earnings, CatalystNewsCategory.Guidance, CatalystNewsCategory.AnalystAction }
            .Select(CompanyNewsAssessmentClassifier.SupportedAssertionPattern);
        var pattern = @"^\s*\$?(?:" + subjects + @")\s+(?<assertion>" + String.Join('|', assertions) +
            @")(?<numeric>" + NumericSuffix + @")\s*\.?\s*$";
        var match = Regex.Match(original, pattern, Options, NewsPatterns.MatchTimeout);
        return match.Success ? symbol + " " + match.Groups["assertion"].Value + match.Groups["numeric"].Value : null;
    }

    /// <summary>Separates an analyst actor from its exact rated issuer and allows only a nondirectional announced price target.</summary>
    private static string? BindAnalystFirst(string original, string symbol, string subjects, DateTimeOffset atUtc)
    {
        var actor = @"(?<actor>[A-Za-z][A-Za-z0-9&'-]*(?:\s+[A-Za-z][A-Za-z0-9&'-]*){0,5})";
        var pattern = @"^\s*" + actor + @"\s+(?<action>Upgrades|Downgrades)\s+(?:" + subjects +
            @")\s+to\s+(?<rating>" + Ratings + @")" +
            @"(?:,\s*(?:Announces|Sets)\s+\$?\d+(?:\.\d+)?\s+Price\s+Target)?\s*\.?\s*$";
        var match = Regex.Match(original, pattern, Options, NewsPatterns.MatchTimeout);
        if (!match.Success || HasUnresolvedActorContext(match.Groups["actor"].Value, atUtc)) return null;
        var action = match.Groups["action"].Value.Equals("Upgrades", StringComparison.OrdinalIgnoreCase) ? "upgraded" : "downgraded";
        return symbol + " " + action + " to " + match.Groups["rating"].Value;
    }

    /// <summary>Uses existing event cues as actor vetoes, plus finite unsupported prose markers; no new event family is admitted.</summary>
    private static bool HasUnresolvedActorContext(string actor, DateTimeOffset atUtc)
    {
        var actorRules = EconomicNewsRuleClassifier.Classify(new(actor), atUtc);
        if (actorRules.MatchedCues.Any(cue => cue.Category != CatalystNewsCategory.PromotionalLowInformation)) return true;
        return Regex.IsMatch(actor,
        @"\b(?:and|but|as|after|because|while|for|from|to|despite|although|unless|not|no|never|denies|denied|" +
        @"rumou?rs?|may|might|could|reportedly|expected|expects?|speculat\w*|rival|competitor|" +
        @"raises?|raised|cuts?|cut|lowers?|lowered|boosts?|boosted|upgrades?|upgraded|downgrades?|downgraded|" +
        @"beats?|beat|misses?|missed|earnings|revenue|profit|sales|shares|stock|guidance|outlook|" +
        @"falls?|fell|slumps?|slumped|surges?|surged|drops?|dropped|loses?|lost|unchanged)\b", Options, NewsPatterns.MatchTimeout);
    }

    /// <summary>Conservatively withholds a finite set of reporting and invalidation language; this is not general language understanding.</summary>
    private static bool HasReportedOrRefutedContext(string text) => Regex.IsMatch(text,
        @"\b(?:reports?|reported|reportedly|reporting|claims?|claimed|claiming|says?|said|saying|" +
        @"alleges?|alleged|allegedly|asserts?|asserted|purports?|purported|purportedly|" +
        @"falsely|false|incorrectly|incorrect|wrongly|erroneously|erroneous|inaccurately|inaccurate|" +
        @"misleadingly|misleading|untrue|denies|denied|denying|refutes?|refuted|refuting|" +
        @"disputes?|disputed|rejects?|rejected|retracts?|retracted|recants?|recanted|debunks?|debunked|" +
        @"fabricated|fabricates?|fake|baseless|unfounded|unsubstantiated|unconfirmed|unverified|mistakenly|mistaken)\b",
        Options, NewsPatterns.MatchTimeout);
}
