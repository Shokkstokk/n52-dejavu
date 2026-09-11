using System.Diagnostics;
using DejaVu.Core.Hardware;
using DejaVu.Core.Input;
using DejaVu.Core.Model;

namespace DejaVu.Core.Engine;

/// <summary>
/// Keys for status messages. The engine holds no display text of its own: it emits a
/// key, and the presentation layer resolves it against the string table.
/// </summary>
public static class StatusKey
{
    /// <summary>
    /// Neither USB interface could be opened: the pad is unplugged, or driver/install.ps1
    /// has never been run on this machine.
    /// </summary>
    public const string DeviceUnavailable = "DeviceUnavailable";

    /// <summary>
    /// One interface opened and the other did not. Neither is a failure - the half that
    /// opened works normally - but neither is silence: the engine reports itself started
    /// while a whole set of the pad's controls quietly does nothing, and with MI_01 down
    /// that includes the lamps the eight keymaps are read from.
    /// </summary>
    public const string LedsUnavailable = "LedsUnavailable";
    public const string KeysUnavailable = "KeysUnavailable";

    public const string Started = "EngineStarted";
    public const string Stopped = "EngineHalted";

    /// <summary>
    /// The pad was open and has gone. Distinct from <see cref="DeviceUnavailable"/>, which
    /// is the state of never having found it: this one says something changed just now, and
    /// that the engine is waiting for it to come back rather than needing anything done.
    /// </summary>
    public const string DeviceLost = "DeviceLost";
    public const string Error = "ErrorMessage|";
    public const string LaunchFailed = "LaunchFailed|";
}

/// <summary>
/// What is currently down, and what has to happen when it comes up.
///
/// A press can leave three things behind: an assignment whose release still has to be sent,
/// a macro looping for as long as the control is held, and the timer reproducing a key's
/// auto-repeat. All three end at the same moment, so they are tracked as one thing rather
/// than as three collections that could fall out of step.
/// </summary>
internal sealed class Engaged
{
    /// <summary>What was sent, and therefore what has to be undone.</summary>
    public required Assignment Sent { get; init; }

    /// <summary>Stops a macro that loops while the control is held.</summary>
    public CancellationTokenSource? Loop { get; set; }

    /// <summary>Stops the timer standing in for Windows' own key repeat.</summary>
    public CancellationTokenSource? Repeat { get; set; }
}

/// <summary>
/// The remapping engine.
///
/// Reads the pad directly over WinUSB - both interfaces, keys and wheel - and injects
/// whatever the active profile says each input should send. Windows never sees the pad at
/// all, so an input this engine does not act on produces nothing unless it is replayed by
/// hand; there is no underlying signal carrying on behind it.
/// </summary>
public sealed class PadRuntime : IDisposable
{
    private readonly LiveSettings _settings;
    private readonly ActiveApp _frontApp = new();

    private readonly Lock _lock = new();

    /// <summary>
    /// Serializes injected keystrokes against the release that ends them.
    ///
    /// Without this, a repeat that has already passed its cancellation check can land a
    /// KeyDown *after* End has sent the KeyUp, leaving the key held with nothing left to
    /// release it. On a modifier that is not a cosmetic bug: a stuck Ctrl turns Escape into
    /// Ctrl+Escape and takes the foreground window with it.
    /// </summary>
    private readonly Lock _send = new();
    private readonly Dictionary<string, Engaged> _engaged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _looping = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _padDown = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _padSending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pad inputs currently driving a keymap action, tracked apart from <see cref="_padSending"/>.
    ///
    /// They cannot live in that set. Switching keymap runs ReleaseEverything, which empties
    /// it - so by the time the direction is let go there is nothing left to diff the release
    /// against. A Hold on a d-pad direction therefore never reverted and behaved as a plain
    /// "switch to", and every auto-repeat of a held direction looked like a fresh press,
    /// which would have cycled the keymaps about thirty times a second.
    ///
    /// Cleared only by <see cref="DrivePad"/>, from the pad's own resolved state.
    /// </summary>
    private readonly HashSet<string> _padModes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pad tiles currently reported as lit, so an unchanged state emits nothing.</summary>
    private readonly HashSet<string> _padShown = new(StringComparer.OrdinalIgnoreCase);


    /// <summary>
    /// Inputs physically down right now, so the engine acts on changes rather than events.
    ///
    /// The device auto-repeats a held key roughly every 30 ms, and every repeat arrives as
    /// another press. Acting on each one runs Begin again: Remember replaces the Engaged
    /// and StartRepeat starts a second repeat loop, leaving the first with no reachable
    /// cancellation token and no way to ever stop. A second of holding orphans dozens of
    /// immortal loops, and the release can only cancel the last. It also re-fires the
    /// one-shot actions — a held Launch key would start the program over and over.
    /// </summary>
    private readonly HashSet<string> _pressed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The middle-button flag, which is how the wheel click arrives from MI_01.
    /// Kept as a constant so the two code paths cannot drift apart.
    /// </summary>
    private const ushort WheelClickFlag = 0x0010;

    /// <summary>
    /// LED pattern per mode: every combination the three lamps can show, in a deliberate
    /// order — dark, then each color alone, then the pairs, then all three.
    ///
    /// Public because the editor draws the same lamps beside each mode button, and two
    /// copies of this table would eventually disagree.
    /// </summary>
    public static readonly LedState[] ModeLeds =
    [
        LedState.Off,                                    // 1
        LedState.Red,                                    // 2
        LedState.Green,                                  // 3
        LedState.Blue,                                   // 4
        LedState.Red | LedState.Green,                   // 5
        LedState.Red | LedState.Blue,                    // 6
        LedState.Green | LedState.Blue,                  // 7
        LedState.Red | LedState.Green | LedState.Blue,   // 8
    ];

    /// <summary>
    /// Scancodes of the modifier keys, left and right hands alike. Used both to suppress
    /// auto-repeat and to force-release them when the engine stops.
    /// </summary>
    private static readonly ushort[] ModifierScanCodes =
    [
        0x1D, // Ctrl, either side
        0x2A, // Left Shift
        0x36, // Right Shift
        0x38, // Alt, either side
        0x5B, // Left Win
        0x5C, // Right Win
    ];

