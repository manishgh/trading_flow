using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TradingFlow.Application.Candidates;

/// <summary>
/// Turns one recorded check into a plain sentence built only from that check's own recorded code and numbers, so clients
/// never carry their own copy of thresholds or wording. It explains a recorded result and never decides pass or fail.
/// A code without special wording is shown as its readable name.
/// </summary>
public static partial class GateCheckExplanation
{
    // Codes whose readable name alone would not say what happened.
    private static readonly IReadOnlyDictionary<string, string> Sentences = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["candidate_not_persisted"] = "The stock is not in the recorded discovery list",
        ["candidate_persisted"] = "The stock is in the recorded discovery list",
        ["candidate_expired"] = "The stock's discovery record has expired",
        ["candidate_fresh"] = "The stock's discovery record is current",
        ["market_state_not_warm"] = "Not enough completed bars are loaded yet",
        ["market_state_warm"] = "Enough completed bars are loaded",
        ["universe_not_eligible"] = "The stock is not in the strategy's eligible universe",
        ["market_regime_on"] = "The market rule allows new entries",
        ["market_regime_off"] = "The market rule does not allow new entries",
        ["outside_session_window"] = "Outside the strategy's trading hours",
        ["inside_session_window"] = "Inside the strategy's trading hours",
        ["setup_signal_not_available"] = "No setup signal could be calculated from the completed bars",
        ["strategy_admission_satisfied"] = "All of the strategy's entry rules pass",
        ["execution_trigger_satisfied"] = "The entry trigger has been met",
        ["not_on_decision_schedule"] = "Today is not one of the strategy's entry days",
        ["vix_lookup_failed"] = "The VIX source could not be read, so new entries are refused",
        ["listing_exchange_unknown"] = "The stock's listing exchange is unknown, so new entries are refused",
        ["listing_exchange_lookup_failed"] = "The stock's listing exchange could not be looked up, so new entries are refused"
    };

    private static readonly IReadOnlyDictionary<string, string?> NoInputs = new Dictionary<string, string?>();

    // How many of an order gate's recorded inputs its sentence names.
    private const int MaxGateInputs = 4;

    // Measurements whose name cannot be read off the code, with the unit their numbers carry.
    private static readonly IReadOnlyDictionary<string, (string Subject, string Unit)> Measurements =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["average_range_pct"] = ("20-day ADR", "%"),
            ["average_dollar_volume"] = ("20-day average dollar volume", "$"),
            ["opening_relative_volume"] = ("Opening relative volume", "×"),
            ["volume_sma_rise"] = ("Volume average rise", "%"),
            ["gap_up"] = ("Gap up", "%"),
            ["prior_entry_gain"] = ("Earlier gain", "%"),
            ["catalyst_price_move"] = ("Price move since the news", "%"),
            ["catalyst_entry"] = ("Price move since the news", "%"),
            ["price_too_far_below_52_week_high"] = ("Price versus the 52-week high", "%"),
            ["price_vs_52_week_low"] = ("Price above the 52-week low", "%"),
            ["close_location"] = ("Close position in the bar's range (0 low, 1 high)", string.Empty),
            ["short_close_location"] = ("Close position in the bar's range (0 low, 1 high)", string.Empty)
        };

    private static readonly string[] MinimumSuffixes = ["_below_minimum", "_below_range"];
    private static readonly string[] MaximumSuffixes = ["_above_maximum", "_above_range", "_too_extended"];
    private static readonly string[] OtherSuffixes = ["_outside_range"];

    /// <summary>
    /// Explains one check. <paramref name="code"/> may carry the engine's own numbers, as in
    /// "adx_below_minimum (Actual: 14.20, Required: 20.00)"; <paramref name="inputs"/> are the check's recorded inputs.
    /// </summary>
    public static string Describe(string code, IReadOnlyDictionary<string, string?> inputs)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(inputs);
        var (name, values) = Parse(code);
        return name switch
        {
            "market_regime_off" => DescribeRegime(inputs) ?? Sentences[name],
            "market_state_not_warm" => DescribeWarmup(inputs) ?? Sentences[name],
            _ => DescribeComparison(name, values) ?? DescribeNamed(name, values)
        };
    }

    /// <summary>
    /// Explains one recorded order gate: its reject code's readable name (or "Passed") with the first few plain values it
    /// recorded, so two gates that share a reject code still read differently.
    /// </summary>
    public static string DescribeOrderGate(string? rejectCode, IReadOnlyDictionary<string, string?> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var shown = inputs.Where(item => item.Value is { Length: > 0 and <= 40 } value && value[0] is not ('{' or '['))
            .Take(MaxGateInputs).Select(item => $"{ReadableKey(item.Key)} {item.Value}").ToArray();
        return (rejectCode is null ? "Passed" : Readable(rejectCode)) + (shown.Length == 0 ? string.Empty : ": " + string.Join(", ", shown));
    }

    // Splits "code (Key: value, Key: value)" into its code and its named values; a bare code has none, and a code that wraps
    // another code ("setup_x_not_triggered (gap_up_below_minimum (...))") carries that code's own sentence as its detail.
    private static (string Name, IReadOnlyList<(string Key, string Value)> Values) Parse(string code)
    {
        var trimmed = code.Trim();
        var open = trimmed.IndexOf(" (", StringComparison.Ordinal);
        if (open < 0 || !trimmed.EndsWith(')')) return (trimmed, []);
        var body = trimmed[(open + 2)..^1];
        if (WrappedCode().IsMatch(body)) return (trimmed[..open], [("detail", Describe(body, NoInputs))]);
        var values = NamedValue().Matches(body).Select(match => (match.Groups["key"].Value, match.Groups["value"].Value.Trim())).ToArray();
        return (trimmed[..open], values.Length == 0 ? [("detail", body)] : values);
    }

    // "20-day ADR 1.82%: needs at least 2.50%" for a code that records an actual and a required value; null otherwise.
    private static string? DescribeComparison(string name, IReadOnlyList<(string Key, string Value)> values)
    {
        var actual = Find(values, "Actual");
        var required = values.FirstOrDefault(item => item.Key.StartsWith("Required", StringComparison.Ordinal));
        if (actual is null || required.Value is null) return null;
        var (subject, unit) = Measurement(name);
        var shown = actual.Equals("n/a", StringComparison.OrdinalIgnoreCase) ? "no value available" : WithUnit(actual, unit);
        return $"{subject} {shown}: needs {Requirement(name, required.Key, required.Value, unit)}";
    }

    // "at least 2.50%", "at most 70.00" or "between 30.00 and 70.00", from the recorded key and the code's own ending.
    private static string Requirement(string name, string key, string value, string unit)
    {
        if (Range().Match(value) is { Success: true } range)
            return $"between {WithUnit(range.Groups["low"].Value, unit)} and {WithUnit(range.Groups["high"].Value, unit)}";
        var atMost = key == "RequiredMax" || MaximumSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal));
        return (atMost && key != "RequiredMin" ? "at most " : "at least ") + WithUnit(value, unit);
    }

    // The measurement a comparison code names, without its "below minimum"-style ending.
    private static (string Subject, string Unit) Measurement(string name)
    {
        if (StretchFromAverage().Match(name) is { Success: true } stretch)
            return ($"Close {stretch.Groups["side"].Value} its {stretch.Groups["period"].Value}-day average by", "%");
        var suffix = MinimumSuffixes.Concat(MaximumSuffixes).Concat(OtherSuffixes)
            .FirstOrDefault(item => name.EndsWith(item, StringComparison.Ordinal));
        var measured = suffix is null ? name : name[..^suffix.Length];
        return Measurements.TryGetValue(measured, out var known) ? known : (Readable(measured), string.Empty);
    }

    // The code's sentence or readable name, followed by any numbers it recorded.
    private static string DescribeNamed(string name, IReadOnlyList<(string Key, string Value)> values)
    {
        var sentence = Sentences.TryGetValue(name, out var known) ? known : Readable(name);
        return values.Count == 0 ? sentence
            : sentence + ": " + string.Join(", ", values.Select(item =>
                item.Key == "detail" ? item.Value : $"{ReadableKey(item.Key)} {ReadableValue(item.Value)}"));
    }

    // Explains the market rule from the evidence recorded with it; null when that evidence names no reason.
    private static string? DescribeRegime(IReadOnlyDictionary<string, string?> inputs)
    {
        if (!inputs.TryGetValue("EvidenceJson", out var json) || ReadObject(json) is not { } evidence) return null;
        var reason = Text(evidence, "rejection") ?? Text(evidence, "regimeRejection");
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var rule = Child(evidence, "rule") ?? Child(evidence, "regime");
        var vix = Child(evidence, "vix");
        return reason switch
        {
            "vix_regime_off_on_entry_day" when Text(vix, "close") is { } close && Text(rule, "vixNoLongsAbove") is { } limit =>
                $"VIX {close} is above the long limit of {limit}{OnSession(vix)}",
            "vix_short_regime_off_on_entry_day" when Text(vix, "close") is { } close && Text(rule, "vixNoShortsBelow") is { } floor =>
                $"VIX {close} is below the short floor of {floor}{OnSession(vix)}",
            "vix_close_unknown_for_previous_session" =>
                $"The VIX close of the previous session{OnSession(vix)} is not available, so new entries are refused",
            "market_regime_off_on_entry_day" when Text(rule, "benchmarkSymbol") is { } symbol =>
                $"{symbol} and its {Text(rule, "smaPeriod") ?? "long"}-day average do not meet the strategy's market rule, or their recent bars could not be checked",
            "nasdaq_market_regime_off_on_entry_day" when Text(rule, "nasdaqListingBenchmark") is { } symbol =>
                $"{symbol}, the home market of Nasdaq listings, does not meet the strategy's market rule, or its recent bars could not be checked",
            _ => DescribeNamed(reason, [])
        };
    }

    // " (close of 2026-11-19)" when the VIX reading names its session.
    private static string OnSession(JsonElement? vix) => Text(vix, "session") is { } session ? $" (close of {session})" : string.Empty;

    // "Not enough completed bars are loaded yet: 1d 120 of 260" for each timeframe still short of its warm-up.
    private static string? DescribeWarmup(IReadOnlyDictionary<string, string?> inputs)
    {
        if (!inputs.TryGetValue("CompletedBarsByTimeframe", out var completedJson) || ReadObject(completedJson) is not { } completed ||
            !inputs.TryGetValue("RequiredWarmupBarsByTimeframe", out var requiredJson) || ReadObject(requiredJson) is not { } required)
            return null;
        var gaps = required.EnumerateObject()
            .Where(item => item.Value.ValueKind == JsonValueKind.Number && item.Value.TryGetInt32(out _))
            .Select(item => (Timeframe: item.Name, Required: item.Value.GetInt32(),
                Loaded: int.TryParse(Text(completed, item.Name), CultureInfo.InvariantCulture, out var loaded) ? loaded : 0))
            .Where(item => item.Loaded < item.Required)
            .Select(item => $"{item.Timeframe} {item.Loaded} of {item.Required}")
            .ToArray();
        return gaps.Length == 0 ? null : Sentences["market_state_not_warm"] + ": " + string.Join(", ", gaps);
    }

    // The value recorded under exactly this key, or null.
    private static string? Find(IReadOnlyList<(string Key, string Value)> values, string key) =>
        values.Where(item => item.Key == key).Select(item => item.Value).FirstOrDefault();

    // Adds a measurement's unit to a recorded number; text that is not a number is left as recorded.
    private static string WithUnit(string value, string unit) => unit switch
    {
        "$" => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            ? "$" + amount.ToString("N0", CultureInfo.InvariantCulture) : value,
        _ => value + unit
    };

    // "adx_not_rising" becomes "ADX not rising"; the "REJECT_" prefix of order gates and "strategy:" prefixes are dropped.
    private static string Readable(string name)
    {
        var words = Prefix().Replace(name, string.Empty).Replace('_', ' ').Replace('-', ' ').Trim();
        if (words.Length == 0) return "Check";
        words = Abbreviation().Replace(words.ToLowerInvariant(), match => match.Value.ToUpperInvariant());
        return char.ToUpperInvariant(words[0]) + words[1..];
    }

    // "OpeningHigh" becomes "opening high"; an abbreviation such as "EMA21" stays together.
    private static string ReadableKey(string key) => WordStart().Replace(key, " $0").Trim().ToLowerInvariant();

    // A recorded value that is itself a code reads as words; any other value is shown as recorded.
    private static string ReadableValue(string value) => CodeOnly().IsMatch(value) ? value.Replace('_', ' ') : value;

    // Parses a JSON object string into a detached element; anything else is null.
    private static JsonElement? ReadObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A child object by name, ignoring case (evidence writers use both camelCase and PascalCase).
    private static JsonElement? Child(JsonElement? parent, string name) =>
        Property(parent, name) is { ValueKind: JsonValueKind.Object } child ? child : null;

    // A scalar property as text by name, ignoring case; null when absent or not a scalar.
    private static string? Text(JsonElement? parent, string name) => Property(parent, name) switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString(),
        { ValueKind: JsonValueKind.Number } number => number.GetRawText(),
        _ => null
    };

    // Any property by name, ignoring case.
    private static JsonElement? Property(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } element) return null;
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    [GeneratedRegex(@"(?<key>[A-Za-z][A-Za-z0-9]*): (?<value>.*?)(?=, [A-Z][A-Za-z0-9]*: |$)", RegexOptions.CultureInvariant)]
    private static partial Regex NamedValue();

    [GeneratedRegex(@"^(?<low>-?\d+(\.\d+)?)-(?<high>-?\d+(\.\d+)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex Range();

    [GeneratedRegex(@"^(strategy:|regime:|eligibility:|REJECT_)", RegexOptions.CultureInvariant)]
    private static partial Regex Prefix();

    [GeneratedRegex(@"^[a-z][a-z0-9]*(_[a-z0-9]+)+( \(.*\))?$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex WrappedCode();

    [GeneratedRegex(@"^[a-z0-9]+(_[a-z0-9]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeOnly();

    [GeneratedRegex(@"^close_not_far_enough_(?<side>above|below)_sma(?<period>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex StretchFromAverage();

    [GeneratedRegex(@"\b(adx|atr|ema|macd|obv|orb|rsi|sip|sma|vix|vwap)\d*\b", RegexOptions.CultureInvariant)]
    private static partial Regex Abbreviation();

    [GeneratedRegex(@"(?<=[a-z0-9])[A-Z]", RegexOptions.CultureInvariant)]
    private static partial Regex WordStart();
}
