using System.Runtime.InteropServices;

using DejaVu.Core.Engine;

namespace DejaVu.Core.Hardware;

/// <summary>
/// Reads the n52's keys, d-pad and thumb buttons straight from MI_00 over WinUSB.
///
/// WHY. A keyboard filter driver was the obvious approach and is the wrong one: filters bind
/// keyboard stacks at boot, so a pad plugged in or re-enumerated afterwards is invisible to
/// them, and every binding silently stops working until a reboot with no error anywhere.
/// Reading the interface directly removes that failure mode, and the need for a kernel
/// driver with it.
///
/// WHAT IT COSTS. Once MI_00 is bound to WinUSB the pad is no longer a keyboard to Windows,
/// so nothing happens at all unless this application is running — an unbound key no longer
/// falls through to the system, it simply does not exist. The engine has to synthesise
/// every keystroke, including the ones the user never remapped.
///
/// WHAT IT BUYS BEYOND RELIABILITY. There is nothing to suppress. A filter races the system
/// to consume a keystroke before it escapes; here the raw input never reaches Windows
/// at all, so the engine decides what exists. Several awkward behaviors — swallowing both
/// arrows of an assigned diagonal, stray keys leaking on the way into a corner — stop being
/// necessary.
///
/// <see cref="Open"/> returns false when MI_00 has not been rebound, which the engine
/// reports as the device being unavailable. `driver/install.ps1` does the rebind.
/// </summary>
public sealed class N52KeyboardDevice : IDisposable
{
    /// <summary>
    /// Device interface GUID declared by driver/n52winusb.inf for MI_00. Distinct from the
    /// MI_01 interface, so the two halves of the pad are addressed separately.
    /// </summary>
    public const string InterfaceGuid = "{0F2D9A41-7C63-4E58-9B10-8E4D2F6A5C37}";

    private nint _file = -1;
    private nint _winUsb;
    private byte _pipe;
    private int _reportLength = HidKeyboard.BootReportLength;

    /// <summary>Polling interval of the interrupt endpoint, in milliseconds.</summary>
    private byte _interval;
    private Thread? _reader;
    private volatile bool _running;

    /// <summary>Keys currently reported down, so only changes are raised.</summary>
    private readonly HashSet<(ushort ScanCode, bool Extended)> _down = [];

    public bool IsOpen => _winUsb != 0;

    /// <summary>Raised when a key goes down or comes up: scancode, extended flag, state.</summary>
    public event Action<ushort, bool, bool>? KeyChanged;

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
    /// Opens MI_00. Returns false when the interface is absent, which is the normal state
    /// on a machine where only the MI_01 half was rebound.
    /// </summary>
    public bool Open()
    {
        if (IsOpen) return true;

        var path = Native.FindInterfacePath(new Guid(InterfaceGuid));
        if (path is null) return false;

        _file = Native.CreateFile(path, Native.GenericRead | Native.GenericWrite,
            Native.ShareRead | Native.ShareWrite, nint.Zero, Native.OpenExisting,
            Native.FlagOverlapped, nint.Zero);

        if (_file == -1) return false;

        if (!Native.WinUsb_Initialize(_file, out _winUsb) || !FindInterruptPipe())
        {
            Close();
            return false;
        }

        // Without a timeout a read blocks until a key is pressed, which would keep the
        // reader thread alive past Dispose and hang shutdown.
        var timeout = 100u;
        Native.WinUsb_SetPipePolicy(_winUsb, _pipe, Native.PipeTransferTimeout, sizeof(uint), ref timeout);

        ReportOnlyOnChange();

        return true;
    }

    /// <summary>
    /// Locates the interrupt IN endpoint rather than assuming one. MI_00 is interface 0 and
    /// its pipe is almost certainly 0x81, but querying costs nothing and does not go wrong
    /// on a device that numbers its endpoints differently.
    /// </summary>
    private bool FindInterruptPipe()
    {
        if (!Native.WinUsb_QueryInterfaceSettings(_winUsb, 0, out var descriptor)) return false;

        for (byte index = 0; index < descriptor.NumEndpoints; index++)
        {
            if (!Native.WinUsb_QueryPipe(_winUsb, 0, index, out var pipe)) continue;
            if (pipe.PipeType != Native.PipeTypeInterrupt) continue;
            if ((pipe.PipeId & Native.PipeDirectionIn) == 0) continue;

            _pipe = pipe.PipeId;
            if (pipe.MaximumPacketSize > 0) _reportLength = pipe.MaximumPacketSize;

            // The endpoint's own polling interval, in milliseconds. This is the floor on how
            // quickly a press can possibly be noticed, and it is the device's choice, not
            // ours - worth reporting before blaming anything in software for feeling slow.
            _interval = pipe.Interval;
            return true;
        }

        return false;
    }