    private N52UsbDevice? _usb;
    private N52KeyboardDevice? _keys;
    private int _mode;

    /// <summary>Mode to fall back to when a held mode-shift is released.</summary>
    private int _modeBeforeHold = -1;

    /// <summary>
    /// The control currently holding a keymap down, so its release can be recognized no
    /// matter which keymap it moved us into. See <see cref="Apply"/>.
    /// </summary>
    private string? _holdButton;

    /// <summary>
    /// Set for as long as the application wants the pad open, which is its whole lifetime.
    ///
    /// Distinct from actually being open. There is no way for a user to switch the engine
    /// off - the application running IS the engine running - so this only ever goes false
    /// on the way out, and everything between is the reconnect loop's business.
    /// </summary>
    private volatile bool _wanted;

    /// <summary>Serializes teardown against the reconnect loop.</summary>
    private readonly Lock _connection = new();

    /// <summary>How long to wait between attempts to find a pad that is not there.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    /// <summary>Last status reported, so a pad that stays missing says so once.</summary>
    private string? _reported;

    /// <summary>Set while the pad is suspended. See <see cref="Paused"/>.</summary>
    private volatile bool _quiet;

    public PadRuntime(LiveSettings config)
    {
        _settings = config;
        _frontApp.Changed += _ =>
        {
            // Let go of everything before the assignments change underneath us. A key held
            // across a profile switch would otherwise be released against the NEW profile's
            // binding — which may be Passthrough, in which case the release returns early
            // and the old action is never ended. That is a stuck key.
            ReleaseEverything();
            ProfileChanged?.Invoke(ActiveProfile?.Name ?? string.Empty);
        };
    }

    /// <summary>
    /// True while at least one of the pad's two interfaces is open.
    ///
    /// Derived from the handles rather than tracked, because a tracked flag is exactly what
    /// used to go stale: a reader thread could die on an unplug and leave a boolean claiming
    /// everything was fine.
    /// </summary>
    public bool Connected => _keys is not null || _usb is not null;

    /// <summary>
    /// Suspends the pad without releasing the device: presses are still reported, so the
    /// editor can light up whichever control was pressed, but nothing is sent - neither the
    /// assignment nor the pad's own key. Used while the editor has focus.
    /// </summary>
    public bool Paused
    {
        get => _quiet;

        set
        {
            var entering = value && !_quiet;
            _quiet = value;

            // Letting go on the way in, not on the way out, and only on the edge. Whatever
            // was held when the pad went quiet can no longer be released by its own key-up -
            // the press that would have ended it is about to start being swallowed - so it
            // happens here or never. Coming back out there is nothing left to let go of.
            if (entering) ReleaseEverything();
        }
    }

    /// <summary>Status message key, for the interface to resolve and display.</summary>
    public event Action<string>? Status;

    /// <summary>Raised when the active profile changes.</summary>
    public event Action<string>? ProfileChanged;

    /// <summary>Raised on every press and release on the n52, for the live tile highlight.</summary>
    public event Action<string, bool>? ButtonActivity;

    /// <summary>Raised when the active mode changes, with the new zero-based index.</summary>
    public event Action<int>? ModeChanged;

    /// <summary>
    /// The active mode, zero-based. Assignments are per mode and per profile, so this
    /// selects which of the profile's four maps is live.
    /// </summary>
    public int CurrentMode => _mode;

    /// <summary>
    /// Switches mode and updates the state LEDs.
    ///
    /// The LEDs are the entire point of modes: the editor is invisible behind a fullscreen
    /// game, so the pad itself has to say which layer is live. When the device is not on
    /// WinUSB the lamps are unavailable and this quietly does nothing but change state.
    /// </summary>
    public void SetMode(int mode)
    {
        if (mode < 0 || mode >= Profile.ModeCount || mode == _mode) return;

        // Anything held belongs to the outgoing mode's assignments, and its release would
        // be looked up in the incoming mode. Let go first.
        ReleaseEverything();

        _mode = mode;
        _usb?.SetLeds(ModeLeds[mode]);
        ModeChanged?.Invoke(mode);
    }

    /// <summary>
    /// The profile the pad is obeying: whichever one claims the application in front, and
    /// the fallback when none does.
    ///
    /// There is deliberately no manual pin. A pinned profile outranking the foreground match
    /// would defeat the behavior people expect from this feature - the pad following the
    /// application - and leave them with a pad stuck on the wrong layout and no obvious sign
    /// why. Read on every input event, so it stays short.
    /// </summary>
    public Profile? ActiveProfile
    {
        get
        {
            var settings = _settings.Current;

            return Claimed(settings) ?? Fallback(settings);
        }
    }

    /// <summary>The profile naming the application in front, when switching is on and one does.</summary>
    private Profile? Claimed(Configuration settings)
    {
        if (!settings.AutoSwitch) return null;

        var front = _frontApp.Current;
        if (string.IsNullOrEmpty(front)) return null;

        foreach (var profile in settings.Profiles)
        {
            // Guarded rather than assumed away: this runs on the pad's own thread, where an
            // exception has nowhere to go and takes the process with it.
            if (profile is null) continue;

            foreach (var claimed in profile.Processes)
            {
                if (string.Equals(claimed, front, StringComparison.OrdinalIgnoreCase)) return profile;
            }
        }

        return null;
    }

    /// <summary>
    /// What is used when nothing claimed the foreground: the chosen fallback, and failing that
    /// whatever comes first - which is the controller default, and cannot be deleted.
    /// </summary>
    private static Profile? Fallback(Configuration settings)
        => settings.Find(settings.DefaultProfileId)
           ?? settings.Profiles.FirstOrDefault(profile => profile is not null);

