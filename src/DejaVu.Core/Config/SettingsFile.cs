using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using DejaVu.Core.Model;

namespace DejaVu.Core.Config;

/// <summary>
/// The one file everything is kept in: profiles, keymaps, macros, themes and window
/// placement, as JSON under <c>%LOCALAPPDATA%\n52-dejavu</c>.
///
/// <para><b>Per-user, and that is not merely tidy.</b> It lived under ProgramData while the
/// application still demanded administrator rights, which worked only because of those
/// rights: a file an elevated process creates there belongs to Administrators, and an
/// ordinary user cannot replace it. The moment elevation was dropped, every save threw, and
/// nothing was catching it - so the window vanished mid-click, taking with it whatever had
/// just been set.</para>
///
/// <para><b>Nothing here throws at the caller.</b> Reading answers with a usable
/// configuration whatever it finds, because the engine has to be able to start; writing
/// answers with a message instead of an exception, because losing one setting is a small
/// problem and losing the running engine over it is a large one.</para>
/// </summary>
public sealed class SettingsFile
{
    private const string FolderName = "n52-dejavu";
    private const string FileName = "config.json";

    /// <summary>
    /// Indented, with enums written by name.
    ///
    /// Both are for the person who opens the file. Enum numbers would turn every action into
    /// a lookup, and a single line of JSON is not something anyone can read or hand-edit -
    /// and hand-editing it is a supported way out of a mess, which the reading path is
    /// written to survive.
    /// </summary>
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serializes readers against writers; both can arrive from either thread.</summary>
    private readonly Lock _exclusive = new();

    /// <summary>The folder the file sits in.</summary>
    public string Directory { get; }

    /// <summary>The file itself.</summary>
    public string Path { get; }

    public SettingsFile(string? directory = null)
    {
        Directory = directory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            FolderName);

        Path = System.IO.Path.Combine(Directory, FileName);
    }

    // =====================================================================
    //  Reading
    // =====================================================================

    /// <summary>
    /// The stored configuration, or a fresh one when there is nothing usable to read.
    /// </summary>
    public Configuration Load()
    {
        lock (_exclusive)
        {
            if (!File.Exists(Path)) return Configuration.CreateDefault();

            Configuration? stored;

            try
            {
                stored = JsonSerializer.Deserialize<Configuration>(
                    File.ReadAllText(Path, Encoding.UTF8), Format);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Set the unreadable file aside before replacing it. Someone whose profiles
                // stopped opening can retrieve them by hand; someone whose profiles were
                // silently overwritten cannot.
                SetAside();
                return Configuration.CreateDefault();
            }

            if (stored is null) return Configuration.CreateDefault();

            return MakeUsable(stored) ?? Configuration.CreateDefault();
        }
    }

    /// <summary>
    /// Brings a file that parsed into a state the engine can actually run on, or gives up.
    ///
    /// Everything here guards against a hand-edited file, which is a supported way out of a
    /// mess and therefore something that will certainly happen. Nothing here converts one
    /// format into another: there is one format.
    /// </summary>
    private static Configuration? MakeUsable(Configuration stored)
    {
        // A null entry in the list takes the engine down the first time it walks the profiles,
        // which is on every single startup.
        stored.Profiles.RemoveAll(profile => profile is null);
        if (stored.Profiles.Count == 0) return null;

        // A keymap list can be short if the file was hand-edited, and every lookup indexes
        // it by mode number.
        stored.EnsureModes();

        // Puts the controller default back if it went missing, empties it if the file filled
        // it in, and moves it to the front. Its entire value is that its contents are known
        // without looking, and this is a text file anyone can edit.
        stored.EnsureControllerDefault();

        // The fallback has to name a profile that exists. The first one is the controller
        // default, which is the right answer when the intended profile has gone.
        if (stored.Find(stored.DefaultProfileId) is null)
            stored.DefaultProfileId = stored.Profiles[0].Id;

        return stored;
    }

    /// <summary>Keeps a copy of a file that would not parse, named by the moment it failed.</summary>
    private void SetAside()
    {
        try
        {
            if (!File.Exists(Path)) return;

            var kept = System.IO.Path.ChangeExtension(Path, $".invalid-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(Path, kept, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort. Failing to keep a copy must not stop the application starting.
        }
    }

    // =====================================================================
    //  Writing
    // =====================================================================

    /// <summary>
    /// Stores the configuration. Null means it worked; anything else is what went wrong,
    /// phrased for the status bar.
    /// </summary>
    public string? Save(Configuration configuration)
    {
        lock (_exclusive)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                // Even if a caller let one through, a null profile must never reach the disk:
                // a poisoned file outlives whatever bug produced it and breaks every launch
                // afterwards.
                configuration.Profiles.RemoveAll(profile => profile is null);

                Swap(JsonSerializer.Serialize(configuration, Format));
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                return ex.Message;
            }
        }
    }

    /// <summary>
    /// Writes beside the real file and then swaps it in.
    ///
    /// Saving happens on nearly every click, so a write interrupted by a crash or a power cut
    /// is a matter of when. Writing over the file directly would leave a truncated one and
    /// take every profile with it; swapping means the worst case is losing the change that
    /// was in flight.
    /// </summary>
    private void Swap(string json)
    {
        var pending = Path + ".tmp";

        // No byte-order mark. It is invisible in an editor, it is not required for UTF-8, and
        // it is one more thing to explain to anyone parsing this file with something else.
        File.WriteAllText(pending, json, new UTF8Encoding(false));

        if (File.Exists(Path)) File.Replace(pending, Path, destinationBackupFileName: null);
        else File.Move(pending, Path);
    }
}
