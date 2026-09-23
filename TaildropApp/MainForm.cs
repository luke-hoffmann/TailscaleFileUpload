using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace TaildropApp;

sealed class MainForm : Form
{
    const int Port = 8787;

    static readonly Color Background = Color.FromArgb(246, 247, 244);
    static readonly Color White = Color.White;
    static readonly Color Ink = Color.FromArgb(25, 28, 24);
    static readonly Color Muted = Color.FromArgb(102, 110, 100);
    static readonly Color Green = Color.FromArgb(30, 106, 61);
    static readonly Color GreenPale = Color.FromArgb(224, 244, 231);
    static readonly Color Red = Color.FromArgb(168, 57, 47);

    readonly Panel _statusPanel;
    readonly Panel _statusDot;
    readonly Label _statusLabel;
    readonly Label _promptLabel;
    readonly Label _urlLabel;
    readonly Button _qrButton;
    readonly Button _copyButton;
    readonly ListView _fileList;
    readonly Label _emptyLabel;
    readonly Button _saveSelectedButton;
    readonly Button _saveAllButton;
    readonly System.Windows.Forms.Timer _inboxTimer;
    readonly System.Windows.Forms.Timer _copyTimer;

    TaildropServer? _server;
    Process? _cleanupProcess;
    string? _taildropUrl;
    string? _inboxPath;
    bool _cleanupStarted;
    string _lastSignature = "";

    public MainForm()
    {
        Text = "Taildrop";
        ClientSize = new Size(620, 710);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 10);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        var title = MakeLabel("Taildrop", 22, FontStyle.Bold, Ink);
        title.Location = new Point(28, 24);
        Controls.Add(title);

        var subtitle = MakeLabel("Phone to this computer, privately over Tailscale", 9.5f, FontStyle.Regular, Muted);
        subtitle.Location = new Point(31, 62);
        Controls.Add(subtitle);

        _statusPanel = new Panel { Location = new Point(28, 102), Size = new Size(564, 188), BackColor = White };
        Controls.Add(_statusPanel);

        _statusDot = new Panel { Location = new Point(22, 24), Size = new Size(10, 10), BackColor = Muted };
        _statusPanel.Controls.Add(_statusDot);
        _statusLabel = MakeLabel("Starting...", 10, FontStyle.Bold, Muted);
        _statusLabel.Location = new Point(42, 19);
        _statusPanel.Controls.Add(_statusLabel);
        _promptLabel = MakeLabel("Scan the QR code with your phone", 17, FontStyle.Bold, Ink);
        _promptLabel.Location = new Point(22, 62);
        _statusPanel.Controls.Add(_promptLabel);
        _urlLabel = MakeLabel("Finding your Tailscale address...", 10, FontStyle.Regular, Muted);
        _urlLabel.Location = new Point(24, 99);
        _urlLabel.MaximumSize = new Size(515, 24);
        _statusPanel.Controls.Add(_urlLabel);

        _qrButton = MakeButton("Show QR code", 146, Green, White);
        _qrButton.Location = new Point(22, 130);
        _qrButton.Enabled = false;
        _qrButton.Click += QrButton_Click;
        _statusPanel.Controls.Add(_qrButton);

        _copyButton = MakeButton("Copy link", 110, GreenPale, Green);
        _copyButton.Location = new Point(178, 130);
        _copyButton.Enabled = false;
        _copyButton.Click += CopyButton_Click;
        _statusPanel.Controls.Add(_copyButton);

        var inboxCaption = MakeLabel("TEMPORARY INBOX", 8, FontStyle.Bold, Green);
        inboxCaption.Location = new Point(31, 316);
        Controls.Add(inboxCaption);
        var inboxHint = MakeLabel("Select files, then drag them into File Explorer", 9, FontStyle.Regular, Muted);
        inboxHint.Location = new Point(31, 338);
        Controls.Add(inboxHint);