    /// <summary>
    /// Takes the pad and keeps it, for as long as the application is running.
    ///
    /// Called once, at startup. A missing pad is not a failure to report and give up on: it
    /// is a pad that has not been plugged in yet, so this returns normally either way and
    /// the reconnect loop goes on trying. That is the whole of what replaced the engine's
    /// on/off switch - there is nothing left for a user to press, because there is no state
    /// they could put it in that it will not reach on its own.
    /// </summary>
    public void Start()
    {
        if (_wanted) return;
        _wanted = true;

        // Ask for 1 ms timer resolution while the engine runs.
        //
        // Windows' default is 15.6 ms, and Task.Delay cannot do better than the system
        // timer: a 31 ms repeat interval lands on the next tick at ~46 ms, so a held key
        // repeats at roughly two thirds of the intended rate and unevenly with it. The
        // release is in Stop, because leaving the resolution raised costs battery.
        TimeBeginPeriod(1);

        lock (_connection) Connect();
        Report();

        new Thread(KeepConnected) { IsBackground = true, Name = "n52 connect" }.Start();
    }

    /// <summary>Opens whichever halves are there. Neither one is required.</summary>
    private void Connect()
    {
        OpenWheel();
        OpenKeys();
    }

    /// <summary>
    /// Reopens the pad whenever it is not open. Runs until <see cref="Stop"/>.
    ///
    /// Polling, rather than WM_DEVICECHANGE, and deliberately: device notifications need a
    /// window and a message pump, which would put hardware plumbing in the interface layer
    /// for something that costs nothing here. While the pad is connected this thread does
    /// nothing but sleep - the reader threads are blocked inside WinUsb_ReadPipe - and it
    /// only does any work at all in the state where there is no work to be had.
    /// </summary>
    private void KeepConnected()
    {
        while (_wanted)
        {
            Thread.Sleep(RetryInterval);
            if (!_wanted || Connected) continue;

            lock (_connection)
            {
                if (!_wanted || Connected) continue;
                Connect();
            }

            Report();
        }
    }

    /// <summary>
    /// Says what the pad is doing, once per change.
    ///
    /// The once matters: with the reconnect loop running every two seconds, reporting
    /// unconditionally would repaint the status bar with the same sentence for as long as
    /// the pad stayed unplugged.
    /// </summary>
    private void Report()
    {
        var key =
            !Connected ? StatusKey.DeviceUnavailable
            : !KeysOnWinUsb ? StatusKey.KeysUnavailable
            : !WheelOnWinUsb ? StatusKey.LedsUnavailable
            : StatusKey.Started;

        if (key == _reported) return;

        _reported = key;
        Status?.Invoke(key);
    }

    /// <summary>
    /// One of the reader threads found its device gone.
    ///
    /// Both halves are closed, not just the one that died. An unplug takes both, and the
    /// case where genuinely one interface fails on its own is not worth a second code path -
    /// closing both and reopening whatever is there gets to the same place.
    ///
    /// The work happens off the reader's own thread. Closing a device joins its reader, and
    /// a thread cannot join itself.
    /// </summary>
    private void OnDeviceLost() => _ = Task.Run(() =>
    {
        lock (_connection)
        {
            if (!Connected) return;

            ReleaseEverything();
            Close();

            _reported = StatusKey.DeviceLost;
            Status?.Invoke(StatusKey.DeviceLost);
        }
    });

    /// <summary>Lets go of both interfaces. Disposing the wheel turns the lamps off.</summary>
    private void Close()
    {
        _keys?.Dispose();
        _keys = null;

        _usb?.Dispose();
        _usb = null;
    }

    /// <summary>
    /// Raises and restores the system timer resolution. Task.Delay is bounded by it, so a
    /// repeat interval finer than the default 15.6 ms is unachievable without this.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);

    /// <summary>
    /// Opens the WinUSB half of the device: the wheel, and the three state LEDs.
    ///
    /// Independent of the keyboard half, because the two interfaces are rebound separately
    /// and either can be present without the other.
    /// </summary>
    private void OpenWheel()
    {
        if (_usb is not null) return;

        var usb = new N52UsbDevice();
        if (!usb.Open())
        {
            usb.Dispose();
            return;
        }

        usb.WheelMoved += OnWheelMoved;
        usb.WheelClicked += OnWheelClicked;
        usb.Lost += OnDeviceLost;
        usb.StartReading();
        _usb = usb;

        // Show the current mode straight away, rather than leaving the lamps dark until
        // the first switch.
        usb.SetLeds(ModeLeds[_mode]);
    }

    /// <summary>
    /// Opens the keys, d-pad and thumb buttons over WinUSB, when MI_00 has been rebound.
    ///
    /// Optional in exactly the way the wheel is: if MI_00 is still an ordinary HID keyboard
    /// this finds nothing and the keys are simply unavailable. One
    /// build serves both, and the pad can be rebound either way without a different binary.
    /// </summary>
    private void OpenKeys()
    {
        if (_keys is not null) return;

        var keys = new N52KeyboardDevice();
        if (!keys.Open())
        {
            keys.Dispose();
            return;
        }

        keys.KeyChanged += OnUsbKeyChanged;
        keys.Lost += OnDeviceLost;
        keys.StartReading();
        _keys = keys;
    }

    /// <summary>
    /// A key read straight from MI_00 over WinUSB.
    ///
    /// Windows never sees these, so there is nothing to suppress and nothing to race: an
    /// unassigned key has to be replayed by hand, exactly as an unbound wheel notch is.
    /// </summary>
    private void OnUsbKeyChanged(ushort scanCode, bool extended, bool isDown)
    {
        var button = PadLayout.Find(PadSignal.Key(scanCode, extended));
        if (button is null) return;

        if (Apply(button, isDown)) return;

        // Not consumed: reproduce the original keystroke, since Windows never saw it.
        //
        // Routed through Begin/End rather than sent directly, so an unassigned key behaves
        // like any other held key: it auto-repeats, it is released on pause or stop, and it
        // cannot be left down. Sending KeyDown here by hand would give a key that types once
        // however long it is held, because Windows only generates repeats for keys it can
        // see - and it cannot see this one.
        var replay = new Assignment { Kind = ActionKind.Key, ScanCode = scanCode, Extended = extended };

        if (isDown) Begin(button.Id, replay);
        else End(button.Id);
    }

