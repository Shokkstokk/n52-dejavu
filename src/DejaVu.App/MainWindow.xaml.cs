using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;

using DejaVu.App.Localization;
using DejaVu.Core.Config;
using DejaVu.Core.Engine;
using DejaVu.Core.Hardware;
using DejaVu.Core.Input;
using DejaVu.Core.Model;

// MouseButton exists in System.Windows.Input too, and both namespaces are imported
// here. In this file it always means the model's version: the button the n52 should
// emit, not the one the user clicked.
using MouseButton = DejaVu.Core.Model.MouseButton;

namespace DejaVu.App;

/// <summary>
/// The editor window. Split across several files by subject rather than by kind - the rows
/// either side of the device, the selection set, the three libraries, the macro tab and the
/// theme tab each own their own partial - and this one holds construction, the assignment
/// panel, the settings strip and the engine controls.
/// </summary>
public partial class MainWindow : Window
{
    private readonly SettingsFile _store;
    private readonly LiveSettings _accessor;
    private readonly PadRuntime _engine;

    /// <summary>How long a momentary input's tile stays lit. See <see cref="Flash"/>.</summary>
    private static readonly TimeSpan FlashDuration = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// How long Detect waits before reading the foreground application.
    ///
    /// Long enough to alt-tab to the program being named and let it settle, short enough
    /// that nobody wonders whether the button worked.
    /// </summary>
    private static readonly TimeSpan DetectionGrace = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Width of the invisible band that widens every control's click target, in artwork units.
    ///
    /// Drawn at the shape's own size the D-Pad arrows are pointed and awkward to hit. This is
    /// applied as a transparent stroke, which WPF hit-tests, so the catchment reaches half of
    /// this beyond the outline while the drawing is untouched. Raising it much past the gap
    /// between two neighboring arms would let their bands overlap, and a click in the overlap
    /// goes to whichever was added last rather than the nearer shape.
    /// </summary>
    private const double HitMargin = 20;

    /// <summary>Every drawn control, by id: the canvas the tiles are lit on.</summary>
    private readonly Dictionary<string, System.Windows.Shapes.Path> _shapes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The three lamps, paired with the shape that draws each one.</summary>
    private readonly List<(LedState Lamp, System.Windows.Shapes.Path Shape)> _leds = [];

    /// <summary>Timers holding a momentary input's tile lit. See <see cref="Flash"/>.</summary>
    private readonly Dictionary<string, DispatcherTimer> _flashing = new(StringComparer.OrdinalIgnoreCase);

    // Bound straight to the lists in XAML rather than reloaded, so an edit made anywhere
    // reaches the screen without a refresh call to forget.
    private readonly ObservableCollection<Profile> _profiles = [];
    private readonly ObservableCollection<MacroStep> _steps = [];
    private readonly ObservableCollection<string> _processes = [];

    private Configuration _config;
    private Profile? _profile;

    /// <summary>
    /// The mode being shown, zero-based. Mirrors the engine's live mode rather than being
    /// a separate editor state, so the window and the pad's lamps always agree.
    /// </summary>
    private int _mode;

    /// <summary>True while the interface is being populated, so input events are ignored.</summary>
    private bool _loading;

    public MainWindow(SettingsFile store, LiveSettings accessor, PadRuntime engine)
    {
        InitializeComponent();

        // Nothing that happens during construction may trigger a save: the collections are
        // still empty, and a ComboBox bound to a CollectionView selects an item by itself
        // the moment it is given a source.
        _loading = true;

        (_store, _accessor, _engine) = (store, accessor, engine);
        _config = accessor.Current;

        LstProfiles.ItemsSource = _profiles;
        CboProfile.ItemsSource = _profiles;
        LstProcesses.ItemsSource = _processes;
        LstSteps.ItemsSource = _steps;
        LstMacros.ItemsSource = _macros;

        LstThemes.ItemsSource = _themes;
        Theme.Changed += RepaintFromTheme;

        // The labels come from the ItemTemplate declared in XAML: setting DisplayMemberPath
        // as well would make WPF throw during construction.
        CboRepeat.ItemsSource = RepeatChoices;
        CboScrollDirection.ItemsSource = ScrollDirections;
        CboModeSwitch.ItemsSource = ModeSwitchChoices;
        CboModeTarget.ItemsSource = ModeChoices;

        // The catalog is presented grouped by category: that is the only way to reach
        // F13 through F24, which exist on no physical keyboard and so can never be
        // captured by pressing them.
        var keys = new ListCollectionView(KeyList.All.ToList());
        keys.GroupDescriptions!.Add(new PropertyGroupDescription(nameof(KeyEntry.Category)));

        // Same reason as the row dropdowns: this selection is driven from the binding, so
        // the view's CurrentItem must not be allowed to move it. See MainWindow.Rows.cs.
        CboKey.IsSynchronizedWithCurrentItem = false;
        CboKey.ItemsSource = keys;

        // Open on whatever the engine is already running, not always on mode 1.
        _mode = _engine.CurrentMode;

        BuildModeSelector();
        BuildShapes();
        BuildLeds();
        BuildAssignRows();
        BuildThemeTargets();
        LoadConfiguration();
        LoadThemes();

        // Status fires whenever the engine starts or stops, including from the tray menu
        // while this window is open. Refreshing only from the button below meant the two
        // could disagree: the tray would start the engine and the window would go on
        // offering to start it, next to the words "engine stopped".
        // BeginInvoke everywhere below, never Invoke. These are raised on the thread that
        // reads the pad, and Invoke blocks it until the interface thread has finished the
        // work - so every press waited on WPF before the next read could be posted, and the
        // endpoint's next poll was missed. Measured: reports arriving every 48 ms against an
        // endpoint that offers one every 24. The pad felt slow because it was being read at
        // half the rate the hardware allows, and only while the window was open.
        _engine.Status += message => Dispatcher.BeginInvoke(() =>
        {
            // The toolbar already says connected or not. The sentence behind that - which
            // half of the device is missing, what to re-run - hangs off it as a tooltip
            // rather than on a line of its own at the opposite corner of the window.
            TxtEngineState.ToolTip = new ToolTip
            {
                MaxWidth = 380,
                Content = new TextBlock { Text = TranslateStatus(message), TextWrapping = TextWrapping.Wrap },
            };
            RefreshEngineState();

            // Starting and stopping both change whether the lamps can be driven at all.
            RefreshLeds();
        });
        _engine.ButtonActivity += (id, down) => Dispatcher.BeginInvoke(() => Highlight(id, down));

        // A mode switch on the pad has to move the editor with it: the tiles, the action
        // panel and the selector all describe one mode, and that mode is the engine's.
        _engine.ModeChanged += mode => Dispatcher.BeginInvoke(() =>
        {
            _mode = mode;
            RefreshModeSelector();
            LoadAction();
            RefreshTiles();
        });

        // The engine re-picks the profile whenever the foreground application changes, so
        // the LIVE marker has to follow it rather than only being set when we save.
        _engine.ProfileChanged += _ => Dispatcher.BeginInvoke(RefreshLiveProfile);

        // The options panel keeps itself open, so something has to close it. A press
        // anywhere in the window does - except on the button that owns it, which toggles and
        // would otherwise be closed here and reopened there. Presses INSIDE the panel never
        // arrive: a Popup's content is its own visual tree and does not route to this window.
        PreviewMouseDown += (_, _) =>
        {
            if (PopOptions.IsOpen && !BtnOptions.IsMouseOver) PopOptions.IsOpen = false;
        };

        // Clicking away from the application entirely counts too, or the panel would sit on
        // top of whatever the user switched to.
        Deactivated += (_, _) => PopOptions.IsOpen = false;

        RestorePlacement();

        // Whole-application activation, not this window's. A modal dialog deactivates its
        // owner, so hanging this off the window unpaused the engine for as long as the
        // "press a key" prompt was open - which is the one dialog a pad button must not be
        // able to answer for itself. Application.Activated is not raised when focus merely
        // moves between windows of this application, which is exactly the distinction wanted.
        Application.Current.Activated += (_, _) => _engine.Paused = true;
        Application.Current.Deactivated += (_, _) => _engine.Paused = false;

        Closing += (_, _) => { SavePlacement(); Persist(); _engine.Paused = false; };

        var build = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        TxtVersion.Text = Strings.Get("VersionLabel", $"{build?.Major ?? 1}.{build?.Minor ?? 0}");
        RefreshEngineState();

        _loading = false;
        LstProfiles.SelectedItem = _config.Find(_config.DefaultProfileId) ?? _profiles.FirstOrDefault();
    }

