using DejaVu.Core.Model;

namespace DejaVu.Core.Engine;

/// <summary>
/// The configuration as it stands right now, shared between the editor and the pad.
///
/// A box around the object rather than the object itself. The two halves of this application
/// run on different threads and disagree about lifetime: the editor replaces the whole
/// configuration whenever anything is saved, while the pad is midway through deciding what a
/// keypress means. Handing the pad a direct reference would mean either restarting it on
/// every save - dropping the device and every held key with it - or letting it read an object
/// that is being rebuilt underneath it.
///
/// <para>Reads and writes go through <see cref="Volatile"/> because the reference is swapped
/// on one thread and read on another. That guarantees the reader sees a whole object, old or
/// new, and never a torn view of the swap. It guarantees nothing about timing, and does not
/// need to: a keypress resolved against the configuration from a moment ago is correct, since
/// that is what was in force when the key went down.</para>
/// </summary>
public sealed class LiveSettings(Configuration initial)
{
    private Configuration _live = initial;

    /// <summary>Whatever is in force at this instant.</summary>
    public Configuration Current => Volatile.Read(ref _live);

    /// <summary>Swaps in a newly saved configuration, atomically.</summary>
    public void Replace(Configuration saved) => Volatile.Write(ref _live, saved);
}