        _fileList = new ListView
        {
            Location = new Point(28, 370),
            Size = new Size(564, 248),
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true,
            HideSelection = false,
            GridLines = false,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = White,
            ForeColor = Ink,
            Font = new Font("Segoe UI", 9.5f)
        };
        _fileList.Columns.Add("Name", 326);
        _fileList.Columns.Add("Size", 92);
        _fileList.Columns.Add("Received", 122);
        _fileList.ItemDrag += FileList_ItemDrag;
        Controls.Add(_fileList);

        _emptyLabel = MakeLabel("Files sent from your phone will appear here.", 10, FontStyle.Regular, Muted);
        _emptyLabel.Location = new Point(169, 472);
        Controls.Add(_emptyLabel);
        _emptyLabel.BringToFront();

        _saveSelectedButton = MakeButton("Save selected", 136, Green, White);
        _saveSelectedButton.Location = new Point(28, 634);
        _saveSelectedButton.Enabled = false;
        _saveSelectedButton.Click += (_, _) => SaveInboxItems(_fileList.SelectedItems.Cast<ListViewItem>());
        Controls.Add(_saveSelectedButton);

        _saveAllButton = MakeButton("Save all", 108, White, Ink);
        _saveAllButton.Location = new Point(174, 634);
        _saveAllButton.Enabled = false;
        _saveAllButton.Click += (_, _) => SaveInboxItems(_fileList.Items.Cast<ListViewItem>());
        Controls.Add(_saveAllButton);

        _fileList.SelectedIndexChanged += (_, _) => _saveSelectedButton.Enabled = _fileList.SelectedItems.Count > 0;

        var temporaryLabel = MakeLabel("Closing Taildrop permanently deletes anything left here.", 8.5f, FontStyle.Regular, Muted);
        temporaryLabel.Location = new Point(31, 687);
        Controls.Add(temporaryLabel);

        _copyTimer = new System.Windows.Forms.Timer { Interval = 1400 };
        _copyTimer.Tick += (_, _) => { _copyButton.Text = "Copy link"; _copyTimer.Stop(); };

        _inboxTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _inboxTimer.Tick += (_, _) => RefreshTemporaryInbox();

