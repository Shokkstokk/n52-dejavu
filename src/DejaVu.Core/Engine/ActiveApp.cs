using System.Runtime.InteropServices;

namespace DejaVu.Core.Engine;

/// <summary>
/// Reports which application is in front, so the engine can pick a profile to match.
///
/// Polled on a timer rather than hooked. A WinEvent hook is the obvious alternative and is
/// worse here: it needs a message pump on the thread that installs it, and games running
/// exclusive fullscreen routinely stop delivering the events. A poll costs nothing and
/// cannot be starved.
///
/// <para><b>The window handle is the cache key, not the process name.</b> Resolving a name
/// means opening the process and asking for its image path - real work, and it would happen
/// three times a second for the whole session if the poll asked every time. The foreground
/// HWND is a single call and changes only when focus actually moves, so a session spent
/// inside one window does no work at all beyond that one call.</para>
/// </summary>
public sealed class ActiveApp : IDisposable
{
    /// <summary>
    /// How often focus is sampled. Fast enough that a profile is in place before the user
    /// has finished alt-tabbing, slow enough to be free.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(300);

    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;

    /// <summary>The last foreground window seen. Only a change to this triggers a lookup.</summary>
    private nint _window;

    private string _current = string.Empty;

    /// <summary>Foreground process name, without extension, lower-cased. Empty if unknown.</summary>
    public string Current => Volatile.Read(ref _current);

    /// <summary>Raised when the foreground application changes.</summary>
    public event Action<string>? Changed;

    public ActiveApp() => _loop = Task.Run(WatchAsync);

    private async Task WatchAsync()
    {
        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token))
            {
                var window = GetForegroundWindow();
                if (window == _window) continue;

                _window = window;

                var name = ProcessNameOf(window);
                if (string.Equals(name, Current, StringComparison.OrdinalIgnoreCase)) continue;

                Volatile.Write(ref _current, name);
                Changed?.Invoke(name);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed. The only way out of the loop.
        }
    }

    /// <summary>
    /// The executable name behind a window, or empty if it cannot be read.
    ///
    /// Deliberately not System.Diagnostics.Process. That builds a managed object per call
    /// only to read one string off it, and it signals an exited process by throwing, which
    /// makes an ordinary race - focus moving as an application closes - into exception
    /// handling on a hot path.
    ///
    /// PROCESS_QUERY_LIMITED_INFORMATION also matters beyond being cheap: it is granted for
    /// processes at a higher integrity level, so a game running as administrator still
    /// resolves by name and still matches a profile. Asking for full query information
    /// would be refused for exactly the applications most likely to want one.
    /// </summary>
    private static string ProcessNameOf(nint window)
    {
        if (window == nint.Zero) return string.Empty;

        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0) return string.Empty;

        var process = OpenProcess(QueryLimitedInformation, inherit: false, processId);
        if (process == nint.Zero) return string.Empty;

        try
        {
            // MAX_PATH is not the limit for a process image path, but a longer one cannot be
            // typed into the profile's application list either, so a truncated read here and
            // a failed match are the same outcome.
            var buffer = new char[260];
            var length = (uint)buffer.Length;

            if (!QueryFullProcessImageNameW(process, 0, buffer, ref length)) return string.Empty;

            var path = new string(buffer, 0, (int)length);
            return Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        // Cancelling a CancellationTokenSource that has already been disposed throws, so a
        // second call has to be a no-op rather than a surprise during shutdown.
        if (_disposed) return;
        _disposed = true;

        _stopping.Cancel();

        // The loop only ever ends by cancellation, so this returns almost at once. The
        // timeout is there so a wedged poll cannot hold up shutdown.
        _loop.Wait(TimeSpan.FromSeconds(1));

        _stopping.Dispose();
    }

    private const uint QueryLimitedInformation = 0x1000;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, char[] name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
