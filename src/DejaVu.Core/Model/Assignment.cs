using System.ComponentModel;
using System.Text.Json.Serialization;

namespace DejaVu.Core.Model;

/// <summary>The kind of action an n52 input triggers.</summary>
public enum ActionKind
{
    /// <summary>Nothing: the input is silenced and never reaches the system.</summary>
    None,

    /// <summary>Let the original signal through unchanged.</summary>
    Passthrough,

    /// <summary>Send a key (with optional modifiers), held for as long as the input is.</summary>
    Key,

    /// <summary>Play a timed sequence.</summary>
    Macro,

    /// <summary>Type a string of text.</summary>
    Text,

    /// <summary>Launch a program, a document or a URL.</summary>
    Launch,

    /// <summary>Send a mouse click, held for as long as the input is.</summary>
    MouseButton,

    /// <summary>Send a wheel notch.</summary>
    MouseWheel,

    /// <summary>
    /// Change the active mode. The n52 has three state LEDs for exactly this: four modes,
    /// shown as off / red / green / blue, each with its own complete set of assignments.
    /// </summary>
    Mode,
}

/// <summary>How a <see cref="ActionKind.Mode"/> action changes the active mode.</summary>
public enum ModeSwitch
{
    /// <summary>Step to the next mode, wrapping back to the first.</summary>
    Cycle,

    /// <summary>Go straight to <see cref="Assignment.ModeIndex"/> and stay there.</summary>
    Select,

    /// <summary>
    /// Hold <see cref="Assignment.ModeIndex"/> while the input is down, then fall back to
    /// whatever mode was active before. A shift key, rather than a latch.
    /// </summary>
    Hold,
}

/// <summary>
/// Keys held down around another key.
///
/// A set rather than a choice, because real combinations stack: Ctrl + Shift + Esc is three
/// of these at once. The values are the usual powers of two so they pack into one number in
/// the configuration file, where a combination reads as "Ctrl, Shift".
/// </summary>
[Flags]
public enum Modifiers
{
    /// <summary>The key on its own.</summary>
    None = 0,

    /// <summary>Either Control key.</summary>
    Ctrl = 1,

    /// <summary>Either Shift key.</summary>
    Shift = 2,

    /// <summary>Either Alt key.</summary>
    Alt = 4,

    /// <summary>Either Windows key.</summary>
    Win = 8,
}

/// <summary>
/// Which mouse button an assignment presses.
///
/// Five, because that is what Windows carries and what a mouse with side buttons sends.
/// Stored by name, so the order here is presentation only and safe to rearrange.
/// </summary>
public enum MouseButton
{
    /// <summary>The primary button.</summary>
    Left,

    /// <summary>The secondary button, the one that opens context menus.</summary>
    Right,

    /// <summary>The wheel pressed inward.</summary>
    Middle,

    /// <summary>First side button; browsers read it as Back.</summary>
    X1,

    /// <summary>Second side button; browsers read it as Forward.</summary>
    X2,
}

/// <summary>How a macro behaves while its input stays held down.</summary>
public enum MacroRepeat
{
    /// <summary>Play the sequence once per press.</summary>
    Once,

    /// <summary>Loop the sequence for as long as the input is held.</summary>
    WhileHeld,

    /// <summary>One press starts the loop, the next stops it.</summary>
    Toggle,
}

/// <summary>The kind of a single macro step.</summary>
public enum MacroStepKind
{
    /// <summary>Press then release the key.</summary>
    KeyPress,

    /// <summary>Press only: the key stays down.</summary>
    KeyDown,

    /// <summary>Release only.</summary>
    KeyUp,

    /// <summary>Wait.</summary>
    Delay,

    /// <summary>Type text.</summary>
    Text,

    /// <summary>A complete mouse click.</summary>
    MouseClick,
}

