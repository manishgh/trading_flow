namespace TradingFlow.Mobile.Resources.Styles;

/// <summary>
/// Resolves the semantic colour tokens declared in <c>Colors.xaml</c> for the
/// theme currently in effect.
/// </summary>
/// <remarks>
/// XAML consumes the tokens through <c>AppThemeBinding</c>, which re-evaluates on
/// its own. Code-behind cannot, so anything that assigns a colour imperatively must
/// resolve it here instead of using a literal. Literals were previously hardcoded to
/// light-theme values and became unreadable once the dark surfaces were introduced.
/// </remarks>
public static class ThemePalette
{
    public static Color TextPrimary => Resolve("TextPrimary");

    public static Color TextSecondary => Resolve("TextSecondary");

    public static Color Accent => Resolve("Accent");

    public static Color AccentStrong => Resolve("AccentStrong");

    public static Color Positive => Resolve("StatePositive");

    public static Color Negative => Resolve("StateNegative");

    public static Color Warning => Resolve("StateWarning");

    public static Color BorderControl => Resolve("BorderControl");

    public static Color SurfacePanel => Resolve("SurfacePanel");

    /// <summary>
    /// Picks the gain, loss, or flat colour for a signed market value. Callers must
    /// still pair the colour with a sign or glyph; colour alone is not an accessible
    /// indicator of direction.
    /// </summary>
    public static Color ForChange(decimal value) =>
        value > 0m ? Positive : value < 0m ? Negative : TextSecondary;

    /// <summary>Picks the colour for a healthy/unhealthy status label.</summary>
    public static Color ForHealth(bool healthy) => healthy ? Positive : Negative;

    private static Color Resolve(string token)
    {
        var suffix = IsDark ? "Dark" : "Light";
        var resources = Application.Current?.Resources;
        if (resources is not null && resources.TryGetValue(token + suffix, out var value) && value is Color color)
        {
            return color;
        }

        // Colors.xaml is missing or not merged yet. Fall back to the theme's primary
        // text colour so the label stays legible rather than defaulting to black.
        return IsDark ? Colors.White : Colors.Black;
    }

    private static bool IsDark =>
        (Application.Current?.RequestedTheme ?? AppInfo.RequestedTheme) == AppTheme.Dark;
}
