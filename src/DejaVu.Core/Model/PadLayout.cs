namespace DejaVu.Core.Model;

/// <summary>Logical grouping of inputs, used by the editor to lay out its tiles.</summary>
public enum ButtonGroup
{
    /// <summary>The main keypad: 14 keys across three rows (5 / 5 / 4).</summary>
    Keypad,

    /// <summary>Thumb controls: the round button and the wide bar below it.</summary>
    Thumb,

    /// <summary>The eight-way directional pad under the thumb.</summary>
    ThumbPad,

    /// <summary>The scroll wheel on the right-hand module: rotation and click.</summary>
    Wheel,
}

/// <summary>
/// One physical input on the n52.
/// Ordinary inputs carry a <see cref="Signal"/>. The pad diagonals have no signal of
/// their own — the device emits two arrows simultaneously — so <see cref="Components"/>
/// names the two cardinal inputs to merge instead.
/// </summary>
/// <remarks>
/// <c>Label</c> is the number printed on the key for the main keypad, and a resource key
/// for everything else: "01" stays "01", but "Round button" comes from the string table.
/// The two cases are told apart by whether the label is all digits.
/// </remarks>
/// <param name="Id">Stable key. Bindings are stored under it, so it must not change.</param>
/// <param name="Label">The number printed on the key, or a name in the string table.</param>
/// <param name="Signal">What the pad emits. Left unset for the diagonals, which emit nothing.</param>
/// <param name="Group">Which cluster it belongs to, for laying the editor out.</param>
/// <param name="Row">Position within that cluster.</param>
/// <param name="Column">Position within that cluster.</param>
/// <param name="Components">For a diagonal: the two cardinals whose signals compose it.</param>
public sealed record PadInput(
    string Id,
    string Label,
    PadSignal Signal,
    ButtonGroup Group,
    int Row,
    int Column,
    string[]? Components = null)
{
    /// <summary>True for the pad diagonals, which are combinations rather than signals.</summary>
    public bool IsCombination => Components is { Length: > 0 };

    /// <summary>
    /// True for inputs the device reports as a single impulse, with no release to follow:
    /// the two wheel rotations. Every other input, the wheel CLICK included, arrives as a
    /// press and a matching release.
    /// </summary>
    /// <remarks>
    /// Callers must supply the missing edge themselves. The engine completes the action
    /// immediately so a bound key cannot latch down; the editor clears the tile highlight
    /// on a timer, since there is no release event to clear it.
    /// </remarks>
    public bool IsMomentary => Signal.Kind == SignalKind.Wheel;

    /// <summary>True when <see cref="Label"/> is a printed number rather than a resource key.</summary>
    public bool HasLiteralLabel => Label.All(char.IsDigit);
}

/// <summary>
/// Physical map of the Belkin Nostromo SpeedPad n52 (model F8GFPC100), hardware id
/// USB\VID_050D&amp;PID_0815 — MI_00 is the keyboard collection, MI_01 the mouse wheel.
///
/// The scancodes are what the device emits with no proprietary driver present. They are
/// fixed in firmware and depend on neither the user's keyboard layout nor the Windows
/// version.
///
/// PROVENANCE. Belkin's own manual documents the default HID map for the n52, in the
/// section "Using the SpeedPad as a Default HID Device", and the values below match it:
/// keys 01-14 report Tab Q W E R / CapsLock A S D F / LShift Z X C, the D-pad
/// reports arrows, and the wheel reports as a mouse wheel with a middle click.
///
/// VERIFIED ON HARDWARE, 2026-09-02, with tools/DejaVu.RawProbe against a physical n52.
/// All 23 signal-bearing inputs reported exactly the values in this table. The whole
/// table is now measured, not inferred — including the two points below.
///
/// ONE DIFFERENCE FROM THE LATER MODEL. On PID_0200 the wide thumb bar emits nothing —
/// it is handled entirely in firmware and is invisible to every HID collection. On the
/// original n52 the same bar reports Space (0x39), so it is a real, assignable input here.
/// Measured: scancode 0x39, vk 0x20, on MI_00. Belkin's manual was right.
///
/// A SECOND DIFFERENCE FROM THE LATER MODEL. That device splits MI_01 into three collections
/// (COL01 mouse / COL02 consumer / COL03 system). This device does not: MI_01 enumerates
/// as a single, undivided node of class Mouse, with no COLxx in its interface path. Both
/// wheel rotation (button flag 0x0400, delta +/-120) and the wheel click (flag 0x0010)
/// arrive there, and both are read from the interrupt pipe without special handling.
/// </summary>
public static class PadLayout
{

