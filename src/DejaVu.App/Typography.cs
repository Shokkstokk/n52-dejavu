using System.Windows;
using System.Windows.Media;

namespace DejaVu.App;

/// <summary>
/// The application's type scale: one family and one size, from which every other size follows.
///
/// The same idea as <see cref="Palette"/>, for the same reason. A theme lets the user pick the
/// few things that carry meaning and derives the rest, so that no combination of settings can
/// produce the version where the hierarchy collapses - the one where a heading, a label and a
/// footnote are all the same size and nothing tells you which is which. Six sizes chosen
/// independently could do that in a dozen ways; one size and a fixed set of offsets cannot do
/// it at all.
///
/// <para><b>The offsets are the ones the window already used.</b> Body 12, tabs and the engine
/// state 13, headings 14, help text 11, markers 10, and the captured key 22 were scattered
/// through the XAML as literals. They are the same numbers here, expressed as distances from
/// the base rather than as absolutes, which is what makes them move together.</para>
///
/// <para><b>Written straight into the application's own resource dictionary, never into a
/// merged one.</b> WPF resolves a dictionary's own entries before anything merged into it, so
/// a theme that supplied these as a merged palette would be silently ignored - the same trap
/// that makes Active, Inactive, EngineOn and EngineOff un-themeable by design. Here the effect
/// is wanted the other way round: these are set by the code that owns the current theme, and
/// being own entries is what lets them win.</para>
/// </summary>
public static class Typography
{
    /// <summary>
    /// The typeface, taken from Windows rather than chosen.
    ///
    /// Offering every installed family was tried and withdrawn. The window is calibrated to
    /// one metric - a control's name is 84 units wide and its dropdown 172 - so a wider face
    /// at the same size overflows them and a narrower one leaves the layout loose; and about a
    /// dozen places distinguish themselves purely by SemiBold against Normal, a weight most
    /// families do not ship, so WPF snapped them to Bold and the hierarchy that carried
    /// collapsed. One family is not a limitation here, it is the thing the design assumes.
    ///
    /// Reading it from the system is better than naming Segoe UI outright: on a Japanese or
    /// Chinese install this returns the face that actually has the glyphs, and anyone who
    /// needs a particular face for readability has already set it system-wide.
    /// </summary>
    public static string SystemFamily => System.Windows.SystemFonts.MessageFontFamily.Source;

    /// <summary>
    /// Bounds on the base size.
    ///
    /// Not taste: the window is still laid out with fixed widths in places - a control's name
    /// is 84 units wide and its dropdown 172 - and text that outgrows them is clipped rather
    /// than wrapped. Ten to sixteen is what those survive. Widening that range means making
    /// those measurements elastic first.
    /// </summary>
    public const double MinimumSize = 10;

    public const double MaximumSize = 16;

    /// <summary>
    /// The size the application starts at: the one Windows itself uses for message text.
    ///
    /// Every size in the window was previously an unstated agreement with this number - the
    /// styles that set nothing inherited it, and the handful that set 13 or 14 were picked to
    /// sit beside it. Taking it as the base keeps that agreement, and keeps the editor
    /// following the system's own text size for anyone who has changed it.
    /// </summary>
    public static double SystemSize => System.Windows.SystemFonts.MessageFontSize;

    /// <summary>
    /// Each role, and how far it sits from the base.
    ///
    /// Body is the base itself. Emphasis covers the page tabs and the engine's own state line
    /// - both are one notch up rather than a heading. Marker is the smallest thing on screen:
    /// LIVE, FALLBACK, APPLIED and the category headers inside the key list. Display is used
    /// once, for the key name in the capture dialog, which is the only text in the application
    /// that has a whole window to itself.
    /// </summary>
    private static readonly (string Key, double Offset)[] Roles =
    [
        ("FontBody", 0),
        ("FontEmphasis", 1),
        ("FontHeading", 2),
        ("FontSecondary", -1),
        ("FontMarker", -2),
        ("FontDisplay", 10),
    ];

    /// <summary>The family and the six sizes, as they stand right now.</summary>
    public static void Apply(double baseSize)
    {
        var resources = Application.Current.Resources;

        resources["AppFontFamily"] = new FontFamily(SystemFamily);

        var size = Math.Clamp(baseSize, MinimumSize, MaximumSize);

        foreach (var (key, offset) in Roles) resources[key] = size + offset;
    }
}