    /// <summary>True when the keyboard interface is open.</summary>
    /// <summary>
    /// Whether MI_00 is open, which is what decides if the keys, d-pad and thumb controls
    /// can be remapped at all. False means they are still an ordinary keyboard to Windows,
    /// typing Tab, Q, W and so on past the engine entirely.
    /// </summary>
    public bool KeysOnWinUsb => _keys is not null;

    /// <summary>
    /// Whether MI_01 is open, which is what decides if the wheel reports and whether the
    /// three state lamps can be driven at all. Null while the engine is stopped, because
    /// stopping disposes the device - and disposing it turns the lamps off - so anything
    /// drawing lamps can read this alone rather than also asking whether we are running.
    /// </summary>
    public bool WheelOnWinUsb => _usb is not null;

    /// <summary>
    /// Lets the pad go. Called on the way out and nowhere else - closing the application is
    /// the only thing that stops the engine.
    /// </summary>
    public void Stop()
    {
        if (!_wanted) return;

        // Down goes the flag before anything is taken apart: the reconnect loop tests it,
        // and must not reopen the device behind the teardown it is racing with.
        _wanted = false;

        lock (_connection)
        {
            ReleaseEverything();
            TimeEndPeriod(1);
            Close();
        }

        _reported = StatusKey.Stopped;
        Status?.Invoke(StatusKey.Stopped);
    }

    /// <summary>
    /// Runs one input through the active profile. Returns true when the signal was
    /// consumed and must not reach the system.
    ///
    /// Every input converges here: the keys, thumb controls and pad from MI_00, the wheel
    /// and its click from MI_01. Returning false means "not consumed", and the caller is
    /// then responsible for reproducing the pad's own signal - Windows never saw it, so
    /// nothing else will.
    /// </summary>
    private bool Apply(PadInput button, bool isDown)
    {
        var pad = button.Group == ButtonGroup.ThumbPad;

        // Tile reporting runs even while paused: pressing inputs to see which tile is which
        // is the whole point of the editor. The pad reports its own resolved state rather
        // than its raw arrows — see TrackPad.
        if (pad) TrackPad(button, isDown);
        else ButtonActivity?.Invoke(button.Id, isDown);

        // Swallowed, not passed on. Returning false here used to mean "not ours", which
        // with a filter driver let the hardware signal through - the only option there. Over
        // WinUSB nothing reaches Windows unless we send it, so false makes the caller replay
        // the pad's own key instead: pressing button 01 in the editor typed a Tab, which
        // moves focus, and the d-pad sent arrows, which change the selected item in whatever
        // dropdown was focused. A setting whose job is to stop the engine and the editor
        // fighting was doing the fighting. The press is still reported above, so shapes still
        // light up while paused; it just goes nowhere else.
        //
        // PRESSES ONLY. A release has to be let through even while paused, because a release
        // can only ever finish something a press already started - and swallowing one leaves
        // that behind for good. The case that found this: the wheel click is pressed while
        // unbound and unpaused, so a real middle-button down is injected; the click lands on
        // the editor, which pauses the engine; the release is then swallowed and no
        // middle-button up is ever sent. Windows goes on believing that button is held, which
        // leaves the desktop in drag state and the machine barely usable until the process is
        // killed. The same hole swallows the release of a held key, modifiers included.
        //
        // Letting a release through costs nothing when there was nothing to finish: an
        // unmatched key-up or button-up is a no-op, and it is the press, not the release, that
        // moved focus or changed a dropdown - which is what pausing exists to prevent.
        if (_quiet && isDown) return true;

        var action = ActiveProfile?.GetBinding(_mode, button.Id) ?? Assignment.None();

        // The pad is resolved as a whole, before any per-input check: a diagonal's action
        // is stored on the diagonal, so an arrow that looks unassigned may still be half of
        // an assigned diagonal. DrivePad is edge-triggered on its own held set.
        if (pad) return DrivePad(button);

        // A held keymap is released by the control that started it, whatever the keymap it
        // moved us into has to say about that control.
        //
        // Resolved here, before the binding is read, because by release time _mode is the
        // keymap being held and the lookup below returns a different assignment entirely:
        // usually the "step to the next keymap" that keymap 1 writes across the others, and
        // sometimes nothing at all - which returns as passthrough before any release is
        // processed. Either way the hold never reverted and the pad stayed where it went.
        //
        // Keymap switches used to live on the profile, so one object answered from every
        // keymap and this fell out for free. Holding them per keymap is what makes it need
        // saying out loud.
        if (!isDown && _holdButton == button.Id)
        {
            lock (_lock) _pressed.Remove(button.Id);
            ReleaseHold();
            return true;
        }

        // Passthrough is decided before the edge guard below, because an unassigned key
        // must keep repeating exactly as the hardware sends it — including its repeats.
        if (action.EffectiveKind == ActionKind.Passthrough) return false;

        // From here the signal is consumed. Note that ActionKind.None also lands here:
        // silencing an input is a deliberate choice, so it must be swallowed, not passed.
        //
        // Act on the edge, not the event. The device auto-repeats a held key, and every
        // repeat arrives here as another press: acting on each one starts a repeat loop per
        // repeat, cycles a mode switch dozens of times a second, and re-runs one-shot
        // actions. A repeat is swallowed — the key IS consumed — but drives nothing.
        if (isDown)
        {
            bool firstPress;
            lock (_lock) firstPress = _pressed.Add(button.Id);
            if (!firstPress) return true;
        }
        else
        {
            lock (_lock) _pressed.Remove(button.Id);
        }

        // Mode switching is handled here rather than in Begin, because it changes which
        // assignments Begin would even be reading. Any input can carry it, arrows included.
        if (action.Kind == ActionKind.Mode)
        {
            HandleModeSwitch(button.Id, action, isDown);

            // A wheel rotation reports one impulse and never a release, so this branch has to
            // finish the press itself - the momentary handling further down is past the return
            // above and never runs for a keymap action.
            //
            // Two things go wrong without it. The edge guard keeps the input in _pressed for
            // good, so the first notch switches keymap and every notch after it is swallowed
            // as a repeat. And a Hold latches: _modeBeforeHold stays set with no release ever
            // coming, which stops every LATER hold, on any control, from recording where to
            // return to - one flick of the wheel and holds stop working pad-wide.
            //
            // Releasing it here makes Hold on a wheel rotation a no-op, which is honest: an
            // input with no held state cannot hold anything.
            if (isDown && button.IsMomentary)
            {
                if (_holdButton == button.Id) ReleaseHold();
                lock (_lock) _pressed.Remove(button.Id);
            }

            return true;
        }

        if (isDown)
        {
            Begin(button.Id, action);

            // A momentary input reports the impulse and nothing else — no release will
            // ever arrive to run End. Complete it here, or a bound key stays down for
            // good: KeyDown with no KeyUp, a mouse button with no release, a while-held
            // macro looping forever. Harmless for the impulse actions (wheel, text,
            // launch, one-shot macro), which register nothing to release, and for a
            // toggle macro, which lives in its own collection and is unaffected.
            if (button.IsMomentary)
            {
                End(button.Id);
                lock (_lock) _pressed.Remove(button.Id);
            }
        }
        else
        {
            End(button.Id);
        }

        return true;
    }