    /// <summary>Every usable input, in the order the editor displays them.</summary>
    public static readonly IReadOnlyList<PadInput> Buttons =
    [
        // --- Main keypad ------------------------------------------------------
        // Row 1: Tab Q W E R (physical positions)
        new("K01", "01", PadSignal.Key(0x0F), ButtonGroup.Keypad, 0, 0),
        new("K02", "02", PadSignal.Key(0x10), ButtonGroup.Keypad, 0, 1),
        new("K03", "03", PadSignal.Key(0x11), ButtonGroup.Keypad, 0, 2),
        new("K04", "04", PadSignal.Key(0x12), ButtonGroup.Keypad, 0, 3),
        new("K05", "05", PadSignal.Key(0x13), ButtonGroup.Keypad, 0, 4),

        // Row 2: CapsLock A S D F
        new("K06", "06", PadSignal.Key(0x3A), ButtonGroup.Keypad, 1, 0),
        new("K07", "07", PadSignal.Key(0x1E), ButtonGroup.Keypad, 1, 1),
        new("K08", "08", PadSignal.Key(0x1F), ButtonGroup.Keypad, 1, 2),
        new("K09", "09", PadSignal.Key(0x20), ButtonGroup.Keypad, 1, 3),
        new("K10", "10", PadSignal.Key(0x21), ButtonGroup.Keypad, 1, 4),

        // Row 3: LShift Z X C
        new("K11", "11", PadSignal.Key(0x2A), ButtonGroup.Keypad, 2, 0),
        new("K12", "12", PadSignal.Key(0x2C), ButtonGroup.Keypad, 2, 1),
        new("K13", "13", PadSignal.Key(0x2D), ButtonGroup.Keypad, 2, 2),
        new("K14", "14", PadSignal.Key(0x2E), ButtonGroup.Keypad, 2, 3),

        // Belkin numbers the wide thumb bar as button 15 and prints that on the device,
        // so it is grouped with the numbered keys rather than with the thumb controls
        // despite sitting under the thumb. It reports Space, an input the later
        // PID_0200 model does not have at all. Confirmed against hardware with DejaVu.RawProbe
        // on 2026-09-02.
        new("BAR", "15", PadSignal.Key(0x39), ButtonGroup.Keypad, 2, 4),

        // --- Thumb controls ---------------------------------------------------
        // Belkin's manual calls this the thumb button. It reports LAlt.
        new("ROUND", "BtnRound", PadSignal.Key(0x38), ButtonGroup.Thumb, 0, 0),

        // --- Thumb pad, eight directions --------------------------------------
        // The four cardinals are extended arrow scancodes. The four diagonals have no
        // signal of their own: the device emits the two neighboring arrows together.
        //
        // "Together" is loose. Measured on hardware, the two switches engage between
        // 176 ms and 336 ms apart — a diagonal is a slow mechanical roll, not a
        // simultaneous event. This is why PadRuntime merges diagonals from the set of
        // arrows currently HELD rather than from an arrival-time window: any window
        // tight enough to be meaningful would miss every real diagonal.
        new("PAD_UP", "BtnUp", PadSignal.Key(0x48, extended: true), ButtonGroup.ThumbPad, 0, 1),
        new("PAD_DOWN", "BtnDown", PadSignal.Key(0x50, extended: true), ButtonGroup.ThumbPad, 2, 1),
        new("PAD_LEFT", "BtnLeft", PadSignal.Key(0x4B, extended: true), ButtonGroup.ThumbPad, 1, 0),
        new("PAD_RIGHT", "BtnRight", PadSignal.Key(0x4D, extended: true), ButtonGroup.ThumbPad, 1, 2),

        new("PAD_UP_LEFT", "BtnUpLeft", default, ButtonGroup.ThumbPad, 0, 0, ["PAD_UP", "PAD_LEFT"]),
        new("PAD_UP_RIGHT", "BtnUpRight", default, ButtonGroup.ThumbPad, 0, 2, ["PAD_UP", "PAD_RIGHT"]),
        new("PAD_DOWN_LEFT", "BtnDownLeft", default, ButtonGroup.ThumbPad, 2, 0, ["PAD_DOWN", "PAD_LEFT"]),
        new("PAD_DOWN_RIGHT", "BtnDownRight", default, ButtonGroup.ThumbPad, 2, 2, ["PAD_DOWN", "PAD_RIGHT"]),

        // --- Scroll wheel -----------------------------------------------------
        // The click arrives as a middle button on the device's mouse collection.
        new("WHEEL_UP", "BtnWheelUp", PadSignal.WheelUp, ButtonGroup.Wheel, 0, 0),
        new("WHEEL_CLICK", "BtnWheelClick", PadSignal.Mouse(0x0010), ButtonGroup.Wheel, 1, 0),
        new("WHEEL_DOWN", "BtnWheelDown", PadSignal.WheelDown, ButtonGroup.Wheel, 2, 0),
    ];

    /// <summary>
    /// The four corners, which the engine has to weigh before the cardinals.
    ///
    /// A corner has no switch of its own, so the only way to notice one is to look at which
    /// arrows are held and ask whether they add up to a diagonal that has been assigned. That
    /// test has to come first: deciding from an arrow's own binding would leave an assigned
    /// corner unreachable unless both of its arrows happened to be bound as well.
    /// </summary>
    public static readonly IReadOnlyList<PadInput> Combinations =
        [.. Buttons.Where(input => input.IsCombination)];

    /// <summary>
    /// Two indexes over the same list, built once at startup.
    ///
    /// The engine asks "which input emits this" for every event the pad produces, and the
    /// editor asks "which input is this id" for every binding it draws. Both would otherwise
    /// walk twenty-seven entries. Corners are left out of the signal index on purpose: they
    /// emit nothing to be found by, and including them would put several entries under one
    /// default key.
    /// </summary>
    private static readonly Dictionary<PadSignal, PadInput> BySignal =
        Buttons.Where(input => !input.IsCombination).ToDictionary(input => input.Signal);

    private static readonly Dictionary<string, PadInput> ByName =
        Buttons.ToDictionary(input => input.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The input that emits a signal, or null when the pad did not send it.</summary>
    public static PadInput? Find(PadSignal signal) => BySignal.GetValueOrDefault(signal);

    /// <summary>The input a stored binding names, or null if that id is no longer known.</summary>
    public static PadInput? FindById(string id) => ByName.GetValueOrDefault(id);

}
