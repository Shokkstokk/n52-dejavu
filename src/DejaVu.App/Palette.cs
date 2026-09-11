using System.Globalization;
using System.Windows.Media;

namespace DejaVu.App;

/// <summary>
/// Builds a theme's ten brushes from the five colors that describe it.
///
/// The background is used exactly as picked. Everything else is derived from it by the
/// lightness steps the shipped palette itself uses: Surface sits about 4 points off the
/// background, Hover about 9, Line about 13. Those three numbers are the design - they are
/// what makes a pane visible against the window and a border visible against a pane - so
/// keeping them constant means no theme can be the one where the panes disappear, whatever
/// color it is built on.
///
/// It did not always work this way, and the first version was wrong in a way worth recording.
/// It took only the HUE of the chosen color and regenerated the grays at fixed saturation and
/// lightness. The arithmetic was fine and the results were defensible, but it made the picker
/// a liar: dragging around the square changed nothing unless the drag crossed into another
/// hue, and asking for black got you the same palette as asking for red. A control that
/// discards most of what it is given is worse than a cruder control that honors it.
///
/// The preset theme files under Themes/ are produced by tools/theme-gen/makepreset.py, which
/// is this arithmetic in Python. Change a step here and change it there, or a preset stops
/// being what the editor would build from the same five colors.
///
/// Direction follows the background. A dark background pushes the panes lighter and takes
/// light text; a light one pushes them darker and takes dark text. That falls out of the same
/// arithmetic rather than being a second mode.
/// </summary>
public static class Palette
{
    /// <summary>
    /// How far Hover and Line sit from the pane, in lightness, measured off the shipped
    /// palette: #24272C surface, #2F343B hover, #3A3F46 line.
    ///
    /// Measured from the pane rather than from the window, because that is what they sit on:
    /// a row border is drawn on a pane, and a hover fill lifts a row off one. Tie them to the
    /// window instead and a pane moved away from it takes its own borders with it.
    /// </summary>
    private static readonly (string Key, double Step)[] Panes =
    [
        ("Hover",  5.1),
        ("Line",   9.4),
    ];

    /// <summary>
    /// Text lightness, away from the background: near-white on a dark theme, near-black on a
    /// light one. Dim text is pulled a long way toward the background because its whole job is
    /// to be secondary.
    /// </summary>
    private const double TextLightness = 91.0;
    private const double TextDimLightness = 62.7;

    /// <summary>
    /// Saturation ceilings for text. The structural grays carry whatever saturation the
    /// background has, because they are the background; text cannot - near-white at high
    /// saturation stops reading as text and starts reading as a color, and dim text is the
    /// first thing to become unreadable when it drifts off neutral.
    /// </summary>
    private const double TextSaturation = 12.0;
    private const double TextDimSaturation = 16.0;

    /// <summary>
    /// The nine brushes of a theme, ready to be put in a ResourceDictionary.
    /// </summary>
    /// <summary>
    /// How far dim text sits from body text, as a fraction of the way to the background.
    ///
    /// Chosen, not inherited. The derived pair sits 36% apart - text at lightness 91 and dim
    /// text at 62.7 over a shell around 12 - and a quarter is deliberately less than that, so
    /// a chosen text color holds dim text nearer to it. Secondary text that has been pulled a
    /// long way toward the background is the first thing to become unreadable, and a color
    /// picked by hand has no guarantee of starting from as much contrast as the derivation
    /// gives itself. The cost is that body and dim text separate a little less sharply.
    ///
    /// Only reached when a theme names its own text color: the derived path still uses the
    /// two lightness constants above, so no preset and no untouched theme moves.
    /// </summary>
    private const double TextDimFraction = 0.25;