    /// <summary>
    /// Applies a <see cref="ActionKind.Mode"/> action.
    ///
    /// Cycle and Select fire on the press and ignore the release. Hold is a shift key: it
    /// takes effect on the press and reverts on the release, so a mode can be borrowed for
    /// the length of one keystroke without leaving the pad in a state you have to
    /// remember to undo.
    /// </summary>
    private void HandleModeSwitch(string buttonId, Assignment action, bool isDown)
    {
        MacroTrace.Write($"ModeSwitch {buttonId} {action.ModeSwitch} target={action.ModeIndex} "
                       + $"down={isDown} mode={_mode} before={_modeBeforeHold} hold={_holdButton ?? "-"}");

        switch (action.ModeSwitch)
        {
            case ModeSwitch.Cycle:
                if (isDown) SetMode((_mode + 1) % Profile.ModeCount);
                return;

            case ModeSwitch.Select:
                if (isDown) SetMode(action.ModeIndex);
                return;

            case ModeSwitch.Hold:
                if (isDown)
                {
                    // Remember where to go back to, and which control to take it from, but
                    // only for the outermost hold: a second hold pressed while the first is
                    // down must not overwrite either.
                    if (_modeBeforeHold < 0)
                    {
                        _modeBeforeHold = _mode;
                        _holdButton = buttonId;
                    }

                    SetMode(action.ModeIndex);
                }
                else
                {
                    ReleaseHold();
                }

                return;
        }
    }

    /// <summary>Returns to the keymap a hold was entered from, if one is being held.</summary>
    private void ReleaseHold()
    {
        if (_modeBeforeHold < 0) return;

        var previous = _modeBeforeHold;
        _modeBeforeHold = -1;
        _holdButton = null;

        SetMode(previous);
    }

    /// <summary>
    /// A wheel notch, read straight from the device over WinUSB. Positive is up.
    ///
    /// Once MI_01 is on WinUSB the wheel is no longer a system mouse, so an unbound notch
    /// has to be replayed by hand — otherwise scrolling would simply stop working outside
    /// the assignments.
    /// </summary>
    private void OnWheelMoved(int direction)
    {
        var signal = direction > 0 ? PadSignal.WheelUp : PadSignal.WheelDown;
        var button = PadLayout.Find(signal);
        if (button is null) return;

        if (!Apply(button, isDown: true)) Injector.Wheel(direction);
    }

    /// <summary>The wheel click, likewise read over WinUSB and replayed when unbound.</summary>
    private void OnWheelClicked(bool isDown)
    {
        var button = PadLayout.Find(PadSignal.Mouse(WheelClickFlag));
        if (button is null) return;

        if (Apply(button, isDown)) return;

        if (isDown) Injector.MouseButtonDown(MouseButton.Middle);
        else Injector.MouseButtonUp(MouseButton.Middle);
    }

    /// <summary>
    /// The steps a macro binding plays, from the shared library.
    ///
    /// A binding pointing at a macro that has since been deleted resolves to nothing and is
    /// silent. The input is still consumed, because the user did assign something to it, and
    /// letting it turn back into a live keystroke mid-game because a macro went missing
    /// would be worse than doing nothing.
    /// </summary>
    private IReadOnlyList<MacroStep> StepsFor(Assignment action)
        => _settings.Current.FindMacro(action.MacroId)?.Steps ?? [];

    private bool IsAssigned(Profile? profile, string buttonId)
        => (profile?.GetBinding(_mode, buttonId).EffectiveKind ?? ActionKind.Passthrough) != ActionKind.Passthrough;

    /// <summary>
    /// True when this input is one of the two arrows behind an assigned diagonal.
    ///
    /// The pad has no diagonal switch: up-left is up plus left, closing tens to hundreds of
    /// milliseconds apart. There is therefore no moment at which "left, alone" can be told
    /// from "left, on the way into up-left", and acting on the first reading fires the
    /// arrow's own binding before the corner is even complete. It goes wrong on the way out
    /// as well - releasing one arrow leaves the other still held, which reads as a plain
    /// cardinal and fires it a second time. Measured on hardware: one roll into an assigned
    /// up-left typed the up binding, the corner, then the up binding again.
    ///
    /// This decides only whether the pad's own arrow key reaches Windows, and NOT whether the
    /// arrow's assignment runs. It used to decide both, and that was wrong: with all four
    /// corners bound it left every cardinal direction dead, so half of a roll around the pad
    /// did nothing at all and read as dropped input. The trailing half of the problem it was
    /// written for - an arrow firing again as the corner breaks up - is handled by the latch
    /// in ResolvePad instead, which holds the diagonal until both arrows are up.
    ///
    /// What remains is one stray cardinal on the way IN to a corner, and that is unavoidable:
    /// at the moment the first switch closes there is nothing to say whether the second is
    /// coming. Anyone who wants corners with no leading arrow can leave the cardinals
    /// unassigned, and then this swallows them and nothing is sent at all.
    /// </summary>
    private bool ClaimedByDiagonal(Profile? profile, string buttonId)
        => PadLayout.Combinations.Any(
            c => c.Components!.Contains(buttonId, StringComparer.OrdinalIgnoreCase)
                 && IsAssigned(profile, c.Id));

