using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace DejaVu.App;

/// <summary>
/// Running with, or without, administrator rights.
///
/// The application asks for neither in its manifest: reading the pad over WinUSB needs no
/// elevation, and demanding it would put a UAC prompt in front of every launch. But UIPI
/// blocks injected input from reaching a window owned by an elevated process, so remapping is
/// silently dead inside an elevated game, launcher or tool - and plenty of ordinary software
/// runs elevated without announcing it.
///
/// So it is a choice, made deliberately and visibly, rather than a permanent demand or a
/// silent limitation.
/// </summary>
public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>
    /// Starts a fresh copy at the requested privilege level. Returns false if it did not
    /// start, in which case the caller must not shut down.
    ///
    /// Elevating is a plain ShellExecute with the "runas" verb, which raises the UAC prompt.
    /// Dropping back down is the awkward direction: a process cannot lower its own token, and
    /// anything it launches inherits elevation. The way down is to ask the shell to do it -
    /// Explorer runs as the ordinary user, so a process it starts on our behalf does too.
    /// </summary>
    public static bool Relaunch(bool elevated)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable)) return false;

        try
        {
            var start = elevated
                ? new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" }
                : new ProcessStartInfo("explorer.exe", $"\"{executable}\"") { UseShellExecute = true };

            Process.Start(start);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Almost always the user declining the UAC prompt. Not an error worth reporting
            // as one: they were asked, and they said no.
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
