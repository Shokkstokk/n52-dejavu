using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using DejaVu.Core.Model;

namespace DejaVu.App;

/// <summary>
/// The application's color palette.
///
/// Same shape as <see cref="Localization.Strings"/>, and for the same reason: the values live
/// in a ResourceDictionary, markup resolves them itself with DynamicResource, and this class
/// exists to put the dictionary in place and to swap it.
///
/// The swap is why the references are dynamic rather than static. A StaticResource is resolved
/// once when the element loads, so a window built before the swap would keep the old colors
/// until it was rebuilt; DynamicResource re-resolves and repaints in place.
///
/// <para><b>A theme is ten brushes, from five chosen colors.</b> Background and Surface are
/// picked outright; Hover, Line, Text and TextDim are derived from them; Accent, AccentMuted,
/// Highlight and Live carry the rest. <see cref="Required"/> is the list.</para>
///
/// <para><b>Four brushes are deliberately outside it.</b> Active, Inactive, EngineOn and
/// EngineOff are declared by App.xaml itself. They report whether the pad is live rather than
/// describing how the window looks, so recoloring them would not restyle anything, it would
/// remove the meaning from the one thing on the window worth interrupting for. Because they
/// are the application dictionary's own entries and this is a merged one, WPF resolves them
/// first and a theme cannot override them even by listing them.</para>
///
/// <para>Brushes assigned from code are the exception and cannot follow a swap on their own -
/// MainWindow sets control outlines with FindResource, which copies the brush rather than
/// referring to it. Those are re-derived on the next refresh, so a caller changing theme has
/// to ask the window to refresh. See <see cref="Changed"/>.</para>
/// </summary>
public static class Theme
{
    public const string Default = "Dark";

    /// <summary>
    /// Every key a theme has to define. A theme dictionary replaces its predecessor outright
    /// rather than layering over it, so a key left out resolves to nothing at all and the
    /// elements using it draw unpainted - a window with holes in it rather than an obvious
    /// error. Checked on load so that cannot ship.
    /// </summary>
    public static readonly string[] Required =
    [
        "Background", "Surface", "Line", "Hover", "Text", "TextDim", "Accent", "AccentMuted",
        "Highlight", "Live",
    ];

    /// <summary>
    /// The themes on offer, in the order they should be listed. The id is both the file name
    /// under Themes/ and what gets written to the configuration.
    /// </summary>
    /// <remarks>
    /// An accent may not be green or red. Those two readings are spoken for: green is the
    /// engine running and a live profile, red is the engine stopped. An accent close to
    /// either would put the same color on things that merely happen to be selected, and the
    /// status colors are fixed precisely so they cannot be diluted.
    /// </remarks>
    public static readonly ThemeOption[] Available =
    [
        new("Dark", "Dark"),
        new("Nightshade", "Nightshade"),
        new("Ember", "Ember"),
        new("Abyss", "Abyss"),
        new("Graphite", "Graphite"),
        new("GoVols", "Go Vols"),
    ];

    /// <summary>
    /// One entry in the picker. A record rather than a named tuple because it is bound to:
    /// a ValueTuple's element names exist only at compile time, so a binding to "Name" would
    /// find nothing and the dropdown would list the type name instead.
    /// </summary>
    public sealed record ThemeOption(string Id, string Name, bool BuiltIn = true);

    /// <summary>
    /// Raised after the palette has changed. Anything holding a brush it copied out of the
    /// resources - rather than referring to one - has to re-read it here.
    /// </summary>
    public static event Action? Changed;

    /// <summary>The theme currently merged.</summary>
    public static string Current { get; private set; } = Default;

    private static ResourceDictionary? _merged;