    /// <summary>
    /// What the pad currently resolves to: either an assigned diagonal, or the individual
    /// arrows being held.
    ///
    /// The device has no diagonal signal — up-left arrives as up plus left — so this is
    /// derived from the set of arrows currently HELD. A diagonal wins over its two
    /// cardinals, but only when it carries a real assignment: a missing binding reads as
    /// Passthrough, and letting that win would swallow both arrows and fire nothing.
    /// </summary>
    private HashSet<string> ResolvePad(out PadInput? diagonal)
    {
        var profile = ActiveProfile;

        string[] held;
        lock (_lock) held = [.. _padDown];

        diagonal = PadLayout.Combinations.FirstOrDefault(
            c => c.Components!.All(held.Contains) && IsAssigned(profile, c.Id));

        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (diagonal is not null) resolved.Add(diagonal.Id);
        else foreach (var id in held) resolved.Add(id);

        return resolved;
    }

    /// <summary>
    /// Updates the held set and reports the pad's tiles.
    ///
    /// The pad reports resolved state, not raw arrows, and it is diffed against what is
    /// already lit so that an unchanged state emits nothing at all. That matters because
    /// holding a diagonal produces a stream of auto-repeat presses roughly every 30 ms:
    /// reporting per event would light the arrow and darken it again on every repeat,
    /// which reads as a flickering tile.
    /// </summary>
    private void TrackPad(PadInput button, bool isDown)
    {
        lock (_lock)
        {
            if (isDown) _padDown.Add(button.Id);
            else _padDown.Remove(button.Id);
        }

        var lit = ResolvePad(out _);

        string[] off, on;
        lock (_lock)
        {
            off = [.. _padShown.Except(lit)];
            on = [.. lit.Except(_padShown)];
            _padShown.Clear();
            foreach (var id in lit) _padShown.Add(id);
        }

        foreach (var id in off) ButtonActivity?.Invoke(id, false);
        foreach (var id in on) ButtonActivity?.Invoke(id, true);
    }

    /// <summary>
    /// Starts and stops the pad's actions to match what it now resolves to. Returns true
    /// when the signal was consumed.
    /// </summary>
    private bool DrivePad(PadInput button)
    {
        var profile = ActiveProfile;
        var resolved = ResolvePad(out var diagonal);

        // Keymap actions are ended from the pad's physical state, not from _padSending.
        string[] modeStop;
        lock (_lock)
        {
            modeStop = [.. _padModes.Except(resolved)];
            foreach (var id in modeStop) _padModes.Remove(id);
        }

        foreach (var id in modeStop)
            if (_holdButton == id)
                ReleaseHold();

        // Only assigned inputs drive anything; the rest pass their arrows through. An arrow
        // an assigned corner has claimed drives nothing of its own - see ClaimedByDiagonal,
        // which is what keeps a corner from typing its cardinals either side of itself.
        //
        // While paused, nothing is wanted at all. Apply turns pad PRESSES back at the door,
        // but a release has to be let through - it only ever finishes something a press
        // started, and swallowing one leaves that behind for good - and the pad resolves as a
        // whole, so a release changes the resolved set and can make something NEW desired.
        // Measured on hardware: rolling the thumb pad with the editor focused started
        // PAD_UP_LEFT, PAD_UP_RIGHT and PAD_DOWN_RIGHT in turn, each on the release of an
        // arrow, and typed all three into the window that pausing exists to protect. Presses
        // did nothing and releases fired, which is why it read as dropped input rather than
        // as the engine being asleep.
        var desired = _quiet
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                resolved.Where(id => IsAssigned(profile, id)),
                StringComparer.OrdinalIgnoreCase);

        string[] toStop, toStart;
        lock (_lock)
        {
            toStop = [.. _padSending.Except(desired)];
            toStart = [.. desired.Except(_padSending)];
            _padSending.Clear();
            foreach (var id in desired) _padSending.Add(id);
        }

        // Keymap actions are routed by hand here. Begin and End work through _engaged, which a
        // mode switch never enters, so a keymap action bound to a pad direction or a diagonal
        // used to reach this point and do nothing whatsoever.
        foreach (var id in toStop)
        {
            if (_holdButton == id) ReleaseHold();
            End(id);
        }

        MacroTrace.Write($"DrivePad {button.Id} mode={_mode} resolved=[{string.Join(",", resolved)}] "
                       + $"desired=[{string.Join(",", desired)}] start=[{string.Join(",", toStart)}] "
                       + $"stop=[{string.Join(",", toStop)}] diagonal={diagonal?.Id ?? "-"}");

        foreach (var id in toStart)
        {
            var action = profile?.GetBinding(_mode, id) ?? Assignment.None();

            if (action.Kind == ActionKind.Mode)
            {
                // Once per physical press. _padSending is wiped by the switch itself, so
                // without this guard a held direction re-fires on every auto-repeat.
                bool first;
                lock (_lock) first = _padModes.Add(id);

                if (first) HandleModeSwitch(id, action, isDown: true);
            }
            else
            {
                Begin(id, action);
            }
        }

