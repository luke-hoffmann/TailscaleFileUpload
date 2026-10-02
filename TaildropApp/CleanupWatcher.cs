namespace TaildropApp;

/// <summary>
/// Runs as a detached copy of this same exe, invoked with (--cleanup-watcher, ownerPid, sessionPath).
/// Waits for the main Taildrop process to exit (normal close, force-kill, or crash) and then
/// deletes the temporary session inbox, so files never outlive the app even on a hard kill.
/// If another program still has a file open (for example after double-click-open), deletion is retried
/// with a growing delay for about two minutes before giving up.
/// </summary>
static class CleanupWatcher
{
    const int GiveUpAfterMs = 120_000;
    const int MaxDelayMs = 5_000;

    public static int Run(string ownerPidText, string sessionPath)
    {
        if (!int.TryParse(ownerPidText, out var ownerPid) || ownerPid < 1) return 2;

        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var fullSessionPath = Path.GetFullPath(sessionPath);
        var parent = Path.GetDirectoryName(fullSessionPath);
        var name = Path.GetFileName(fullSessionPath);
        if (parent is null || !string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar), tempRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith("Taildrop-", StringComparison.Ordinal))
        {
            return 2;
        }

        while (OwnerIsRunning(ownerPid)) Thread.Sleep(250);

        var deadline = Environment.TickCount64 + GiveUpAfterMs;
        var delay = 250;
        while (true)
        {
            try
            {
                if (Directory.Exists(fullSessionPath)) Directory.Delete(fullSessionPath, recursive: true);
                return 0;
            }
            catch
            {
                // Something still holds a file open (or it is read-only); clear attributes and try again.
                ClearReadOnly(fullSessionPath);
            }

            if (Environment.TickCount64 >= deadline) return 0;
            Thread.Sleep(delay);
            delay = Math.Min(delay * 2, MaxDelayMs);
        }
    }

    static void ClearReadOnly(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }

    static bool OwnerIsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
