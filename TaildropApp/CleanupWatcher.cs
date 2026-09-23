namespace TaildropApp;

/// <summary>
/// Runs as a detached copy of this same exe, invoked with (--cleanup-watcher, ownerPid, sessionPath).
/// Waits for the main Taildrop process to exit (normal close, force-kill, or crash) and then
/// deletes the temporary session inbox, so files never outlive the app even on a hard kill.
/// </summary>
static class CleanupWatcher
{
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

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Directory.Delete(fullSessionPath, recursive: true);
                break;
            }
            catch
            {
                Thread.Sleep(250);
            }
        }

        return 0;
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
