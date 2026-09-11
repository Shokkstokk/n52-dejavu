namespace DejaVu.Core.Model;

/// <summary>
/// A color theme the user built, stored by its five chosen colors rather than by the ten
/// brushes they produce.
///
/// Five colors because that is all a theme in this application actually is. The panes,
/// borders and text are not chosen: they are the shipped palette's own lightness steps applied
/// to the background, so every theme keeps identical contrast between Background, Surface, Line
/// and Hover no matter what color it is built on. Storing the inputs rather than the outputs
/// means a later change to those steps improves every saved theme instead of leaving them all
/// behind at the old values.
///
/// Lives here rather than in the editor because this is the object that gets written to
/// config.json, and that file is defined by this assembly. Nothing in the engine reads it.
/// </summary>
public sealed class CustomTheme
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    /// <summary>
    /// The window's background color, as #RRGGBB - used exactly as chosen.
    ///
    /// The panes, borders and text are derived from it rather than stored: see Palette, which
    /// also records why this is the color itself and not, as it once was, merely a hue.
    /// </summary>
    public string Shell { get; set; } = "#1B1D21";

    /// <summary>
    /// The panes that sit on the window - the assignment lists, the device, the macro and
    /// theme libraries - as #RRGGBB.
    ///
    /// Its own color rather than a fixed step off the background, because how far a pane
    /// should stand off the window is a matter of taste: barely at all reads as one flat
    /// surface, and a long way reads as cards laid on a desk. Row borders and hover fills are
    /// still derived, from this rather than from the background, so they follow the pane they
    /// are drawn on.
    /// </summary>
    public string Pane { get; set; } = "#24272C";

    /// <summary>The pane frames, the device drawing and the keymap in use, as #RRGGBB.</summary>
    public string Accent { get; set; } = "#4C8DFF";

    /// <summary>
    /// What a control and its row wear while they are the one being edited, as #RRGGBB.
    ///
    /// Separate from <see cref="Accent"/> because it has to be told apart from it at a glance:
    /// the accent frames every pane and draws the whole device, so a highlight wearing it would
    /// be one more accent-colored line among a hundred.
    ///
    /// Note what this is not. It is interface feedback - "the one you are looking at" - and not
    /// a report about the hardware. Whether the engine is running, and which profile it is
    /// using, stay green in every theme and are not settable from here at all.
    /// </summary>
    public string Highlight { get; set; } = "#3DD68C";

    /// <summary>
    /// What a control and its row flash while the key is physically down, as #RRGGBB.
    ///
    /// Separate from <see cref="Highlight"/> so that pressing the control you are already
    /// editing still shows something. Sharing one color made that press invisible.
    /// </summary>
    public string Live { get; set; } = "#7CFFC4";

    /// <summary>
    /// Body text, as #RRGGBB. Empty means derived from <see cref="Shell"/>, which is what it
    /// always was and still is for every theme that does not set it.
    ///
    /// An override rather than a sixth required color. Deriving text is what guarantees a
    /// theme stays readable - near-white on a dark shell, near-black on a light one, at a
    /// saturation low enough that it still reads as text rather than as a color - and that
    /// guarantee is worth keeping as the default for anyone who does not care. Dim text
    /// follows this rather than being chosen too: it is the same color pulled toward the
    /// background by a quarter of the distance between them, so the relationship between the
    /// two survives whatever is picked here.
    ///
    /// Text SIZE is deliberately not here. It belongs to the application - see
    /// Configuration.FontSize - because the shipped themes carry no size of their own, so a
    /// size stored per theme meant previewing one snapped the window back to the system
    /// default and relaid out the page.
    /// </summary>
    public string Text { get; set; } = "";

    public CustomTheme Clone() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = Name,
        Shell = Shell,
        Pane = Pane,
        Accent = Accent,
        Highlight = Highlight,
        Live = Live,
        Text = Text,
    };
}