    // =====================================================================
    //  Choice lists
    // =====================================================================

    private static KeyValuePair<ModeSwitch, string>[] ModeSwitchChoices =>
    [
        new(ModeSwitch.Cycle, Strings.Get("ModeSwitchCycle")),
        new(ModeSwitch.Select, Strings.Get("ModeSwitchSelect")),
        new(ModeSwitch.Hold, Strings.Get("ModeSwitchHold")),
    ];

    /// <summary>The four modes, labeled by the lamp each one lights.</summary>
    private static KeyValuePair<int, string>[] ModeChoices =>
        [.. Enumerable.Range(0, Profile.ModeCount)
            .Select(i => new KeyValuePair<int, string>(i, Strings.Get($"ModeName{i + 1}")))];

    /// <summary>Which way a scroll assignment goes, as the sign it becomes.</summary>
    private static KeyValuePair<int, string>[] ScrollDirections =>
    [
        new(1, Strings.Get("ScrollUp")),
        new(-1, Strings.Get("ScrollDown")),
    ];

    private static KeyValuePair<MacroRepeat, string>[] RepeatChoices =>
    [
        new(MacroRepeat.Once, Strings.Get("RepeatOnce")),
        new(MacroRepeat.WhileHeld, Strings.Get("RepeatWhileHeld")),
        new(MacroRepeat.Toggle, Strings.Get("RepeatToggle")),
    ];

    // =====================================================================
    //  Labels
    // =====================================================================

    /// <summary>Label of an input named by its id.</summary>
    private static string Describe(string buttonId)
    {
        var button = PadLayout.FindById(buttonId);
        return button is null ? buttonId : LabelOf(button);
    }

    /// <summary>
    /// Label of a physical input. Main keypad keys carry the number printed on them;
    /// everything else carries a resource key.
    /// </summary>
    private static string LabelOf(PadInput button)
        => button.HasLiteralLabel ? button.Label : Strings.Get(button.Label);

    /// <summary>
    /// A scroll, with its count when there is more than one of them. "scroll up" reads better
    /// than "scroll up ×1" for the common case, and the count matters the moment it is not 1.
    /// </summary>
    private static string DescribeScroll(Assignment action)
    {
        var way = Strings.Get(action.WheelDelta < 0 ? "DescWheelDown" : "DescWheelUp");
        var count = Math.Abs(action.WheelDelta);

        return count > 1 ? Strings.Get("DescWheelCount", way, count) : way;
    }

    /// <summary>
    /// Summary of an assignment, shown on its tile. This lives in the presentation layer
    /// rather than the catalog: the core holds no display text.
    /// </summary>
    private string Describe(Assignment action)
    {
        // A label the user typed wins outright. Nothing derived can be more use than the
        // words they chose for it themselves.
        if (!string.IsNullOrWhiteSpace(action.Label)) return action.Label!;

        return action.Kind switch
        {
            ActionKind.None => Strings.Get("DescNone"),
            ActionKind.Passthrough => Strings.Get("DescPassthrough"),
            ActionKind.Key => KeyList.Prefix(action.Modifiers)
                              + KeyList.Describe(action.ScanCode, action.Extended),
            ActionKind.Macro => BoundMacro(action) is { } macro
                                   ? Strings.Get("ChoiceMacroNamed", macro.Name)
                                   : Strings.Get("DescMacroMissing"),
            ActionKind.Text => Strings.Get("DescText", KeyList.Shorten(action.Text)),
            ActionKind.Launch => Strings.Get("DescLaunch",
                                     KeyList.Shorten(Path.GetFileName(action.Path) ?? action.Path)),
            ActionKind.MouseButton => Strings.Get($"Click{action.MouseButton}"),
            ActionKind.MouseWheel => DescribeScroll(action),

            // Without this a mode switch fell through to the default and displayed the
            // "nothing assigned" dash - an assigned input claiming to be unassigned.
            ActionKind.Mode => action.ModeSwitch switch
            {
                ModeSwitch.Cycle => Strings.Get("DescModeCycle"),
                ModeSwitch.Select => Strings.Get("DescModeSelect", action.ModeIndex + 1),
                ModeSwitch.Hold => Strings.Get("DescModeHold", action.ModeIndex + 1),
                _ => Strings.Get("DescModeCycle"),
            },

            _ => Strings.Get("DescNone"),
        };
    }

    // =====================================================================
    //  Device view
    // =====================================================================

    /// <summary>
    /// Places one shape per input on the device artwork.
    ///
    /// The shapes are the artist's own traced outlines, loaded from DeviceGeometry.xaml and
    /// keyed by button id. They ARE the controls: each catches its own clicks, anchors its
    /// own label and lights up when assigned. There is deliberately no table of coordinates
    /// alongside the picture that could drift out of step with it.
    /// </summary>
    private void BuildShapes()
    {
        foreach (var button in PadLayout.Buttons)
        {
            if (TryFindResource($"Geo_{button.Id}") is not Geometry geometry) continue;

            // Two shapes per control, because catching a click and showing a highlight want
            // different sizes. The hit shape is invisible and generous; the outline is thin
            // and exact. Conflating them would mean a selected control drew a 20px band.
            //
            // A control may also supply a separate catchment shape entirely. The eight D-Pad
            // arrows do: they taper to a point, so no amount of stroke makes the drawn shape
            // comfortable to click, and Geo_Hit_* replaces each with a wedge covering its
            // whole sector of the pad. The arrow is still what gets drawn.
            var target = TryFindResource($"Geo_Hit_{button.Id}") as Geometry ?? geometry;

            var hit = new System.Windows.Shapes.Path
            {
                Data = target,

                // A transparent fill makes the interior clickable, and a transparent stroke
                // adds an invisible margin around the outline — WPF hit-tests both. That
                // matters for the thin geometry: the D-Pad arrows are pointy shapes, fiddly
                // to hit at their true size, and this widens the catchment without touching
                // the artwork or changing what is drawn.
                Fill = System.Windows.Media.Brushes.Transparent,
                Stroke = System.Windows.Media.Brushes.Transparent,
                StrokeThickness = HitMargin,
                Cursor = Cursors.Hand,
                Tag = button,
                ToolTip = button.IsCombination
                    ? Strings.Get("DiagonalTooltip", string.Join(" + ", button.Components!.Select(Describe)))
                    : $"{LabelOf(button)} — {button.Signal}",
            };

            hit.MouseLeftButtonUp += (_, _) => Select(button);
            DeviceCanvas.Children.Add(hit);

            var shape = new System.Windows.Shapes.Path
            {
                Data = geometry,
                StrokeThickness = 3,
                IsHitTestVisible = false,
            };

            DeviceCanvas.Children.Add(shape);
            _shapes[button.Id] = shape;

        }
    }