    /// <summary>
    /// Applies whichever theme an id names - a preset compiled into the assembly, or one the
    /// user built and saved. Falls back to the default when the id matches neither, which is
    /// what happens to a configuration naming a theme that has since been deleted.
    /// </summary>
    public static void Apply(string? id, IReadOnlyList<CustomTheme> saved)
    {
        if (id is not null && saved.FirstOrDefault(t => t.Id == id) is { } custom)
        {
            LoadCustom(custom);
            return;
        }

        Load(id);
    }

    /// <summary>
    /// Builds a palette from the user's two colors and merges it, without going near a file.
    ///
    /// The generated dictionary is interchangeable with a preset's: the same eight keys, put
    /// in the same place, so everything downstream - the DynamicResource lookups, the
    /// <see cref="Changed"/> repaint, the status brushes App.xaml keeps out of reach - behaves
    /// exactly as it does for a theme that shipped with the application.
    /// </summary>
    public static void LoadCustom(CustomTheme custom)
    {
        // Parsed defensively: these three strings come out of a file a user may have edited
        // by hand, and a typo should cost one color rather than the ability to start.
        var shell = Palette.Parse(custom.Shell) ?? Color.FromRgb(0x1B, 0x1D, 0x21);
        var pane = Palette.Parse(custom.Pane) ?? Color.FromRgb(0x24, 0x27, 0x2C);
        var accent = Palette.Parse(custom.Accent) ?? Colors.CornflowerBlue;
        var highlight = Palette.Parse(custom.Highlight) ?? Color.FromRgb(0x3D, 0xD6, 0x8C);
        var live = Palette.Parse(custom.Live) ?? Color.FromRgb(0x7C, 0xFF, 0xC4);

        var dictionary = new ResourceDictionary();

        // Null unless the theme names one, which is what keeps the derivation - and the
        // readability it guarantees - the default for every theme that does not care.
        var text = Palette.Parse(custom.Text);

        foreach (var (key, brush) in Palette.Build(shell, pane, accent, highlight, live, text))
            dictionary[key] = brush;


        Merge(dictionary, custom.Id);
    }

    /// <summary>
    /// Merges a preset palette by name, replacing whichever one is already there. Must run
    /// before the first window is shown: with no palette merged, every DynamicResource in the
    /// markup resolves to nothing and the window draws unpainted.
    ///
    /// An unknown name, a file that will not load, or one missing a required key falls back to
    /// the default rather than throwing. A configuration naming a theme that a later build
    /// renamed or dropped is not worth refusing to start over.
    /// </summary>
    public static void Load(string? name)
    {
        var id = Array.Exists(Available, t => t.Id == name) ? name! : Default;
        var dictionary = TryOpen(id);

        // The default itself failing is a broken build rather than a bad configuration: the
        // file is compiled into the assembly, so it is either there and complete or the
        // application was shipped wrong. Nothing to fall back to, and no point pretending.
        if (dictionary is null && id == Default)
            throw new InvalidOperationException(
                $"The {Default} theme is missing or incomplete. Themes/{Default}.xaml must " +
                $"define: {string.Join(", ", Required)}.");

        if (dictionary is null)
        {
            Load(Default);
            return;
        }


        Merge(dictionary, id);
    }

    private static void Merge(ResourceDictionary dictionary, string id)
    {
        var resources = Application.Current.Resources.MergedDictionaries;

        // Replaced in place, not appended: the dictionaries are searched in reverse order, so
        // a stack of old palettes would still resolve to the newest one and look correct while
        // growing by one dictionary per change.
        if (_merged is not null) resources.Remove(_merged);
        resources.Add(dictionary);

        _merged = dictionary;
        Current = id;

        Changed?.Invoke();
    }

    /// <summary>Loads a theme file and checks it is complete. Null if either fails.</summary>
    private static ResourceDictionary? TryOpen(string id)
    {
        ResourceDictionary dictionary;

        try
        {
            dictionary = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Themes/{id}.xaml", UriKind.Absolute),
            };
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or XamlParseException)
        {
            return null;
        }

        return Array.TrueForAll(Required, key => dictionary[key] is Brush) ? dictionary : null;
    }
}
