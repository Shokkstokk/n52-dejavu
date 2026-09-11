using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using DejaVu.Core.Input;
using DejaVu.Core.Model;

namespace DejaVu.TypeProbe;

/// <summary>
/// Measures whether injected text arrives intact, and finds out where it stops doing so.
///
/// Text typed by a macro was coming out corrupted in Notepad: runs of the wrong character in
/// the middle of an otherwise correct string, as in "this is a test" arriving as
/// "this eeeeeeest". Guessing at causes through the user cost a rebuild and a hand test per
/// attempt, so this reproduces it without them.
///
/// Three things vary between a clean run and the reported failure, and this separates them:
/// the target application, the pacing, and whether the text goes out as one direct call or as
/// the steps of a macro. Typing into its own box tests the sender alone; driving a real
/// Notepad tests the target the failure was actually seen in; and the step sequence repeats
/// the exact shape of the macro that failed.
/// </summary>
internal static class Program
{
    private const string Sample = "this is a test - If you see this, then you are good. 0123456789";

    private static readonly TimeSpan[] Intervals =
    [
        TimeSpan.FromMilliseconds(3),
        TimeSpan.FromMilliseconds(5),
        TimeSpan.FromMilliseconds(8),
        TimeSpan.FromMilliseconds(10),
        TimeSpan.FromMilliseconds(14),
        TimeSpan.FromMilliseconds(20),
    ];

    /// <summary>Runs per interval. One clean pass proves nothing about a race.</summary>
    private const int Rounds = 4;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var box = new TextBox { Multiline = true, Dock = DockStyle.Fill, Font = new Font("Consolas", 10) };
        var form = new Form
        {
            Text = "n52 type probe",
            Width = 1000,
            Height = 520,
            StartPosition = FormStartPosition.CenterScreen,
            Controls = { box },
        };

        var log = new StringBuilder();
        log.AppendLine($"sample ({Sample.Length} chars): {Sample}");
        log.AppendLine();

        form.Shown += async (_, _) =>
        {
            await RunNotepad(log);

            var path = Path.Combine(AppContext.BaseDirectory, "typeprobe-log.txt");
            File.WriteAllText(path, log.ToString());
            box.Text = log.ToString();
        };

