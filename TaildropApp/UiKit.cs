using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TaildropApp;

static class Palette
{
    public static readonly Color Background = Color.FromArgb(246, 247, 244);
    public static readonly Color Surface = Color.White;
    public static readonly Color Ink = Color.FromArgb(25, 28, 24);
    public static readonly Color Muted = Color.FromArgb(102, 110, 100);
    public static readonly Color Green = Color.FromArgb(30, 106, 61);
    public static readonly Color GreenPale = Color.FromArgb(224, 244, 231);
    public static readonly Color Red = Color.FromArgb(168, 57, 47);

    public static readonly Color Border = Color.FromArgb(222, 227, 219);
    public static readonly Color ControlBorder = Color.FromArgb(205, 212, 202);
    public static readonly Color Hover = Color.FromArgb(242, 245, 240);
    public static readonly Color SelectionIdle = Color.FromArgb(214, 228, 219);
    public static readonly Color DisabledFill = Color.FromArgb(232, 235, 230);
    public static readonly Color DisabledText = Color.FromArgb(152, 160, 150);
    public static readonly Color Placeholder = Color.FromArgb(240, 242, 238);
}

/// <summary>Shared fonts. Static on purpose: they live as long as the app, and controls never dispose a font they were handed.</summary>
static class UiFonts
{
    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font BodyStrong = new("Segoe UI Semibold", 10f);
    public static readonly Font Small = new("Segoe UI", 9f);
    public static readonly Font Caption = new("Segoe UI", 8.5f);
    public static readonly Font CaptionStrong = new("Segoe UI Semibold", 8f);
    public static readonly Font Tile = new("Segoe UI Semibold", 7f);
    public static readonly Font Heading = new("Segoe UI Semibold", 12.5f);
    public static readonly Font Display = new("Segoe UI Semibold", 20f);
    public static readonly Font Button = new("Segoe UI Semibold", 10f);
}

static class UiKit
{
    /// <summary>Converts a 96-dpi design value to real pixels. Only for custom painting and runtime sizes; the form's AutoScaleMode already scales designed control sizes.</summary>
    public static int Px(this Control control, int logical) => (int)Math.Round(logical * control.DeviceDpi / 96.0);

    public static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (d <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>The colour behind a control (first opaque ancestor), so anti-aliased corners blend into it.</summary>
    public static Color BackdropOf(Control control)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.BackColor.A == 255) return parent.BackColor;
        }
        return Palette.Background;
    }
}

static class Native
{
    const uint FLASHW_TRAY = 0x2;
    const uint FLASHW_TIMERNOFG = 0xC;
    const int LVM_GETEDITCONTROL = 0x1018;
    const int EM_SETSEL = 0xB1;

    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    static extern bool FlashWindowEx(ref FLASHWINFO info);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hWnd, string subAppName, string? subIdList);

    /// <summary>Highlights the taskbar button until the window comes to the foreground.</summary>
    public static void FlashTaskbar(Form form)
    {
        if (!form.IsHandleCreated) return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = form.Handle,
            dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
            uCount = uint.MaxValue,
            dwTimeout = 0
        };
        FlashWindowEx(ref info);
    }

    /// <summary>Modern scroll bars for a list view.</summary>
    public static void UseExplorerTheme(IntPtr handle)
    {
        try { SetWindowTheme(handle, "Explorer", null); } catch { /* cosmetic only */ }
    }

    /// <summary>Selects the first <paramref name="length"/> characters of the list view's in-place label editor, if one is open.</summary>
    public static void SelectInLabelEditor(IntPtr listViewHandle, int length)
    {
        var edit = SendMessage(listViewHandle, LVM_GETEDITCONTROL, IntPtr.Zero, IntPtr.Zero);
        if (edit != IntPtr.Zero) SendMessage(edit, EM_SETSEL, IntPtr.Zero, (IntPtr)length);
    }
}

enum ButtonKind { Primary, Secondary, Plain }

/// <summary>
/// Owner-painted button: an anti-aliased rounded shape filled over the parent's colour, with hover, pressed,
/// disabled and keyboard-focus states. The shape is inset from the control edge to leave room for the focus ring,
/// so a 46px control paints a 40px button.
/// </summary>
sealed class RoundedButton : Button
{
    const int Radius = 10;
    const int Inset = 3;

