using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BluetoothBatteryTray;

// Standalone popup for the second-monitor overlay. Native tray tooltip remains unchanged.
internal sealed class BatteryInfoPopup : Form
{
    private string _deviceName = "Bluetooth Headphones";
    private string _batteryText = "Battery: --";
    private bool _hasLevel;
    private int _level;
    private readonly System.Windows.Forms.Timer _motion = new() { Interval = 60 };
    private float _phase;

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x00000080 | 0x08000000 | 0x00000020; // TOOLWINDOW, NOACTIVATE, TRANSPARENT to hit test
            return p;
        }
    }

    public BatteryInfoPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Size = new Size(300, 112);
        BackColor = Color.FromArgb(40, 45, 56);
        DoubleBuffered = true;
        Opacity = 1.0;
        _motion.Tick += (_, _) => { if(Visible) { _phase += 0.07f; Invalidate(); } };
        _motion.Start();
    }

    public void SetBatteryInfo(string fullText)
    {
        fullText = (fullText ?? "").Trim();
        // Accepted input: "Hesh Evo Hands-Free AG: 90% · PnP/System".
        // Device source after the percentage must not invalidate a real reading.
        var match = System.Text.RegularExpressions.Regex.Match(
            fullText,
            @"^(?<name>.+?):\s*(?<percent>\d{1,3})%(?:\s*[·•]\s*.*)?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (match.Success && int.TryParse(match.Groups["percent"].Value, out int parsed)
                          && parsed >= 0 && parsed <= 100)
        {
            _deviceName = match.Groups["name"].Value.Trim();
            _level = parsed;
            _hasLevel = true;
            _batteryText = $"Battery: {_level}%";
        }
        else
        {
            // Never reuse the last battery reading for an unknown/disconnected device.
            _hasLevel = false;
            _level = 0;
            _deviceName = fullText.Split(':')[0].Trim();
            if (string.IsNullOrWhiteSpace(_deviceName) ||
                _deviceName.StartsWith("No compatible", StringComparison.OrdinalIgnoreCase) ||
                _deviceName.StartsWith("Checking", StringComparison.OrdinalIgnoreCase))
                _deviceName = "Bluetooth device";
            _batteryText = "Battery unavailable";
        }
        // Size to content without allowing long names to overflow the card.
        using var font = new Font("Segoe UI", 10.0f, FontStyle.Bold);
        int titleWidth = TextRenderer.MeasureText(_deviceName, font).Width;
        Width = Math.Clamp(titleWidth + 34, 300, 410);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(40, 45, 56));
        var bounds = new Rectangle(1, 1, Width - 3, Height - 3);
        using var frame = Rounded(bounds, 15);
        using var bg = new LinearGradientBrush(bounds,
            Color.FromArgb(43, 48, 59), Color.FromArgb(37, 42, 53), LinearGradientMode.ForwardDiagonal);
        g.FillPath(bg, frame);
        g.SetClip(frame);
        // Soft ambient lighting matching the device card in dashboard.html.
        for (int i = 0; i < 5; i++)
        {
            float x = ((i * 83 + 45) % Math.Max(1, Width)) + MathF.Sin(_phase + i * 1.3f) * 8;
            float y = ((i * 41 + 8) % Math.Max(1, Height)) + MathF.Cos(_phase * .6f + i) * 5;
            using var halo = new GraphicsPath();
            halo.AddEllipse(x - 26, y - 26, 52, 52);
            using var soft = new PathGradientBrush(halo)
            {
                CenterColor = Color.FromArgb(27, 246, 202, 122),
                SurroundColors = new[] { Color.FromArgb(0, 246, 202, 122) }
            };
            g.FillPath(soft, halo);
        }
        g.ResetClip();
        using var border = new Pen(Color.FromArgb(167, 163, 131, 80), 1f);
        g.DrawPath(border, frame);

        using var titleFont = new Font("Segoe UI", 9.3f, FontStyle.Bold);
        using var labelFont = new Font("Segoe UI", 8.2f, FontStyle.Regular);
        using var levelFont = new Font("Segoe UI", 18.5f, FontStyle.Bold);
        TextRenderer.DrawText(g, _deviceName, titleFont,
            new Rectangle(16, 11, Width - 32, 24), Color.FromArgb(250, 249, 248),
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, _hasLevel ? "Battery reported by Windows" : "Battery unavailable", labelFont,
            new Rectangle(Width - 200, 52, 184, 21),
            _hasLevel ? Color.FromArgb(153, 225, 177) : Color.FromArgb(166, 171, 180),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, _hasLevel ? $"{_level}%" : "--", levelFont,
            new Rectangle(15, 36, 128, 44), Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        // Grey rail and warm gold fill reproduce the device-card battery bar.
        var rail = new Rectangle(16, 88, Width - 32, 7);
        using var railPath = Rounded(rail, 3);
        using var track = new SolidBrush(Color.FromArgb(71, 76, 89));
        g.FillPath(track, railPath);
        if (_hasLevel && _level > 0)
        {
            int fillWidth = Math.Clamp((int)Math.Round(rail.Width * _level / 100d), 3, rail.Width);
            using var fillPath = Rounded(new Rectangle(rail.X, rail.Y, fillWidth, rail.Height), 3);
            using var fill = new LinearGradientBrush(rail,
                Color.FromArgb(188, 135, 62), Color.FromArgb(255, 225, 163), LinearGradientMode.Horizontal);
            g.FillPath(fill, fillPath);
        }
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _motion.Stop(); _motion.Dispose(); base.OnFormClosed(e);
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0) return;
        using var outline = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 16);
        var oldRegion = Region;
        Region = new Region(outline);
        oldRegion?.Dispose();
    }

}

internal static class GraphicsExtensions
{
    public static void DrawRoundedRectangleCompat(this Graphics g, Pen p, Rectangle r, int radius)
    {
        using var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, radius * 2, radius * 2, 180, 90);
        path.AddArc(r.Right - radius * 2, r.Top, radius * 2, radius * 2, 270, 90);
        path.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(r.Left, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        g.DrawPath(p, path);
    }
}