        Application.Run(form);
    }

    /// <summary>Straight into our own text box: the sender with nothing else involved.</summary>
    private static async Task RunOwnBox(Form form, TextBox box, StringBuilder log)
    {
        log.AppendLine("--- own text box, direct ---");
        form.Activate();

        foreach (var interval in Intervals)
        {
            box.Clear();
            box.Focus();
            await Task.Delay(200);

            var before = Injector.Dropped;
            await Injector.TypeAsync(Sample, interval: interval);
            await Task.Delay(400);

            Report(log, interval.TotalMilliseconds, box.Text, Injector.Dropped - before);
        }

        log.AppendLine();
    }

    /// <summary>
    /// Into a real Notepad, as the reported failure was. Also runs the macro's own shape -
    /// a keypress, then waits, then several text steps - in case the corruption needs the
    /// sequence rather than a single string.
    /// </summary>
    private static async Task RunNotepad(StringBuilder log)
    {
        log.AppendLine("--- notepad ---");

        // Any Notepad left over from a previous run is killed rather than closed. Closing
        // one that holds unsaved text raises a save prompt, and that dialog takes the focus
        // the probe is about to type into - which is what made the last run report an empty
        // Notepad and look like a failure that was really an invalid test.
        KillNotepads();
        await Task.Delay(400);

        using var notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        if (notepad is null) { log.AppendLine("could not start notepad"); return; }

        await Task.Delay(2000);

        foreach (var interval in Intervals)
        {
            var passes = 0;
            var invalid = 0;
            var worst = string.Empty;

            for (var round = 0; round < Rounds; round++)
            {
                if (!FocusNotepad(notepad)) { log.AppendLine("could not focus notepad"); return; }
                await Task.Delay(350);

                // Compared as a delta rather than cleared first. Windows 11 Notepad restores
                // the previous session's text on launch, and Ctrl+A over a restored tab was
                // leaving content behind - every round then read the same stale string and
                // reported a failure that had nothing to do with the typing.
                var before = ReadNotepad(notepad);
                await Injector.TypeAsync(Sample, interval: interval);
                await Task.Delay(700);

                var after = ReadNotepad(notepad);

                if (after.Length <= before.Length) { invalid++; continue; }

                var typed = after[before.Length..].Replace("\r", "").Replace("\n", "");

                if (typed == Sample) passes++;
                else if (worst.Length == 0) worst = typed;
            }

            log.AppendLine($"interval {interval.TotalMilliseconds,5:0.#} ms  "
                         + $"{passes}/{Rounds} clean" + (invalid > 0 ? $"  ({invalid} invalid)" : ""));
            if (worst.Length > 0) log.AppendLine($"    e.g. {worst}");
        }

        // The macro shape: the steps of the one that failed, minus the long waits.
        log.AppendLine();
        log.AppendLine("--- notepad, as macro steps ---");

        if (FocusNotepad(notepad))
        {
            await Task.Delay(300);
            var startedAt = ReadNotepad(notepad).Length;
            var before = Injector.Dropped;

            Injector.KeyDown(0x2E, false);
            await Task.Delay(20);
            Injector.KeyUp(0x2E, false);
            await Task.Delay(100);
            await Injector.TypeAsync("lol");
            await Task.Delay(300);
            await Injector.TypeAsync("this is a test");
            await Task.Delay(300);
            await Injector.TypeAsync("If you see this, then you are good.");
            await Task.Delay(600);

            var expected = "clolthis is a testIf you see this, then you are good.";
            var got = ReadNotepad(notepad)[startedAt..].Replace("\r", "").Replace("\n", "");
            log.AppendLine($"steps  {(got == expected ? "OK  " : "FAIL")}  "
                         + $"length {got.Length}/{expected.Length}  dropped {Injector.Dropped - before}");
            if (got != expected)
            {
                log.AppendLine($"    expected: {expected}");
                log.AppendLine($"    got:      {got}");
            }
        }

        KillNotepads();
    }

    private static void KillNotepads()
    {
        foreach (var p in Process.GetProcessesByName("notepad"))
        {
            try { p.Kill(); } catch { /* already gone */ }
        }
    }

    private static void Report(StringBuilder log, double ms, string got, long dropped)
    {
        got = got.Replace("\r", "").Replace("\n", "");
        var ok = got == Sample;
        log.AppendLine($"interval {ms,5:0.#} ms  {(ok ? "OK  " : "FAIL")}  "
                     + $"length {got.Length,3}/{Sample.Length}  dropped {dropped}");
        if (!ok) log.AppendLine($"    got: {got}");
    }

    // --- Win32 -----------------------------------------------------------------

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr param);

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam);

    private const uint WmGetText = 0x000D;
    private const uint WmGetTextLength = 0x000E;

    /// <summary>
    /// Finds Notepad's window by enumerating, not through the Process we started.
    ///
    /// Windows 11 ships Notepad as a packaged application: starting notepad.exe hands off to
    /// a different process and the one we hold exits, so its MainWindowHandle stays zero and
    /// the window is never found. Enumerating top-level windows finds it whoever owns it.
    /// </summary>
    private static IntPtr NotepadWindow()
    {
        var found = IntPtr.Zero;
        var classes = new[] { "Notepad", "ApplicationFrameWindow", "WinUIDesktopWin32WindowClass" };

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            var title = new StringBuilder(GetWindowTextLength(hWnd) + 2);
            GetWindowText(hWnd, title, title.Capacity);
            if (!title.ToString().Contains("Notepad", StringComparison.OrdinalIgnoreCase)) return true;

            var cls = new StringBuilder(256);
            GetClassName(hWnd, cls, cls.Capacity);
            if (!classes.Contains(cls.ToString())) return true;

            found = hWnd;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    private static bool FocusNotepad(Process notepad)
    {
        var window = NotepadWindow();
        if (window == IntPtr.Zero) return false;

        ShowWindow(window, 9);   // SW_RESTORE, in case it opened minimized
        return SetForegroundWindow(window);
    }

    /// <summary>
    /// Reads the text back out of Notepad's edit control. The class name differs between the
    /// classic control and the Windows 11 rewrite, so both are tried.
    /// </summary>
    private static string ReadNotepad(Process notepad)
    {
        var main = NotepadWindow();
        if (main == IntPtr.Zero) return "<no notepad window>";

        var edit = FindEdit(main, 0);
        if (edit == IntPtr.Zero) return "<could not find notepad's edit control>";

        var length = SendMessage(edit, WmGetTextLength, IntPtr.Zero, null!);
        var buffer = new StringBuilder(length + 2);
        SendMessage(edit, WmGetText, buffer.Capacity, buffer);
        return buffer.ToString();
    }

    private static IntPtr FindEdit(IntPtr parent, int depth)
    {
        if (depth > 6) return IntPtr.Zero;

        var wanted = new[] { "Edit", "RichEditD2DPT", "RICHEDIT50W", "RichEdit20W" };

        for (var child = FindWindowEx(parent, IntPtr.Zero, null, null);
             child != IntPtr.Zero;
             child = FindWindowEx(parent, child, null, null))
        {
            var cls = new StringBuilder(256);
            GetClassName(child, cls, cls.Capacity);
            if (wanted.Contains(cls.ToString())) return child;

            var deeper = FindEdit(child, depth + 1);
            if (deeper != IntPtr.Zero) return deeper;
        }

        return IntPtr.Zero;
    }
}