/// <summary>
/// One instruction in a macro.
///
/// Every step carries every field, and <see cref="Kind"/> decides which of them mean
/// anything. A step-per-type hierarchy would be tidier in the abstract and worse here: this
/// is stored as JSON a person is expected to be able to open and edit, and a flat object
/// with obvious names survives that far better than a discriminated union does.
/// </summary>
public sealed class MacroStep
{
    /// <summary>What this step does. Everything below is read according to it.</summary>
    public MacroStepKind Kind { get; set; } = MacroStepKind.KeyPress;

    /// <summary>Held around the key, for the three keyboard kinds.</summary>
    public Modifiers Modifiers { get; set; } = Modifiers.None;

    /// <summary>Physical key position, for the three keyboard kinds.</summary>
    public ushort ScanCode { get; set; }

    /// <summary>Whether that position needs its E0 prefix to mean what it says.</summary>
    public bool Extended { get; set; }

    /// <summary>What to type, for a text step.</summary>
    public string? Text { get; set; }

    /// <summary>Which button to click, for a click step.</summary>
    public MouseButton MouseButton { get; set; } = MouseButton.Left;

    /// <summary>
    /// How long to wait - the whole point of a delay step, and how long a press or a click
    /// is held down for the two that send something.
    /// </summary>
    public int DelayMs { get; set; }

    /// <summary>How the step reads in the editor's list.</summary>
    public override string ToString()
    {
        var held = DelayMs > 0 ? $"  ({DelayMs} ms)" : string.Empty;

        return Kind switch
        {
            MacroStepKind.KeyDown => $"Hold  {Named()}",
            MacroStepKind.KeyUp => $"Release  {Named()}",
            MacroStepKind.KeyPress => $"Press  {Named()}{held}",
            MacroStepKind.MouseClick => $"Click  {MouseButton}{held}",
            MacroStepKind.Text => $"Text  \"{Text}\"",
            MacroStepKind.Delay => $"Delay  {DelayMs} ms",
            _ => Kind.ToString(),
        };
    }

    /// <summary>
    /// The key as a person would name it, modifiers and all.
    ///
    /// A list of steps reading "Press 0x2E" tells nobody what the macro does. The catalog
    /// that already holds those names lives in this assembly, so naming it here is a lookup
    /// rather than display text leaking into the model.
    /// </summary>
    private string Named() => Input.KeyList.Prefix(Modifiers)
                              + Input.KeyList.Describe(ScanCode, Extended);
}

/// <summary>
/// A named sequence of steps, stored once and referenced by every binding that plays it.
///
/// Macros used to live inside the binding that played them, which meant they had no
/// identity: the same sequence on two buttons was two unrelated copies, and fixing one
/// left the other wrong. Naming them and keeping them in one place is what makes them
/// editable in a single spot and reusable across profiles.
/// </summary>
public sealed class MacroDefinition : INotifyPropertyChanged
{
    private string _name = string.Empty;

    /// <summary>
    /// Identity, and what a binding actually stores.
    ///
    /// Bindings refer to a macro by this rather than by name, which is what lets a macro be
    /// renamed without every control that plays it going quiet.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// What it is called, wherever it is offered or shown as bound.
    ///
    /// Announces itself when it changes, because a rename has to reach the library list and
    /// every assignment row naming this macro at the same moment.
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            if (string.Equals(_name, value, StringComparison.Ordinal)) return;

            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    /// <summary>What it does, in order.</summary>
    public List<MacroStep> Steps { get; set; } = [];

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    public MacroDefinition Clone() => new()
    {
        Name = Name,
        Steps = [.. Steps.Select(step => new MacroStep
        {
            Kind = step.Kind,
            ScanCode = step.ScanCode,
            Extended = step.Extended,
            Modifiers = step.Modifiers,
            DelayMs = step.DelayMs,
            Text = step.Text,
            MouseButton = step.MouseButton,
        })],
    };
}

/// <summary>
/// What an n52 input triggers. A single object covers every action type: the
/// <see cref="Kind"/> field decides which other fields matter, which keeps the JSON
/// serialization readable and editable by hand.
/// </summary>
public sealed class Assignment
{
    /// <summary>Which of the fields below carry meaning. Everything else is ignored.</summary>
    public ActionKind Kind { get; set; } = ActionKind.None;