    ButtonKind _kind;
    bool _hover;
    bool _mouseDown;
    bool _spaceDown;

    public RoundedButton(string text, ButtonKind kind, int width = 120)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _kind = kind;
        Text = text;
        AccessibleName = text;
        Font = UiFonts.Button;
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        Margin = Padding.Empty;
        Size = new Size(width, 46);
    }

    public ButtonKind Kind
    {
        get => _kind;
        set { _kind = value; Invalidate(); }
    }

    (Color Fill, Color Text, Color Border) CurrentColors()
    {
        if (!Enabled)
        {
            return _kind == ButtonKind.Plain
                ? (Palette.Surface, Palette.DisabledText, Palette.Border)
                : (Palette.DisabledFill, Palette.DisabledText, Color.Transparent);
        }

        var down = _spaceDown || (_mouseDown && _hover);
        return _kind switch
        {
            ButtonKind.Primary => (
                down ? Color.FromArgb(20, 80, 44) : _hover ? Color.FromArgb(38, 124, 72) : Palette.Green,
                Color.White,
                Color.Transparent),
            ButtonKind.Secondary => (
                down ? Color.FromArgb(184, 224, 199) : _hover ? Color.FromArgb(207, 236, 218) : Palette.GreenPale,
                Palette.Green,
                Color.Transparent),
            _ => (
                down ? Color.FromArgb(231, 235, 228) : _hover ? Palette.Hover : Palette.Surface,
                Palette.Ink,
                _hover || down ? Color.FromArgb(178, 188, 175) : Palette.ControlBorder)
        };
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        // OnPaint clears to the parent's colour itself.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(UiKit.BackdropOf(this));

        var inset = this.Px(Inset);
        var shape = new RectangleF(inset + 0.5f, inset + 0.5f, Width - 2 * inset - 1f, Height - 2 * inset - 1f);
        if (shape.Width < 4 || shape.Height < 4) return;

        var (fill, textColor, border) = CurrentColors();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = UiKit.RoundedRect(shape, this.Px(Radius)))
        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, path);
            if (border.A != 0)
            {
                using var pen = new Pen(border);
                g.DrawPath(pen, path);
            }
        }

        if (Enabled && Focused && ShowFocusCues)
        {
            var ringWidth = Math.Max(2f, this.Px(2));
            var ring = new RectangleF(ringWidth / 2, ringWidth / 2, Width - ringWidth, Height - ringWidth);
            using var ringPath = UiKit.RoundedRect(ring, this.Px(Radius + Inset - 1));
            using var ringPen = new Pen(Color.FromArgb(150, Palette.Green), ringWidth);
            g.DrawPath(ringPen, ringPath);
        }
        g.SmoothingMode = SmoothingMode.Default;

        var textBounds = Rectangle.Round(shape);
        if (Enabled && (_spaceDown || (_mouseDown && _hover))) textBounds.Offset(0, 1);
        TextRenderer.DrawText(g, Text, Font, textBounds, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left) { _mouseDown = true; Invalidate(); }
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _mouseDown = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnKeyDown(KeyEventArgs kevent)
    {
        if (kevent.KeyCode == Keys.Space) { _spaceDown = true; Invalidate(); }
        base.OnKeyDown(kevent);
    }

    protected override void OnKeyUp(KeyEventArgs kevent)
    {
        if (kevent.KeyCode == Keys.Space) { _spaceDown = false; Invalidate(); }
        base.OnKeyUp(kevent);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { _spaceDown = false; Invalidate(); base.OnLostFocus(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnEnabledChanged(EventArgs e)
    {
        _hover = _mouseDown = _spaceDown = false;
        Invalidate();
        base.OnEnabledChanged(e);
    }
}

/// <summary>Anti-aliased round status indicator, with an optional soft halo.</summary>
sealed class StatusDot : Control
{
    Color _color = Palette.Muted;
    bool _halo;

    public StatusDot()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Size = new Size(18, 18);
    }

    public Color DotColor
    {
        get => _color;
        set { _color = value; Invalidate(); }
    }

    public bool Halo
    {
        get => _halo;
        set { _halo = value; Invalidate(); }
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(UiKit.BackdropOf(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var cx = Width / 2f;
        var cy = Height / 2f;
        var dot = this.Px(10) / 2f;
        if (_halo)
        {
            var halo = Math.Min(Math.Min(Width, Height) / 2f, dot + this.Px(4));
            using var haloBrush = new SolidBrush(Color.FromArgb(54, _color));
            g.FillEllipse(haloBrush, cx - halo, cy - halo, halo * 2, halo * 2);
        }
        using var brush = new SolidBrush(_color);
        g.FillEllipse(brush, cx - dot, cy - dot, dot * 2, dot * 2);
    }
}

/// <summary>White rounded surface with a hairline border. Children sit inside its Padding.</summary>
sealed class CardPanel : Panel
{
    const int Radius = 14;

    public CardPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.Surface;
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(UiKit.BackdropOf(this));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiKit.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), this.Px(Radius));
        using var brush = new SolidBrush(BackColor);
        using var pen = new Pen(Palette.Border);
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
        base.OnPaint(e);
    }
}

