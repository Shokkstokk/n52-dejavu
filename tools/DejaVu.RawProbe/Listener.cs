using System.Runtime.InteropServices;

using DejaVu.Core.Model;

namespace DejaVu.RawProbe;

/// <summary>
/// One decoded WM_INPUT message.
///
/// The probe never handles raw bytes: <see cref="Listener.Decode"/> turns a message straight
/// into this, already carrying the <see cref="PadSignal"/> the engine would recognize. That
/// is the whole point of the tool - the question being asked is "does this unit emit what
/// PadLayout claims", and the comparison is only meaningful if the probe builds its signals
/// the same way the engine does.
/// </summary>
/// <param name="Interface">Which USB interface it arrived on, as MI_00 or MI_01.</param>
/// <param name="Path">The full device interface path, for identifying the device.</param>
/// <param name="Signal">What the engine would call this, or null when nothing matches.</param>
/// <param name="Released">True for a key-up or button-up rather than a press.</param>
/// <param name="Detail">Human-readable description, for the unmatched log.</param>
internal sealed record Observation(
    string Interface,
    string Path,
    PadSignal? Signal,
    bool Released,
    string Detail);

/// <summary>
/// Raw Input, reduced to the one question this tool asks.
///
/// Raw Input rather than anything that hooks or filters: it observes every HID collection a
/// device puts up, including the ones a keyboard or mouse filter never sees, and it cannot
/// alter or swallow anything. For a measuring instrument that is exactly the right trade -
/// the pad must behave here precisely as it behaves with the probe closed.
/// </summary>
internal static class Listener
{
    public const int WmInput = 0x00FF;

    private const uint InputSink = 0x00000100;
    private const uint CommandInput = 0x10000003;
    private const uint CommandDeviceName = 0x20000007;

    private const uint KindMouse = 0;
    private const uint KindKeyboard = 1;

    /// <summary>
    /// Usage families to subscribe to.
    ///
    /// Generic Desktop mouse and keyboard cover everything this pad emits. System and consumer
    /// control are listened for as well, not because the n52 uses them but because a unit that
    /// reported somewhere unexpected would otherwise be invisible - and "nothing appeared" is
    /// the least useful answer a probe can give.
    /// </summary>
    private static readonly (ushort Page, ushort Usage)[] Families =
    [
        (0x01, 0x02),
        (0x01, 0x06),
        (0x01, 0x80),
        (0x0C, 0x01),
    ];

    [StructLayout(LayoutKind.Sequential)]
    private struct Subscription
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Window;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public uint Kind;
        public uint Size;
        public nint Device;
        public nint WParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(Subscription[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint message, uint command, nint buffer, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoW(nint device, uint command, nint buffer, ref uint size);

    private static readonly Dictionary<nint, string> Paths = [];

    /// <summary>
    /// Starts listening, without needing the window in front.
    ///
    /// The sink flag is what makes the readings trustworthy: the pad can be pressed while the
    /// probe sits behind whatever you are actually looking at, so nothing about the measurement
    /// depends on this window having focus.
    /// </summary>
    public static void Listen(nint window)
    {
        var subscriptions = new Subscription[Families.Length];

        for (var i = 0; i < Families.Length; i++)
        {
            subscriptions[i] = new Subscription
            {
                UsagePage = Families[i].Page,
                Usage = Families[i].Usage,
                Flags = InputSink,
                Window = window,
            };
        }

        if (!RegisterRawInputDevices(subscriptions, (uint)subscriptions.Length, (uint)Marshal.SizeOf<Subscription>()))
            throw new InvalidOperationException($"Raw Input refused the subscription (error {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>
    /// Turns one WM_INPUT into an <see cref="Observation"/>, or null when there is nothing
    /// worth reporting.
    ///
    /// Bare mouse movement is the case worth discarding: the pad's wheel and click arrive on
    /// the same collection as the pointer, so a probe that reported everything would bury one
    /// wheel notch under several hundred lines of travel.
    /// </summary>
    public static Observation? Decode(nint message)
    {
        var block = Read(message);
        if (block is null) return null;

        var headerSize = Marshal.SizeOf<Header>();
        if (block.Length < headerSize) return null;

        var kind = BitConverter.ToUInt32(block, 0);
        var device = (nint)BitConverter.ToInt64(block, 8);
        var path = PathOf(device);

        var face = path.Contains("MI_00", StringComparison.OrdinalIgnoreCase) ? "MI_00"
                 : path.Contains("MI_01", StringComparison.OrdinalIgnoreCase) ? "MI_01"
                 : "?";

        return kind switch
        {
            KindKeyboard => Keyboard(block, headerSize, face, path),
            KindMouse => Pointer(block, headerSize, face, path),
            _ => null,
        };
    }

    private static Observation Keyboard(byte[] block, int offset, string face, string path)
    {
        var code = BitConverter.ToUInt16(block, offset + 0);
        var flags = BitConverter.ToUInt16(block, offset + 2);
        var virtualKey = BitConverter.ToUInt16(block, offset + 6);

        var released = (flags & 0x01) != 0;
        var extended = (flags & 0x02) != 0;

        return new Observation(
            face, path,
            PadSignal.Key(code, extended),
            released,
            $"key 0x{code:X2}{(extended ? " E0" : "")}, vk 0x{virtualKey:X2}");
    }

    private static Observation? Pointer(byte[] block, int offset, string face, string path)
    {
        var buttons = BitConverter.ToUInt16(block, offset + 4);
        var wheel = (short)BitConverter.ToUInt16(block, offset + 6);

        if (buttons == 0) return null;

        // A rotation carries its delta in the same field a button uses for extra data, and is
        // told apart by the flag. Down and up are separate flags on the same button, so a
        // release is recognized by its flag rather than by anything in the payload.
        if ((buttons & 0x0400) != 0)
        {
            var up = wheel > 0;
            return new Observation(
                face, path,
                up ? PadSignal.WheelUp : PadSignal.WheelDown,
                Released: false,
                $"wheel {(up ? "up" : "down")} ({wheel:+#;-#;0})");
        }

        var press = (ushort)(buttons & 0x0155);
        var release = (ushort)(buttons & 0x02AA);

        return new Observation(
            face, path,
            press != 0 ? PadSignal.Mouse(press) : null,
            release != 0 && press == 0,
            $"button flags 0x{buttons:X4}");
    }

    private static byte[]? Read(nint message)
    {
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<Header>();

        if (GetRawInputData(message, CommandInput, nint.Zero, ref size, headerSize) != 0 || size == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(message, CommandInput, buffer, ref size, headerSize) != size) return null;

            var managed = new byte[size];
            Marshal.Copy(buffer, managed, 0, (int)size);
            return managed;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The device interface path, cached.
    ///
    /// Asked for once per device rather than once per event: the path never changes for a
    /// given handle, and a held key arrives every 30 milliseconds.
    /// </summary>
    private static string PathOf(nint device)
    {
        if (Paths.TryGetValue(device, out var known)) return known;

        uint size = 0;
        var path = string.Empty;

        if (GetRawInputDeviceInfoW(device, CommandDeviceName, nint.Zero, ref size) == 0 && size != 0)
        {
            var buffer = Marshal.AllocHGlobal((int)size * sizeof(char));
            try
            {
                if (GetRawInputDeviceInfoW(device, CommandDeviceName, buffer, ref size) != uint.MaxValue)
                    path = Marshal.PtrToStringUni(buffer) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        Paths[device] = path;
        return path;
    }
}