    /// <summary>
    /// Draws the three indicator lamps onto the device picture.
    ///
    /// These are not controls: they take no clicks, hold no binding and appear in no list.
    /// They exist so the drawing agrees with the hardware - the pad lights these same three
    /// lamps for the current mode, and a picture that did not would be actively misleading
    /// while the user is looking at both.
    ///
    /// Three shapes cover all eight modes because <see cref="LedState"/> is a flags enum and
    /// the lamps switch independently: mode 5 simply lights two of them. There is no need for
    /// a shape per mode, and drawing one would have meant eight near-identical traces.
    /// </summary>
    private void BuildLeds()
    {
        foreach (var (lamp, lit, _) in Lamps)
        {
            if (TryFindResource($"Geo_LED_{lamp.ToString().ToUpperInvariant()}") is not Geometry geometry) continue;

            var shape = new System.Windows.Shapes.Path
            {
                Data = geometry,
                Fill = new SolidColorBrush(lit),
                IsHitTestVisible = false,

                // Seated into the case with a stroke the color of the ground behind the
                // drawing. The housing outline runs right up to where each lamp sits, and
                // with the lamp painted over it the line stops dead at the rectangle's edge
                // and reads as a seam. A stroke in the background color swallows the last
                // fraction of that line so the lamp looks set into the case rather than
                // stuck on top of it.
                Stroke = (Brush)FindResource("Background"),
                StrokeThickness = 2.5,

                // A lit lamp bleeds a little light onto the case around it. Without this the
                // shapes read as flat colored stickers rather than something illuminated,
                // which matters here because their whole job is to look like the real ones.
                Effect = new DropShadowEffect
                {
                    Color = lit,
                    ShadowDepth = 0,
                    BlurRadius = 18,
                    Opacity = 0.9,
                },
            };

            DeviceCanvas.Children.Add(shape);
            _leds.Add((lamp, shape));
        }
    }

    /// <summary>
    /// Matches the drawn lamps to the mode the pad is actually in.
    ///
    /// An unlit lamp is drawn in its own dark color rather than hidden or faded out. The
    /// artwork carries no outline for these - the drawing leaves that part of the case
    /// blank - so a lamp that vanished when dark would leave a hole rather than an unlit
    /// lens, and simply lowering the opacity washes the color toward the background
    /// instead of deepening it. Two explicit colors give a real off state.
    /// </summary>
    private void RefreshLeds()
    {
        // The pips mirror the lamps on the device, so they must not claim to be lit when
        // nothing can light them. With MI_01 off WinUSB every SetLeds call is a null-safe
        // no-op, and the window used to go on showing the keymap in a lamp pattern the pad
        // was not wearing - which is worse than having no indicator, because the eight
        // keymaps are read off those lamps.
        //
        // This covers the stopped engine too: Stop disposes the device and disposing it
        // turns the lamps off, so "not open" and "not lit" are the same condition.
        var state = _engine.WheelOnWinUsb ? PadRuntime.ModeLeds[_mode] : LedState.Off;

        foreach (var (lamp, shape) in _leds)
        {
            var on = state.HasFlag(lamp);
            var color = Lamps.First(l => l.Lamp == lamp);

            shape.Fill = new SolidColorBrush(on ? color.Lit : color.Dark);
            if (shape.Effect is DropShadowEffect glow) glow.Opacity = on ? 0.9 : 0.0;
        }
    }

    private void RefreshTiles()
    {
        RefreshLeds();
        RefreshRows();
        RecolorShapes();
    }

    /// <summary>
    /// Repaints the outlines on the device drawing without touching what they mean.
    ///
    /// Split from <see cref="RefreshTiles"/> for the reason <see cref="RecolorRows"/> was
    /// split from <see cref="RefreshRows"/>: a theme change is only a change of brush, and
    /// the two brushes here are copied out with FindResource so they cannot follow one on
    /// their own. Rebuilding twenty-five dropdowns to move a color is what made changing
    /// theme lurch, and it happened on every movement of the color picker as well.
    /// </summary>
    private void RecolorShapes()
    {
        foreach (var (id, shape) in _shapes)
        {
            var action = _profile?.GetBinding(_mode, id) ?? Assignment.Passthrough();
            var assigned = action.Kind is not (ActionKind.None or ActionKind.Passthrough);

            // State is carried entirely by the outline. Filling an assigned control tinted it
            // solid and buried the drawing underneath, which defeats the point of tracing the
            // artwork in the first place - a traced outline that gets painted over is just an
            // expensively shaped rectangle.
            // Selection is Highlight, not Accent. The drawing underneath is drawn in Accent,
            // so an Accent outline on top of it would be invisible - the mark would vanish into
            // the thing it is marking.
            var chosen = IsMarked(id);
            shape.Stroke = chosen
                ? (Brush)FindResource("Highlight")
                : assigned ? (Brush)FindResource("AccentMuted") : System.Windows.Media.Brushes.Transparent;
        }
    }

    /// <summary>
    /// Shows a control being pressed, on the drawing and on its row at once.
    ///
    /// Both, because the two halves of the window describe the same control and a press is the
    /// one moment it is worth being told which row goes with which key - the lists are long
    /// enough that finding the row for the key under your finger is otherwise a hunt.
    ///
    /// Live sits above selection, so pressing the control being edited still changes something.
    /// That was invisible while the two shared a color.
    /// </summary>
    private void Highlight(string buttonId, bool down)
    {
        var chosen = IsMarked(buttonId);
        var assigned = _profile?.GetBinding(_mode, buttonId) is { Kind: not (ActionKind.None or ActionKind.Passthrough) };

        if (_shapes.TryGetValue(buttonId, out var shape))
            shape.Stroke = down ? (Brush)FindResource("Live")
                : chosen ? (Brush)FindResource("Highlight")
                : assigned ? (Brush)FindResource("AccentMuted")
                : System.Windows.Media.Brushes.Transparent;

        if (_rows.TryGetValue(buttonId, out var row))
        {
            row.Container.BorderBrush = down ? (Brush)FindResource("Live")
                : chosen ? (Brush)FindResource("Highlight")
                : (Brush)FindResource("Line");

            row.Container.Background = down || chosen
                ? (Brush)FindResource("Hover")
                : System.Windows.Media.Brushes.Transparent;
        }

        if (down && PadLayout.FindById(buttonId) is { IsMomentary: true }) Flash(buttonId);
    }

    /// <summary>
    /// Clears a momentary input's highlight after a moment.
    ///
    /// The wheel rotations report one impulse per notch and never a release, so nothing
    /// would ever turn the tile off again. A timer supplies the missing edge. It has to
    /// be long enough to see — an immediate clear would render as no change at all.
    /// </summary>
    private void Flash(string buttonId)
    {
        if (!_flashing.TryGetValue(buttonId, out var timer))
        {
            timer = new DispatcherTimer { Interval = FlashDuration };
            timer.Tick += (sender, _) =>
            {
                ((DispatcherTimer)sender!).Stop();
                Highlight(buttonId, false);
            };
            _flashing[buttonId] = timer;
        }

        // Restart rather than let it run: while the wheel keeps turning the tile should
        // stay lit, instead of going dark on the first notch's deadline.
        timer.Stop();
        timer.Start();
    }

    /// <summary>
    /// Selects one control and nothing else - what a plain click, a click on the drawing
    /// and every path that edits a single assignment all mean.
    /// </summary>
    private void Select(PadInput button) => SetSelection([button], button);

    // =====================================================================
    //  Loading and saving
    // =====================================================================

    private void LoadConfiguration()
    {
        _loading = true;

        LoadMacros();

        _profiles.Clear();
        foreach (var profile in _config.Profiles) _profiles.Add(profile);
        RefreshFallbackMarker();

        ChkAutoSwitch.IsChecked = _config.AutoSwitch;
        ChkLockWindowSize.IsChecked = _config.LockWindowSize;
        ChkStartWithWindows.IsChecked = LaunchAtLogon.IsEnabled();

        // Reality, not the stored preference: if this copy was launched by hand without
        // elevating, the box has to say so rather than claim otherwise.
        ChkRunElevated.IsChecked = Elevation.IsElevated;
    }

