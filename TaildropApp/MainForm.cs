using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Taildrop.Core;

namespace TaildropApp;

sealed partial class MainForm : Form
{
    const int Port = 8787;
    const int NewFileTintMs = 3500;
    const int StatusClearMs = 20000;

    static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();
    static readonly HashSet<string> ReservedNames = new(
        new[] { "CON", "PRN", "AUX", "NUL" }.Concat(Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })),
        StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> RiskyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".pif", ".ps1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".hta", ".lnk", ".reg", ".cpl", ".jar", ".msc"
    };

    // Connect card
    readonly StatusDot _statusDot = new();
    readonly Label _statusLabel = NewLabel("Starting...", UiFonts.BodyStrong, Palette.Muted);
    readonly Label _captionLabel = NewLabel("", UiFonts.Small, Palette.Muted);
    readonly QrView _qrView = new() { Placeholder = "Starting..." };
    readonly Label _messageLabel = NewLabel("Finding your Tailscale address...", UiFonts.Body, Palette.Muted);
    // Doubles as "Try again" when the receiver could not start.
    readonly RoundedButton _copyButton = new("Copy link", ButtonKind.Primary);

    // Inbox pane
    readonly InboxListView _fileList = new();
    readonly EmptyState _emptyState = new("Your inbox is empty", "Files and scans sent from your phone will appear here.");
    readonly RoundedButton _saveSelectedButton = new("Save selected", ButtonKind.Primary, 140);
    readonly RoundedButton _saveAllButton = new("Save all", ButtonKind.Secondary, 100);
    readonly RoundedButton _removeButton = new("Remove", ButtonKind.Plain, 100);
    readonly Label _resultLabel = NewLabel("", UiFonts.Small, Palette.Muted);
    readonly LinkLabel _revealLink = new();
    readonly ContextMenuStrip _menu = new() { Font = UiFonts.Body, ShowImageMargin = false };
    readonly ToolStripMenuItem _openItem = new("Open");
    readonly ToolStripMenuItem _saveItem = new("Save...");
    readonly ToolStripMenuItem _renameItem = new("Rename") { ShortcutKeyDisplayString = "F2" };
    readonly ToolStripMenuItem _removeItem = new("Remove from inbox") { ShortcutKeyDisplayString = "Del" };
    readonly ImageList _rowSizer = new() { ColorDepth = ColorDepth.Depth32Bit };

    readonly System.Windows.Forms.Timer _inboxTimer = new() { Interval = 400 };
    readonly System.Windows.Forms.Timer _copyTimer = new() { Interval = 1400 };
    readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = StatusClearMs };

    readonly Dictionary<string, long> _recent = new(StringComparer.OrdinalIgnoreCase); // file name -> tint expiry (TickCount64)
    HashSet<string> _knownNames = new(StringComparer.OrdinalIgnoreCase);

    TaildropServer? _server;
    Process? _cleanupProcess;
    Bitmap? _qrBitmap;
    string? _taildropUrl;
    string? _inboxPath;
    string? _revealPath;
    string? _pendingSelect;
    int? _pendingIndex;
    string _lastSignature = "";
    string _lastSaveFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    bool _cleanupStarted;
    bool _starting;
    bool _failed;
    bool _saving;
    bool _editing;
    bool _rebuilding;

    public MainForm()
    {
        SuspendLayout();
        Text = "Taildrop";
        BackColor = Palette.Background;
        ForeColor = Palette.Ink;
        Font = UiFonts.Body;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(24, 16, 24, 20);

        _rowSizer.ImageSize = new Size(1, this.Px(40)); // gives list rows a roomy, easy-to-hit height

        // WinForms docks in reverse z-order: the fill pane is added first, edge panels after it.
        var inbox = BuildInboxPane();
        var gap = new Panel { Dock = DockStyle.Left, Width = 20 };
        var connect = BuildConnectCard();
        var header = BuildHeader();
        connect.TabIndex = 0;
        inbox.TabIndex = 1;
        Controls.Add(inbox);
        Controls.Add(gap);
        Controls.Add(connect);
        Controls.Add(header);

        _copyTimer.Tick += (_, _) => { _copyButton.Text = "Copy link"; _copyTimer.Stop(); };
        _inboxTimer.Tick += (_, _) => RefreshTemporaryInbox();
        _statusTimer.Tick += (_, _) => ShowStatus("", Palette.Muted);

        // Everything above is authored at 96 dpi; the form scales it. Custom painting scales itself (UiKit.Px).
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);

        Shown += MainForm_Shown;
        FormClosing += MainForm_FormClosing;
    }

    protected override void OnLoad(EventArgs e)
    {
        // Sized here, in real pixels: a 1080p screen at 150% only has ~720 logical px of height.
        var area = Screen.FromPoint(MousePosition).WorkingArea;
        MinimumSize = new Size(Math.Min(this.Px(800), area.Width), Math.Min(this.Px(560), area.Height));
        var width = Math.Min(this.Px(980), area.Width - this.Px(32));
        var height = Math.Min(this.Px(640), area.Height - this.Px(32));
        Size = new Size(Math.Max(width, MinimumSize.Width), Math.Max(height, MinimumSize.Height));
        CenterToScreen();
        base.OnLoad(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inboxTimer.Dispose();
            _copyTimer.Dispose();
            _statusTimer.Dispose();
        }
        base.Dispose(disposing); // disposes the child controls first, so nothing still points at the items below
        if (disposing)
        {
            _menu.Dispose();
            _rowSizer.Dispose();
            _qrBitmap?.Dispose();
            _qrBitmap = null;
        }
    }

    static Label NewLabel(string text, Font font, Color color) => new()
    {
        Text = text,
        Font = font,
        ForeColor = color,
        AutoSize = true,
        UseMnemonic = false
    };

    #region Layout

    Control BuildHeader()
    {
        var title = NewLabel("Taildrop", UiFonts.Display, Palette.Ink);
        title.Dock = DockStyle.Left;
        title.TextAlign = ContentAlignment.MiddleLeft;

        var subtitle = NewLabel("Phone to this computer, privately over Tailscale", UiFonts.Small, Palette.Muted);
        subtitle.AutoSize = false;
        subtitle.Dock = DockStyle.Fill;
        subtitle.TextAlign = ContentAlignment.BottomLeft;
        subtitle.Padding = new Padding(12, 0, 0, 10);

        var header = new Panel { Dock = DockStyle.Top, Height = 52 };
        header.Controls.Add(subtitle);
        header.Controls.Add(title);
        return header;
    }

    Control BuildConnectCard()
    {
        var card = new CardPanel { Dock = DockStyle.Left, Width = 340, Padding = new Padding(18) };

        var heading = NewLabel("Connect your phone", UiFonts.Heading, Palette.Ink);
        heading.AutoSize = false;
        heading.Dock = DockStyle.Top;
        heading.Height = 28;
        heading.TextAlign = ContentAlignment.MiddleLeft;

        _statusLabel.AutoSize = false;
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Padding = new Padding(6, 0, 0, 0);
        _statusDot.Dock = DockStyle.Left;
        var statusRow = new Panel { Dock = DockStyle.Top, Height = 26 };
        statusRow.Controls.Add(_statusLabel);
        statusRow.Controls.Add(_statusDot);

        _captionLabel.AutoSize = false;
        _captionLabel.Dock = DockStyle.Top;
        _captionLabel.Height = 24;
        _captionLabel.TextAlign = ContentAlignment.MiddleLeft;

        _qrView.Dock = DockStyle.Fill;
        _qrView.Click += (_, _) => ShowQrDialog();

        _messageLabel.AutoSize = false;
        _messageLabel.Dock = DockStyle.Bottom;
        _messageLabel.Height = 52;
        _messageLabel.TextAlign = ContentAlignment.MiddleCenter;
        _messageLabel.Padding = new Padding(0, 4, 0, 4);
        _messageLabel.AutoEllipsis = true;

        _copyButton.Dock = DockStyle.Bottom;
        _copyButton.Enabled = false;
        _copyButton.Click += CopyButton_Click;

        // Fill first, then edge docks; the last one added is laid out first (heading topmost, Copy lowest).
        card.Controls.Add(_qrView);
        card.Controls.Add(_messageLabel);
        card.Controls.Add(_copyButton);
        card.Controls.Add(_captionLabel);
        card.Controls.Add(statusRow);
        card.Controls.Add(heading);
        return card;
    }

    Control BuildInboxPane()
    {
        var pane = new Panel { Dock = DockStyle.Fill };

        var title = NewLabel("Inbox", UiFonts.Heading, Palette.Ink);
        title.Dock = DockStyle.Left;
        title.TextAlign = ContentAlignment.MiddleLeft;
        var hint = NewLabel("Double-click to open  ·  drag files out to Explorer", UiFonts.Small, Palette.Muted);
        hint.AutoSize = false;
        hint.Dock = DockStyle.Fill;
        hint.TextAlign = ContentAlignment.MiddleRight;
        hint.AutoEllipsis = true;
        var headingRow = new Panel { Dock = DockStyle.Top, Height = 34 };
        headingRow.Controls.Add(hint);
        headingRow.Controls.Add(title);

        ConfigureList();
        var listCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(6) };
        _fileList.Dock = DockStyle.Fill;
        _emptyState.Dock = DockStyle.Fill;
        listCard.Controls.Add(_fileList);
        listCard.Controls.Add(_emptyState);

        var spacer = new Panel { Dock = DockStyle.Bottom, Height = 12 };

        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        foreach (var button in new[] { _saveSelectedButton, _saveAllButton, _removeButton })
        {
            button.Margin = new Padding(0, 0, 8, 0);
            button.Enabled = false;
            actions.Controls.Add(button);
        }
        _saveSelectedButton.Click += async (_, _) => await SaveInboxItemsAsync(SelectedItems());
        _saveAllButton.Click += async (_, _) => await SaveInboxItemsAsync(_fileList.Items.Cast<ListViewItem>());
        _removeButton.Click += (_, _) => RemoveSelected();

        _resultLabel.Margin = new Padding(0, 6, 0, 0);
        _revealLink.Text = "Show in folder";
        _revealLink.LinkArea = new LinkArea(0, _revealLink.Text.Length); // the whole label is the link
        _revealLink.Font = UiFonts.Small;
        _revealLink.AutoSize = true;
        _revealLink.Margin = new Padding(6, 6, 0, 0);
        _revealLink.LinkColor = Palette.Green;
        _revealLink.ActiveLinkColor = Color.FromArgb(20, 80, 44);
        _revealLink.VisitedLinkColor = Palette.Green;
        _revealLink.LinkBehavior = LinkBehavior.HoverUnderline;
        _revealLink.UseMnemonic = false;
        _revealLink.Visible = false;
        _revealLink.LinkClicked += (_, _) => RevealInExplorer();
        var statusRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 28, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        statusRow.Controls.Add(_resultLabel);
        statusRow.Controls.Add(_revealLink);

        var footer = NewLabel("Closing Taildrop permanently deletes anything left here.", UiFonts.Caption, Palette.Muted);
        footer.AutoSize = false;
        footer.Dock = DockStyle.Bottom;
        footer.Height = 24;
        footer.TextAlign = ContentAlignment.MiddleLeft;

        // Fill first; bottom stack is laid out last-added-first (footer lowest, then status, actions, gap).
        pane.Controls.Add(listCard);
        pane.Controls.Add(spacer);
        pane.Controls.Add(actions);
        pane.Controls.Add(statusRow);
        pane.Controls.Add(footer);
        pane.Controls.Add(headingRow);

        BuildMenu();
        return pane;
    }

    void ConfigureList()
    {
        _fileList.View = View.Details;
        _fileList.FullRowSelect = true;
        _fileList.MultiSelect = true;
        _fileList.HideSelection = false;
        _fileList.GridLines = false;
        _fileList.BorderStyle = BorderStyle.None;
        _fileList.BackColor = Palette.Surface;
        _fileList.ForeColor = Palette.Ink;
        _fileList.Font = UiFonts.Body;
        _fileList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _fileList.OwnerDraw = true;
        _fileList.LabelEdit = true;
        _fileList.SmallImageList = _rowSizer;
        _fileList.AccessibleName = "Inbox files";
        _fileList.Visible = false; // the empty state shows until the first file arrives
        _fileList.Columns.Add("Name", 300);
        _fileList.Columns.Add("Size", 90);
        _fileList.Columns.Add("Received", 110);

        _fileList.ItemDrag += FileList_ItemDrag;
        _fileList.SelectedIndexChanged += (_, _) => { if (!_rebuilding) UpdateActionStates(); };
        _fileList.MouseDoubleClick += FileList_MouseDoubleClick;
        _fileList.MouseDown += FileList_MouseDown;
        _fileList.KeyDown += FileList_KeyDown;
        _fileList.BeforeLabelEdit += FileList_BeforeLabelEdit;
        _fileList.AfterLabelEdit += FileList_AfterLabelEdit;
        _fileList.DrawColumnHeader += FileList_DrawColumnHeader;
        _fileList.DrawItem += FileList_DrawItem;
        _fileList.ClientSizeChanged += (_, _) => LayoutColumns();
        _fileList.GotFocus += (_, _) => _fileList.Invalidate();
        _fileList.LostFocus += (_, _) => _fileList.Invalidate();
        _fileList.ContextMenuStrip = _menu;
    }

    void BuildMenu()
    {
        _openItem.Font = UiFonts.BodyStrong;
        _openItem.Click += (_, _) => OpenItems(SelectedItems());
        _saveItem.Click += async (_, _) => await SaveInboxItemsAsync(SelectedItems());
        _renameItem.Click += (_, _) => BeginRename();
        _removeItem.Click += (_, _) => RemoveSelected();
        _menu.Items.AddRange(new ToolStripItem[] { _openItem, _saveItem, _renameItem, new ToolStripSeparator(), _removeItem });
        _menu.Opening += (_, e) =>
        {
            if (_editing || _fileList.SelectedItems.Count == 0) { e.Cancel = true; return; }
            _renameItem.Enabled = _fileList.SelectedItems.Count == 1;
        };
    }

    void LayoutColumns()
    {
        var total = _fileList.ClientSize.Width;
        if (total <= 0) return;
        var size = this.Px(90);
        var received = this.Px(110);
        _fileList.SetColumnWidths(Math.Max(this.Px(160), total - size - received), size, received);
    }

    #endregion

    #region Startup, shutdown, server

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
        _statusTimer.Stop();
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
        _starting = true;
        try
        {
            SetStartingState();
            await StopTaildropAsync();

            var tailscaleIp = await Task.Run(GetTailscaleIPv4);
            if (tailscaleIp is null)
            {
                SetErrorState("Connect Tailscale on this computer, then click Try again.");
                return;
            }

            _taildropUrl = $"http://{tailscaleIp}:{Port}";
            try
            {
                _server = new TaildropServer(_inboxPath!);
                await _server.StartAsync(IPAddress.Parse(tailscaleIp), Port);
                if (_cleanupProcess is null) StartCleanupWatcher(Environment.ProcessId);
            }
            catch (Exception ex)
            {
                await StopTaildropAsync();
                SetErrorState(ex.Message.Contains("already in use") || ex.Message.Contains("address")
                    ? $"Port {Port} is busy or the receiver could not start. Close any other program using it, then click Try again."
                    : ex.Message);
                return;
            }

            SetReadyState(_taildropUrl);
        }
        finally
        {
            _starting = false;
        }
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

    #endregion

    #region Connect card state

    void SetStartingState()
    {
        _failed = false;
        _statusDot.DotColor = Palette.Muted;
        _statusDot.Halo = false;
        _statusLabel.Text = "Starting...";
        _statusLabel.ForeColor = Palette.Muted;
        _captionLabel.Text = "";
        SetQr(null, "Starting...");
        _messageLabel.Font = UiFonts.Body;
        _messageLabel.Text = "Finding your Tailscale address...";
        _messageLabel.ForeColor = Palette.Muted;
        _copyTimer.Stop();
        _copyButton.Text = "Copy link";
        _copyButton.Enabled = false;
    }

    void SetReadyState(string url)
    {
        _failed = false;
        _statusDot.DotColor = Palette.Green;
        _statusDot.Halo = true;
        _statusLabel.Text = "Ready - keep this window open";
        _statusLabel.ForeColor = Palette.Green;
        _captionLabel.Text = "Scan with your phone's camera";
        try
        {
            SetQr(CreateQrBitmap(url), "");
        }
        catch
        {
            SetQr(null, "QR code unavailable. Use the link below.");
        }
        _messageLabel.Font = UiFonts.BodyStrong;
        _messageLabel.Text = url;
        _messageLabel.ForeColor = Palette.Ink;
        _copyButton.Text = "Copy link";
        _copyButton.Enabled = true;
    }

    void SetErrorState(string message)
    {
        _failed = true;
        _statusDot.DotColor = Palette.Red;
        _statusDot.Halo = false;
        _statusLabel.Text = "Not running";
        _statusLabel.ForeColor = Palette.Red;
        _captionLabel.Text = "";
        SetQr(null, "No QR code yet");
        _messageLabel.Font = UiFonts.Small;
        _messageLabel.Text = message;
        _messageLabel.ForeColor = Palette.Red;
        _copyTimer.Stop();
        _copyButton.Text = "Try again";
        _copyButton.Enabled = true;
    }

    void SetQr(Bitmap? bitmap, string placeholder)
    {
        _qrView.Image = bitmap;
        _qrView.Placeholder = placeholder;
        _qrBitmap?.Dispose();
        _qrBitmap = bitmap;
    }

    static Bitmap CreateQrBitmap(string url)
    {
        using var stream = new MemoryStream(QrCode.GeneratePng(url));
        using var source = Image.FromStream(stream);
        return new Bitmap(source); // detached copy: the stream can go away
    }

    async void CopyButton_Click(object? sender, EventArgs e)
    {
        if (_failed)
        {
            if (_starting || _inboxPath is null) return;
            try { await StartTaildropAsync(); }
            catch (Exception ex) { SetErrorState(ex.Message); }
            return;
        }

        if (_taildropUrl is null) return;
        try { Clipboard.SetText(_taildropUrl); }
        catch
        {
            ShowStatus("Could not reach the clipboard. Please try again.", Palette.Red);
            return;
        }
        _copyButton.Text = "Copied";
        _copyTimer.Stop();
        _copyTimer.Start();
    }

    void ShowQrDialog()
    {
        if (_qrBitmap is null || _taildropUrl is null) return;
        using var dialog = new QrDialog(_qrBitmap, _taildropUrl);
        dialog.ShowDialog(this);
    }

    #endregion

    #region Inline status

    /// <summary>One status line under the inbox actions: replaces modal "done" dialogs. With a path it adds a "Show in folder" link.</summary>
    void ShowStatus(string text, Color color, string? revealPath = null)
    {
        _statusTimer.Stop();
        _resultLabel.Text = revealPath is null ? text : text + "  ·"; // the link's left margin supplies the space after the dot
        _resultLabel.ForeColor = color;
        _revealPath = revealPath;
        _revealLink.Visible = revealPath is not null;
        if (text.Length > 0) _statusTimer.Start();
    }

    void RevealInExplorer()
    {
        if (_revealPath is not { } path) return;
        try
        {
            // One Arguments string on purpose: ArgumentList would escape the quotes and break /select,.
            var arguments = File.Exists(path)
                ? $"/select,\"{path}\""
                : $"\"{Path.GetDirectoryName(path)}\"";
            using var explorer = Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = arguments, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            ShowStatus($"Could not open File Explorer: {ex.Message}", Palette.Red);
        }
    }

    #endregion

    #region Tailscale discovery

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

    #endregion
}