        // Consume when a diagonal is driving, when this arrow has its own assignment, or when
        // a corner has claimed it. That last case is what stops the hardware's own arrow key
        // escaping to Windows while the corner is still being rolled into.
        return diagonal is not null
            || IsAssigned(profile, button.Id)
            || ClaimedByDiagonal(profile, button.Id);
    }

    private void Begin(string buttonId, Assignment action)
    {
        // EffectiveKind, not Kind: a disabled assignment falls into the None case and does
        // nothing, while keeping everything it was configured to do.
        switch (action.EffectiveKind)
        {
            // The two that outlive the press lead, because both have to file themselves
            // somewhere End can find them again.
            case ActionKind.Key:
                Injector.ModifiersDown(action.Modifiers);
                Injector.KeyDown(action.ScanCode, action.Extended);
                Remember(buttonId, action);
                StartRepeat(buttonId, action);
                break;

            case ActionKind.Macro:
                StartMacro(buttonId, action);
                break;

            case ActionKind.MouseButton:
                Injector.MouseButtonDown(action.MouseButton);
                Remember(buttonId, action);
                break;

            case ActionKind.MouseWheel:
                Injector.Wheel(action.WheelDelta);
                break;

            case ActionKind.Text:
                // Off the input thread: typing is paced now, so it takes as long as the text
                // is long, and blocking here would stall every other input on the pad.
                _ = Task.Run(() => Injector.TypeAsync(action.Text ?? string.Empty));
                break;

            case ActionKind.Launch:
                Spawn(action);
                break;

            // None and Passthrough fall off the end, and doing nothing here is the whole of
            // what each of them means.
        }
    }

    private void End(string buttonId)
    {
        Engaged? held;
        lock (_lock)
        {
            if (!_engaged.Remove(buttonId, out held)) return;
        }

        held.Loop?.Cancel();

        // Cancel and release under one lock, so a repeat cannot slip a KeyDown in behind
        // the KeyUp. Outside it, the key can be left held with nothing to release it.
        lock (_send)
        {
            held.Repeat?.Cancel();

            switch (held.Sent.Kind)
            {
                case ActionKind.Key:
                    Injector.KeyUp(held.Sent.ScanCode, held.Sent.Extended);
                    Injector.ModifiersUp(held.Sent.Modifiers);
                    break;

                case ActionKind.MouseButton:
                    Injector.MouseButtonUp(held.Sent.MouseButton);
                    break;
            }
        }
    }

    private void Remember(string buttonId, Assignment action)
    {
        CancellationTokenSource? orphan = null;

        lock (_lock)
        {
            // Replacing an entry would strand whatever the old one was running, leaving a
            // repeat loop with no reachable token — the failure that flooded a held key
            // with keystrokes that nothing could stop. The _pressed guard should prevent this
            // from ever happening; cancel anyway, because the cost of being wrong is a key
            // the user cannot release.
            if (_engaged.TryGetValue(buttonId, out var previous))
            {
                orphan = previous.Repeat;
                previous.Loop?.Cancel();
            }

            _engaged[buttonId] = new Engaged { Sent = action };
        }

        orphan?.Cancel();
    }

    /// <summary>
    /// Repeats a held key at the system's own rate.
    ///
    /// Windows generates auto-repeat from the physical keyboard, not from the input queue,
    /// so an injected KeyDown fires exactly once however long the input is held. Without
    /// this, holding a remapped key types one character where holding the real key would
    /// type a stream — the remap would feel different from the key it replaces.
    ///
    /// Only <see cref="ActionKind.Key"/> repeats. A real mouse button does not, and macros
    /// have their own <see cref="MacroRepeat"/> setting.
    /// </summary>
    private void StartRepeat(string buttonId, Assignment action)
    {
        // Never repeat a modifier. Modifier state is level-triggered, so repeating a held
        // Ctrl or Shift changes nothing for any application — it is all risk and no
        // benefit, and a modifier is the worst thing to leave stuck.
        if (ModifierScanCodes.Contains(action.ScanCode)) return;

        var (delay, interval) = Injector.RepeatTiming();
        var cancellation = new CancellationTokenSource();

        lock (_lock)
        {
            // Begin always Remembers before calling this, but a release racing in can have
            // removed the entry already: in that case the key is no longer held.
            if (!_engaged.TryGetValue(buttonId, out var held)) { cancellation.Dispose(); return; }
            held.Repeat = cancellation;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cancellation.Token);

                while (true)
                {
                    // The check and the send must be atomic against End. Testing the token
                    // outside the lock leaves a window in which the release happens between
                    // the check and the KeyDown, and the key is then held for good.
                    lock (_send)
                    {
                        if (cancellation.Token.IsCancellationRequested) return;

                        // A repeat is another KeyDown with no KeyUp, which is what the
                        // hardware does: the release still comes once, from End.
                        Injector.KeyDown(action.ScanCode, action.Extended);
                    }

                    await Task.Delay(interval, cancellation.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Released before or during a wait: the normal way this loop ends.
            }
        }, cancellation.Token);
    }

    private void StartMacro(string buttonId, Assignment action)
    {
        // Resolved once, here, and carried through the playback rather than looked up again
        // inside it. The library can be edited while a macro is looping, and a sequence that
        // changed length between two passes would leave keys pressed by the old version with
        // nothing left to release them.
        var steps = StepsFor(action);
        if (steps.Count == 0) return;

        MacroTrace.Write($"StartMacro {buttonId}  {steps.Count} steps  repeat={action.Repeat}  "
                       + $"held={_engaged.Count} toggled={_looping.Count} down={_pressed.Count}");

        switch (action.Repeat)
        {
            case MacroRepeat.Toggle:
                Toggle(buttonId, action, steps);
                return;

            case MacroRepeat.WhileHeld:
                Loop(buttonId, action, steps);
                return;

            default:
                // Nothing to remember: it finishes on its own, and there is no release to
                // wait for. Deliberately not awaited - the pad's read loop must not sit
                // behind a sequence that could contain a five-second wait.
                _ = Task.Run(() => RunMacro(steps, CancellationToken.None));
                return;
        }
    }

    /// <summary>
    /// One press starts the loop, the next stops it.
    ///
    /// The stop has to be recognized before anything else happens, and the removal and the
    /// cancellation have to be one step: two presses arriving together would otherwise both
    /// find a loop running, both cancel it, and leave a second loop started by neither.
    /// </summary>
    private void Toggle(string buttonId, Assignment action, IReadOnlyList<MacroStep> steps)
    {
        lock (_lock)
        {
            if (_looping.Remove(buttonId, out var already))
            {
                already.Cancel();
                return;
            }
        }

        var stop = new CancellationTokenSource();

        lock (_lock) _looping[buttonId] = stop;

        _ = Task.Run(() => RunMacroLoop(action, steps, stop.Token), stop.Token);
    }

    /// <summary>
    /// Runs for as long as the control is down.
    ///
    /// Recorded as engaged rather than as looping, because what ends it is the release of the
    /// control - and every release goes through End, which cancels whatever it finds there.
    /// </summary>
    private void Loop(string buttonId, Assignment action, IReadOnlyList<MacroStep> steps)
    {
        var stop = new CancellationTokenSource();

        lock (_lock) _engaged[buttonId] = new Engaged { Sent = action, Loop = stop };

        _ = Task.Run(() => RunMacroLoop(action, steps, stop.Token), stop.Token);
    }

    /// <summary>
    /// Plays a sequence over and over until it is told to stop.
    ///
    /// The finally is the important half. A loop can be canceled anywhere - between two
    /// steps, or in the middle of a wait - and a sequence that pressed a key down without
    /// reaching the step that lifts it would leave that key held with nothing left running to
    /// release it. Every exit runs the same cleanup, cancellation included.
    /// </summary>
    private static async Task RunMacroLoop(Assignment action, IReadOnlyList<MacroStep> steps, CancellationToken stopping)
    {
        var pause = action.RepeatDelayMs;

        try
        {
            while (true)
            {
                stopping.ThrowIfCancellationRequested();

                await RunMacro(steps, stopping);

                if (pause > 0) await Task.Delay(pause, stopping);
            }
        }
        catch (OperationCanceledException)
        {
            // Asked to stop. Not a failure, and the cleanup below runs either way.
        }
        finally
        {
            ReleaseMacroKeys(steps);
        }
    }

    private static async Task RunMacro(IReadOnlyList<MacroStep> steps, CancellationToken token)
    {
        // A press and a click each hold their target down for the step's own delay, and a
        // Delay step is that same wait with nothing held - one local covers all three. The
        // zero test earns its keep: Task.Delay(0) still yields, so a macro of forty instant
        // steps would hand the scheduler forty chances to put it down mid-sequence.
        async Task Hold(int ms)
        {
            if (ms > 0) await Task.Delay(ms, token);
        }

        MacroTrace.Write($"RunMacro enter  {steps.Count} steps");

        foreach (var step in steps)
        {
            token.ThrowIfCancellationRequested();

            switch (step.Kind)
            {
                case MacroStepKind.KeyPress:
                    Injector.ModifiersDown(step.Modifiers);
                    Injector.KeyDown(step.ScanCode, step.Extended);
                    await Hold(step.DelayMs);
                    Injector.KeyUp(step.ScanCode, step.Extended);
                    Injector.ModifiersUp(step.Modifiers);
                    break;

                case MacroStepKind.MouseClick:
                    Injector.MouseButtonDown(step.MouseButton);
                    await Hold(step.DelayMs);
                    Injector.MouseButtonUp(step.MouseButton);
                    break;

                case MacroStepKind.Text:
                {
                    var before = Injector.Dropped;
                    MacroTrace.Write($"  text in  \"{step.Text}\"");
                    await Injector.TypeAsync(step.Text ?? string.Empty, token);
                    MacroTrace.Write($"  text out \"{step.Text}\"  dropped={Injector.Dropped - before}");
                    break;
                }

                // The unpaired halves. Each leaves a key down for the rest of the sequence,
                // which is what ReleaseMacroKeys exists to sweep up afterwards.
                case MacroStepKind.KeyDown:
                    Injector.KeyDown(step.ScanCode, step.Extended);
                    break;

                case MacroStepKind.KeyUp:
                    Injector.KeyUp(step.ScanCode, step.Extended);
                    break;

                case MacroStepKind.Delay:
                    await Hold(step.DelayMs);
                    break;
            }
        }
    }

    /// <summary>
    /// Releases any key a macro may have left held. Without this, a macro interrupted
    /// mid-KeyDown leaves that key stuck down until the next restart.
    /// </summary>
    private static void ReleaseMacroKeys(IReadOnlyList<MacroStep> steps)
    {
        foreach (var step in steps.Where(s => s.Kind == MacroStepKind.KeyDown))
            Injector.KeyUp(step.ScanCode, step.Extended);
    }

    private void Spawn(Assignment action)
    {
        var target = action.Path;
        if (string.IsNullOrWhiteSpace(target)) return;

        // Through the shell, not executed directly: that is what lets the target be a
        // document or a URL rather than only a program, and what makes a shortcut resolve
        // to the thing it points at.
        var start = new ProcessStartInfo(target) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(action.Arguments)) start.Arguments = action.Arguments;

        try
        {
            Process.Start(start);
        }
        catch (Exception ex)
        {
            // Every way this goes wrong is the user's to fix and none of them is ours to die
            // on - a path deleted since it was chosen, a URL scheme with no handler, an
            // executable policy says no to. Name it in the status line and carry on.
            var detail = action.Path + '\u001f' + ex.Message;
            Status?.Invoke(StatusKey.LaunchFailed + detail);
        }
    }

    /// <summary>Releases everything currently held. Called on stop and on pause.</summary>
    private void ReleaseEverything()
    {
        // Copied out under the lock, acted on outside it. End takes the same lock, so
        // finishing them while still holding it would deadlock on the first one.
        string[] outstanding;
        CancellationTokenSource[] loops;

        lock (_lock)
        {
            outstanding = [.. _engaged.Keys];
            loops = [.. _looping.Values];

            _looping.Clear();
            _pressed.Clear();
            _padDown.Clear();
            _padShown.Clear();
            _padSending.Clear();
        }

        // Loops first. A macro still running would otherwise go on sending keys after the
        // control that started it has been let go.
        foreach (var loop in loops) loop.Cancel();
        foreach (var control in outstanding) End(control);

        // Safety net: release every modifier whether or not we think we are holding it.
        //
        // Releasing only what _engaged knows about is not enough, because the dangerous case
        // is precisely the one where a key was left down without the engine recording it —
        // a bug here leaves the user with a stuck Ctrl and no way to clear it from inside
        // the application. A redundant KeyUp for a key that is already up is harmless, so
        // this costs nothing and closes the whole class of failure.
        lock (_send)
        {
            foreach (var scanCode in ModifierScanCodes)
            {
                Injector.KeyUp(scanCode, extended: false);
                Injector.KeyUp(scanCode, extended: true);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _frontApp.Dispose();
    }
}
