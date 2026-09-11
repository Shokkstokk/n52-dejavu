using DejaVu.Core.Model;

namespace DejaVu.Core.Input;

/// <summary>
/// One key the pad can be told to send.
///
/// Identified by where it sits rather than by what it prints: a scancode is a switch
/// position, so an assignment made here means the same thing on a keyboard laid out for
/// another language, where the letter under that position is different.
/// </summary>
/// <param name="Name">How the key is written in the picker. US labelling.</param>
/// <param name="ScanCode">The switch position the keyboard controller reports.</param>
/// <param name="Extended">Whether that position needs its E0 prefix to be unambiguous.</param>
/// <param name="Category">
/// A name in the string table, not a label. Grouping the picker is presentation, and this
/// assembly holds no display text of its own.
/// </param>
public sealed record KeyEntry(string Name, ushort ScanCode, bool Extended, string Category);

/// <summary>
/// Catalog of keys the engine can send, indexed by scancode.
///
/// Everything works in scancodes rather than virtual keys, for two reasons: games that
/// read the keyboard through DirectInput ignore purely virtual injections, and a scancode
/// names a physical position, so the result does not depend on the active layout.
///
/// The names below are the legends of a US QWERTY keyboard - what is actually printed on
/// the keys. This matters: a scancode is a position, not a letter, so the same 0x10 that
/// reads "Q" here is a different letter on a keyboard laid out differently. If these labels
/// are ever redone for another layout, change only the Name strings - never the scancodes,
/// which are what the device and the games agree on.
/// </summary>
public static class KeyList
{
    public static readonly IReadOnlyList<KeyEntry> All =
    [
        // --- Letters (US QWERTY legends) --------------------------------------
        new("A", 0x1E, false, "CatLetters"),
        new("B", 0x30, false, "CatLetters"),
        new("C", 0x2E, false, "CatLetters"),
        new("D", 0x20, false, "CatLetters"),
        new("E", 0x12, false, "CatLetters"),
        new("F", 0x21, false, "CatLetters"),
        new("G", 0x22, false, "CatLetters"),
        new("H", 0x23, false, "CatLetters"),
        new("I", 0x17, false, "CatLetters"),
        new("J", 0x24, false, "CatLetters"),
        new("K", 0x25, false, "CatLetters"),
        new("L", 0x26, false, "CatLetters"),
        new("M", 0x32, false, "CatLetters"),
        new("N", 0x31, false, "CatLetters"),
        new("O", 0x18, false, "CatLetters"),
        new("P", 0x19, false, "CatLetters"),
        new("Q", 0x10, false, "CatLetters"),
        new("R", 0x13, false, "CatLetters"),
        new("S", 0x1F, false, "CatLetters"),
        new("T", 0x14, false, "CatLetters"),
        new("U", 0x16, false, "CatLetters"),
        new("V", 0x2F, false, "CatLetters"),
        new("W", 0x11, false, "CatLetters"),
        new("X", 0x2D, false, "CatLetters"),
        new("Y", 0x15, false, "CatLetters"),
        new("Z", 0x2C, false, "CatLetters"),

        // --- Number row --------------------------------------------------------
        new("1", 0x02, false, "CatDigits"),
        new("2", 0x03, false, "CatDigits"),
        new("3", 0x04, false, "CatDigits"),
        new("4", 0x05, false, "CatDigits"),
        new("5", 0x06, false, "CatDigits"),
        new("6", 0x07, false, "CatDigits"),
        new("7", 0x08, false, "CatDigits"),
        new("8", 0x09, false, "CatDigits"),
        new("9", 0x0A, false, "CatDigits"),
        new("0", 0x0B, false, "CatDigits"),

        // --- Punctuation and symbols -------------------------------------------
        new("`  ~", 0x29, false, "CatPunctuation"),
        new("-  _", 0x0C, false, "CatPunctuation"),
        new("=  +", 0x0D, false, "CatPunctuation"),
        new("[  {", 0x1A, false, "CatPunctuation"),
        new("]  }", 0x1B, false, "CatPunctuation"),
        new("\\  |", 0x2B, false, "CatPunctuation"),
        new(";  :", 0x27, false, "CatPunctuation"),
        new("'  \"", 0x28, false, "CatPunctuation"),
        new(",  <", 0x33, false, "CatPunctuation"),
        new(".  >", 0x34, false, "CatPunctuation"),
        new("/  ?", 0x35, false, "CatPunctuation"),

        // --- Function keys ------------------------------------------------------
        new("F1", 0x3B, false, "CatFunction"),
        new("F2", 0x3C, false, "CatFunction"),
        new("F3", 0x3D, false, "CatFunction"),
        new("F4", 0x3E, false, "CatFunction"),
        new("F5", 0x3F, false, "CatFunction"),
        new("F6", 0x40, false, "CatFunction"),
        new("F7", 0x41, false, "CatFunction"),
        new("F8", 0x42, false, "CatFunction"),
        new("F9", 0x43, false, "CatFunction"),
        new("F10", 0x44, false, "CatFunction"),
        new("F11", 0x57, false, "CatFunction"),
        new("F12", 0x58, false, "CatFunction"),

        // F13 to F24 exist on no common physical keyboard, so they cannot be captured by
        // pressing them — only chosen from this list. That is exactly why they are useful
        // here: nothing else on the system is bound to them, so there are no conflicts.
        new("F13", 0x64, false, "CatFunctionHigh"),
        new("F14", 0x65, false, "CatFunctionHigh"),
        new("F15", 0x66, false, "CatFunctionHigh"),
        new("F16", 0x67, false, "CatFunctionHigh"),
        new("F17", 0x68, false, "CatFunctionHigh"),
        new("F18", 0x69, false, "CatFunctionHigh"),
        new("F19", 0x6A, false, "CatFunctionHigh"),
        new("F20", 0x6B, false, "CatFunctionHigh"),
        new("F21", 0x6C, false, "CatFunctionHigh"),
        new("F22", 0x6D, false, "CatFunctionHigh"),
        new("F23", 0x6E, false, "CatFunctionHigh"),
        new("F24", 0x76, false, "CatFunctionHigh"),

        // --- Editing and navigation ---------------------------------------------
        new("Esc", 0x01, false, "CatNavigation"),
        new("Tab", 0x0F, false, "CatNavigation"),
        new("Enter", 0x1C, false, "CatNavigation"),
        new("Space", 0x39, false, "CatNavigation"),
        new("Backspace", 0x0E, false, "CatNavigation"),
        new("Insert", 0x52, true, "CatNavigation"),
        new("Delete", 0x53, true, "CatNavigation"),
        new("Home", 0x47, true, "CatNavigation"),
        new("End", 0x4F, true, "CatNavigation"),
        new("Page Up", 0x49, true, "CatNavigation"),
        new("Page Down", 0x51, true, "CatNavigation"),
        new("Up arrow", 0x48, true, "CatNavigation"),
        new("Down arrow", 0x50, true, "CatNavigation"),
        new("Left arrow", 0x4B, true, "CatNavigation"),
        new("Right arrow", 0x4D, true, "CatNavigation"),
        new("Print Screen", 0x37, true, "CatNavigation"),
        new("Caps Lock", 0x3A, false, "CatNavigation"),
        new("Scroll Lock", 0x46, false, "CatNavigation"),
        new("Menu", 0x5D, true, "CatNavigation"),

        // --- Modifiers ------------------------------------------------------------
        new("Left Ctrl", 0x1D, false, "CatModifiers"),
        new("Right Ctrl", 0x1D, true, "CatModifiers"),
        new("Left Shift", 0x2A, false, "CatModifiers"),
        new("Right Shift", 0x36, false, "CatModifiers"),
        new("Left Alt", 0x38, false, "CatModifiers"),
        new("Right Alt", 0x38, true, "CatModifiers"),
        new("Left Win", 0x5B, true, "CatModifiers"),
        new("Right Win", 0x5C, true, "CatModifiers"),

        // --- Numeric keypad --------------------------------------------------------
        new("Num 0", 0x52, false, "CatNumpad"),
        new("Num 1", 0x4F, false, "CatNumpad"),
        new("Num 2", 0x50, false, "CatNumpad"),
        new("Num 3", 0x51, false, "CatNumpad"),
        new("Num 4", 0x4B, false, "CatNumpad"),
        new("Num 5", 0x4C, false, "CatNumpad"),
        new("Num 6", 0x4D, false, "CatNumpad"),
        new("Num 7", 0x47, false, "CatNumpad"),
        new("Num 8", 0x48, false, "CatNumpad"),
        new("Num 9", 0x49, false, "CatNumpad"),
        new("Num .", 0x53, false, "CatNumpad"),
        new("Num +", 0x4E, false, "CatNumpad"),
        new("Num -", 0x4A, false, "CatNumpad"),
        new("Num *", 0x37, false, "CatNumpad"),
        new("Num /", 0x35, true, "CatNumpad"),
        new("Num Enter", 0x1C, true, "CatNumpad"),
        new("Num Lock", 0x45, false, "CatNumpad"),
    ];

