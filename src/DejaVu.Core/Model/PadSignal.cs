namespace DejaVu.Core.Model;

/// <summary>Which of the pad's two interfaces a signal came in on, and in what form.</summary>
public enum SignalKind
{
    /// <summary>MI_00: the fourteen keys, the wide bar, the round button and the thumb pad.</summary>
    Keyboard,

    /// <summary>MI_01: the wheel's click, which the pad reports as a middle mouse button.</summary>
    MouseButton,

    /// <summary>MI_01: a wheel notch, up or down.</summary>
    Wheel,
}

/// <summary>
/// One thing the pad can emit, in the pad's own terms.
///
/// <para><b>The extended prefix is stored inside the code, not beside it.</b> A PS/2 extended
/// key is not "scancode 0x48 with a flag set" - on the wire it is two bytes, <c>E0 48</c>,
/// and the prefix is what distinguishes the arrow keys from the numeric keypad positions
/// that share their second byte. Packing it the same way keeps this type to two fields,
/// makes equality a single comparison of them, and removes the possibility of a scancode
/// traveling without the flag that gives it meaning. <see cref="Extended"/> and
/// <see cref="Code"/> still read the way callers expect; they just take the value apart
/// again rather than storing it twice.</para>
///
/// <para>The three kinds do not share a numbering scheme and are not meant to: a keyboard
/// code is a physical switch position, a mouse code is a bit in the flags word MI_01 sends,
/// and a wheel code is simply a direction. <see cref="Kind"/> is what says which of those
/// <see cref="Code"/> means, and two signals of different kinds are never equal whatever
/// their codes.</para>
/// </summary>
public readonly record struct PadSignal
{
    /// <summary>The E0 lead byte, held in the top half of <see cref="Packed"/>.</summary>
    private const ushort Prefix = 0xE000;

    private PadSignal(SignalKind kind, ushort packed)
    {
        Kind = kind;
        Packed = packed;
    }

    /// <summary>Which interface, and in what form.</summary>
    public SignalKind Kind { get; }

    /// <summary>
    /// The value as stored: a keyboard signal carries its E0 prefix here, everything else is
    /// the raw number. Public so the record's own equality is built from it.
    /// </summary>
    public ushort Packed { get; }

    /// <summary>
    /// The number on its own - a scancode, a mouse button flag, or 1 and 2 for the wheel.
    ///
    /// Only a keyboard signal has anything to strip: a mouse flags word genuinely uses its
    /// high bits, so masking one would corrupt it.
    /// </summary>
    public ushort Code => Kind == SignalKind.Keyboard ? (ushort)(Packed & 0x00FF) : Packed;

    /// <summary>True when the scancode needs its E0 lead byte to mean what was pressed.</summary>
    public bool Extended => Kind == SignalKind.Keyboard && (Packed & Prefix) == Prefix;

    /// <summary>A key at a physical position, optionally from the extended set.</summary>
    public static PadSignal Key(ushort scanCode, bool extended = false)
        => new(SignalKind.Keyboard, extended ? (ushort)(Prefix | (scanCode & 0x00FF)) : scanCode);

    /// <summary>A mouse button, named by the bit MI_01 raises when it goes down.</summary>
    public static PadSignal Mouse(ushort downFlag) => new(SignalKind.MouseButton, downFlag);

    /// <summary>One notch away from you.</summary>
    public static PadSignal WheelUp { get; } = new(SignalKind.Wheel, 1);

    /// <summary>One notch toward you.</summary>
    public static PadSignal WheelDown { get; } = new(SignalKind.Wheel, 2);

    public override string ToString() => Kind switch
    {
        SignalKind.Keyboard when Extended => $"key E0 {Code:X2}",
        SignalKind.Keyboard => $"key {Code:X2}",
        SignalKind.MouseButton => $"button {Code:X4}",
        _ => Code == 1 ? "wheel up" : "wheel down",
    };
}
