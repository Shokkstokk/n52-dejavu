using System.Runtime.InteropServices;

using DejaVu.Core.Engine;

namespace DejaVu.Core.Hardware;

/// <summary>
/// Direct USB access to the n52's MI_01 interface, through Microsoft's inbox WinUSB driver.
///
/// WHY THIS EXISTS. MI_01 carries the scroll wheel and the three state LEDs. While it is
/// bound as a system mouse collection, Windows refuses to carry an output report to it from
/// user mode, so the LEDs cannot be driven at all — and no input filter driver can help,
/// because such a driver sends events into the input stream and has no channel back to the
/// device. Binding WinUSB to this one interface gets underneath that. The reasoning, and
/// the two user-mode routes that cannot work, are on <see cref="SetLeds"/>.
///
/// THE PRICE. MI_01 stops being a system mouse, so nothing else can read the wheel. This
/// class therefore owns BOTH halves: it reads wheel input from the interrupt pipe and writes
/// the LED state. MI_00 — the keys, d-pad and thumb inputs — is a separate interface, read
/// by <see cref="N52KeyboardDevice"/>.
///
/// Everything here is a no-op when the device is absent or still bound to HID; the engine
/// reports that as the device being unavailable.
/// </summary>
public sealed class N52UsbDevice : IDisposable
{
    /// <summary>
    /// Device interface GUID declared by driver/n52winusb.inf. Changing it here without
    /// changing it there means the device is simply never found.
    /// </summary>
    public const string InterfaceGuid = "{B8B0B1C4-6A31-4E8D-9A2F-5C7E1D3A9F60}";

    /// <summary>The interrupt IN pipe carrying mouse reports: 4-byte packets every 10 ms.</summary>
    private const byte WheelPipe = 0x82;

    /// <summary>Interface number of MI_01, used as wIndex on the control transfer.</summary>
    private const ushort InterfaceNumber = 1;

    private const int WheelPacketSize = 4;

    private nint _file = -1;
    private nint _winUsb;
    private Thread? _reader;
    private volatile bool _running;

    /// <summary>True when the device was found and opened.</summary>
    public bool IsOpen => _winUsb != 0;

    /// <summary>Raised for each wheel notch. Positive is up, negative is down.</summary>
    public event Action<int>? WheelMoved;

    /// <summary>Raised on press and release of the wheel click.</summary>
    public event Action<bool>? WheelClicked;

    /// <summary>
    /// Raised when the read loop ends on its own - the pad was unplugged, or the driver was
    /// rebound underneath us.
    ///
    /// Nothing upstream can detect this by looking: the handle is still non-null and the
    /// device still answers IsOpen, so without this the engine goes on reporting a working
    /// pad while the thread that reads it no longer exists.
    /// </summary>
    public event Action? Lost;

    /// <summary>
    /// Opens the device. Returns false when it is not present, which is the normal state
    /// on a machine where the WinUSB package was never installed.
    /// </summary>
    public bool Open()
    {
        if (IsOpen) return true;

        var path = Native.FindInterfacePath(new Guid(InterfaceGuid));
        if (path is null) return false;

        // FILE_FLAG_OVERLAPPED is required by WinUSB even though the reads below are
        // issued synchronously: the pipe timeout policy is what stops them blocking.
        _file = Native.CreateFile(path, Native.GenericRead | Native.GenericWrite,
            Native.ShareRead | Native.ShareWrite, nint.Zero, Native.OpenExisting,
            Native.FlagOverlapped, nint.Zero);

        if (_file == -1) return false;

        if (!Native.WinUsb_Initialize(_file, out _winUsb))
        {
            Native.CloseHandle(_file);
            _file = -1;
            _winUsb = 0;
            return false;
        }

        // Without a timeout a read blocks until the user touches the wheel, which would
        // keep the reader thread alive past Dispose and hang shutdown.
        var timeout = 100u;
        Native.WinUsb_SetPipePolicy(_winUsb, WheelPipe, Native.PipeTransferTimeout,
            sizeof(uint), ref timeout);

        return true;
    }

