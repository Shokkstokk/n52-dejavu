using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DejaVu.Core.Model;

namespace DejaVu.Core.Input;

/// <summary>
/// Injects keystrokes, clicks and wheel notches through SendInput.
///
/// <para><b>Everything goes out as a scancode</b>, never a virtual key. Games that read the
/// keyboard through DirectInput or Raw Input see the scancode and ignore a purely virtual
/// injection outright, so a virtual-key remap works everywhere except the applications this
/// exists for.</para>
///
/// <para><b>Nothing here allocates.</b> Events are built on the stack and handed to Windows
/// by reference. That matters because of where this sits: a held key repeats about thirty
/// times a second, each repeat is a call through here, and a macro looping while held is
/// several more. An array per event would be garbage generated in proportion to how long the
/// user leans on a key.</para>
/// </summary>
public static class Injector
{
    // ---- SendInput's own vocabulary. Values fixed by Windows. --------------------

    private const uint SourceMouse = 0;
    private const uint SourceKeyboard = 1;

    private const uint KeyIsExtended = 0x0001;
    private const uint KeyIsRelease = 0x0002;
    private const uint KeyIsUnicode = 0x0004;
    private const uint KeyIsScanCode = 0x0008;

    private const uint LeftPress = 0x0002, LeftRelease = 0x0004;
    private const uint RightPress = 0x0008, RightRelease = 0x0010;
    private const uint MiddlePress = 0x0020, MiddleRelease = 0x0040;
    private const uint SidePress = 0x0080, SideRelease = 0x0100;
    private const uint WheelTurn = 0x0800;

    private const uint SideButton1 = 0x0001, SideButton2 = 0x0002;

    /// <summary>One wheel notch, as Windows counts them.</summary>
    private const int NotchDelta = 120;