    /// <summary>
    /// Puts a failed save in front of the user.
    ///
    /// Silence here would be worse than the failure: they would carry on assigning keys, and
    /// find none of it survived the next launch. The engine still has the configuration in
    /// memory, so remapping keeps working for this session either way.
    /// </summary>
    private void ReportSave(string? error)
    {
        if (error is null) return;

        // Loud, because it is rare and because the alternative is the user carrying on
        // making changes that are not being kept. This used to be a line in the status bar,
        // which is exactly where it would not be noticed.
        MessageBox.Show(Strings.Get("SaveFailed", error), Strings.Get("AppTitle"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Persist()
    {
        // Guard against the class of bug that once cost a whole configuration: a save
        // triggered before the load completed would wipe everything.
        if (_loading || _profiles.Count == 0) return;

        _config.Profiles = [.. _profiles];
        ReportSave(_store.Save(_config));
        _accessor.Replace(_config);
        RefreshLiveProfile();
    }

    /// <summary>
    /// The toolbar's profile box. It does not hold the selection itself: it hands it to the
    /// list on the profiles tab, which is the single place the current profile is decided,
    /// and everything else follows from there.
    /// </summary>
    private void ProfileChosen(object sender, SelectionChangedEventArgs e)
    {
        if (CboProfile.SelectedItem is Profile chosen && !ReferenceEquals(chosen, LstProfiles.SelectedItem))
            LstProfiles.SelectedItem = chosen;
    }

    private void ProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        _profile = LstProfiles.SelectedItem as Profile;

        // Keep the toolbar in step. Guarded against bouncing back: assigning the same
        // object is a no-op for the check in ProfileChosen.
        if (!ReferenceEquals(CboProfile.SelectedItem, _profile)) CboProfile.SelectedItem = _profile;

        _loading = true;
        TxtProfileHeading.Text = _profile?.Name ?? string.Empty;

        // The controller default explains itself where it is selected, on both pages that
        // can show it. The applications list stays live: what is locked is what the pad
        // does, not which programs it does nothing in.
        TxtLockedProfileNote.Visibility = _profile is { IsControllerDefault: true }
            ? Visibility.Visible : Visibility.Collapsed;

        _processes.Clear();
        foreach (var process in _profile?.Processes ?? []) _processes.Add(process);
        _loading = false;

        // ShowSelection rather than LoadAction: the strip's whole shape depends on whether
        // the profile now selected can be edited, and it ends in RefreshTiles anyway.
        ShowSelection();
    }

    private void AddProfile(object sender, RoutedEventArgs e)
    {
        var name = AskName(Strings.Get("NewProfileQuestion"),
            UniqueProfileName(Strings.Get("NewProfileName")),
            n => _profiles.Any(p => SameName(p.Name, n)));

        if (name is null) return;

        var profile = new Profile { Name = name };
        _profiles.Add(profile);
        Persist();
        LstProfiles.SelectedItem = profile;
        LstProfiles.ScrollIntoView(profile);
    }

    /// <summary>Keeps names distinct, so two profiles cannot be told apart only by position.</summary>
    private string UniqueProfileName(string wanted)
    {
        if (_profiles.All(p => !SameName(p.Name, wanted))) return wanted;

        for (var n = 2; ; n++)
        {
            var candidate = $"{wanted} {n}";
            if (_profiles.All(p => !SameName(p.Name, candidate))) return candidate;
        }
    }

    private void DuplicateProfile()
    {
        if (_profile is null) return;

        var copy = _profile.Clone();
        copy.Name = UniqueProfileName(Strings.Get("CopyOf", _profile.Name));

        _profiles.Add(copy);
        Persist();
        LstProfiles.SelectedItem = copy;
        LstProfiles.ScrollIntoView(copy);
    }

    /// <summary>
    /// Deletes a profile. Never the controller default, which is why the list can no longer
    /// run out: there is always at least that one, so the old "this is the last one" refusal
    /// is gone with it.
    /// </summary>
    private void DeleteProfile()
    {
        if (_profile is not { IsControllerDefault: false } removed) return;

        var answer = MessageBox.Show(
            Strings.Get("ConfirmDelete", removed.Name),
            Strings.Get("AppTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        _profiles.Remove(removed);

        // Deleting the fallback has to leave one, and the controller default is the safe
        // answer: it is the profile that cannot itself have been deleted.
        if (_config.DefaultProfileId == removed.Id)
        {
            _config.DefaultProfileId = Profile.ControllerDefaultId;
            RefreshFallbackMarker();
        }

        Persist();
        LstProfiles.SelectedItem = _profiles[0];
    }

    private void AddProcess(object sender, RoutedEventArgs e)
    {
        if (_profile is null) return;

        // Folded to the shape ActiveApp reports - no extension, lower case - before anything
        // compares it. Typing "Game.exe" and typing "game" have to reach one entry, or a
        // profile ends up holding two that look the same and matching on neither.
        var name = TxtProcess.Text.Trim().ToLowerInvariant();
        if (name.EndsWith(".exe", StringComparison.Ordinal)) name = name[..^4];

        if (name.Length == 0 || _processes.Contains(name)) return;

        _processes.Add(name);
        _profile.Processes = [.. _processes];

        TxtProcess.Clear();
        Persist();
    }

    private void RemoveProcess(object sender, RoutedEventArgs e)
    {
        if (_profile is null) return;
        if (LstProcesses.SelectedItem is not string name) return;

        _processes.Remove(name);

        // The collection is what the list edits; the profile takes a snapshot of it.
        _profile.Processes = [.. _processes];
        Persist();
    }

    /// <summary>
    /// Where a program picker should open.
    ///
    /// The Start menu, because that is where programs are listed by the name people know them
    /// by. Both machine-wide and per-user are tried: anything installed for one account only
    /// lives in the second, and a machine can have the first missing entirely.
    ///
    /// An empty answer is deliberate rather than a fallback to somewhere arbitrary. A file
    /// dialog given a folder that does not exist falls back to the process's current
    /// directory - which for an application started by Explorer is system32, and that is
    /// exactly the wrong place to drop somebody looking for their games.
    /// </summary>
    private static string StartMenu()
    {
        foreach (var root in new[] { Environment.SpecialFolder.CommonStartMenu,
                                     Environment.SpecialFolder.StartMenu })
        {
            var programs = Path.Combine(Environment.GetFolderPath(root), "Programs");
            if (Directory.Exists(programs)) return programs;
        }

        return string.Empty;
    }

    /// <summary>
    /// Picks a program and derives the process name from its filename.
    ///
    /// Fills the box rather than adding straight away, the same as detection does: what
    /// gets stored is a process name, and browsing to a launcher captures the launcher
    /// rather than the game it starts — so the value wants checking before it is added.
    /// </summary>
    private void BrowseProcess(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = Strings.Get("ChooseProgram"),
            Filter = Strings.Get("ProgramFilter"),
            InitialDirectory = StartMenu(),
            RestoreDirectory = true,
        };

        if (picker.ShowDialog() != true) return;

        // ActiveApp reports process names without the extension, lower case.
        var name = Path.GetFileNameWithoutExtension(picker.FileName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name)) return;

        TxtProcess.Text = name;
        TxtDetect.Text = Strings.Get("DetectionOk", name);
    }

    private void DetectProcess(object sender, RoutedEventArgs e)
    {
        // Give the user time to switch to the window they want named.
        TxtDetect.Text = Strings.Get("DetectionPending");

        var countdown = new DispatcherTimer { Interval = DetectionGrace };
        countdown.Tick += (_, _) =>
        {
            countdown.Stop();

            using var watcher = new ActiveApp();
            Thread.Sleep(350);
            var name = watcher.Current;
            if (string.IsNullOrEmpty(name)) { TxtDetect.Text = Strings.Get("DetectionFailed"); return; }
            TxtProcess.Text = name;
            TxtDetect.Text = Strings.Get("DetectionOk", name);
            Activate();
        };

        countdown.Start();
    }

    /// <summary>
    /// Re-reads the brushes this window assigned from code.
    ///
    /// Everything set in markup follows a theme change by itself. These do not: FindResource
    /// hands back the brush rather than a reference to the key, so a control outline, a row
    /// background or the engine state keeps whatever color it was given until something
    /// works it out again. Each of these methods already recomputes from the current
    /// resources, so the fix is to run them - not to hold references and update them.
    /// </summary>
    /// <summary>
    /// Re-reads every brush that was copied rather than referred to.
    ///
    /// Deliberately the cheap half of a refresh. Nothing a theme changes can alter what a row
    /// says, only what color it says it in - so this recolors and does not rebuild. It used to
    /// call RefreshTiles, which rebuilds all twenty-five dropdowns and their collection views,
    /// and then RecolorRows on top of work RefreshRows had already done: three passes over
    /// every row where one was needed, on every theme change and on every movement of the
    /// color picker.
    /// </summary>
    private void RepaintFromTheme()
    {
        RefreshLeds();
        RecolorRows();
        RecolorShapes();
        RefreshModeSelector();
        RefreshEngineState();
        ShowSlots();
    }

    /// <summary>
    /// Theme.Changed is static and this window is not: without this the event would hold the
    /// window alive after it closed, and repaint a dead one on the next theme change.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        Theme.Changed -= RepaintFromTheme;
        AbandonThemePreview();
        base.OnClosed(e);
    }

    /// <summary>
    /// Watches for the user leaving the Appearance page with a theme previewed but not
    /// applied, and puts back the one in use.
    ///
    /// The guard is not optional. SelectionChanged is a bubbling event, so every ComboBox in
    /// an assignment row and every ListBox on every page raises it through this handler as
    /// well - without the check, choosing a key would count as changing tab.
    /// </summary>
    private void TabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl) return;
        if (!ReferenceEquals(Tabs.SelectedItem, TabAppearancePage)) AbandonThemePreview();
    }

    /// <summary>
    /// Drops the options panel below its button, right edges aligned.
    ///
    /// Placement="Bottom" lines the popup's LEFT edge up with the button's, which for a
    /// button sitting at the window's right edge hangs the panel off the side of the screen.
    /// The width is measured rather than assumed, so it stays right whatever the text size is
    /// set to.
    /// </summary>
    private void OptionsOpen()
    {
        var width = OptionsPanel.ActualWidth;

        if (width <= 0)
        {
            OptionsPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width = OptionsPanel.DesiredSize.Width;
        }

        PopOptions.HorizontalOffset = BtnOptions.ActualWidth - width;
        PopOptions.IsOpen = true;
    }

    /// <summary>
    /// Opens the options panel, or closes it if it is already open.
    ///
    /// The popup is StaysOpen and closed by hand, which is the whole reason this can be a
    /// plain toggle. StaysOpen="False" looks like the obvious choice and is a trap: it
    /// dismisses the popup on the mouse-down and then lets that same press continue to
    /// whatever it landed on, so a press on this button closed the panel and reopened it in
    /// one gesture, and the panel could never be closed by pressing the thing that opened it.
    /// Guarding on IsOpen does not help - by the time the button is told about the press, the
    /// popup has already gone - and neither does timing the two, which was tried and did not
    /// hold. Owning the dismissal outright is the only version with no race in it.
    /// </summary>
    private void OptionsToggle(object sender, RoutedEventArgs e)
    {
        if (PopOptions.IsOpen)
        {
            PopOptions.IsOpen = false;
            return;
        }

        OptionsOpen();
    }

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _config.AutoSwitch = ChkAutoSwitch.IsChecked == true;
        _config.LockWindowSize = ChkLockWindowSize.IsChecked == true;
        ApplyWindowLock();

        var error = LaunchAtLogon.SetEnabled(ChkStartWithWindows.IsChecked == true, Elevation.IsElevated);
        if (error is not null)
        {
            MessageBox.Show(Strings.Get("StartupFailed", error), Strings.Get("AppTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);

            // The checkbox must reflect reality, not intent: otherwise the user believes
            // automatic startup is on when it is not.
            _loading = true;
            ChkStartWithWindows.IsChecked = LaunchAtLogon.IsEnabled();
            _loading = false;
        }

        Persist();
    }

    /// <summary>
    /// Switches the application between running as administrator and not.
    ///
    /// Elevation cannot be changed in place - a process cannot raise or lower its own token -
    /// so this restarts. The startup entry is rewritten BEFORE the restart, while this
    /// instance still holds whatever rights the change needs: removing a scheduled task
    /// registered with highest privileges requires elevation, and the unelevated copy that
    /// takes over would not be able to.
    /// </summary>
    private void ElevationChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var wanted = ChkRunElevated.IsChecked == true;
        if (wanted == Elevation.IsElevated) return;

        var confirm = MessageBox.Show(
            Strings.Get(wanted ? "ConfirmElevate" : "ConfirmUnelevate"), "n52 DejaVu",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            _loading = true;
            ChkRunElevated.IsChecked = Elevation.IsElevated;
            _loading = false;
            return;
        }

        _config.RunElevated = wanted;
        Persist();

        // While we still have the rights for it, whichever direction that is.
        if (LaunchAtLogon.IsEnabled()) LaunchAtLogon.SetEnabled(true, wanted);

        if (!Elevation.Relaunch(wanted))
        {
            MessageBox.Show(Strings.Get("ElevationFailed"), Strings.Get("AppTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);

            _loading = true;
            ChkRunElevated.IsChecked = Elevation.IsElevated;
            _loading = false;
            return;
        }

        // The replacement is starting; this one has to go, or the single-instance mutex will
        // turn it away.
        SavePlacement();
        _engine.Paused = false;
        System.Windows.Application.Current.Shutdown();
    }


    // =====================================================================
    //  Action editor
    // =====================================================================

    private Assignment CurrentAction()
    {
        if (_profile is null || Selected is null) return Assignment.Passthrough();

        if (_profile.Modes[_mode].TryGetValue(Selected.Id, out var existing)) return existing;

        // Selecting an input must not change its behavior: create a neutral entry, not
        // a silenced one.
        var created = Assignment.Passthrough();
        _profile.SetBinding(_mode, Selected.Id, created);
        return created;
    }

    /// <summary>The four modifier checkboxes, collected into one flag set.</summary>
    private Modifiers CheckedModifiers()
    {
        var held = Modifiers.None;

        if (ChkCtrl.IsChecked == true) held |= Modifiers.Ctrl;
        if (ChkShift.IsChecked == true) held |= Modifiers.Shift;
        if (ChkAlt.IsChecked == true) held |= Modifiers.Alt;
        if (ChkWin.IsChecked == true) held |= Modifiers.Win;

        return held;
    }

    private void LoadAction()
    {
        // The guard goes up first. Every assignment below raises a changed event, and one
        // arriving before the flag is set writes the half-filled panel back to the profile.
        _loading = true;
        var action = CurrentAction();

        CboKey.SelectedItem = FindKey(action.ScanCode, action.Extended);
        ChkCtrl.IsChecked = action.Modifiers.HasFlag(Modifiers.Ctrl);
        ChkShift.IsChecked = action.Modifiers.HasFlag(Modifiers.Shift);
        ChkAlt.IsChecked = action.Modifiers.HasFlag(Modifiers.Alt);
        ChkWin.IsChecked = action.Modifiers.HasFlag(Modifiers.Win);
        TxtText.Text = action.Text ?? string.Empty;
        TxtPath.Text = action.Path ?? string.Empty;
        TxtArguments.Text = action.Arguments ?? string.Empty;
        // Shown as a count and a direction; stored as one signed number.
        TxtNotches.Text = Math.Max(1, Math.Abs(action.WheelDelta)).ToString();
        CboScrollDirection.SelectedItem = ScrollDirections.First(d => d.Key == (action.WheelDelta < 0 ? -1 : 1));
        CboRepeat.SelectedItem = RepeatChoices.First(c => c.Key == action.Repeat);
        TxtRepeatDelay.Text = action.RepeatDelayMs.ToString();
        CboModeSwitch.SelectedItem = ModeSwitchChoices.First(c => c.Key == action.ModeSwitch);
        CboModeTarget.SelectedItem = ModeChoices.First(c => c.Key == Math.Clamp(action.ModeIndex, 0, Profile.ModeCount - 1));
        LstBoundMacro.SelectedItem = BoundMacro(action);

        _loading = false;
        ShowPanels(action.Kind);
    }

    private void ShowPanels(ActionKind kind)
    {
        Visibility For(ActionKind wanted)
            => kind == wanted ? Visibility.Visible : Visibility.Collapsed;

        PanKey.Visibility = For(ActionKind.Key);
        PanText.Visibility = For(ActionKind.Text);
        PanLaunch.Visibility = For(ActionKind.Launch);
        PanWheel.Visibility = For(ActionKind.MouseWheel);
        PanMacro.Visibility = For(ActionKind.Macro);
        PanMacroRepeat.Visibility = For(ActionKind.Macro);
        PanMode.Visibility = For(ActionKind.Mode);

        // The strip exists only for what a dropdown cannot finish. Most assignments need
        // nothing here, and leaving an empty panel on screen would imply otherwise.
        PanelDetail.Visibility =
            kind is ActionKind.Key or ActionKind.Text or ActionKind.Launch
                 or ActionKind.Macro or ActionKind.Mode or ActionKind.MouseWheel
                ? Visibility.Visible
                : Visibility.Collapsed;

        // Cycle has no target: it just steps to the next mode.
        if (kind == ActionKind.Mode)
        {
            var cycling = CboModeSwitch.SelectedItem is KeyValuePair<ModeSwitch, string> { Key: ModeSwitch.Cycle };
            PanModeTarget.Visibility = cycling ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>
    /// The three physical lamps and their colors, for the pips drawn on each mode button.
    ///
    /// A mode can light any combination, so a single colored dot cannot represent one —
    /// mode 5 is red AND green. Three pips per button, lit or dim, say exactly what the pad
    /// will show. Colors are fully qualified so this reads unambiguously beside the tray
    /// icon's own lamp table, which is drawn with GDI+ and uses System.Drawing.Color.
    /// </summary>
    private static readonly (LedState Lamp, System.Windows.Media.Color Lit, System.Windows.Media.Color Dark)[] Lamps =
    [
        (LedState.Red,
            System.Windows.Media.Color.FromRgb(0xE0, 0x52, 0x52),
            System.Windows.Media.Color.FromRgb(0x3A, 0x14, 0x14)),
        (LedState.Green,
            System.Windows.Media.Color.FromRgb(0x52, 0xC7, 0x77),
            System.Windows.Media.Color.FromRgb(0x12, 0x3A, 0x1E)),
        (LedState.Blue,
            System.Windows.Media.Color.FromRgb(0x5B, 0x8D, 0xEF),
            System.Windows.Media.Color.FromRgb(0x14, 0x21, 0x3A)),
    ];

    /// <summary>
    /// Builds the mode selector.
    ///
    /// Selecting a mode here switches the pad as well: the editor and the hardware show one
    /// mode between them, not two. Keeping them separate meant the lamps and the window
    /// could disagree, which is exactly the confusion the lamps exist to prevent.
    /// </summary>
    private void BuildModeSelector()
    {
        PanelModes.Children.Clear();

        foreach (var (index, label) in ModeChoices)
        {
            // Three pips showing which lamps this mode lights, sitting after the name so the
            // keymap reads first and the lamps annotate it. Fixed per button, so they are set
            // here rather than on refresh: what a mode displays never changes, only which mode
            // is live.
            var pips = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(7, 0, 0, 0),
            };

            foreach (var (lamp, color, _) in Lamps)
            {
                // Qualified rather than imported: a using for System.Windows.Shapes would
                // make Path ambiguous against System.IO.Path, which this file also uses.
                pips.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Margin = new Thickness(2, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Fill = new SolidColorBrush(color),
                    Opacity = PadRuntime.ModeLeds[index].HasFlag(lamp) ? 1.0 : 0.16,
                });
            }

            var button = new Button
            {
                Tag = index,
                Margin = new Thickness(0, 0, 6, 4),
                Padding = new Thickness(9, 4, 9, 4),
                Style = (Style)FindResource("ModeButton"),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, pips },
                },
            };

            button.Click += (s, _) =>
            {
                if (s is Button { Tag: int target }) _engine.SetMode(target);
            };

            PanelModes.Children.Add(button);
        }

        RefreshModeSelector();
    }

    /// <summary>
    /// Marks the live mode. Driven by the engine, so it follows a switch made on the pad
    /// just as readily as one made here.
    /// </summary>
    private void RefreshModeSelector()
    {
        foreach (var child in PanelModes.Children)
        {
            if (child is not Button { Tag: int index } button) continue;

            var live = index == _mode;

            // Border only, never a fill: a solid background swallows the lamp pips and the
            // label, which are the parts actually carrying the information. The pips
            // themselves are fixed — they describe the mode, not whether it is selected.
            button.Background = (Brush)FindResource("Surface");
            button.BorderBrush = (Brush)FindResource(live ? "Accent" : "Line");
            button.BorderThickness = new Thickness(live ? 2 : 1);

            if (button.Content is StackPanel { Children: [TextBlock text, _] })
                text.FontWeight = live ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>What a field held when it was focused, so Escape can put it back.</summary>
    private readonly Dictionary<TextBox, string> _beforeEditing = [];

    private void FieldFocused(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) _beforeEditing[box] = box.Text;
    }

    /// <summary>
    /// Writes the field. Reached by leaving it, or by Enter.
    ///
    /// These used to write on every keystroke, which meant typing a word saved the whole
    /// configuration to disk once per letter and rebuilt all twenty-five tiles with it. There
    /// was also no moment that meant "done", so a half-typed path was indistinguishable from a
    /// finished one - to the software and to the person looking at it.
    ///
    /// No new color marks the pending state, because there already is one: the field's
    /// border goes Highlight while it holds the keyboard, and the template says why - a
    /// focused field is the thing being edited. With the write happening on the way out,
    /// focused and uncommitted are the same moment.
    /// </summary>
    private void FieldCommitted(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (sender is TextBox box) _beforeEditing.Remove(box);
        FieldChanged(sender, e);
    }

    private void FieldKey(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;

        // Enter finishes a single-line field. The text box for a macro's text takes returns
        // as content and is not one of these.
        if (e.Key == Key.Enter && !box.AcceptsReturn)
        {
            FieldCommitted(box, e);
            e.Handled = true;
            return;
        }

        // Escape puts back whatever was there on the way in. Nothing has been written yet, so
        // there is nothing to undo beyond the box itself.
        if (e.Key == Key.Escape && _beforeEditing.TryGetValue(box, out var before))
        {
            box.Text = before;
            e.Handled = true;
        }
    }

    /// <summary>
    /// Edits the assignment's text in the prompt the macro text step already uses.
    ///
    /// Null means Cancel and changes nothing. An empty string is a real answer - clearing the
    /// text is something someone may well mean - so it is not treated as a refusal.
    /// </summary>
    private void EditTextClick(object sender, RoutedEventArgs e)
    {
        var typed = InputDialog.Ask(this, Strings.Get("TextToType"), TxtText.Text, lines: true);
        if (typed is null) return;

        TxtText.Text = typed;
        FieldChanged(sender, e);
    }

    /// <summary>Empties the text. A no-op when there is nothing there to empty.</summary>
    private void ClearTextClick(object sender, RoutedEventArgs e)
    {
        if (TxtText.Text.Length == 0) return;

        TxtText.Text = string.Empty;
        FieldChanged(sender, e);
    }

    private void FieldChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var action = CurrentAction();
        action.Modifiers = CheckedModifiers();

        action.Text = TxtText.Text;
        action.Path = TxtPath.Text;
        action.Arguments = TxtArguments.Text;
        if (int.TryParse(TxtNotches.Text, out var clicks)
            && CboScrollDirection.SelectedItem is KeyValuePair<int, string> scroll)
        {
            // At least one, and never negative here: a zero would be an assignment that does
            // nothing, and the minus sign now lives in the dropdown beside it.
            var count = Math.Max(1, Math.Abs(clicks));
            action.WheelDelta = scroll.Key * count;

            // Put the tidied number back, so what is stored and what is shown agree.
            var tidy = count.ToString();
            if (TxtNotches.Text != tidy) TxtNotches.Text = tidy;
        }
        if (CboRepeat.SelectedItem is KeyValuePair<MacroRepeat, string> repeat) action.Repeat = repeat.Key;
        if (int.TryParse(TxtRepeatDelay.Text, out var delay)) action.RepeatDelayMs = Math.Max(0, delay);
        if (CboModeSwitch.SelectedItem is KeyValuePair<ModeSwitch, string> mode) action.ModeSwitch = mode.Key;
        if (CboModeTarget.SelectedItem is KeyValuePair<int, string> target) action.ModeIndex = target.Key;

        // Changing between Cycle and the others shows or hides the target picker.
        if (action.Kind == ActionKind.Mode) ShowPanels(ActionKind.Mode);

        Commit();
    }

    private void FieldChanged(object sender, TextChangedEventArgs e) => FieldChanged(sender, (RoutedEventArgs)e);

    private void FieldChanged(object sender, SelectionChangedEventArgs e) => FieldChanged(sender, (RoutedEventArgs)e);

    private void Commit()
    {
        if (_profile is null || Selected is null) return;

        // Stored in the keymap being edited, whatever its kind - keymap switches included.
        // They were once lifted out to profile level here instead, which made one assignment
        // answer from all eight keymaps and could not be undone from any of them.
        var action = CurrentAction();
        _profile.SetBinding(_mode, Selected.Id, action);
        GiveEveryKeymapAWayBack(Selected.Id, action);

        Persist();
        RefreshTiles();
    }

    /// <summary>
    /// Puts "switch to the next keymap" on the same control in every other keymap, whenever
    /// a keymap switch that MOVES you is set on it in keymap 1.
    ///
    /// Without it, per-keymap switches strand you: bind "switch to keymap 5" on the bar in
    /// keymap 1, press it, and keymap 5 has nothing on that bar, so the only way back is to
    /// open this window and click a keymap button. A layout that only works while the editor
    /// is open is not a layout.
    ///
    /// <para><b>Which actions count, and why.</b> "Switch to keymap N" and "step to the next
    /// keymap" both leave you somewhere else, in a keymap that may have nothing at all on the
    /// control you just pressed - so both need a way back. Hold does not: it reverts the
    /// instant the control comes up, so it can never strand anyone, and laying a net for it
    /// would rewrite seven keymaps to guard against nothing. Every other kind of assignment -
    /// a key, a macro, text, a program - stays where it is put.</para>
    ///
    /// <para><b>Keymap 1 only, and deliberately.</b> Keymap 1 is where a layout is built, and
    /// it is the one keymap every profile starts in. Letting all eight write to each other is
    /// where this began: switches used to be a single shared assignment, so setting keymap 3's
    /// bar to "go to keymap 1" rewrote the other seven at the same time and there was no way
    /// to express a pair of keymaps that pointed at each other.</para>
    ///
    /// <para>The other keymaps stay editable afterwards. What is written here is a default,
    /// not a rule - override it in keymap 4 and keymap 4 keeps what you gave it, until the
    /// next time keymap 1's binding for that control changes and the net is laid again.</para>
    /// </summary>
    private void GiveEveryKeymapAWayBack(string buttonId, Assignment action)
    {
        if (_profile is null || _mode != 0) return;
        if (action.Kind != ActionKind.Mode || action.ModeSwitch == ModeSwitch.Hold) return;

        for (var i = 1; i < _profile.Modes.Count; i++)
        {
            // A fresh object per keymap. Handing all seven the same instance is exactly the
            // bug this whole change exists to undo.
            _profile.Modes[i][buttonId] = new Assignment
            {
                Kind = ActionKind.Mode,
                ModeSwitch = ModeSwitch.Cycle,
            };
        }
    }

    private void CaptureKey(object sender, RoutedEventArgs e)
    {
        var capture = new KeyPrompt { Owner = this };
        if (capture.ShowDialog() != true) return;

        var action = CurrentAction();
        action.ScanCode = capture.ScanCode;
        action.Extended = capture.Extended;
        CboKey.SelectedItem = FindKey(action.ScanCode, action.Extended);
        Commit();
    }

    /// <summary>The catalog entry matching a scancode, if there is one.</summary>
    private static KeyEntry? FindKey(ushort scanCode, bool extended)
        => KeyList.All.FirstOrDefault(k => k.ScanCode == scanCode && k.Extended == extended);

    private void KeyChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CboKey.SelectedItem is not KeyEntry key) return;

        var action = CurrentAction();
        action.ScanCode = key.ScanCode;
        action.Extended = key.Extended;
        Commit();
    }

    /// <summary>
    /// Chooses what a Launch binding runs, starting in the Start menu.
    ///
    /// Deliberately opens on the installed programs rather than on the file system, and takes
    /// a shortcut in preference to an executable. Picking the .lnk answers three questions at
    /// once that picking an .exe leaves open: which executable the installer actually intends
    /// to be run, what arguments and working directory it needs, and what the program is
    /// called. GIMP is the case that made this obvious - its bin folder holds 23 executables,
    /// the plausible-looking gimp.exe is not the one its shortcut points at, and only the
    /// shortcut knows it is called "GIMP 3.2.0".
    ///
    /// ShellExecute follows a shortcut on its own, so nothing downstream has to resolve it.
    /// Browsing elsewhere still works, for portable programs and scripts that have none.
    /// </summary>
    private void BrowseForFile(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = Strings.Get("ChooseProgram"),
            Filter = Strings.Get("ProgramFilter"),
            InitialDirectory = StartMenu(),

            // Return the shortcut, not what it points at. Left on, the dialog resolves
            // Discord.lnk to whatever launcher stub it currently names, which is the thing
            // that changes on every update - the shortcut is the stable target, and it
            // carries the working directory and arguments the program expects. ShellExecute
            // follows it at launch, so nothing downstream has to.
            DereferenceLinks = false,

            // The dialog otherwise leaves the process sitting in whatever folder was last
            // browsed, which is a side effect no file picker should have.
            RestoreDirectory = true,
        };

        if (picker.ShowDialog() != true) return;

        TxtPath.Text = picker.FileName;
    }

    /// <summary>
    /// Opens the user guide, which ships beside the executable.
    ///
    /// Through the shell rather than a browser by name, so it opens in whatever the machine
    /// uses for Markdown - and says so plainly if the file was not deployed, rather than
    /// failing silently on a click that looked like it should do something.
    /// </summary>
    private void OpenHelp(object sender, MouseButtonEventArgs e)
    {
        var guide = Path.Combine(AppContext.BaseDirectory, "help.md");

        if (!File.Exists(guide))
        {
            MessageBox.Show(Strings.Get("HelpMissing"), Strings.Get("AppTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(guide) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Strings.Get("AppTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Marks the profile the engine is actually using.
    ///
    /// The mark on the row is the whole of it. A line of text naming the same profile used to
    /// sit under the heading as well, which said nothing the list was not already saying an
    /// inch to the left.
    /// </summary>
    private void RefreshLiveProfile()
    {
        var live = _engine.ActiveProfile;
        foreach (var profile in _profiles) profile.IsLive = profile.Id == live?.Id;
    }

    // =====================================================================
    //  Window placement
    // =====================================================================

    /// <summary>
    /// Restores the size and position from the last run.
    ///
    /// The saved rectangle is checked against the current screens before use: a window
    /// remembered on a monitor that is no longer attached would otherwise open completely
    /// off-screen, with no way to drag it back.
    /// </summary>
    private void RestorePlacement()
    {
        if (_config.WindowWidth <= 0 || _config.WindowHeight <= 0) return;

        Width = Math.Max(MinWidth, _config.WindowWidth);
        Height = Math.Max(MinHeight, _config.WindowHeight);

        var bounds = new Rect(_config.WindowLeft, _config.WindowTop, Width, Height);
        if (IsOnAScreen(bounds))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _config.WindowLeft;
            Top = _config.WindowTop;
        }

        if (_config.WindowMaximized) WindowState = WindowState.Maximized;

        ApplyWindowLock();
    }

    /// <summary>
    /// Holds the window at its current size, or lets it move again.
    ///
    /// CanMinimize rather than NoResize: NoResize takes the minimize button away too, and a
    /// locked window that cannot be got out of the way is a worse problem than a resizable
    /// one. The maximize button and the drag borders both go either way.
    ///
    /// Locking while the window is maximized leaves it maximized, because the alternative -
    /// restoring it first - would resize the window as a side effect of an option that says
    /// it prevents exactly that. The box is on the toolbar and unchecking it gives the buttons
    /// back, so it strands nothing.
    /// </summary>
    private void ApplyWindowLock()
        => ResizeMode = _config.LockWindowSize ? ResizeMode.CanMinimize : ResizeMode.CanResize;

    /// <summary>
    /// True when a worthwhile part of the rectangle lands on a connected screen.
    ///
    /// Enumerated straight from the OS. The work area, not the full monitor rectangle: a
    /// window restored under the taskbar is as lost as one restored off-screen.
    /// </summary>
    private static bool IsOnAScreen(Rect bounds)
        => WorkAreas().Any(area =>
        {
            var visible = Rect.Intersect(bounds, area);
            return !visible.IsEmpty && visible.Width >= 120 && visible.Height >= 80;
        });

    /// <summary>Every connected monitor's work area, in physical pixels.</summary>
    private static List<Rect> WorkAreas()
    {
        var areas = new List<Rect>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };

            if (GetMonitorInfoW(monitor, ref info))
            {
                areas.Add(new Rect(
                    info.rcWork.Left,
                    info.rcWork.Top,
                    info.rcWork.Right - info.rcWork.Left,
                    info.rcWork.Bottom - info.rcWork.Top));
            }

            return true;
        }, IntPtr.Zero);

        return areas;
    }

    private delegate bool MonitorCallback(IntPtr monitor, IntPtr context, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr device, IntPtr clip, MonitorCallback callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    private void SavePlacement()
    {
        _config.WindowMaximized = WindowState == WindowState.Maximized;

        // RestoreBounds carries the pre-maximized rectangle; Width/Top would report the
        // maximized size, which would then be restored as if the user had chosen it.
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (bounds.IsEmpty) return;

        _config.WindowLeft = bounds.Left;
        _config.WindowTop = bounds.Top;
        _config.WindowWidth = bounds.Width;
        _config.WindowHeight = bounds.Height;
    }

    // =====================================================================
    //  Macro steps
    // =====================================================================

    /// <summary>
    /// How long a generated press holds its key down.
    ///
    /// Nought would send the down and the up in one burst, which some games sample straight
    /// past. Twenty is short enough that a sequence of them still reads as instant.
    /// </summary>
    private const int GeneratedHoldMs = 20;

    /// <summary>Appends a step and writes the macro back. Every Add button ends here.</summary>
    private void AddStep(MacroStep step)
    {
        _steps.Add(step);
        CommitMacros();
    }

    private void AddKeyStepClick(object sender, RoutedEventArgs e)
    {
        var prompt = new KeyPrompt { Owner = this };
        if (prompt.ShowDialog() != true) return;

        AddStep(new MacroStep
        {
            Kind = MacroStepKind.KeyPress,
            ScanCode = prompt.ScanCode,
            Extended = prompt.Extended,
            DelayMs = GeneratedHoldMs,
        });
    }

    private void AddDelayStepClick(object sender, RoutedEventArgs e)
    {
        var typed = InputDialog.Ask(this, Strings.Get("AskDelay"), "100");
        if (typed is null || !int.TryParse(typed, out var ms)) return;

        AddStep(new MacroStep { Kind = MacroStepKind.Delay, DelayMs = Math.Max(0, ms) });
    }

    /// <summary>
    /// Presses a key and leaves it down, so the steps after it run while it is held.
    ///
    /// No delay of its own: what it holds for is however long the rest of the macro takes.
    /// An unmatched one is not a leak - ReleaseMacroKeys lets go of every key a sequence
    /// held, on every exit including a cancellation - but it is almost always a mistake, so
    /// the Release key button sits next to this one.
    /// </summary>
    private void AddHoldStepClick(object sender, RoutedEventArgs e)
    {
        var prompt = new KeyPrompt { Owner = this };
        if (prompt.ShowDialog() != true) return;

        AddStep(new MacroStep
        {
            Kind = MacroStepKind.KeyDown,
            ScanCode = prompt.ScanCode,
            Extended = prompt.Extended,
        });
    }

    private void AddReleaseStepClick(object sender, RoutedEventArgs e)
    {
        var prompt = new KeyPrompt { Owner = this };
        if (prompt.ShowDialog() != true) return;

        AddStep(new MacroStep
        {
            Kind = MacroStepKind.KeyUp,
            ScanCode = prompt.ScanCode,
            Extended = prompt.Extended,
        });
    }

    private void AddTextStepClick(object sender, RoutedEventArgs e)
    {
        var typed = InputDialog.Ask(this, Strings.Get("AskText"), string.Empty, lines: true);
        if (string.IsNullOrEmpty(typed)) return;

        AddStep(new MacroStep { Kind = MacroStepKind.Text, Text = typed });
    }

    /// <summary>
    /// Adds a Return.
    ///
    /// A newline typed into a text step works - the typist turns it into a real Enter, since
    /// a Unicode newline is ignored by most applications - but it is invisible in the step
    /// list, where a deliberate Return is usually the step most worth seeing. This makes it
    /// a row of its own.
    /// </summary>
    private void AddEnterStepClick(object sender, RoutedEventArgs e)
        => AddStep(new MacroStep
        {
            Kind = MacroStepKind.KeyPress,
            ScanCode = 0x1C,
            DelayMs = GeneratedHoldMs,
        });

    private void AddClickStepClick(object sender, RoutedEventArgs e)
        => AddStep(new MacroStep
        {
            Kind = MacroStepKind.MouseClick,
            MouseButton = MouseButton.Left,
            DelayMs = GeneratedHoldMs,
        });

    private void MoveStepUp(object sender, RoutedEventArgs e) => Reorder(-1);

    private void MoveStepDown(object sender, RoutedEventArgs e) => Reorder(+1);

    /// <summary>
    /// Slides the selected step one place. The selection travels with it, so pressing the
    /// button twice walks one step down the list rather than moving two different ones.
    /// </summary>
    private void Reorder(int direction)
    {
        var from = LstSteps.SelectedIndex;
        if (from < 0) return;

        var to = from + direction;
        if (to < 0 || to >= _steps.Count) return;

        _steps.Move(from, to);
        LstSteps.SelectedIndex = to;
        CommitMacros();
    }

    private void RemoveStep(object sender, RoutedEventArgs e)
    {
        if (LstSteps.SelectedIndex < 0) return;
        _steps.RemoveAt(LstSteps.SelectedIndex);
        CommitMacros();
    }

    // =====================================================================
    //  Engine
    // =====================================================================

    /// <summary>
    /// Resolves a status key emitted by the engine. The "key|argument" form carries a
    /// detail that cannot be translated, such as a file path or a system message.
    /// </summary>
    private static string TranslateStatus(string message)
    {
        // "key", or "key|detail". A detail carrying two fields separates them with a unit
        // separator rather than a comma or a colon, because both of those turn up inside the
        // things being carried - file paths and messages Windows wrote.
        var bar = message.IndexOf('|');
        if (bar < 0) return Strings.Get(message);

        var key = message[..bar];
        var detail = message[(bar + 1)..].Split('\u001f');

        return detail.Length > 1
            ? Strings.Get(key, detail[0], detail[1])
            : Strings.Get(key, detail[0]);
    }

    /// <summary>
    /// Shows whether the pad is there. Not a button, and there is no button beside it: the
    /// application running is the engine running, so this reports rather than offers.
    /// </summary>
    private void RefreshEngineState()
    {
        var connected = _engine.Connected;

        TxtEngineState.Text = Strings.Get(connected ? "EngineRunning" : "EngineStopped");

        // Loud on purpose. Whether the pad is live is the single most important thing on
        // this window, and it was being missed at ordinary weight and color. The glow is a
        // zero-depth drop shadow in the text's own color, which reads as the text emitting
        // light rather than casting a shadow - the difference between "colored" and
        // "unmissable" at a glance across the room.
        var color = (SolidColorBrush)FindResource(connected ? "EngineOn" : "EngineOff");

        TxtEngineState.Foreground = color;
        TxtEngineState.FontWeight = FontWeights.Bold;

        // A reference, not the number behind it: assigning FindResource's answer would copy
        // whatever the size happens to be now and then stop following it. Same rule as the
        // brushes - see the note in CLAUDE.md about FindResource versus SetResourceReference.
        TxtEngineState.SetResourceReference(FontSizeProperty, "FontEmphasis");
        TxtEngineState.Effect = new DropShadowEffect
        {
            Color = color.Color,
            ShadowDepth = 0,
            BlurRadius = 14,
            Opacity = 0.85,
        };
    }
}