    /// <summary>
    /// Sets the three state LEDs: bit 0 red, bit 1 green, bit 2 blue. Silently does nothing
    /// when the device is not open.
    ///
    /// The lamps are host-driven. Firmware flashes them red-green-blue once at enumeration
    /// as a self-test and then leaves them dark forever unless something drives them, so
    /// every lit lamp on this pad is one this application put there.
    ///
    /// <para><b>Why a control transfer, and not either obvious route.</b> Both user-mode
    /// alternatives return ERROR_INVALID_FUNCTION, for two unrelated reasons, and neither is
    /// worth retrying:</para>
    /// <list type="bullet">
    ///   <item>HidD_SetOutputReport is refused outright - Windows will not carry an output
    ///   report from user mode to a collection it owns as a system mouse.</item>
    ///   <item>WriteFile cannot work at all. MI_01 has exactly one endpoint, an interrupt IN,
    ///   and there is no OUT endpoint to write to.</item>
    /// </list>
    /// <para>A control transfer was always the only possible route. Belkin reached the lamps
    /// with bcgame.sys, a kernel-mode HID minidriver bound as the device's own service; it is
    /// still in their 3.2.4 installer, but its WHQL catalog expired in 2006 and will not load
    /// against Secure Boot plus HVCI. Binding Microsoft's winusb.sys to MI_01 gets underneath
    /// the layer that refuses us, with no kernel code of our own and Memory Integrity left on.</para>
    ///
    /// <para>The report descriptor declares them on the HID LED page (0x08), usages 0x01-0x03,
    /// IsRange, BitField 0x02 - three independently settable bits. Bit map measured on
    /// hardware: 0x01 red, 0x02 green, 0x04 blue.</para>
    /// </summary>
    public bool SetLeds(LedState state)
    {
        if (!IsOpen) return false;

        // HID class request SET_REPORT, report type 2 (Output), report id 0.
        var setup = new Native.SetupPacket
        {
            RequestType = 0x21,
            Request = 0x09,
            Value = 0x0200,
            Index = InterfaceNumber,
            Length = 1,
        };

        return Native.WinUsb_ControlTransfer(_winUsb, setup, [(byte)state], 1, out _, nint.Zero);
    }

    /// <summary>Starts reading wheel input on a background thread.</summary>
    public void StartReading()
    {
        if (!IsOpen || _running) return;

        _running = true;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "n52 wheel" };
        _reader.Start();
    }

    private void ReadLoop()
    {
        var buffer = new byte[WheelPacketSize];
        var clickHeld = false;

        while (_running)
        {
            if (!Native.WinUsb_ReadPipe(_winUsb, WheelPipe, buffer, WheelPacketSize, out var read, nint.Zero))
            {
                // A timeout is the normal case: the wheel is simply not moving.
                if (Marshal.GetLastWin32Error() is Native.ErrorSemTimeout or Native.ErrorOperationAborted) continue;

                // Anything else means the device went away — a replug, or the driver being
                // uninstalled underneath us. Stop rather than spin on a dead handle.
                break;
            }

            if (read < WheelPacketSize) continue;

            // Boot-protocol mouse report: buttons, dx, dy, wheel.
            var click = (buffer[0] & 0x04) != 0;
            if (click != clickHeld)
            {
                clickHeld = click;
                MacroTrace.Write($"wheel  click {(click ? "down" : "up")}");
                WheelClicked?.Invoke(click);
            }

            var delta = (sbyte)buffer[3];
            if (delta != 0)
            {
                // The wheel is on the other interface and had no trace of its own, which made
                // "did the pad send that, or did we?" unanswerable from the log.
                MacroTrace.Write($"wheel  hardware notch {Math.Sign(delta):+0;-0}  (raw {delta})");
                WheelMoved?.Invoke(Math.Sign(delta));
            }
        }

        // Same reasoning as the keyboard half: falling out while still wanted means the
        // device went away, and nobody finds that out unless we say so.
        if (_running) Lost?.Invoke();
    }

    public void Dispose()
    {
        _running = false;
        _reader?.Join(TimeSpan.FromSeconds(1));
        _reader = null;

        // Leave the lamps dark rather than stuck on whatever mode was last active.
        if (IsOpen) SetLeds(LedState.Off);

        if (_winUsb != 0)
        {
            Native.WinUsb_Free(_winUsb);
            _winUsb = 0;
        }

        if (_file != -1)
        {
            Native.CloseHandle(_file);
            _file = -1;
        }
    }
}

/// <summary>
/// The three lamps, as the bits the device expects. Measured on hardware: the LED usages
/// sit on HID page 0x08, usages 0x01-0x03, and map to red, green and blue in that order.
/// </summary>
[Flags]
public enum LedState : byte
{
    Off = 0x00,
    Red = 0x01,
    Green = 0x02,
    Blue = 0x04,
}