    /// <summary>
    /// Position back to name.
    ///
    /// Keyed on the position and its prefix together, because neither identifies a key on
    /// its own: Right Arrow and Num 6 are both 0x4D, and only the E0 tells them apart. The
    /// first entry wins where the catalog lists a position twice, so the picker's own
    /// order decides which name a duplicate reports under.
    /// </summary>
    private static readonly Dictionary<(ushort Position, bool Prefixed), KeyEntry> Lookup =
        All.DistinctBy(key => (key.ScanCode, key.Extended))
           .ToDictionary(key => (key.ScanCode, key.Extended));

    /// <summary>
    /// What to call a scancode.
    ///
    /// A position the catalog does not list still has to read as something, so it falls
    /// back to the number itself rather than to an empty string - a macro step saying
    /// "Press 0x7B" is at least diagnosable, where a blank one looks like a bug.
    /// </summary>
    public static string Describe(ushort scanCode, bool extended)
    {
        if (Lookup.TryGetValue((scanCode, extended), out var known)) return known.Name;

        return extended ? $"scancode E0 {scanCode:X2}" : $"scancode 0x{scanCode:X2}";
    }

    /// <summary>The modifiers held around a key, ready to be put in front of its name.</summary>
    private static readonly (Modifiers Flag, string Word)[] Held =
    [
        (Modifiers.Ctrl, "Ctrl"),
        (Modifiers.Shift, "Shift"),
        (Modifiers.Alt, "Alt"),
        (Modifiers.Win, "Win"),
    ];

    /// <summary>
    /// The held keys as a prefix - "Ctrl + Shift + " - or nothing at all.
    ///
    /// Written in the order above rather than the order the flags were set, so the same
    /// combination always reads the same way. That order is the one keyboards are described
    /// in, which is what makes "Ctrl + Alt + Del" recognizable at a glance.
    /// </summary>
    public static string Prefix(Modifiers modifiers)
    {
        if (modifiers == Modifiers.None) return string.Empty;

        var text = new System.Text.StringBuilder(24);

        foreach (var (flag, word) in Held)
        {
            if (modifiers.HasFlag(flag)) text.Append(word).Append(" + ");
        }

        return text.ToString();
    }

    /// <summary>
    /// Cuts a value down to fit a tile, marking that something was removed.
    ///
    /// The ellipsis is a single character rather than three dots: it is one glyph wide in
    /// the space that matters, and the point of trimming is to fit.
    /// </summary>
    public static string Shorten(string? value, int room = 24)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        return value.Length <= room ? value : string.Concat(value.AsSpan(0, room), "…");
    }
}