        Shown += MainForm_Shown;
        FormClosing += MainForm_FormClosing;
    }

    static Label MakeLabel(string text, float size, FontStyle style, Color color) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", size, style),
        ForeColor = color,
        BackColor = Color.Transparent,
        AutoSize = true
    };

    static Button MakeButton(string text, int width, Color backColor, Color foreColor)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(width, 44),
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = foreColor,
            Font = new Font("Segoe UI Semibold", 10),
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    async void MainForm_Shown(object? sender, EventArgs e)
    {
        try
        {
            _inboxPath = NewTemporaryInbox();
            await StartTaildropAsync();
            RefreshTemporaryInbox();
            _inboxTimer.Start();
        }
        catch (Exception ex)
        {
            SetErrorState(ex.Message);
        }
    }

    async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_cleanupStarted) return;
        _cleanupStarted = true;
        e.Cancel = true;
        _inboxTimer.Stop();
        await StopTaildropAsync();
        RemoveTemporaryInbox();
        Close();
    }

    static string NewTemporaryInbox()
    {
        var path = Path.Combine(Path.GetTempPath(), "Taildrop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    void RemoveTemporaryInbox()
    {
        if (_inboxPath is null) return;
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var candidate = Path.GetFullPath(_inboxPath);
        if (candidate.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(candidate).StartsWith("Taildrop-", StringComparison.Ordinal))
        {
            try { Directory.Delete(candidate, recursive: true); } catch { /* the watcher process will retry */ }
        }
        _inboxPath = null;
    }

    void StartCleanupWatcher(int ownerPid)
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? Environment.ProcessPath!;
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--cleanup-watcher");
        startInfo.ArgumentList.Add(ownerPid.ToString());
        startInfo.ArgumentList.Add(_inboxPath!);
        _cleanupProcess = Process.Start(startInfo);
    }

    async Task StartTaildropAsync()
    {
        await StopTaildropAsync();

        var tailscaleIp = GetTailscaleIPv4();
        if (tailscaleIp is null)
        {
            SetErrorState("Connect Tailscale on this computer, then reopen this app.");
            return;
        }

        _taildropUrl = $"http://{tailscaleIp}:{Port}";
        try
        {
            _server = new TaildropServer(_inboxPath!);
            await _server.StartAsync(IPAddress.Parse(tailscaleIp), Port);
            StartCleanupWatcher(Environment.ProcessId);
        }
        catch (Exception ex)
        {
            await StopTaildropAsync();
            SetErrorState(ex.Message.Contains("already in use") || ex.Message.Contains("address")
                ? "Port 8787 is busy or the receiver could not start."
                : ex.Message);
            return;
        }

        _statusDot.BackColor = Green;
        _statusLabel.Text = "Ready - keep this window open";
        _statusLabel.ForeColor = Green;
        _promptLabel.Text = "Scan the QR code with your phone";
        _urlLabel.Text = _taildropUrl;
        _urlLabel.ForeColor = Muted;
        _qrButton.Enabled = true;
        _copyButton.Enabled = true;
    }

    async Task StopTaildropAsync()
    {
        if (_server is not null)
        {
            try { await _server.StopAsync(); } catch { /* best effort */ }
            _server = null;
        }
        _taildropUrl = null;
        if (_inboxPath is not null && Directory.Exists(_inboxPath))
        {
            foreach (var file in Directory.EnumerateFiles(_inboxPath, ".taildrop-*.part"))
            {
                try { File.Delete(file); } catch { /* best effort */ }
            }
        }
    }

    void SetErrorState(string message)
    {
        _statusDot.BackColor = Red;
        _statusLabel.Text = "Not running";
        _statusLabel.ForeColor = Red;
        _promptLabel.Text = "Could not start Taildrop";
        _urlLabel.Text = message;
        _urlLabel.ForeColor = Red;
        _qrButton.Enabled = false;
        _copyButton.Enabled = false;
    }

    void CopyButton_Click(object? sender, EventArgs e)
    {
        if (_taildropUrl is null) return;
        Clipboard.SetText(_taildropUrl);
        _copyButton.Text = "Copied";
        _copyTimer.Stop();
        _copyTimer.Start();
    }

    void QrButton_Click(object? sender, EventArgs e)
    {
        if (_taildropUrl is null) return;
        try
        {
            var bytes = QrCode.GeneratePng(_taildropUrl);
            using var stream = new MemoryStream(bytes);
            using var sourceImage = Image.FromStream(stream);
            var bitmap = new Bitmap(sourceImage);

            using var qrForm = new Form
            {
                Text = "Scan to connect",
                ClientSize = new Size(430, 500),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = White,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };
            var qrTitle = MakeLabel("Scan with your phone", 18, FontStyle.Bold, Ink);
            qrTitle.Location = new Point(104, 20);
            qrForm.Controls.Add(qrTitle);
            var picture = new PictureBox
            {
                Image = bitmap,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(360, 360),
                Location = new Point(35, 70)
            };
            qrForm.Controls.Add(picture);
            var hint = MakeLabel("Tailscale must be connected on your phone.", 9, FontStyle.Regular, Muted);
            hint.Location = new Point(88, 453);
            qrForm.Controls.Add(hint);
            qrForm.FormClosed += (_, _) => { picture.Image?.Dispose(); };
            qrForm.ShowDialog(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not create the QR code.\r\n{ex.Message}", "Taildrop", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void FileList_ItemDrag(object? sender, ItemDragEventArgs e)
    {
        var paths = new System.Collections.Specialized.StringCollection();
        foreach (ListViewItem item in _fileList.SelectedItems)
        {
            if (item.Tag is string path && File.Exists(path)) paths.Add(path);
        }
        if (paths.Count > 0)
        {
            var data = new DataObject();
            data.SetFileDropList(paths);
            _fileList.DoDragDrop(data, DragDropEffects.Copy);
        }
    }

    void RefreshTemporaryInbox()
    {
        if (_inboxPath is null || !Directory.Exists(_inboxPath)) return;
        var selectedNames = _fileList.SelectedItems.Cast<ListViewItem>().Select(i => i.Text).ToHashSet();

        var files = new DirectoryInfo(_inboxPath).GetFiles()
            .Where(f => !(f.Name.StartsWith(".taildrop-", StringComparison.Ordinal) && f.Name.EndsWith(".part", StringComparison.Ordinal)))
            .OrderByDescending(f => f.LastWriteTime)
            .ToList();

        var newSignature = string.Join("\n", files.Select(f => $"{f.Name}|{FormatFileSize(f.Length)}|{f.LastWriteTime:h:mm:ss tt}"));
        if (_lastSignature != newSignature)
        {
            _lastSignature = newSignature;
            _fileList.BeginUpdate();
            _fileList.Items.Clear();
            foreach (var file in files)
            {
                var item = new ListViewItem(file.Name);
                item.SubItems.Add(FormatFileSize(file.Length));
                item.SubItems.Add(file.LastWriteTime.ToString("h:mm:ss tt"));
                item.Tag = file.FullName;
                if (selectedNames.Contains(file.Name)) item.Selected = true;
                _fileList.Items.Add(item);
            }
            _fileList.EndUpdate();
        }

        var hasFiles = files.Count > 0;
        _emptyLabel.Visible = !hasFiles;
        _saveAllButton.Enabled = hasFiles;
        _saveSelectedButton.Enabled = _fileList.SelectedItems.Count > 0;
    }

    static string FormatFileSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):N1} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):N1} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):N0} KB";
        return $"{bytes} B";
    }

    static string GetAvailableSavePath(string folder, string name)
    {
        var candidate = Path.Combine(folder, name);
        if (!File.Exists(candidate)) return candidate;
        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        for (var i = 1; i < 10000; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(folder, $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    void SaveInboxItems(IEnumerable<ListViewItem> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose where these files should be saved",
            SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ShowNewFolderButton = true
        };
        if (picker.ShowDialog(this) == DialogResult.OK)
        {
            var saved = 0;
            foreach (var item in list)
            {
                if (item.Tag is string sourcePath && File.Exists(sourcePath))
                {
                    var destination = GetAvailableSavePath(picker.SelectedPath, item.Text);
                    File.Copy(sourcePath, destination, overwrite: false);
                    saved++;
                }
            }
            MessageBox.Show($"Saved {saved} file{(saved == 1 ? "" : "s")}.", "Taildrop", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    static string? FindTailscaleExe()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            try
            {
                if (dir.Length == 0) continue;
                var candidate = Path.Combine(dir, "tailscale.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* ignore malformed PATH entries */ }
        }

        var fallbacks = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tailscale", "tailscale.exe")
        };
        return fallbacks.FirstOrDefault(File.Exists);
    }

    static string? GetTailscaleIPv4()
    {
        var tailscalePath = FindTailscaleExe();
        if (tailscalePath is not null)
        {
            try
            {
                var startInfo = new ProcessStartInfo(tailscalePath)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("ip");
                startInfo.ArgumentList.Add("-4");
                using var process = Process.Start(startInfo);
                if (process is not null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(3000);
                    var firstLine = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
                    if (firstLine is not null && Regex.IsMatch(firstLine, @"^100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.\d{1,3}\.\d{1,3}$"))
                        return firstLine;
                }
            }
            catch { /* fall through to network interface scan */ }
        }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var addrInfo in nic.GetIPProperties().UnicastAddresses)
            {
                if (addrInfo.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var bytes = addrInfo.Address.GetAddressBytes();
                if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return addrInfo.Address.ToString();
            }
        }
        return null;
    }
}
