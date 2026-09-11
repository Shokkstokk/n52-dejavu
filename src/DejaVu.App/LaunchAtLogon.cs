using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace DejaVu.App;

/// <summary>
/// Starting automatically with the Windows session.
///
/// Two mechanisms, because there is no single one that covers both cases.
///
/// Ordinarily this is one value under the current user's Run key: the plain way an
/// application asks to start with Windows, nothing privileged, and visible in Task Manager's
/// Startup tab where it can be switched off without coming back here.
///
/// But a Run key entry cannot start an elevated program. Windows will not raise a UAC prompt
/// at logon, so such an entry fails silently. When the user has chosen to run as administrator
/// - see <see cref="Elevation"/> for why anyone would - the only mechanism that starts an
/// elevated program at logon without a prompt is a scheduled task registered to run with
/// highest privileges.
///
/// That shape reasonably makes people uneasy: a logon task with highest privileges is what
/// you would expect to find behind something unwelcome. So it is never created quietly. It
/// appears only as the direct, visible consequence of the user turning on "Run as
/// administrator", and disappears again when they turn it off.
/// </summary>
public static class LaunchAtLogon
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "n52 DejaVu";
    private const string TaskName = "n52 DejaVu";

    /// <summary>True when either mechanism is registered.</summary>
    public static bool IsEnabled() => HasRunKey() || HasTask();

    /// <summary>
    /// Enables or disables automatic startup, using whichever mechanism suits the privilege
    /// level asked for. Returns null on success, or an actionable error message.
    ///
    /// Always removes the mechanism it is not using, so the two can never both be registered
    /// and start two copies at one logon.
    /// </summary>
    public static string? SetEnabled(bool enabled, bool elevated)
    {
        if (!enabled)
        {
            RemoveRunKey();
            RemoveTask();
            return null;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
            return "Could not determine the executable path.";

        if (elevated)
        {
            var error = CreateTask(executable);
            if (error is not null) return error;

            RemoveRunKey();
            return null;
        }

        // Removed first and while we may still have the rights to do it: a task registered
        // with highest privileges cannot be deleted by an unelevated process, so this is the
        // moment it has to happen - during the change, not after the restart.
        RemoveTask();
        return SetRunKey(executable);
    }

    /// <summary>
    /// Brings an existing startup entry into line with the executable that is running and the
    /// privilege level the user has chosen.
    ///
    /// Two things go stale. The entry stores an absolute path, so anything that moves or
    /// renames the executable leaves it aimed at a file that is not there - and nothing
    /// reports that: the entry still exists, <see cref="IsEnabled"/> still says true, the
    /// checkbox still shows checked, and the only symptom is that the application quietly does
    /// not start with Windows. That has already happened once here, on a rename.
    ///
    /// The other is the mechanism itself. Switching elevation on or off has to move the entry
    /// between the Run key and the scheduled task, and the half of that which needs elevation
    /// can only be done by whichever instance has it.
    /// </summary>
    public static void RepairIfStale(bool elevated)
    {
        if (!IsEnabled()) return;

        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable)) return;

        // Right mechanism, wrong mechanism, or right mechanism with a stale path - all three
        // are fixed by writing it again.
        var correct = elevated
            ? HasTask() && TaskCommand()?.Contains(executable, StringComparison.OrdinalIgnoreCase) == true
            : CurrentRunKey()?.Contains(executable, StringComparison.OrdinalIgnoreCase) == true && !HasTask();

        if (correct) return;

        SetEnabled(true, elevated);
    }

    // =====================================================================
    //  Run key
    // =====================================================================

    private static string? CurrentRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool HasRunKey() => !string.IsNullOrEmpty(CurrentRunKey());

    private static string? SetRunKey(string executable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return "Could not open the Windows startup key.";

            // Quoted: the path may contain spaces, and Windows would otherwise take the first
            // one as the end of the command and the rest as arguments.
            key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ex.Message;
        }
    }

    private static void RemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A startup entry that will not delete is not worth failing the change over.
        }
    }

    // =====================================================================
    //  Scheduled task, for the elevated case only
    // =====================================================================

    private static bool HasTask() => Run($"/Query /TN \"{TaskName}\"").Code == 0;

    private static string? TaskCommand()
    {
        var (code, output) = Run($"/Query /TN \"{TaskName}\" /FO LIST /V");
        return code == 0 ? output : null;
    }

    private static void RemoveTask()
    {
        // Deleting a task registered with highest privileges needs elevation itself, so this
        // can fail. Best effort: the caller arranges to try while it still has the rights.
        Run($"/Delete /TN \"{TaskName}\" /F");
    }

    /// <summary>
    /// Registers the logon task from a full XML definition.
    ///
    /// schtasks' command-line switches cannot express the settings this needs, and its
    /// defaults are wrong for a program meant to sit in the notification area indefinitely.
    /// Two matter. ExecutionTimeLimit defaults to 72 hours, after which Task Scheduler
    /// terminates the task - remapping would simply stop after three days of uptime, with
    /// nothing to say why. And DisallowStartIfOnBatteries defaults to true, so on a laptop the
    /// pad would be dead at every logon on battery and stop working the moment the charger
    /// came out.
    /// </summary>
    private static string? CreateTask(string executable)
    {
        var user = Environment.UserDomainName + "\\" + Environment.UserName;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts n52 DejaVu at logon, elevated, so remapping works inside elevated applications. Created by the "Run as administrator" option and removed when it is turned off.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{Escape(user)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Escape(executable)}</Command>
                </Exec>
              </Actions>
            </Task>
            """;

        var path = Path.Combine(Path.GetTempPath(), "n52-dejavu-startup.xml");

        try
        {
            // Unicode, not UTF-8: the declaration says UTF-16 and Task Scheduler believes it.
            File.WriteAllText(path, xml, Encoding.Unicode);

            var (code, output) = Run($"/Create /TN \"{TaskName}\" /XML \"{path}\" /F");
            return code == 0 ? null : output;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
        finally
        {
            try { File.Delete(path); } catch { /* a stray temp file is not worth reporting */ }
        }
    }

    private static string Escape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// Hands a command to the task scheduler and waits for its answer.
    ///
    /// Both streams are captured and joined, because schtasks is inconsistent about which one
    /// it explains itself on, and the explanation is the only useful thing it produces when
    /// something goes wrong. A negative code means it never ran at all, which is a different
    /// failure from one it ran and rejected.
    /// </summary>
    private static (int Code, string Output) Run(string arguments)
    {
        var start = new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        try
        {
            using var scheduler = Process.Start(start);
            if (scheduler is null) return (-1, "schtasks.exe would not start.");

            var said = scheduler.StandardOutput.ReadToEnd() + scheduler.StandardError.ReadToEnd();

            // Bounded, because a wait with no limit is how a hung child process becomes a
            // hung application - and this runs while a checkbox is waiting to report back.
            scheduler.WaitForExit(10_000);

            return (scheduler.ExitCode, said.Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
