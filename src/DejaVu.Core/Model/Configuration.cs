using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace DejaVu.Core.Model;

/// <summary>
/// A complete set of assignments for the n52's inputs.
/// A profile can claim a list of processes: when one of them comes to the foreground,
/// the engine switches to that profile automatically.
/// </summary>
public sealed class Profile : INotifyPropertyChanged
{
    private string _name = "New profile";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The one profile the user cannot rename, delete or edit: every control at its
    /// controller default, in all eight keymaps.
    ///
    /// It is worth having as a fixed thing rather than as a convention. A layout that has
    /// grown over months is exactly the thing that goes wrong at the worst moment, and the
    /// way out has to be a profile that is known-good by construction - not one that was
    /// known-good until it was edited. Being empty is what makes it that: an input with no
    /// binding passes through, so "holds nothing" and "does what the hardware does" are the
    /// same state, and there is nothing in it that can rot.
    ///
    /// A fixed id rather than a flag, so that nothing can produce a second one and no
    /// ordinary profile can be turned into it by editing the file by hand.
    /// </summary>
    public const string ControllerDefaultId = "controller-default";

    /// <summary>True for the profile above, which the editor treats as read-only.</summary>
    [JsonIgnore]
    public bool IsControllerDefault => Id == ControllerDefaultId;

    /// <summary>
    /// Display name. Raises a change notification so bound lists refresh themselves:
    /// removing and re-inserting the item to force a redraw meant mutating the
    /// collection during a selection notification, with all the side effects that brings.
    /// </summary>
    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>
    /// Process names that activate this profile, without the .exe (e.g. "wowclassic").
    /// Empty means a manual profile, never selected automatically.
    /// </summary>
    public List<string> Processes { get; set; } = [];

    private bool _isLive;

    /// <summary>
    /// True while the engine is actually using this profile.
    ///
    /// Presentation only, and deliberately not serialized: which profile is live follows
    /// the foreground application and is recomputed at runtime. It exists so the editor can
    /// mark the live one in the list — you can edit any profile, so it has to be obvious
    /// which one the pad is currently obeying.
    /// </summary>
    [JsonIgnore]
    public bool IsLive
    {
        get => _isLive;
        set => Set(ref _isLive, value);
    }

    private bool _isFallback;

    /// <summary>
    /// True for the profile the engine drops back to when no application matches.
    ///
    /// Presentation only, like <see cref="IsLive"/>: the fact itself lives in
    /// <see cref="Configuration.DefaultProfileId"/>, and this mirrors it so the list can
    /// mark it. Two profiles can never carry it at once, which is why it is not stored.
    /// </summary>
    [JsonIgnore]
    public bool IsFallback
    {
        get => _isFallback;
        set => Set(ref _isFallback, value);
    }

    /// <summary>
    /// Assignments per mode, keyed by input id (see <see cref="PadLayout"/>).
    ///
    /// The pad has four modes, indicated on the hardware by the three state LEDs
    /// (off / red / green / blue). Every input can carry a different action in each, so a
    /// profile holds four independent maps rather than one. Modes live inside the profile,
    /// so a game's four layers are its own.
    /// </summary>
    public List<Dictionary<string, Assignment>> Modes { get; set; } = CreateModes();

    /// <summary>
    /// Number of modes the hardware can indicate.
    ///
    /// Eight, not four. The three LEDs are independently settable bits, so they distinguish
    /// every combination: dark, each color alone, each pair, and all three. Belkin's own
    /// software used only four — dark and the three singles — which is the precedent this
    /// followed at first, but it is a limit of that software rather than of the device.
    /// </summary>
    public const int ModeCount = 8;

    private static List<Dictionary<string, Assignment>> CreateModes()
        => [.. Enumerable.Range(0, ModeCount).Select(_ =>
            new Dictionary<string, Assignment>(StringComparer.OrdinalIgnoreCase))];

    /// <summary>
    /// Pads the keymap list back to full length. Every lookup indexes it by mode number, so
    /// a file that lost an entry to hand-editing would throw on the first press in that mode.
    /// </summary>
    public void EnsureModes()
    {
        while (Modes.Count < ModeCount)
            Modes.Add(new Dictionary<string, Assignment>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The action bound to an input in the given mode. An input missing from the
    /// configuration passes its original signal through: an empty profile must never leave
    /// the device mute. Silencing a key is an explicit choice
    /// (<see cref="ActionKind.None"/>).
    /// </summary>
    public Assignment GetBinding(int mode, string buttonId)
    {
        if (mode < 0 || mode >= Modes.Count) return Assignment.Passthrough();
        return Modes[mode].TryGetValue(buttonId, out var action) ? action : Assignment.Passthrough();
    }

    /// <summary>Assigns an action to an input in one mode.</summary>
    public void SetBinding(int mode, string buttonId, Assignment action)
    {
        if (mode < 0 || mode >= Modes.Count) return;
        Modes[mode][buttonId] = action;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Stores a value and announces it, but only when it actually changed.
    ///
    /// The guard is the point. Lists here are bound to, and re-announcing an identical value
    /// makes every bound view rebuild for nothing - which, on the profile list, meant a
    /// redraw on each keystroke while a name was being typed.
    /// </summary>
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        return true;
    }

    public Profile Clone() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = Name + " (copy)",
        Processes = [.. Processes],
        // Value.Clone(), not Value. New dictionaries holding the SAME assignments is not a
        // copy: every editor path fetches the stored Assignment and mutates it in place, so
        // duplicating a profile and then editing the duplicate rewrote the profile it came
        // from - and saved the damage, because an edit persists immediately.
        Modes = [.. Modes.Select(m => m.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase))],
    };
}

/// <summary>The application's complete configuration.</summary>
public sealed class Configuration
{
    public List<Profile> Profiles { get; set; } = [];

