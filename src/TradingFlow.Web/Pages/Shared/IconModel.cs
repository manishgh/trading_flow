namespace TradingFlow.Web.Pages.Shared;

/// <summary>
/// One inline Lucide icon. The geometry lives here rather than in the view so a
/// name that has no icon fails visibly at one place instead of rendering an
/// empty box on a trading screen.
/// </summary>
/// <param name="Name">Lucide icon name, e.g. <c>refresh-cw</c>.</param>
/// <param name="Size">Rendered edge length in px. The screens use 14, 16 or 19.</param>
public sealed record IconModel(string Name, int Size = 16)
{
    /// <summary>
    /// Icon bodies from lucide.dev, drawn on a 24x24 viewBox. Rounded corners
    /// are flattened to <c>rx="0"</c> because the system rounds nothing.
    /// Only the icons the screens actually use are carried; adding one means
    /// copying its body from Lucide verbatim rather than drawing a new shape.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Paths =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bell"] =
                """<path d="M10.268 21a2 2 0 0 0 3.464 0"/><path d="M3.262 15.326A1 1 0 0 0 4 17h16a1 1 0 0 0 .74-1.673C19.41 13.956 18 12.499 18 8A6 6 0 0 0 6 8c0 4.499-1.411 5.956-2.738 7.326"/>""",
            ["refresh-cw"] =
                """<path d="M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8"/><path d="M21 3v5h-5"/><path d="M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16"/><path d="M8 16H3v5"/>""",
            ["columns-3"] =
                """<rect width="18" height="18" x="3" y="3" rx="0"/><path d="M9 3v18"/><path d="M15 3v18"/>""",
            ["play"] = """<polygon points="6 3 20 12 6 21 6 3"/>""",
            ["square"] = """<rect width="18" height="18" x="3" y="3" rx="0"/>""",
            ["layers"] =
                """<path d="M12.83 2.18a2 2 0 0 0-1.66 0L2.6 6.08a1 1 0 0 0 0 1.83l8.58 3.91a2 2 0 0 0 1.66 0l8.58-3.9a1 1 0 0 0 0-1.83Z"/><path d="m22 17.65-9.17 4.16a2 2 0 0 1-1.66 0L2 17.65"/><path d="m22 12.65-9.17 4.16a2 2 0 0 1-1.66 0L2 12.65"/>""",
            ["trending-up"] =
                """<polyline points="22 7 13.5 15.5 8.5 10.5 2 17"/><polyline points="16 7 22 7 22 13"/>""",
            ["clock"] = """<circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/>""",
            ["briefcase"] =
                """<path d="M16 20V4a2 2 0 0 0-2-2h-4a2 2 0 0 0-2 2v16"/><rect width="20" height="14" x="2" y="6" rx="0"/>""",
            ["newspaper"] =
                """<path d="M15 18h-5"/><path d="M18 14h-8"/><path d="M4 22h16a2 2 0 0 0 2-2V4a2 2 0 0 0-2-2H8a2 2 0 0 0-2 2v16a2 2 0 0 1-4 0v-9a2 2 0 0 1 2-2h2"/><rect width="8" height="4" x="10" y="6" rx="0"/>""",
            ["chevron-right"] = """<path d="m9 18 6-6-6-6"/>""",
            ["chevron-left"] = """<path d="m15 18-6-6 6-6"/>""",
            ["check"] = """<path d="M20 6 9 17l-5-5"/>""",
            ["download"] =
                """<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" x2="12" y1="15" y2="3"/>""",
            ["calendar"] =
                """<path d="M8 2v4"/><path d="M16 2v4"/><rect width="18" height="18" x="3" y="4" rx="0"/><path d="M3 10h18"/>""",
            ["arrow-right"] = """<path d="M5 12h14"/><path d="m12 5 7 7-7 7"/>""",
            ["area-chart"] =
                """<path d="M3 3v16a2 2 0 0 0 2 2h16"/><path d="M7 11.207a.5.5 0 0 1 .146-.353l2-2a.5.5 0 0 1 .708 0l3.292 3.292a.5.5 0 0 0 .708 0l4.292-4.292a.5.5 0 0 1 .854.353V16a1 1 0 0 1-1 1H8a1 1 0 0 1-1-1z"/>""",
            ["wifi"] =
                """<path d="M12 20h.01"/><path d="M2 8.82a15 15 0 0 1 20 0"/><path d="M5 12.859a10 10 0 0 1 14 0"/><path d="M8.5 16.429a5 5 0 0 1 7 0"/>""",
            ["battery"] = """<rect width="16" height="10" x="2" y="7" rx="0"/><path d="M22 11v2"/>"""
        };

    /// <summary>SVG body for <paramref name="name"/>, or null when unknown.</summary>
    public static string? PathFor(string name) => Paths.TryGetValue(name, out var path) ? path : null;
}
