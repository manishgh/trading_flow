using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingFlow.Finviz;

/// <summary>
/// One of Finviz's built-in screener signals - the "Signal" dropdown on the
/// screener, sent as the <c>s=</c> parameter.
/// </summary>
/// <param name="Code">The exact <c>s=</c> value Finviz accepts.</param>
/// <param name="Label">Finviz's own display name for the signal.</param>
/// <param name="Group">Operator-facing grouping for the desk picker.</param>
public sealed record FinvizSignal(string Code, string Label, string Group)
{
    /// <summary>Screener query for this signal alone, in the shape the export endpoint takes.</summary>
    public string Query => $"s={Code}";
}

/// <summary>
/// The built-in Finviz signals the desk offers as ready-made screens.
/// </summary>
/// <remarks>
/// Finviz has no endpoint that lists its signals or the screens an Elite user
/// saved in the browser, so this list is maintained by hand. Every code below was
/// checked against a live Finviz screener page whose title carries that exact
/// label for that exact <c>s=</c> value (September 2026). A signal that cannot be
/// confirmed that way is left out rather than guessed. The grouping is the desk's
/// own reading aid, not a Finviz classification.
/// </remarks>
public static class FinvizSignalCatalog
{
    public const string BullishPatterns = "Swing patterns · bullish";
    public const string Momentum = "Momentum";
    public const string BearishMomentum = "Momentum · bearish";
    public const string MeanReversion = "Mean reversion";
    public const string Catalysts = "Catalysts";
    public const string BearishPatterns = "Swing patterns · bearish";
    public const string NeutralPatterns = "Swing patterns · neutral";

    /// <summary>Prefix that marks a desk screener input as a catalogue signal.</summary>
    public const string InputPrefix = "signal:";

    public static IReadOnlyList<FinvizSignal> All { get; } =
    [
        new("ta_p_channelup", "Channel Up", BullishPatterns),
        new("ta_p_doublebottom", "Double Bottom", BullishPatterns),
        new("ta_p_multiplebottom", "Multiple Bottom", BullishPatterns),
        new("ta_p_wedgeup", "Wedge Up", BullishPatterns),
        new("ta_p_wedgeresistance", "Triangle Ascending", BullishPatterns),
        new("ta_p_tlsupport", "TL Support", BullishPatterns),
        new("ta_p_headandshouldersinv", "Head & Shoulders Inverse", BullishPatterns),

        new("ta_newhigh", "New High", Momentum),
        new("ta_topgainers", "Top Gainers", Momentum),
        new("ta_unusualvolume", "Unusual Volume", Momentum),
        new("ta_mostactive", "Most Active", Momentum),
        new("ta_mostvolatile", "Most Volatile", Momentum),

        new("ta_toplosers", "Top Losers", BearishMomentum),
        new("ta_newlow", "New Low", BearishMomentum),

        new("ta_oversold", "Oversold", MeanReversion),
        new("ta_overbought", "Overbought", MeanReversion),

        new("n_upgrades", "Upgrades", Catalysts),
        new("n_downgrades", "Downgrades", Catalysts),
        new("n_earningsbefore", "Earnings Before", Catalysts),
        new("n_earningsafter", "Earnings After", Catalysts),
        new("n_majornews", "Major News", Catalysts),
        new("it_latestbuys", "Recent Insider Buying", Catalysts),
        new("it_latestsales", "Recent Insider Selling", Catalysts),

        new("ta_p_channel", "Channel", NeutralPatterns),
        new("ta_p_wedge", "Wedge", NeutralPatterns),
        new("ta_p_horizontal", "Horizontal S/R", NeutralPatterns),
        new("ta_p_tlresistance", "TL Resistance", NeutralPatterns),

        new("ta_p_channeldown", "Channel Down", BearishPatterns),
        new("ta_p_doubletop", "Double Top", BearishPatterns),
        new("ta_p_multipletop", "Multiple Top", BearishPatterns),
        new("ta_p_wedgedown", "Wedge Down", BearishPatterns),
        new("ta_p_wedgesupport", "Triangle Descending", BearishPatterns),
        new("ta_p_headandshoulders", "Head & Shoulders", BearishPatterns)
    ];

    /// <summary>Groups in the order the desk shows them.</summary>
    public static IReadOnlyList<string> Groups { get; } =
        All.Select(signal => signal.Group).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>The desk input value that selects <paramref name="signal"/>.</summary>
    public static string ToInput(FinvizSignal signal) => InputPrefix + signal.Code;

    /// <summary>
    /// Resolves a desk input of the form <c>signal:code</c>. Anything else,
    /// including an unknown code, returns false so the caller can fall through to
    /// its other input shapes instead of sending an unverified signal to Finviz.
    /// </summary>
    public static bool TryResolveInput(string? input, out FinvizSignal signal)
    {
        signal = null!;
        var trimmed = input?.Trim();
        if (String.IsNullOrEmpty(trimmed) ||
            !trimmed.StartsWith(InputPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var code = trimmed[InputPrefix.Length..].Trim();
        var match = All.FirstOrDefault(candidate => candidate.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return false;
        }

        signal = match;
        return true;
    }
}