    /// <summary>
    /// Every macro, shared by all profiles. Global rather than per-profile: a profile
    /// answers "which application am I in", which is a poor reason to lose access to a
    /// sequence you already wrote, and one copy means fixing a macro fixes it everywhere.
    /// </summary>
    public List<MacroDefinition> Macros { get; set; } = [];

    /// <summary>
    /// Themes the user has built. Global for the same reason macros are: appearance is not a
    /// property of which application happens to be in the foreground.
    /// </summary>
    public List<CustomTheme> Themes { get; set; } = [];

    /// <summary>
    /// Profile used when no specific profile matches the foreground application.
    ///
    /// A choice of the user's, and separate from <see cref="Profile.ControllerDefaultId"/> on
    /// purpose. The two used to be one field, which meant that locking the controller-default
    /// profile would also have locked everyone's fallback to "no remapping at all" - and
    /// wanting the pad to carry a general desktop layout when no game matches is an ordinary
    /// thing to want.
    /// </summary>
    public string? DefaultProfileId { get; set; }

    /// <summary>Switch profiles automatically based on the foreground application.</summary>
    public bool AutoSwitch { get; set; } = true;

    /// <summary>
    /// Editor window placement, remembered between runs. Zero width or height means "never
    /// saved", so the window falls back to its designed size.
    /// </summary>
    public double WindowWidth { get; set; }

    public double WindowHeight { get; set; }

    public double WindowLeft { get; set; }

    public double WindowTop { get; set; }

    public bool WindowMaximized { get; set; }

    /// <summary>
    /// Base text size for the whole window. Zero means the size Windows uses for its own
    /// message text, which is what the window was built around.
    ///
    /// The application's, not a theme's. It began as a theme property - fonts are where
    /// people look for them, next to the colors - and the control still sits on that page,
    /// but the value could not stay there: the shipped themes carry no size, so merely
    /// previewing one snapped the text back to 12 and relaid out the page, and a size set on
    /// one theme was lost the moment another was applied.
    /// </summary>
    public double FontSize { get; set; }

    /// <summary>
    /// Hold the editor at the size it is: no resizing, and no maximizing.
    ///
    /// Minimizing is deliberately still allowed, so locking cannot strand the window in front
    /// of whatever is behind it.
    /// </summary>
    public bool LockWindowSize { get; set; }

    /// <summary>
    /// Whether the user has chosen to run as administrator.
    ///
    /// Stored rather than inferred from the running process, because it has to survive a
    /// launch that did not manage it - and because it decides which startup mechanism is
    /// correct, which has to be known before the application is in a position to elevate.
    /// </summary>
    public bool RunElevated { get; set; }

    /// <summary>
    /// The color theme, by name. Purely an appearance preference - nothing in the engine
    /// reads it, and it sits here only because this file is the one thing that persists.
    ///
    /// Left null until the user picks one, so a configuration written by a build that had no
    /// themes stays valid and an untouched installation does not pin itself to a name that a
    /// later build might rename. The editor treats null as the default.
    /// </summary>
    public string? Theme { get; set; }

    public MacroDefinition? FindMacro(string? id)
        => id is null ? null : Macros.FirstOrDefault(m => m.Id == id);

    /// <summary>Pads every profile's keymap list back to full length.</summary>
    public void EnsureModes()
    {
        foreach (var profile in Profiles) profile.EnsureModes();
    }

    public Profile? Find(string? id)
        => id is null ? null : Profiles.FirstOrDefault(p => p?.Id == id);

    /// <summary>
    /// Name given to the controller-default profile. Supplied by the presentation layer,
    /// which owns the string table.
    /// </summary>
    public static string DefaultProfileName { get; set; } = "Default";

    /// <summary>Creates a starting configuration holding only the controller default.</summary>
    public static Configuration CreateDefault()
    {
        var configuration = new Configuration();
        configuration.EnsureControllerDefault();

        // Nothing else exists yet, so it is also the fallback. The user can point that
        // somewhere else the moment they have somewhere to point it.
        configuration.DefaultProfileId = Profile.ControllerDefaultId;
        return configuration;
    }

    /// <summary>
    /// Guarantees the controller-default profile exists, is empty, and comes first.
    ///
    /// Called on every load rather than only on a first run, because it is the one profile
    /// whose absence the application cannot recover from - it is what the fallback drops
    /// back to when the chosen one has been deleted. Emptied rather than trusted, since a
    /// configuration file is an editable text file and this profile's whole value is that
    /// its contents are known without looking.
    ///
    /// An existing empty profile is adopted rather than left beside a new one, so an
    /// installation that predates this does not end up with two profiles that do nothing,
    /// one of which is redundant.
    /// </summary>
    public void EnsureControllerDefault()
    {
        var locked = Profiles.FirstOrDefault(p => p.IsControllerDefault);

        if (locked is null)
        {
            locked = Profiles.FirstOrDefault(p =>
                p.Modes.All(m => m.Count == 0) && p.Processes.Count == 0);

            if (locked is null)
            {
                locked = new Profile();
                Profiles.Insert(0, locked);
            }

            locked.Id = Profile.ControllerDefaultId;
        }

        locked.Name = DefaultProfileName;
        foreach (var keymap in locked.Modes) keymap.Clear();

        // Processes are deliberately NOT cleared. What is locked is what the pad does, not
        // when it does it - and "leave the pad alone in this application" is a real thing to
        // want, which this profile is the natural vehicle for.

        // First in the list, because it is where a layout that has gone wrong is escaped to
        // and hunting for it is the last thing wanted at that point.
        if (Profiles.IndexOf(locked) > 0)
        {
            Profiles.Remove(locked);
            Profiles.Insert(0, locked);
        }
    }
}