    public static IEnumerable<(string Key, SolidColorBrush Brush)> Build(
        Color background, Color pane, Color accent, Color highlight, Color live,
        Color? text = null)
    {
        var (_, _, lightness) = ToHsl(background);
        var (paneHue, paneSaturation, paneLightness) = ToHsl(pane);

        // Which way the derived grays move. A dark theme lifts them toward the light; a light
        // one presses them toward the dark. Anything else runs them off the end of the scale.
        var dark = lightness < 50;
        var direction = dark ? 1 : -1;

        yield return ("Background", Frozen(background));
        yield return ("Surface", Frozen(pane));

        foreach (var (key, step) in Panes)
            yield return (key, Frozen(FromHsl(paneHue, paneSaturation, paneLightness + step * direction)));

        var (hue, saturation, _) = ToHsl(background);

        if (text is { } chosen)
        {
            // Taken as given, and dim text taken from it: the same hue and saturation, moved
            // the usual fraction of the way toward the background. Deriving the pair together
            // is what stops a chosen text color arriving next to a dim tone with no visible
            // relation to it - the one thing that made text worth deriving in the first place.
            var (textHue, textSaturation, textLightness) = ToHsl(chosen);
            var toward = textLightness + (lightness - textLightness) * TextDimFraction;

            yield return ("Text", Frozen(chosen));
            yield return ("TextDim", Frozen(FromHsl(textHue, textSaturation, toward)));
        }
        else
        {
            yield return ("Text", Frozen(FromHsl(hue, Math.Min(saturation, TextSaturation),
                dark ? TextLightness : 100 - TextLightness)));

            yield return ("TextDim", Frozen(FromHsl(hue, Math.Min(saturation, TextDimSaturation),
                dark ? TextDimLightness : 100 - TextDimLightness)));
        }

        yield return ("Accent", Frozen(accent));

        // A dark, desaturated relative of the accent: it fills a selected row and outlines an
        // assigned control, so it has to sit close enough to read as related and far enough
        // not to compete with the accent itself.
        yield return ("AccentMuted", Frozen(FromHsl(HueOf(accent), 45, dark ? 26 : 78)));

        // The control you are working on, and the control you are pressing. Two colors rather
        // than one because they answer different questions - "which one am I editing" persists
        // while "which one is down right now" flickers - and because a press on the control
        // already selected has to be visible, which it cannot be if both wear the same color.
        //
        // Chosen rather than derived, both of them: their whole job is to be unmistakable
        // against the accent and against each other, and no formula knows how far is far
        // enough.
        yield return ("Highlight", Frozen(highlight));
        yield return ("Live", Frozen(live));
    }

    public static (double Hue, double Saturation, double Lightness) ToHsl(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var span = max - min;
        var lightness = (max + min) / 2;

        if (span < 1e-9) return (0, 0, lightness * 100);

        var saturation = lightness > 0.5
            ? span / (2 - max - min)
            : span / (max + min);

        return (HueOf(color), saturation * 100, lightness * 100);
    }

    // =====================================================================
    //  HSV, for the color picker
    // =====================================================================
    //
    //  HSL is how a palette is described - it has a lightness axis, which is what makes the
    //  shell grays comparable across hues. HSV is how a color is picked: its square is the
    //  saturation/value plane every picker since Photoshop has drawn, and its top-right corner
    //  is the pure hue rather than a washed-out mid tone. Both, therefore, rather than one.

    public static (double Hue, double Saturation, double Value) ToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var span = max - min;

        var hue = span < 1e-9 ? 0
                : max == r ? ((g - b) / span % 6 + 6) % 6
                : max == g ? (b - r) / span + 2
                : (r - g) / span + 4;

        return (hue * 60, max < 1e-9 ? 0 : span / max, max);
    }

    /// <summary>Hue in degrees, saturation and value from 0 to 1.</summary>
    public static Color FromHsv(double hue, double saturation, double value)
    {
        hue = (hue % 360 + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;

        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));
    }

    /// <summary>Frozen because these are shared across the whole window and never change after
    /// construction - a frozen brush skips change tracking and can cross threads.</summary>
    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static double HueOf(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var span = max - min;

        // Gray has no hue. Zero is as good an answer as any and keeps callers from having to
        // handle a null.
        if (span < 1e-9) return 0;

        var hue = max == r ? (g - b) / span % 6
                : max == g ? (b - r) / span + 2
                : (r - g) / span + 4;

        return (hue * 60 + 360) % 360;
    }

    public static Color FromHsl(double hue, double saturation, double lightness)
    {
        hue = (hue % 360 + 360) % 360;
        var s = Math.Clamp(saturation, 0, 100) / 100.0;

        // Clamped, because callers add and subtract steps from a lightness they were given -
        // a background already near white plus a step runs off the end otherwise, and the
        // formula returns nonsense rather than white.
        var l = Math.Clamp(lightness, 0, 100) / 100.0;

        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = l - c / 2;

        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));
    }

    private static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);

    /// <summary>Parses #RRGGBB. Returns null rather than throwing: these strings come out of a
    /// configuration file that a user may well have edited.</summary>
    public static Color? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var text = value.Trim().TrimStart('#');
        if (text.Length != 6) return null;

        return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : null;
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