    // ---- The INPUT record, laid out as Windows expects ---------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerEvent
    {
        public int DeltaX;
        public int DeltaY;
        public uint Payload;
        public uint Flags;
        public uint Timestamp;
        public nint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyEvent
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Timestamp;
        public nint Extra;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct EventBody
    {
        [FieldOffset(0)] public PointerEvent Pointer;
        [FieldOffset(0)] public KeyEvent Key;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputRecord
    {
        public uint Source;
        public EventBody Body;

        /// <summary>Size Windows is told to expect. Wrong here and SendInput refuses everything.</summary>
        public static readonly int Size = Unsafe.SizeOf<InputRecord>();
    }

    // DllImport rather than the source-generated LibraryImport. LibraryImport would need
    // AllowUnsafeBlocks switched on for the whole assembly, and buying generated marshalling
    // for two calls is not worth giving up that guarantee across every file in DejaVu.Core.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, in InputRecord first, int size);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, out uint value, uint update);

    // ---- Dispatch ----------------------------------------------------------------

    /// <summary>Events Windows refused to queue, counted since the process started.</summary>
    public static long Dropped;

    /// <summary>
    /// Hands a batch to Windows and records anything it would not take.
    ///
    /// The return value is not decoration. SendInput queues events one at a time and stops
    /// dead at the first one it cannot deliver - UIPI blocking a lower-integrity process, or
    /// another injector holding the queue - then reports how many it managed. Discarding that
    /// count turns a half-delivered keystroke, a press whose release was dropped, into
    /// something that looks like a bug in what was built rather than in whether Windows
    /// accepted it.
    /// </summary>
    private static void Dispatch(scoped ReadOnlySpan<InputRecord> batch)
    {
        if (batch.IsEmpty) return;

        var accepted = SendInput((uint)batch.Length, in MemoryMarshal.GetReference(batch), InputRecord.Size);
        if (accepted < batch.Length) Interlocked.Add(ref Dropped, batch.Length - accepted);
    }

    private static void Dispatch(in InputRecord single)
        => Dispatch(MemoryMarshal.CreateReadOnlySpan(in single, 1));

    // ---- Building events ---------------------------------------------------------

    private static InputRecord Stroke(ushort scanCode, bool extended, bool release)
    {
        var flags = KeyIsScanCode;
        if (extended) flags |= KeyIsExtended;
        if (release) flags |= KeyIsRelease;

        return new InputRecord
        {
            Source = SourceKeyboard,
            Body = new EventBody { Key = new KeyEvent { ScanCode = scanCode, Flags = flags } },
        };
    }

    private static InputRecord Character(char value, bool release)
    {
        var flags = KeyIsUnicode;
        if (release) flags |= KeyIsRelease;

        return new InputRecord
        {
            Source = SourceKeyboard,
            Body = new EventBody { Key = new KeyEvent { ScanCode = value, Flags = flags } },
        };
    }

    private static InputRecord Pointer(uint flags, uint payload = 0)
        => new()
        {
            Source = SourceMouse,
            Body = new EventBody { Pointer = new PointerEvent { Flags = flags, Payload = payload } },
        };

    // ---- Keys --------------------------------------------------------------------

    public static void KeyDown(ushort scanCode, bool extended)
        => Dispatch(Stroke(scanCode, extended, release: false));

    public static void KeyUp(ushort scanCode, bool extended)
        => Dispatch(Stroke(scanCode, extended, release: true));

    /// <summary>The left-hand scancode for each modifier. Win is an extended key; the rest are not.</summary>
    private static readonly (Modifiers Flag, ushort ScanCode, bool Extended)[] ModifierKeys =
    [
        (Modifiers.Ctrl, 0x1D, false),
        (Modifiers.Shift, 0x2A, false),
        (Modifiers.Alt, 0x38, false),
        (Modifiers.Win, 0x5B, true),
    ];

    /// <summary>Holds down the requested modifiers, in the order Ctrl, Shift, Alt, Win.</summary>
    public static void ModifiersDown(Modifiers modifiers)
    {
        Span<InputRecord> batch = stackalloc InputRecord[ModifierKeys.Length];
        var count = 0;

        foreach (var (flag, scanCode, extended) in ModifierKeys)
            if (modifiers.HasFlag(flag))
                batch[count++] = Stroke(scanCode, extended, release: false);

        Dispatch(batch[..count]);
    }

    /// <summary>Releases them again, unwinding in the reverse order.</summary>
    public static void ModifiersUp(Modifiers modifiers)
    {
        Span<InputRecord> batch = stackalloc InputRecord[ModifierKeys.Length];
        var count = 0;

        for (var i = ModifierKeys.Length - 1; i >= 0; i--)
        {
            var (flag, scanCode, extended) = ModifierKeys[i];
            if (modifiers.HasFlag(flag)) batch[count++] = Stroke(scanCode, extended, release: true);
        }

        Dispatch(batch[..count]);
    }

    // ---- Mouse -------------------------------------------------------------------

    public static void MouseButtonDown(MouseButton button) => Dispatch(Click(button, pressed: true));

    public static void MouseButtonUp(MouseButton button) => Dispatch(Click(button, pressed: false));

    private static InputRecord Click(MouseButton button, bool pressed) => button switch
    {
        MouseButton.Right => Pointer(pressed ? RightPress : RightRelease),
        MouseButton.Middle => Pointer(pressed ? MiddlePress : MiddleRelease),
        MouseButton.X1 => Pointer(pressed ? SidePress : SideRelease, SideButton1),
        MouseButton.X2 => Pointer(pressed ? SidePress : SideRelease, SideButton2),
        _ => Pointer(pressed ? LeftPress : LeftRelease),
    };

    /// <summary>
    /// Turns the wheel, one notch per event. Positive is towards the screen.
    ///
    /// <b>Separate events, not one large one.</b> Sending three notches as a single message of
    /// 3 x 120 is not what the hardware does and not what applications expect: plenty of them
    /// treat any wheel message as one step and ignore its magnitude, so three notches in one
    /// message scrolled once while three real clicks of the wheel scrolled three times. The
    /// two have to be indistinguishable, because the whole point of the assignment is to be a
    /// wheel.
    ///
    /// Sent as one batch, so they arrive together and in order rather than racing anything
    /// else being injected.
    /// </summary>
    public static void Wheel(int notches)
    {
        if (notches == 0) return;

        var step = unchecked((uint)(Math.Sign(notches) * NotchDelta));
        var count = Math.Min(Math.Abs(notches), MaxNotches);

        Span<InputRecord> batch = stackalloc InputRecord[count];
        for (var i = 0; i < count; i++) batch[i] = Pointer(WheelTurn, step);

        Engine.MacroTrace.Write($"wheel  injecting {count} notch(es) {(notches < 0 ? "down" : "up")}"
                              + $"  ({NotchDelta} each, one event per notch)");
        Dispatch(batch);
    }

    /// <summary>
    /// Ceiling on one assignment's worth of scrolling.
    ///
    /// The count comes from a text box, and a stray keystroke turning 3 into 300 should cost a
    /// long scroll rather than a stack allocation sized by whatever was typed.
    /// </summary>
    private const int MaxNotches = 64;

    // ---- Auto-repeat timing ------------------------------------------------------

    private const uint SpiKeyboardSpeed = 0x000A;
    private const uint SpiKeyboardDelay = 0x0016;

    /// <summary>Repeats per second at SPI_GETKEYBOARDSPEED 0, the slowest setting.</summary>
    private const double SlowestRepeatRate = 2.5;

    /// <summary>
    /// Repeats per second at SPI_GETKEYBOARDSPEED 31, the fastest setting.
    ///
    /// Measured rather than taken from the documentation. Microsoft describes the ceiling as
    /// "approximately 30 repetitions per second", but a real keyboard held down on this
    /// machine at speed 31 repeats every 30-32 ms, so about 32 a second. Using the documented
    /// 30 leaves a remapped key visibly lagging a real one held beside it.
    /// </summary>
    private const double FastestRepeatRate = 32.0;

    /// <summary>
    /// The user's own auto-repeat delay and interval.
    ///
    /// An injected press does not repeat by itself: Windows generates repeats from the
    /// physical keyboard, not from the input queue, so a held remap has to reproduce them.
    /// Reading the machine's own settings is what makes a remapped key repeat at the rate
    /// every other key on it does.
    /// </summary>
    public static (TimeSpan Delay, TimeSpan Interval) RepeatTiming()
    {
        // The delay setting is 0-3, meaning 250 ms to 1000 ms in even steps. Measured at
        // 510 ms against a setting of 1, which confirms the mapping.
        var delayStep = SystemParametersInfo(SpiKeyboardDelay, 0, out var storedDelay, 0) ? storedDelay : 1;
        var delay = TimeSpan.FromMilliseconds(250 * (Math.Min(delayStep, 3u) + 1));

        // The speed setting is 0-31. Windows does not publish the curve between its
        // endpoints, so this interpolates: exact at either end, approximate in the middle.
        var speedStep = SystemParametersInfo(SpiKeyboardSpeed, 0, out var storedSpeed, 0) ? storedSpeed : 31;
        var rate = SlowestRepeatRate
                 + (Math.Min(speedStep, 31u) * (FastestRepeatRate - SlowestRepeatRate) / 31.0);

        return (delay, TimeSpan.FromSeconds(1.0 / rate));
    }

    // ---- Typing ------------------------------------------------------------------

    /// <summary>
    /// Gap left between characters while typing.
    ///
    /// Measured, not guessed. Typing into another application from an ELEVATED process
    /// corrupts below roughly 10 ms per character: the right number of characters arrive but
    /// runs of them come out as the wrong one, so "this is a test" lands as "this eeeeeeest".
    /// The same code typing the same string into the same Notepad from an unelevated process
    /// is perfect at any speed, including no gap at all - which is what made it slow to find,
    /// since it cannot be reproduced without elevation.
    ///
    /// tools/DejaVu.TypeProbe measures it, four rounds per interval into a real Notepad:
    ///
    ///     3 ms  0/4 clean      10 ms  4/4 clean
    ///     5 ms  3/4 clean      14 ms  4/4 clean
    ///     8 ms  3/4 clean      20 ms  4/4 clean
    ///
    /// 15 ms is chosen for margin over that boundary rather than for speed. It is still an
    /// order of magnitude quicker than a fast typist, and it applies only to text - a macro's
    /// key steps are unaffected, so nothing timing-sensitive is slowed by it.
    ///
    /// The mechanism inside Windows is not established here, only the boundary. If this needs
    /// revisiting, re-run the probe rather than reasoning about it.
    /// </summary>
    private static readonly TimeSpan TypingInterval = TimeSpan.FromMilliseconds(15);

    /// <summary>Enter, for the newlines that Unicode injection cannot express.</summary>
    private const ushort EnterScanCode = 0x1C;

    /// <summary>
    /// Types a string as direct Unicode, so it arrives identical whatever keyboard layout is
    /// active - accents and emoji included.
    ///
    /// One character per pass with a gap between, and that pacing is load bearing. Unicode
    /// injection works by sending VK_PACKET, and the receiving application resolves which
    /// character that was when it PROCESSES the message rather than when it is queued. Send a
    /// whole string at once and it outruns the target's message pump: the first few arrive
    /// correctly and everything after resolves to the last character of the batch, so "this is
    /// a test" comes out as "this ttttttttt" - right length, wrong contents. The gap stops the
    /// tail of a batch overwriting the queue before the target has read the front of it.
    /// </summary>
    public static async Task TypeAsync(string text, CancellationToken token = default,
                                       TimeSpan? interval = null)
    {
        if (string.IsNullOrEmpty(text)) return;

        var gap = interval ?? TypingInterval;

        foreach (var character in text)
        {
            token.ThrowIfCancellationRequested();

            // A carriage return would only duplicate the newline beside it.
            if (character is '\r') continue;

            SendCharacter(character);

            await Task.Delay(gap, token).ConfigureAwait(false);
        }
    }

    /// <summary>Press and release for one character, kept out of the async method so it can stackalloc.</summary>
    private static void SendCharacter(char character)
    {
        Span<InputRecord> pair = stackalloc InputRecord[2];

        // A newline has to go through the real Enter key. Injected as Unicode it is simply
        // not interpreted by most applications.
        if (character is '\n')
        {
            pair[0] = Stroke(EnterScanCode, extended: false, release: false);
            pair[1] = Stroke(EnterScanCode, extended: false, release: true);
        }
        else
        {
            pair[0] = Character(character, release: false);
            pair[1] = Character(character, release: true);
        }

        Dispatch(pair);
    }
}