    /// <summary>
    /// Whether the assignment is applied at all.
    ///
    /// Switching one off parks it: the key, the macro and every setting stay exactly as they
    /// were, waiting to be switched back on. The control goes dead in the meantime - it sends
    /// neither the assignment nor the pad's own signal - which is a third state, and
    /// deliberately not the same as a control that was never assigned.
    ///
    /// True by default so that files written before this field existed load as enabled. A
    /// missing boolean deserializes to false, which would quietly kill every binding at once.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional wording shown in the editor in place of the generated description.</summary>
    public string? Label { get; set; }

    /// <summary>
    /// What the assignment behaves as, which is what every decision should be taken on.
    ///
    /// A switched-off assignment behaves as <see cref="ActionKind.None"/> - swallowed,
    /// sending nothing - while <see cref="Kind"/> goes on holding what it will be again when
    /// it is switched back on.
    /// </summary>
    [JsonIgnore]
    public ActionKind EffectiveKind => Enabled ? Kind : ActionKind.None;

    //  Sending a key
    //  ------------------------------------------------------------------

    /// <summary>The physical key position to send.</summary>
    public ushort ScanCode { get; set; }

    /// <summary>Whether that position needs the E0 prefix to mean what it says.</summary>
    public bool Extended { get; set; }

    /// <summary>Held down around it, and released with it.</summary>
    public Modifiers Modifiers { get; set; } = Modifiers.None;

    //  Mouse
    //  ------------------------------------------------------------------

    /// <summary>Which button, for a click.</summary>
    public MouseButton MouseButton { get; set; } = MouseButton.Left;

    /// <summary>How many wheel notches, positive being away from you.</summary>
    public int WheelDelta { get; set; } = 1;

    //  Text and programs
    //  ------------------------------------------------------------------

    /// <summary>The string to type.</summary>
    public string? Text { get; set; }

    /// <summary>The program, document or URL to open.</summary>
    public string? Path { get; set; }

    /// <summary>Anything to pass to it.</summary>
    public string? Arguments { get; set; }

    //  Keymaps
    //  ------------------------------------------------------------------

    /// <summary>Step onward, jump to one, or hold one down.</summary>
    public ModeSwitch ModeSwitch { get; set; } = ModeSwitch.Cycle;

    /// <summary>Which keymap to jump to or hold, counted from zero. Ignored when stepping.</summary>
    public int ModeIndex { get; set; }

    //  Macros
    //  ------------------------------------------------------------------

    /// <summary>Which macro in <see cref="Configuration.Macros"/> this plays.</summary>
    public string? MacroId { get; set; }

    /// <summary>
    /// How this button plays the macro. Deliberately here and not on the definition: the
    /// sequence is what to send, and once / while-held / toggle is how one button sends it,
    /// so the same macro can be one-shot on one key and looping on another.
    /// </summary>
    public MacroRepeat Repeat { get; set; } = MacroRepeat.Once;

    /// <summary>How long to wait between repeats, when it repeats.</summary>
    public int RepeatDelayMs { get; set; } = 50;

    /// <summary>
    /// An independent copy. Needed wherever one assignment is written to several keymaps at
    /// once: sharing the instance would make the eight copies one binding wearing eight
    /// hats, so editing any of them would rewrite the rest.
    /// </summary>
    public Assignment Clone() => new()
    {
        Kind = Kind,
        ScanCode = ScanCode,
        Extended = Extended,
        Modifiers = Modifiers,
        MacroId = MacroId,
        Repeat = Repeat,
        RepeatDelayMs = RepeatDelayMs,
        Text = Text,
        Path = Path,
        Arguments = Arguments,
        MouseButton = MouseButton,
        WheelDelta = WheelDelta,
        ModeSwitch = ModeSwitch,
        ModeIndex = ModeIndex,
        Label = Label,
        Enabled = Enabled,
    };

    public static Assignment None() => new() { Kind = ActionKind.None };

    public static Assignment Passthrough() => new() { Kind = ActionKind.Passthrough };

}