    public void StartReading()
    {
        if (!IsOpen || _running) return;

        _running = true;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "n52 keys" };
        _reader.Start();
    }

    /// <summary>
    /// Asks the pad to send a report only when something actually changes.
    ///
    /// HID's SET_IDLE with a duration of zero. Windows' own HID driver issues this to every
    /// keyboard it owns, which is why the pad behaves that way for everyone else - but over
    /// WinUSB we own the interface and nobody sends it, so the device falls back to its
    /// power-on default and streams its state on every poll. Measured here: a report every
    /// 48 ms, indefinitely, 35,662 of them saying "no keys are down".
    ///
    /// The device is free to refuse - SET_IDLE is optional, and a device that does not
    /// support it stalls the request. That costs nothing: the diff in Report already treats a
    /// repeated report as no news, so refusing only means the traffic stays.
    /// </summary>
    private void ReportOnlyOnChange()
    {
        if (!Native.WinUsb_QueryInterfaceSettings(_winUsb, 0, out var descriptor)) return;

        var setup = new Native.SetupPacket
        {
            RequestType = 0x21,   // host to device, class request, recipient is the interface
            Request = 0x0A,       // SET_IDLE
            Value = 0x0000,       // duration 0 (report on change only), for every report id
            Index = descriptor.InterfaceNumber,
            Length = 0,
        };

        var accepted = Native.WinUsb_ControlTransfer(_winUsb, setup, [], 0, out _, nint.Zero);

        // Once, at open. Worth recording: whether the pad took it decides whether the reader
        // thread wakes on every poll or only when something moves, and the two are otherwise
        // indistinguishable from a trace that only prints changes.
        MacroTrace.Write($"keys open: pipe 0x{_pipe:X2}, {_reportLength}-byte reports, "
                       + $"polled every {_interval} ms; SET_IDLE "
                       + (accepted ? "accepted" : $"refused, error {Marshal.GetLastWin32Error()}"));
    }

    private void ReadLoop()
    {
        var buffer = new byte[Math.Max(_reportLength, HidKeyboard.BootReportLength)];

        while (_running)
        {
            if (!Native.WinUsb_ReadPipe(_winUsb, _pipe, buffer, (uint)_reportLength, out var read, nint.Zero))
            {
                // A timeout just means no key changed state.
                if (Marshal.GetLastWin32Error() is Native.ErrorSemTimeout or Native.ErrorOperationAborted) continue;

                // Anything else means the device went away.
                break;
            }

            if (read >= HidKeyboard.BootReportLength) Report(buffer);
        }

        // Still wanted, yet out of the loop: the read failed rather than Dispose asking us
        // to stop, so the pad is gone rather than being put down.
        if (_running) Lost?.Invoke();
    }

    /// <summary>
    /// Turns one report into key changes.
    ///
    /// A HID keyboard reports STATE, not events: each report lists everything currently
    /// held. Diffing successive reports is what produces presses and releases — and it is
    /// also why this path is immune to the auto-repeat problem that plagued the filtered
    /// path, where a held key arrives as an endless stream of presses. Here a held key
    /// simply keeps appearing in the report, and nothing changes.
    /// </summary>
    private void Report(byte[] report)
    {
        var current = new HashSet<(ushort ScanCode, bool Extended)>();

        var modifiers = report[0];
        for (var bit = 0; bit < HidKeyboard.Modifiers.Length; bit++)
        {
            if ((modifiers & (1 << bit)) != 0) current.Add(HidKeyboard.Modifiers[bit]);
        }

        for (var i = HidKeyboard.FirstKeyIndex; i < HidKeyboard.BootReportLength; i++)
        {
            var usage = report[i];

            // 0 is an empty slot; 1-3 are the rollover and error codes, which name no key.
            if (usage <= 0x03) continue;

            if (HidKeyboard.Translate(usage) is { } key) current.Add(key);
        }

        // Nothing changed: no events, and nothing to say about it.
        //
        // The early return is not only tidiness. A report arrives on every poll whether or not
        // anything moved, and the trace below writes to disk synchronously, on this thread -
        // the one thread whose whole job is to notice the pad promptly. Logging every report
        // put twenty file opens a second in front of the reads and made the pad feel slow.
        if (current.SetEquals(_down)) return;

        // The bytes as they arrived, behind the same switch as the macro trace. The diff below
        // is only ever as good as what the device sent, and a switch that chatters under a
        // resting thumb looks identical, from above, to a diff that has gone wrong.
        if (MacroTrace.Enabled)
        {
            MacroTrace.Write($"HID {Convert.ToHexString(report, 0, HidKeyboard.BootReportLength)}"
                           + $"  keys=[{string.Join(",", current.Select(k => k.ScanCode.ToString("X2")))}]");
        }

        foreach (var key in _down.Except(current).ToList()) KeyChanged?.Invoke(key.ScanCode, key.Extended, false);
        foreach (var key in current.Except(_down).ToList()) KeyChanged?.Invoke(key.ScanCode, key.Extended, true);

        _down.Clear();
        foreach (var key in current) _down.Add(key);
    }

    private void Close()
    {
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

    public void Dispose()
    {
        _running = false;
        _reader?.Join(TimeSpan.FromSeconds(1));
        _reader = null;

        // Release anything still held, or the key stays down with the reader gone.
        foreach (var key in _down) KeyChanged?.Invoke(key.ScanCode, key.Extended, false);
        _down.Clear();

        Close();
    }
}
