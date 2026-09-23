namespace TaildropApp;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--cleanup-watcher")
        {
            return CleanupWatcher.Run(args[1], args[2]);
        }

        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, "Local\\TaildropDesktopReceiver", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Taildrop is already open.", "Taildrop", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.Run(new MainForm());
        return 0;
    }
}
