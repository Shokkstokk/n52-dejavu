namespace DejaVu.Core.Hardware;

/// <summary>
/// Translates USB HID keyboard reports into the PS/2 set-1 scancodes the rest of the
/// application speaks.
///
/// WHY THIS EXISTS. Everything in <c>PadLayout</c>, <c>KeyList</c> and the saved
/// configuration is expressed as a scancode, because that is what SendInput takes and
/// what SendInput accepts. Reading MI_00 over WinUSB instead gives raw HID reports, which
/// identify keys by USB usage id — a different numbering entirely. This is the bridge, and
/// it exists so that switching input path does not disturb a single stored binding.
/// </summary>
internal static class HidKeyboard
{
    /// <summary>Length of a boot-protocol keyboard report: modifiers, reserved, six keys.</summary>
    public const int BootReportLength = 8;

    /// <summary>Index of the first key slot in a boot report.</summary>
    public const int FirstKeyIndex = 2;

    /// <summary>
    /// Modifier bits of byte 0, in order, as (scancode, extended).
    ///
    /// Modifiers do not appear in the key slots — they are a bitmask of their own — so the
    /// n52's shift key (K11) and round thumb button (LAlt) arrive here rather than in the
    /// array, and would be missed entirely by a reader that only walked the key slots.
    /// </summary>
    public static readonly (ushort ScanCode, bool Extended)[] Modifiers =
    [
        (0x1D, false), // bit 0: left Ctrl
        (0x2A, false), // bit 1: left Shift   <- n52 key 11
        (0x38, false), // bit 2: left Alt     <- n52 round thumb button
        (0x5B, true),  // bit 3: left Win
        (0x1D, true),  // bit 4: right Ctrl
        (0x36, false), // bit 5: right Shift
        (0x38, true),  // bit 6: right Alt
        (0x5C, true),  // bit 7: right Win
    ];

    /// <summary>
    /// USB HID usage (page 0x07) to scancode. Covers the whole main keyboard rather than
    /// just the eighteen usages the n52 emits: the cost is a table, and the benefit is that
    /// a pad with different firmware, or a future device, does not silently drop keys.
    /// </summary>
    private static readonly (ushort ScanCode, bool Extended)?[] Map = BuildMap();

    /// <summary>Scancode for a HID usage, or null when the usage is not a key we can send.</summary>
    public static (ushort ScanCode, bool Extended)? Translate(byte usage)
        => usage < Map.Length ? Map[usage] : null;

    private static (ushort, bool)?[] BuildMap()
    {
        var map = new (ushort, bool)?[0x100];

        void Set(byte usage, ushort scan, bool extended = false) => map[usage] = (scan, extended);

        // Letters, usage 0x04-0x1D, in alphabetical order.
        ushort[] letters =
        [
            0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, // a-j
            0x25, 0x26, 0x32, 0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, // k-t
            0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C,                          // u-z
        ];
        for (byte i = 0; i < letters.Length; i++) Set((byte)(0x04 + i), letters[i]);

        // Digits 1-9 then 0, usage 0x1E-0x27.
        ushort[] digits = [0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B];
        for (byte i = 0; i < digits.Length; i++) Set((byte)(0x1E + i), digits[i]);

        Set(0x28, 0x1C);        // Enter
        Set(0x29, 0x01);        // Escape
        Set(0x2A, 0x0E);        // Backspace
        Set(0x2B, 0x0F);        // Tab            <- n52 key 01
        Set(0x2C, 0x39);        // Space          <- n52 wide thumb bar
        Set(0x2D, 0x0C);        // -
        Set(0x2E, 0x0D);        // =
        Set(0x2F, 0x1A);        // [
        Set(0x30, 0x1B);        // ]
        Set(0x31, 0x2B);        // backslash
        Set(0x33, 0x27);        // ;
        Set(0x34, 0x28);        // '
        Set(0x35, 0x29);        // `
        Set(0x36, 0x33);        // ,
        Set(0x37, 0x34);        // .
        Set(0x38, 0x35);        // /
        Set(0x39, 0x3A);        // Caps Lock      <- n52 key 06

        // Function keys F1-F12, usage 0x3A-0x45.
        ushort[] functions = [0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58];
        for (byte i = 0; i < functions.Length; i++) Set((byte)(0x3A + i), functions[i]);

        Set(0x46, 0x37, true);  // Print Screen
        Set(0x47, 0x46);        // Scroll Lock
        Set(0x48, 0x45);        // Pause
        Set(0x49, 0x52, true);  // Insert
        Set(0x4A, 0x47, true);  // Home
        Set(0x4B, 0x49, true);  // Page Up
        Set(0x4C, 0x53, true);  // Delete
        Set(0x4D, 0x4F, true);  // End
        Set(0x4E, 0x51, true);  // Page Down

        // The four arrows. Extended, which is what separates them from the numpad keys
        // sharing their scancodes — the same distinction that broke key capture yesterday.
        Set(0x4F, 0x4D, true);  // Right          <- n52 pad
        Set(0x50, 0x4B, true);  // Left           <- n52 pad
        Set(0x51, 0x50, true);  // Down           <- n52 pad
        Set(0x52, 0x48, true);  // Up             <- n52 pad

        Set(0x53, 0x45);        // Num Lock
        Set(0x54, 0x35, true);  // Numpad /
        Set(0x55, 0x37);        // Numpad *
        Set(0x56, 0x4A);        // Numpad -
        Set(0x57, 0x4E);        // Numpad +
        Set(0x58, 0x1C, true);  // Numpad Enter
        Set(0x59, 0x4F);        // Numpad 1
        Set(0x5A, 0x50);
        Set(0x5B, 0x51);
        Set(0x5C, 0x4B);
        Set(0x5D, 0x4C);
        Set(0x5E, 0x4D);
        Set(0x5F, 0x47);
        Set(0x60, 0x48);
        Set(0x61, 0x49);
        Set(0x62, 0x52);        // Numpad 0
        Set(0x63, 0x53);        // Numpad .
        Set(0x65, 0x5D, true);  // Application / Menu

        return map;
    }
}
