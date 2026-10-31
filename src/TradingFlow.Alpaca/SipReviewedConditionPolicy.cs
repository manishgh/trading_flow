using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace TradingFlow.Alpaca;

/// <summary>Reuse the independently reviewed 19 October ordinary-print diagnostic only against identical captured provider vocabulary.</summary>
public static class SipReviewedConditionPolicy
{
    public const string ReviewedContractFileSha256 = "ac9d7047f274ca2330b9b4e6aee1b540196b3dab101c05e7f6337a833f5af029";

    /// <summary>Preserve every reviewed eligibility/exclusion; changed dictionaries or names remain unknown pending review.</summary>
    public static SipPortfolioSourcePolicy Bind(IReadOnlyList<SipMetadataCapture> captures)
    {
        using var input = typeof(SipReviewedConditionPolicy).Assembly.GetManifestResourceStream(
            "TradingFlow.Alpaca.Resources.sip-ordinary-print-research-condition-v1.json")
            ?? throw new InvalidDataException("Reviewed SIP condition contract resource is missing.");
        using var buffer = new MemoryStream(); input.CopyTo(buffer);
        var reviewedBytes = buffer.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(reviewedBytes)) != ReviewedContractFileSha256)
            throw new InvalidDataException("Reviewed SIP condition contract bytes changed.");
        var reviewed = JsonSerializer.Deserialize<SipExecutionConditionContract>(reviewedBytes)
            ?? throw new InvalidDataException("Reviewed SIP condition contract is empty.");
        var sources = ImmutableArray.CreateBuilder<SipExecutionConditionMetadata>();
        var rules = ImmutableArray.CreateBuilder<SipExecutionConditionRule>();
        foreach (var capture in captures)
        {
            var kind = capture.Kind == "quote" ? SipArchiveEventKind.Quote : capture.Kind == "trade" ? SipArchiveEventKind.Trade
                : throw new InvalidDataException("Condition capture has an unknown event kind.");
            var bytes = File.ReadAllBytes(capture.PayloadPath);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != capture.PayloadSha256 || capture.Tape is not ("A" or "B" or "C"))
                throw new InvalidDataException("Condition metadata bytes or tape differ from the captured identity.");
            sources.Add(new(kind, capture.Tape, capture.PayloadSha256));
            var sameVocabulary = reviewed.MetadataSources.Any(x => x.Kind == kind && x.Tape == capture.Tape && x.Sha256 == capture.PayloadSha256);
            using var document = JsonDocument.Parse(bytes);
            foreach (var code in document.RootElement.EnumerateObject())
            {
                var name = code.Value.GetString();
                if (String.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Provider condition metadata omitted a code's meaning.");
                var prior = sameVocabulary ? reviewed.Rules.SingleOrDefault(x => x.Kind == kind && x.Tape == capture.Tape &&
                    x.Code == code.Name && x.Name == name) : null;
                rules.Add(prior ?? new(kind, capture.Tape, code.Name, name, SipExecutionConditionDisposition.Unknown));
            }
        }
        if (sources.Count != 6 || sources.Select(x => (x.Kind, x.Tape)).Distinct().Count() != 6)
            throw new InvalidDataException("All six distinct official condition dictionaries are required.");
        return new("sip-retrospective-zero-source-delay-v1", SipPortfolioKnownAtBasis.RetrospectiveSourceTimePlusDeclaredDelay, 0,
            new(reviewed.Version + "-captured-vocabulary-binding-v1", sources.ToImmutable(), rules.ToImmutable()));
    }
}
