using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace TaildropApp;

// The inbox half of the main window: list drawing, open/rename/remove, refresh polling and saving.
sealed partial class MainForm
{
    #region Owner-drawn list

    int NameInset => this.Px(4 + 10 + 32 + 10); // row plate inset + tile left gap + tile + gap

    void FileList_DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        var g = e.Graphics;
        using (var back = new SolidBrush(Palette.Surface)) g.FillRectangle(back, e.Bounds);
        using (var line = new Pen(Palette.Border)) g.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

        var right = e.ColumnIndex > 0;
        var rect = e.Bounds;
        rect.X += e.ColumnIndex == 0 ? NameInset : 0;
        rect.Width -= (e.ColumnIndex == 0 ? NameInset : 0) + this.Px(right ? 16 : 8);
        if (rect.Width <= 0) return;
        TextRenderer.DrawText(g, e.Header?.Text.ToUpperInvariant() ?? "", UiFonts.CaptionStrong, rect, Palette.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding |
            (right ? TextFormatFlags.Right : TextFormatFlags.Left));
    }

    void FileList_DrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        var g = e.Graphics;
        var item = e.Item;
        var row = new Rectangle(0, e.Bounds.Top, _fileList.ClientSize.Width, e.Bounds.Height);
        using (var back = new SolidBrush(Palette.Surface)) g.FillRectangle(back, row);

        var listFocused = _fileList.Focused;
        var selected = item.Selected;
        var onGreen = selected && listFocused;
        var recent = IsRecent(item.Text);
        var plate = Rectangle.Inflate(row, -this.Px(4), -this.Px(2));

        Color? plateColor = null;
        if (selected) plateColor = listFocused ? Palette.Green : Palette.SelectionIdle;
        else if (recent) plateColor = Palette.GreenPale;
        else if (ReferenceEquals(item, _fileList.HotItem)) plateColor = Palette.Hover;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (plateColor is { } fill)
        {
            using var path = UiKit.RoundedRect(plate, this.Px(8));
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }

        var tile = new Rectangle(plate.Left + this.Px(10), row.Top + (row.Height - this.Px(24)) / 2, this.Px(32), this.Px(24));
        using (var path = UiKit.RoundedRect(tile, this.Px(6)))
        using (var brush = new SolidBrush(onGreen ? Color.FromArgb(60, Color.White) : recent && !selected ? Color.White : Palette.GreenPale))
        {
            g.FillPath(brush, path);
        }
        g.SmoothingMode = SmoothingMode.Default;

        if (plateColor is null)
        {
            using var separator = new Pen(Color.FromArgb(236, 239, 233));
            g.DrawLine(separator, plate.Left + this.Px(10), row.Bottom - 1, plate.Right - this.Px(10), row.Bottom - 1);
        }

        const TextFormatFlags common = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        var ink = onGreen ? Color.White : Palette.Ink;
        var muted = onGreen ? Color.FromArgb(222, 241, 229) : Palette.Muted;

        TextRenderer.DrawText(g, ExtensionLabel(item.Text), UiFonts.Tile, tile, onGreen ? Color.White : Palette.Green,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        var nameWidth = _fileList.Columns[0].Width;
        var sizeWidth = _fileList.Columns[1].Width;
        var receivedWidth = _fileList.Columns[2].Width;
        var nameRect = new Rectangle(tile.Right + this.Px(10), row.Top, nameWidth - (tile.Right + this.Px(10)) - this.Px(8), row.Height);
        var sizeRect = new Rectangle(row.Left + nameWidth, row.Top, sizeWidth - this.Px(16), row.Height);
        var receivedRect = new Rectangle(row.Left + nameWidth + sizeWidth, row.Top, receivedWidth - this.Px(16), row.Height);
        if (nameRect.Width > 0) TextRenderer.DrawText(g, item.Text, UiFonts.Body, nameRect, ink, common | TextFormatFlags.Left);
        if (sizeRect.Width > 0) TextRenderer.DrawText(g, item.SubItems[1].Text, UiFonts.Small, sizeRect, muted, common | TextFormatFlags.Right);
        if (receivedRect.Width > 0) TextRenderer.DrawText(g, item.SubItems[2].Text, UiFonts.Small, receivedRect, muted, common | TextFormatFlags.Right);
    }

    static string ExtensionLabel(string name)
    {
        var extension = Path.GetExtension(name).TrimStart('.');
        if (extension.Length == 0) return "FILE";
        return (extension.Length > 4 ? extension[..4] : extension).ToUpperInvariant();
    }

    bool IsRecent(string name) => _recent.TryGetValue(name, out var until) && until > Environment.TickCount64;

    #endregion

    #region Inbox

    IEnumerable<ListViewItem> SelectedItems() => _fileList.SelectedItems.Cast<ListViewItem>().ToList();

    void UpdateActionStates()
    {
        var selected = _fileList.SelectedItems.Count;
        _saveSelectedButton.Enabled = selected > 0 && !_saving;
        _saveAllButton.Enabled = _fileList.Items.Count > 0 && !_saving;
        _removeButton.Enabled = selected > 0;
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

    void FileList_MouseDown(object? sender, MouseEventArgs e)
    {
        // Right-click on an unselected row selects just that row, like Explorer, so the menu acts on what was clicked.
        if (e.Button != MouseButtons.Right) return;
        var item = _fileList.HitTest(e.Location).Item;
        if (item is not null && item.Selected) return;
        _fileList.SelectedItems.Clear();
        if (item is null) return; // empty space: no selection, so no menu
        item.Selected = true;
        item.Focused = true;
    }

    void FileList_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_fileList.HitTest(e.Location).Item?.Tag is string path) OpenFile(path);
    }

    void FileList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_editing) return;
        if (e.KeyCode == Keys.Delete && !e.Control && !e.Alt)
        {
            RemoveSelected();
        }
        else if (e.KeyCode == Keys.F2)
        {
            BeginRename();
        }
        else if (e.KeyCode == Keys.A && e.Control)
        {
            _fileList.BeginUpdate();
            try { foreach (ListViewItem item in _fileList.Items) item.Selected = true; }
            finally { _fileList.EndUpdate(); }
        }
        else if (e.KeyCode == Keys.Enter)
        {
            OpenItems(SelectedItems());
        }
        else
        {
            return;
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    void OpenItems(IEnumerable<ListViewItem> items)
    {
        foreach (var item in items)
        {
            if (item.Tag is string path) OpenFile(path);
        }
    }

    void OpenFile(string path)
    {
        var name = Path.GetFileName(path);
        if (!File.Exists(path))
        {
            ShowStatus($"{name} is no longer in the inbox.", Palette.Red);
            RefreshTemporaryInbox();
            return;
        }
        // Files from a phone never carry a "downloaded from the internet" mark, so Windows would run these without asking.
        if (RiskyExtensions.Contains(Path.GetExtension(path)) &&
            MessageBox.Show(this, $"{name} is a program or script and can run code on this computer.\r\n\r\nOpen it anyway?", "Taildrop",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus($"Could not open {name}: {ex.Message}", Palette.Red);
        }
    }

    void RemoveSelected()
    {
        if (_editing) return;
        var items = SelectedItems().ToList();
        if (items.Count == 0) return;

        var firstIndex = items[0].Index;
        var removed = 0;
        string? failure = null;
        foreach (var item in items)
        {
            if (item.Tag is not string path) continue;
            try
            {
                File.Delete(path); // already-missing files are fine
                _server?.OnInboxFileRemoved(item.Text);
                removed++;
            }
            catch (Exception ex)
            {
                failure ??= $"{item.Text}: {ex.Message}";
            }
        }

        _pendingIndex = firstIndex;
        RefreshTemporaryInbox();
        _pendingIndex = null; // consumed by the rebuild above, or not needed if nothing changed
        if (failure is not null)
            ShowStatus($"Could not remove everything. {failure}", Palette.Red);
        else
            ShowStatus($"Removed {removed} file{(removed == 1 ? "" : "s")} from the inbox.", Palette.Muted);
    }

    void BeginRename()
    {
        if (_editing || _fileList.SelectedItems.Count == 0) return;
        var item = _fileList.FocusedItem is { Selected: true } focused ? focused : _fileList.SelectedItems[0];
        item.EnsureVisible();
        item.BeginEdit();
    }

    void FileList_BeforeLabelEdit(object? sender, LabelEditEventArgs e)
    {
        _editing = true; // the refresh timer must not rebuild the list under the editor
        if (e.Item < 0 || e.Item >= _fileList.Items.Count) return;
        var name = _fileList.Items[e.Item].Text;
        var stem = Path.GetFileNameWithoutExtension(name).Length;
        // Like Explorer: select the name, leave the extension out of the selection.
        BeginInvoke(new Action(() => _fileList.SelectLabelEditorPrefix(stem > 0 ? stem : name.Length)));
    }

    void FileList_AfterLabelEdit(object? sender, LabelEditEventArgs e)
    {
        var item = e.Item >= 0 && e.Item < _fileList.Items.Count ? _fileList.Items[e.Item] : null;
        e.CancelEdit = true; // the row is rebuilt from disk once the file has actually moved

        string? newName = null;
        if (e.Label is not null && item?.Tag is string oldPath)
            newName = RenameInboxFile(oldPath, e.Label);

        // Deferred so the list is not rebuilt inside its own edit notification.
        BeginInvoke(new Action(() =>
        {
            _editing = false;
            if (newName is not null)
            {
                _pendingSelect = newName;
                RefreshTemporaryInbox();
                _pendingSelect = null;
            }
        }));
    }

    /// <summary>Renames a file inside the inbox. Returns the final name, or null if nothing changed.</summary>
    string? RenameInboxFile(string oldPath, string typed)
    {
        var oldName = Path.GetFileName(oldPath);
        var cleaned = SanitizeFileName(typed);
        if (cleaned.Length == 0)
        {
            ShowStatus("A file name cannot be empty.", Palette.Red);
            return null;
        }
        var oldExtension = Path.GetExtension(oldName);
        if (Path.GetExtension(cleaned).Length == 0 && oldExtension.Length > 0) cleaned += oldExtension; // keep the type unless a new one was typed
        if (string.Equals(cleaned, oldName, StringComparison.Ordinal)) return null;

        if (!File.Exists(oldPath))
        {
            ShowStatus($"{oldName} is no longer in the inbox.", Palette.Red);
            return null;
        }

        var folder = Path.GetDirectoryName(oldPath)!;
        // A change of letter case only is the same file as far as Windows is concerned, so it is not a collision.
        var target = string.Equals(cleaned, oldName, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(folder, cleaned)
            : GetAvailableSavePath(folder, cleaned);
        try
        {
            File.Move(oldPath, target);
        }
        catch (Exception ex)
        {
            ShowStatus($"Could not rename {oldName}: {ex.Message}", Palette.Red);
            return null;
        }

        var finalName = Path.GetFileName(target);
        _server?.OnInboxFileRenamed(oldName, finalName);
        _knownNames.Remove(oldName);
        _knownNames.Add(finalName); // a rename is not a new arrival
        return finalName;
    }

    static string SanitizeFileName(string name)
    {
        var chars = name.Trim().Select(c => Array.IndexOf(InvalidNameChars, c) >= 0 ? '_' : c).ToArray();
        var cleaned = new string(chars);
        if (cleaned.Length > 180) cleaned = cleaned[..180];
        cleaned = cleaned.TrimEnd('.', ' '); // Windows silently drops these, which could make two names collide
        if (cleaned.Length == 0) return "";
        if (cleaned.StartsWith(".taildrop-", StringComparison.Ordinal)) cleaned = "_" + cleaned; // reserved for in-flight uploads
        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(cleaned))) cleaned = "_" + cleaned;
        return cleaned;
    }

    #endregion

    #region Inbox refresh

    static IEnumerable<FileInfo> ReadInbox(string path) =>
        new DirectoryInfo(path).GetFiles() // top level only: the hidden .scans folder is a directory and never listed
            .Where(f => !(f.Name.StartsWith(".taildrop-", StringComparison.Ordinal) && f.Name.EndsWith(".part", StringComparison.Ordinal)))
            .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .OrderByDescending(f => f.LastWriteTime);

    void RefreshTemporaryInbox()
    {
        if (_inboxPath is null || _editing || !Directory.Exists(_inboxPath)) return;

        List<FileInfo> files;
        try { files = ReadInbox(_inboxPath).ToList(); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        var signature = string.Join("\n", files.Select(f => $"{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}"));
        if (_lastSignature != signature)
        {
            _lastSignature = signature;
            RebuildList(files);
        }
        PruneRecent();

        var hasFiles = files.Count > 0;
        if (_fileList.Visible != hasFiles)
        {
            _fileList.Visible = hasFiles;
            if (hasFiles) LayoutColumns(); // an invisible control is skipped by docking, so its width may be stale until now
        }
        _emptyState.Visible = !hasFiles;
        UpdateActionStates();
    }

    void RebuildList(List<FileInfo> files)
    {
        var selectedNames = _fileList.SelectedItems.Cast<ListViewItem>().Select(i => i.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_pendingSelect is not null)
        {
            selectedNames.Clear();
            selectedNames.Add(_pendingSelect);
        }
        var focusedName = _fileList.FocusedItem?.Text ?? _pendingSelect;
        var top = _fileList.TopItem;
        var topName = top is not null && top.Index > 0 ? top.Text : null; // stay at the top when already there, so new files are seen

        var arrivals = files.Where(f => !_knownNames.Contains(f.Name)).Select(f => f.Name).ToList();
        _knownNames = files.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = files.Select(file =>
        {
            var item = new ListViewItem(file.Name) { Tag = file.FullName };
            item.SubItems.Add(FormatFileSize(file.Length));
            item.SubItems.Add(file.LastWriteTime.ToString("h:mm:ss tt"));
            return item;
        }).ToArray();

        _rebuilding = true;
        _fileList.BeginUpdate();
        try
        {
            _fileList.Items.Clear();
            _fileList.Items.AddRange(items);
            foreach (var item in items)
            {
                if (selectedNames.Contains(item.Text)) item.Selected = true;
                if (string.Equals(item.Text, focusedName, StringComparison.OrdinalIgnoreCase)) item.Focused = true;
            }
            if (_pendingIndex is { } index && items.Length > 0 && _fileList.SelectedItems.Count == 0)
            {
                // After a removal, keep keyboard flow going with the neighbouring row.
                var next = items[Math.Min(index, items.Length - 1)];
                next.Selected = true;
                next.Focused = true;
            }
            if (topName is not null)
            {
                var restore = items.FirstOrDefault(i => string.Equals(i.Text, topName, StringComparison.OrdinalIgnoreCase));
                if (restore is not null) _fileList.TopItem = restore;
            }
        }
        finally
        {
            _fileList.EndUpdate();
            _rebuilding = false;
        }
        _pendingSelect = null;
        _pendingIndex = null;
        LayoutColumns();

        Text = files.Count > 0 ? $"Taildrop ({files.Count})" : "Taildrop";
        if (arrivals.Count > 0) OnFilesArrived(arrivals);
    }

    void OnFilesArrived(List<string> names)
    {
        var until = Environment.TickCount64 + NewFileTintMs;
        foreach (var name in names) _recent[name] = until;
        if (!ReferenceEquals(ActiveForm, this)) Native.FlashTaskbar(this);
    }

    void PruneRecent()
    {
        if (_recent.Count == 0) return;
        var now = Environment.TickCount64;
        var expired = _recent.Where(kv => kv.Value <= now || !_knownNames.Contains(kv.Key)).Select(kv => kv.Key).ToList();
        if (expired.Count == 0) return;
        foreach (var name in expired) _recent.Remove(name);
        _fileList.Invalidate();
    }

    static string FormatFileSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):N1} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):N1} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):N0} KB";
        return $"{bytes} B";
    }

    #endregion

    #region Saving

    static string GetAvailableSavePath(string folder, string name)
    {
        var candidate = Path.Combine(folder, name);
        if (!PathExists(candidate)) return candidate;
        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        for (var i = 1; i < 10000; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!PathExists(candidate)) return candidate;
        }
        return Path.Combine(folder, $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    async Task SaveInboxItemsAsync(IEnumerable<ListViewItem> items)
    {
        if (_saving || _editing) return;
        var sources = items.Where(i => i.Tag is string).Select(i => (Path: (string)i.Tag!, Name: i.Text)).ToList();
        if (sources.Count == 0) return;

        using var picker = new FolderBrowserDialog
        {
            Description = "Choose where these files should be saved",
            UseDescriptionForTitle = true,
            SelectedPath = _lastSaveFolder,
            ShowNewFolderButton = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        var folder = picker.SelectedPath;
        _lastSaveFolder = folder;
        _saving = true;
        UpdateActionStates();
        ShowStatus($"Saving {sources.Count} file{(sources.Count == 1 ? "" : "s")}...", Palette.Muted);

        var saved = new List<string>();
        string? failure = null;
        await Task.Run(() =>
        {
            foreach (var (sourcePath, name) in sources)
            {
                try
                {
                    if (!File.Exists(sourcePath)) continue;
                    var destination = GetAvailableSavePath(folder, name);
                    File.Copy(sourcePath, destination, overwrite: false);
                    saved.Add(destination);
                }
                catch (Exception ex)
                {
                    failure ??= $"{name}: {ex.Message}";
                }
            }
        });

        if (IsDisposed) return;
        _saving = false;
        UpdateActionStates();

        var folderName = Path.GetFileName(folder.TrimEnd('\\', '/'));
        if (folderName.Length == 0) folderName = folder;
        if (failure is not null)
            ShowStatus($"Saved {saved.Count} of {sources.Count}. Could not save {failure}", Palette.Red);
        else if (saved.Count == 0)
            ShowStatus("Nothing to save: those files are no longer in the inbox.", Palette.Red);
        else
            ShowStatus($"Saved {saved.Count} file{(saved.Count == 1 ? "" : "s")} to {folderName}", Palette.Green, saved[0]);
    }

    #endregion
}