/// <summary>
/// Shows the QR code as a rounded white tile (largest square that fits, capped at <see cref="MaxSide"/>),
/// or a neutral placeholder. When clickable it offers a hover hint and acts like a button.
/// </summary>
sealed class QrView : Control
{
    Image? _image;
    string _placeholder = "";
    bool _hover;
    bool _clickable = true;

    public QrView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = "QR code. Activate to enlarge.";
        UpdateInteraction();
    }

    /// <summary>Largest side in 96-dpi pixels.</summary>
    public int MaxSide { get; set; } = 220;

    public bool Clickable
    {
        get => _clickable;
        set { _clickable = value; UpdateInteraction(); }
    }

    /// <summary>The picture to show; not owned (the caller disposes it).</summary>
    public Image? Image
    {
        get => _image;
        set { _image = value; UpdateInteraction(); Invalidate(); }
    }

    public string Placeholder
    {
        get => _placeholder;
        set { _placeholder = value; Invalidate(); }
    }

    bool Interactive => Clickable && _image is not null;

    void UpdateInteraction()
    {
        Cursor = Interactive ? Cursors.Hand : Cursors.Default;
        TabStop = Interactive;
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(UiKit.BackdropOf(this));

        var side = Math.Min(Math.Min(ClientSize.Width, ClientSize.Height), this.Px(MaxSide)) - 2;
        if (side < 24) return;
        var tile = new RectangleF((Width - side) / 2f + 0.5f, (Height - side) / 2f + 0.5f, side - 1f, side - 1f);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = UiKit.RoundedRect(tile, this.Px(14)))
        using (var brush = new SolidBrush(_image is null ? Palette.Placeholder : Color.White))
        using (var pen = new Pen(Palette.Border))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        if (_image is null)
        {
            g.SmoothingMode = SmoothingMode.Default;
            TextRenderer.DrawText(g, _placeholder, UiFonts.Small, Rectangle.Round(tile), Palette.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }

        var pad = this.Px(8);
        var picture = new RectangleF(tile.X + pad, tile.Y + pad, tile.Width - 2 * pad, tile.Height - 2 * pad);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using (var attributes = new System.Drawing.Imaging.ImageAttributes())
        {
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(_image, Rectangle.Round(picture), 0, 0, _image.Width, _image.Height, GraphicsUnit.Pixel, attributes);
        }

        if (Interactive && (_hover || (Focused && ShowFocusCues)))
        {
            const string hint = "Click to enlarge";
            var text = TextRenderer.MeasureText(g, hint, UiFonts.CaptionStrong, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            var chip = new RectangleF(0, 0, text.Width + this.Px(20), text.Height + this.Px(10));
            chip.X = tile.X + (tile.Width - chip.Width) / 2;
            chip.Y = tile.Bottom - chip.Height - this.Px(10);
            using var chipPath = UiKit.RoundedRect(chip, chip.Height / 2);
            using var chipBrush = new SolidBrush(Color.FromArgb(215, Palette.Ink));
            g.FillPath(chipBrush, chipPath);
            g.SmoothingMode = SmoothingMode.Default;
            TextRenderer.DrawText(g, hint, UiFonts.CaptionStrong, Rectangle.Round(chip), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        if (Interactive && Focused && ShowFocusCues)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var ringRect = RectangleF.Inflate(tile, 2f, 2f);
            using var ringPath = UiKit.RoundedRect(ringRect, this.Px(16));
            using var ringPen = new Pen(Color.FromArgb(150, Palette.Green), 2f);
            g.DrawPath(ringPen, ringPath);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        if (Interactive) base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Interactive && (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space))
        {
            e.Handled = true;
            OnClick(EventArgs.Empty);
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
}

/// <summary>Centered "nothing here yet" message with a small tray icon; fills whatever space it is given.</summary>
sealed class EmptyState : Control
{
    readonly string _title;
    readonly string _body;

    public EmptyState(string title, string body)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        BackColor = Palette.Surface;
        _title = title;
        _body = body;
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        var maxWidth = Math.Max(this.Px(120), Width - this.Px(48));
        const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        var titleSize = TextRenderer.MeasureText(g, _title, UiFonts.BodyStrong, new Size(maxWidth, int.MaxValue), flags);
        var bodySize = TextRenderer.MeasureText(g, _body, UiFonts.Small, new Size(maxWidth, int.MaxValue), flags);
        var icon = this.Px(56);
        var gap = this.Px(14);
        var tight = this.Px(4);

        var showIcon = Height >= icon + gap + titleSize.Height + tight + bodySize.Height + this.Px(16);
        var total = (showIcon ? icon + gap : 0) + titleSize.Height + tight + bodySize.Height;
        var top = Math.Max(0, (Height - total) / 2);

        if (showIcon)
        {
            DrawTrayIcon(g, new PointF(Width / 2f, top + icon / 2f), icon);
            top += icon + gap;
        }
        TextRenderer.DrawText(g, _title, UiFonts.BodyStrong, new Rectangle((Width - maxWidth) / 2, top, maxWidth, titleSize.Height), Palette.Ink, flags);
        top += titleSize.Height + tight;
        TextRenderer.DrawText(g, _body, UiFonts.Small, new Rectangle((Width - maxWidth) / 2, top, maxWidth, bodySize.Height), Palette.Muted, flags);
    }

    static void DrawTrayIcon(Graphics g, PointF center, int diameter)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var disc = new SolidBrush(Palette.GreenPale))
        {
            g.FillEllipse(disc, center.X - diameter / 2f, center.Y - diameter / 2f, diameter, diameter);
        }

        var k = diameter / 56f;
        using var pen = new Pen(Palette.Green, Math.Max(2f, 2.4f * k)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        PointF P(float x, float y) => new(center.X + x * k, center.Y + y * k);
        g.DrawLine(pen, P(0, -12), P(0, 4));
        g.DrawLines(pen, new[] { P(-6, -2), P(0, 4), P(6, -2) });
        g.DrawLines(pen, new[] { P(-11, 3), P(-11, 11), P(11, 11), P(11, 3) });
        g.SmoothingMode = SmoothingMode.Default;
    }
}

/// <summary>
/// Details-view list whose rows are painted by the owner (see MainForm.FileList_DrawItem). Adds double buffering,
/// hover tracking, fixed column widths and access to the in-place label editor.
/// </summary>
sealed class InboxListView : ListView
{
    bool _settingColumns;

    public InboxListView()
    {
        DoubleBuffered = true;
    }

    public ListViewItem? HotItem { get; private set; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.UseExplorerTheme(Handle);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHot(HitTest(e.Location).Item);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHot(null);
    }

    void SetHot(ListViewItem? item)
    {
        if (ReferenceEquals(item, HotItem)) return;
        var previous = HotItem;
        HotItem = item;
        Redraw(previous);
        Redraw(item);
    }

    void Redraw(ListViewItem? item)
    {
        if (item is not null && ReferenceEquals(item.ListView, this) && item.Index >= 0) RedrawItems(item.Index, item.Index, false);
    }

    /// <summary>Columns are sized by the form to fill the list; the user cannot drag them.</summary>
    public void SetColumnWidths(params int[] widths)
    {
        _settingColumns = true;
        try
        {
            for (var i = 0; i < widths.Length && i < Columns.Count; i++)
            {
                if (Columns[i].Width != widths[i]) Columns[i].Width = widths[i];
            }
        }
        finally { _settingColumns = false; }
    }

    protected override void OnColumnWidthChanging(ColumnWidthChangingEventArgs e)
    {
        if (!_settingColumns) e.Cancel = true;
        base.OnColumnWidthChanging(e);
    }

    public void SelectLabelEditorPrefix(int length) => Native.SelectInLabelEditor(Handle, length);
}
