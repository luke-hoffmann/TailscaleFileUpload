namespace TaildropApp;

/// <summary>Large QR code for scanning from across a desk. Esc, Enter or Done closes it.</summary>
sealed class QrDialog : Form
{
    public QrDialog(Image qr, string url)
    {
        SuspendLayout();
        Text = "Scan to connect";
        BackColor = Palette.Surface;
        ForeColor = Palette.Ink;
        Font = UiFonts.Body;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(24, 8, 24, 16);

        var title = new Label
        {
            Text = "Scan with your phone",
            Font = UiFonts.Heading,
            Dock = DockStyle.Top,
            Height = 44,
            TextAlign = ContentAlignment.MiddleCenter,
            UseMnemonic = false
        };
        var picture = new QrView { Dock = DockStyle.Fill, Image = qr, MaxSide = 380, Clickable = false };
        var urlLabel = new Label
        {
            Text = url,
            Font = UiFonts.BodyStrong,
            Dock = DockStyle.Bottom,
            Height = 30,
            TextAlign = ContentAlignment.MiddleCenter,
            UseMnemonic = false
        };
        var hint = new Label
        {
            Text = "Tailscale must be connected on your phone.",
            Font = UiFonts.Small,
            ForeColor = Palette.Muted,
            Dock = DockStyle.Bottom,
            Height = 28,
            TextAlign = ContentAlignment.MiddleCenter
        };
        var done = new RoundedButton("Done", ButtonKind.Primary) { Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };

        // Fill first, then the bottom stack (last added is laid out first, so Done ends up lowest).
        Controls.Add(picture);
        Controls.Add(urlLabel);
        Controls.Add(hint);
        Controls.Add(done);
        Controls.Add(title);
        AcceptButton = done;
        CancelButton = done;

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(440, 560);
        ResumeLayout(false);
    }

    protected override void OnLoad(EventArgs e)
    {
        // On a small screen the QR tile just shrinks to fit; Dock.Fill takes care of it.
        var area = Screen.FromPoint(MousePosition).WorkingArea;
        if (Height > area.Height) Height = area.Height;
        base.OnLoad(e);
    }
}
