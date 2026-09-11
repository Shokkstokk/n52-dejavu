using System.Threading;
using System.Windows;
using DejaVu.Core.Config;
using DejaVu.Core.Engine;
using DejaVu.App.Localization;

namespace DejaVu.App;

public partial class App : System.Windows.Application
{
    /// <summary>Held for the life of the process; released only on the way out.</summary>
    private Mutex? _onlyCopy;

    /// <summary>Where the configuration is read from and written back to.</summary>
    private SettingsFile _store = null!;

    /// <summary>The live configuration, shared with the engine so a save is seen at once.</summary>
    private LiveSettings _settings = null!;

    /// <summary>The thing that actually reads the pad and sends what it decides.</summary>
    private PadRuntime _engine = null!;

    /// <summary>The application's permanent presence. Outlives the window.</summary>
    private StatusIcon _tray = null!;

    /// <summary>Built on demand, and forgotten again when closed. Null most of the time.</summary>
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The string table has to be in place before the first label is resolved,
        // including the "already running" message box below.
        Strings.Load();

        // The palette. Loaded with the default first, because the stored choice is inside the
        // configuration and the configuration cannot be read until the single-instance check
        // below has passed - and the "already running" message box has to be painted with
        // something. Re-applied from the configuration once it is loaded.
        // Before anything is shown. These are resolved as DynamicResource from every style in
        // App.xaml, so a window built before they exist would render at WPF's own defaults.
        Typography.Apply(Typography.SystemSize);

        Theme.Load(Theme.Default);

        // One instance at a time: two engines cannot both hold the pad open over WinUSB,
        // and the second would find the device unavailable and report it as such.
        //
        // Waited for rather than tested once, because switching "Run as administrator"
        // starts a replacement and then shuts this copy down - and the replacement can get
        // here first. Going up, the UAC prompt happens to delay it long enough; going down,
        // the shell returns instantly, so the new copy arrived while the old one still held
        // this, declared itself a duplicate and exited. Both then quit and nothing was left
        // running. A few seconds of patience costs nothing and closes the whole class.
        _onlyCopy = new Mutex(initiallyOwned: false, "Local\\n52-dejavu");

        var acquired = false;

        try
        {
            acquired = _onlyCopy.WaitOne(TimeSpan.FromSeconds(8));
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died without releasing. The mutex is ours regardless, and
            // whatever it was protecting died with it.
            acquired = true;
        }

        if (!acquired)
        {
            MessageBox.Show(Strings.Get("AlreadyRunning"), "n52 DejaVu",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Must be set before loading: it is the load that creates the default profile
        // when no configuration exists yet.
        Core.Model.Configuration.DefaultProfileName = Strings.Get("DefaultProfileName");

        // Macro playback trace, off unless asked for. It earned its place finding the
        // elevated typing corruption, and the next macro problem will want it again, but it
        // writes to disk on every step so it stays behind an environment variable:
        //     setx N52_MACRO_TRACE 1
        if (Environment.GetEnvironmentVariable("N52_MACRO_TRACE") is not null)
            Core.Engine.MacroTrace.Begin(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "n52-dejavu", "macro-trace.log"));

        _store = new SettingsFile();
        var configuration = _store.Load();

        // Now that the stored choice is available. A no-op when it is the default or unset,
        // and Load falls back on its own if the name belongs to a theme this build dropped.
        Theme.Apply(configuration.Theme, configuration.Themes);

        // The one place the stored text size is applied. It belongs to the application rather
        // than to a theme, so nothing in Theme touches it and switching theme cannot move it.
        Typography.Apply(configuration.FontSize > 0 ? configuration.FontSize : Typography.SystemSize);

        _settings = new LiveSettings(configuration);
        _engine = new PadRuntime(_settings);

        _tray = new StatusIcon(_engine);
        _tray.OpenRequested += ShowEditor;
        _tray.QuitRequested += Quit;

        // If the executable has moved or been renamed since the startup task was created,
        // the task is still registered but points at nothing. Fix it before that becomes a
        // silent failure to start at the next logon.
        LaunchAtLogon.RepairIfStale(configuration.RunElevated);

        _engine.Start();

        // On a first run nothing is configured yet: open the editor rather than leaving a
        // mute icon sitting in the notification area.
        if (_settings.Current.Profiles.Sum(p => p.Modes.Sum(m => m.Count)) == 0) ShowEditor();
    }

    /// <summary>
    /// Brings the editor up, building it the first time and whenever it has been closed.
    ///
    /// Discarded on close rather than hidden. The window is the larger half of this
    /// application by far - twenty-seven traced shapes, twenty-five assignment rows, six
    /// theme palettes - and someone who closes it has said they are done configuring. Keeping
    /// all of that resident for the rest of a session that may last days buys a fraction of a
    /// second the next time it opens.
    /// </summary>
    private void ShowEditor()
    {
        if (_window is null)
        {
            _window = new MainWindow(_store, _settings, _engine);
            _window.Closed += (_, _) => _window = null;
        }

        // Minimized counts as open, so Show alone would leave it on the taskbar with nothing
        // on screen - and the tray menu would look as though it had done nothing at all.
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;

        _window.Show();
        _window.Activate();
    }

    /// <summary>
    /// Leaves the cleanup to <see cref="OnExit"/> rather than doing it here first.
    ///
    /// This used to dispose the engine and the icon itself and THEN call Shutdown, which
    /// raises OnExit, which disposes both again - and the second pass reached a
    /// CancellationTokenSource the first had already disposed, so cancelling it threw on
    /// the interface thread in the middle of shutting down.
    /// </summary>
    private void Quit() => Shutdown();

    protected override void OnExit(ExitEventArgs e)
    {
        // Safety net: if the application dies by any route other than the tray menu, the
        // device still has to be released.
        //
        // Order matters when a replacement is already waiting: the pad has to be let go
        // before the mutex is, or the copy taking over acquires the mutex and then finds the
        // device still held. Released explicitly rather than left to Dispose, which abandons
        // it and hands the next waiter an AbandonedMutexException.
        _engine?.Dispose();
        _tray?.Dispose();

        try { _onlyCopy?.ReleaseMutex(); }
        catch (ApplicationException) { /* not the owner: nothing to release */ }

        _onlyCopy?.Dispose();
        base.OnExit(e);
    }
}
