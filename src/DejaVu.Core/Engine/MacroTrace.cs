using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace DejaVu.Core.Engine;

/// <summary>
/// A trace of what the engine did, written to disk.
///
/// Exists because macro text was arriving corrupted and no amount of reasoning about it was
/// converging. It could not be reproduced outside the application either: a probe typing the
/// same string into the same Notepad, with the same pacing and the same step sequence, came
/// back byte-perfect. Whatever is going wrong needs the real path - a physical key held down,
/// its hardware auto-repeat being swallowed, an elevated process - so the real path has to be
/// the thing that reports.
///
/// <para><b>Nothing here touches the disk on the caller's thread.</b> It used to:
/// <c>File.AppendAllText</c> per line, which opens, writes, flushes and closes, under a lock.
/// Callers include the thread that reads the pad, and putting a file open in front of every
/// read made the pad feel sluggish - a diagnostic that changed the behavior it was there to
/// measure. Writing is now a queue push, and one background thread does the I/O.</para>
///
/// <para>The stream is flushed per line rather than buffered, because the interesting runs
/// end with something going wrong and a buffer is exactly what would be lost. Timestamps are
/// taken when the line is queued, not when it is written, since the question is nearly always
/// about ordering and overlap.</para>
/// </summary>
public static class MacroTrace
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static BlockingCollection<string>? _pending;

    /// <summary>Where the trace is written. Set once, at startup.</summary>
    public static string? Path { get; set; }

    public static bool Enabled => Path is not null;

    /// <summary>
    /// Queues a line. Costs a string format and an enqueue, and never blocks on I/O.
    /// </summary>
    public static void Write(string message)
    {
        var queue = _pending;
        if (queue is null) return;

        try
        {
            queue.Add($"{Clock.Elapsed.TotalMilliseconds,10:0.0} ms  " +
                      $"[t{Environment.CurrentManagedThreadId:00}]  {message}");
        }
        catch (InvalidOperationException)
        {
            // The writer has shut down and the queue is closed. Nothing to do about it, and
            // certainly nothing worth taking the engine down for.
        }
    }

    /// <summary>Starts a fresh file, so one run's trace is not read as another's.</summary>
    public static void Begin(string path)
    {
        try
        {
            File.WriteAllText(path, $"n52 trace  {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Path = null;
            return;
        }

        Path = path;
        _pending = new BlockingCollection<string>();

        // Background, and below normal priority: a trace is never the reason the application
        // is running, and it must not compete with the thread reading the device.
        var writer = new Thread(() => Drain(path))
        {
            IsBackground = true,
            Name = "n52 trace",
            Priority = ThreadPriority.BelowNormal,
        };

        writer.Start();
    }

    private static void Drain(string path)
    {
        var queue = _pending;
        if (queue is null) return;

        try
        {
            using var file = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
            foreach (var line in queue.GetConsumingEnumerable()) file.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never take the application down with them. Drop the queue so
            // callers stop paying to fill something nobody is reading.
            _pending = null;
        }
    }
}
