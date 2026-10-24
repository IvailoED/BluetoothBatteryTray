using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BluetoothBatteryTray;

// One separate layered window per monitor, NOT a notification-area icon.
// Windows does not expose a supported API for moving NotifyIcon to secondary taskbars.
internal sealed class OverlayForm : Form
{
    private int WidthPx = 62;
    private int _fontWeight = 4;
    private string _colorMode = "White";
    // Keep the overlay aspect ratio when changing the requested width.
    // A fixed height made the glyphs height-limited, so width changes were invisible.
    private int HeightPx => Math.Clamp((int)Math.Round(WidthPx * 28.0 / 62.0), 16, 48);
    private readonly string _configPath;
    private string _digits = "?";
    private bool _batteryMode;
    private bool _dragging;
    private Point _dragMouse;
    private Point _dragWindow;
    private int? _rightOffset;
    private int? _bottomOffset;
    private bool _ready;
    private bool _allowSave = true;
    private readonly ContextMenuStrip _contextMenu;
    // Custom popup styled like Windows 11 quick info, not a native ToolTip balloon.
    private readonly BatteryInfoPopup _popup = new();
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 80 };
    private long _hoverStart = -1;
    private readonly System.Windows.Forms.Timer _dismissTimer = new() { Interval = 12000 };
    private readonly System.Windows.Forms.Timer _backgroundTimer = new() { Interval = 90 };
    private readonly long _backgroundEpoch = Environment.TickCount64;
    private string _fullToolTip = "Checking Bluetooth battery...";
    private sealed class Settings
    {
        public bool Visible { get; set; } = true;
        public int? RightOffset { get; set; }
        public int? BottomOffset { get; set; }
    }

    public string MonitorId { get; set; } = "";
    public bool EnabledOnScreen { get; set; } = true;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000; // LAYERED, TOOLWINDOW, NOACTIVATE
            return p;
        }
    }

    public OverlayForm(ContextMenuStrip contextMenu, string monitorId)
    {
        _contextMenu = contextMenu;
        MonitorId = monitorId;
        // Each display stores its own position; dragging one overlay never moves the others.
        string safeMonitor = string.Concat(monitorId.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        _configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BluetoothBatteryTray", $"overlay_{safeMonitor}.json");
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Size = new Size(WidthPx, HeightPx);
        Text = "Bluetooth Battery Overlay";
        MouseDown += HandleMouseDown;
        MouseMove += HandleMouseMove;
        MouseUp += HandleMouseUp;
        // Layered windows may have fully transparent pixels that do not receive
        // MouseEnter/MouseLeave. Poll the actual pointer position instead.
        _hoverTimer.Tick += (_, _) =>
        {
            bool hovering = Visible && EnabledOnScreen && !_dragging
                && Bounds.Contains(Cursor.Position);
            if (!hovering)
            {
                _hoverStart = -1;
                if (_popup.Visible) HidePopup();
                return;
            }
            if (_hoverStart < 0) _hoverStart = Environment.TickCount64;
            if (!_popup.Visible && Environment.TickCount64 - _hoverStart >= 380)
                ShowPopup();
        };
        _hoverTimer.Start();
        // Animate the gold *inside the numeral glyphs*, not behind them.
        _backgroundTimer.Interval = 100;
        _backgroundTimer.Tick += (_, _) => { if (_ready && Visible && !_dragging) DrawLayer(); };
        _backgroundTimer.Start();
        // Static, transparent overlay: repaint only on value or style changes.
        _dismissTimer.Tick += (_, _) => { HidePopup(); _hoverStart = Environment.TickCount64 + 30000; };
        LoadSettings();
    }

    private Screen TargetScreen()
    {
        Screen[] all = Screen.AllScreens;
        return all.FirstOrDefault(s => s.DeviceName.Equals(MonitorId, StringComparison.OrdinalIgnoreCase))
            ?? Screen.PrimaryScreen!;
    }

    public void ApplyMonitor()
    {
        if (IsDisposed) return;
        if (!EnabledOnScreen) { HidePopup(); Hide(); SaveSettings(); return; }
        Screen screen = TargetScreen();
        MonitorId = screen.DeviceName;
        Rectangle bounds = screen.Bounds;
        Rectangle work = screen.WorkingArea;
        // Default placement: left of the time/notification area on a bottom taskbar.
        int x = _rightOffset.HasValue ? bounds.Right - _rightOffset.Value : bounds.Right - 192;
        int y;
        if (_bottomOffset.HasValue)
            y = bounds.Bottom - _bottomOffset.Value;
        else if (work.Bottom < bounds.Bottom)  // bottom taskbar
            y = work.Bottom + Math.Max(0, (bounds.Bottom - work.Bottom - HeightPx) / 2);
        else if (work.Top > bounds.Top)      // top taskbar
            y = bounds.Top + Math.Max(0, (work.Top - bounds.Top - HeightPx) / 2);
        else                                 // auto-hide, or side taskbar
            y = bounds.Bottom - HeightPx - 6;
        x = Math.Clamp(x, bounds.Left, bounds.Right - WidthPx);
        y = Math.Clamp(y, bounds.Top, bounds.Bottom - HeightPx);
        Location = new Point(x, y);
        if (!Visible) Show();
        _ready = true;
        DrawLayer();
        SaveSettings();
    }

    public void SetStyle(int width, int weight, string mode)
    {
        WidthPx = Math.Clamp(width, 36, 100);
        _fontWeight = Math.Clamp(weight, 1, 10);
        _colorMode = mode;
        Size = new Size(WidthPx, HeightPx);
        // Resize and re-anchor on the same monitor; don't reset saved drag offsets.
        if (_ready && Visible)
            ApplyMonitor();
    }

    public void SetIndicatorStyle(string style)
    {
        _batteryMode = style == "Battery Icon + Percentage";
        if (_ready && Visible) DrawLayer();
    }

    public void ResetPosition()
    {
        _rightOffset = null;
        _bottomOffset = null;
    }

    public void SetBattery(string digits, string toolTip)
    {
        _digits = digits;
        Text = toolTip;
        _fullToolTip = toolTip;
        _popup.SetBatteryInfo(toolTip);
        if (_popup.Visible) PositionPopup();
        if (_ready && Visible) DrawLayer();
    }

    private void HandleMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { HidePopup(); _contextMenu.Show(Cursor.Position); return; }
        if (e.Button != MouseButtons.Left) return;
        HidePopup();
        _hoverStart = -1;
        _dragging = true;
        _dragMouse = Cursor.Position;
        _dragWindow = Location;
        Capture = true;
    }
    private void HandleMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var mouse = Cursor.Position;
        Rectangle b = TargetScreen().Bounds;
        int x = Math.Clamp(_dragWindow.X + mouse.X - _dragMouse.X, b.Left, b.Right - WidthPx);
        int y = Math.Clamp(_dragWindow.Y + mouse.Y - _dragMouse.Y, b.Top, b.Bottom - HeightPx);
        Location = new Point(x, y);
        DrawLayer();
    }
    private void HandleMouseUp(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _hoverStart = -1;
        Capture = false;
        Rectangle bounds = TargetScreen().Bounds;
        _rightOffset = bounds.Right - Left;
        _bottomOffset = bounds.Bottom - Top;
        SaveSettings();
    }

    private void ShowPopup()
    {
        _popup.SetBatteryInfo(_fullToolTip);
        PositionPopup();
        _popup.Show();
        _popup.BringToFront();
        _dismissTimer.Stop();
        _dismissTimer.Start();
    }

    private void PositionPopup()
    {
        Rectangle work = TargetScreen().Bounds;
        int x = Math.Clamp(Left + (Width - _popup.Width) / 2,
                           work.Left + 6, Math.Max(work.Left + 6, work.Right - _popup.Width - 6));
        int above = Top - _popup.Height - 12;
        int below = Bottom + 12;
        int y = above >= work.Top + 4 ? above :
                below + _popup.Height <= work.Bottom ? below : work.Top + 4;
        _popup.Location = new Point(x, y);
    }

    private void HidePopup()
    {
        _dismissTimer.Stop();
        _popup.Hide();
    }


    private static GraphicsPath RoundedBounds(Rectangle r, int radius)
    {
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void DrawGoldenRain(Graphics g)
    {
        // Deterministic ambient animation: no full image loading or WebView2 on taskbar.
        // Similar palette and drifting glow as the CSS dashboard background.
        float t = (Environment.TickCount64 - _backgroundEpoch) / 1000f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < 9; i++)
        {
            float x = (i * 23.7f + 9f) % WidthPx;
            float len = 4f + i % 4 * 2.1f;
            float y = ((i * 13.1f + t * (5f + i % 3 * 2f)) % (HeightPx + len + 8)) - len;
            using var streak = new LinearGradientBrush(
                new PointF(x, y), new PointF(x, y + len),
                Color.FromArgb(0, 255, 208, 120),
                Color.FromArgb(75, 255, 220, 151));
            using var pen = new Pen(streak, .7f);
            g.DrawLine(pen, x, y, x, y + len);
        }
        for (int i = 0; i < 3; i++)
        {
            float x = WidthPx * (.2f + .3f * i);
            float y = HeightPx * (.22f + .22f * i) + MathF.Sin(t * .7f + i) * 2f;
            float r = Math.Max(4f, HeightPx * (.22f + i * .025f));
            using var glow = new PathGradientBrush(new[] {
                new PointF(x-r, y-r), new PointF(x+r, y-r),
                new PointF(x+r, y+r), new PointF(x-r, y+r) })
            {
                CenterPoint = new PointF(x, y),
                CenterColor = Color.FromArgb(40, 255, 198, 99),
                SurroundColors = new[] { Color.FromArgb(0, 255, 198, 99) }
            };
            g.FillEllipse(glow, x-r, y-r, r*2, r*2);
        }
    }

    private void DrawLayer()
    {
        if (!IsHandleCreated || !Visible) return;
        // Draw at 4x and downsample: bold antialiased white glyphs with true alpha.
        using var high = new Bitmap(WidthPx * 4, HeightPx * 4, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(high))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var path = new GraphicsPath();
            using var font = new FontFamily("Arial");
            path.AddString(_digits, font, (int)FontStyle.Bold, 91f, new PointF(0f, 0f), StringFormat.GenericTypographic);
            RectangleF r = path.GetBounds();
            if (r.Width <= 0 || r.Height <= 0) return;
            float textLeft = _batteryMode ? high.Width * .33f : 0f;
            float maxTextWidth = high.Width - textLeft - 12f;
            float scale = Math.Min((maxTextWidth - 10f) / r.Width, (high.Height - 20f) / r.Height);
            using var m = new Matrix();
            m.Translate(-r.Left, -r.Top);
            m.Scale(scale, scale, MatrixOrder.Append);
            m.Translate(textLeft + (maxTextWidth - r.Width * scale) / 2, (high.Height - r.Height * scale) / 2, MatrixOrder.Append);
            path.Transform(m);
            if (_batteryMode)
            {
                // Make the battery part of the same clip: Golden Rain fills the glyph AND digits.
                float bx = high.Width * .045f, by = high.Height * .27f;
                float bw = high.Width * .22f, bh = high.Height * .49f;
                path.AddRectangle(new RectangleF(bx, by, bw, bh));
                path.AddRectangle(new RectangleF(bx + bw, by + bh * .31f, high.Width * .033f, bh * .38f));
            }
            // Text-shaped mask: the moving dashboard colors are ONLY inside the digits.
            // The pixels outside the numbers stay transparent, with no capsule or card.
            g.SetClip(path);
            float t = (Environment.TickCount64 - _backgroundEpoch) / 1000f;
            using (var baseGold = new LinearGradientBrush(
                new Rectangle(0, 0, high.Width, high.Height),
                Color.FromArgb(255, 246, 222, 158),
                Color.FromArgb(255, 122, 74, 31), 38f))
            {
                var cb = new ColorBlend
                {
                    Positions = new[] { 0f, .32f, .66f, 1f },
                    Colors = new[] {
                        Color.FromArgb(255, 255, 233, 179),
                        Color.FromArgb(255, 201, 147, 74),
                        Color.FromArgb(255, 99, 65, 40),
                        Color.FromArgb(255, 244, 205, 132)
                    }
                };
                baseGold.InterpolationColors = cb;
                g.FillRectangle(baseGold, 0, 0, high.Width, high.Height);
            }
            // Soft, animated golden blooms, clipped to the number outlines.
            for (int i = 0; i < 5; i++)
            {
                float x = (float)(high.Width * (.1 + i * .21) + Math.Sin(t * .48 + i) * 24);
                float y = (float)(high.Height * (.2 + (i % 3) * .28) + Math.Cos(t * .65 + i) * 18);
                float radius = high.Height * (.36f + (i % 2) * .12f);
                using var glowPath = new GraphicsPath();
                glowPath.AddEllipse(x - radius, y - radius, radius * 2, radius * 2);
                using var glowBrush = new PathGradientBrush(glowPath)
                {
                    CenterColor = Color.FromArgb(190, 255, 243, 190),
                    SurroundColors = new[] { Color.FromArgb(0, 255, 205, 102) }
                };
                g.FillPath(glowBrush, glowPath);
            }
            // Small vertical gold streaks move down through the digit texture.
            for (int i = 0; i < 23; i++)
            {
                float x = (i * 71.23f + 19) % high.Width;
                float length = 10 + (i % 5) * 10;
                float y = ((i * 39f + t * (15 + i % 4 * 8)) % (high.Height + length)) - length;
                using var streak = new LinearGradientBrush(
                    new PointF(x, y), new PointF(x, y + length),
                    Color.FromArgb(0, 255, 248, 190),
                    Color.FromArgb(210, 255, 247, 201));
                using var pen = new Pen(streak, 1.5f + i % 3);
                g.DrawLine(pen, x, y, x, y + length);
            }
            g.ResetClip();
            // Very subtle gold contour. The edge does not extend to a badge.
            using var edgePen = new Pen(Color.FromArgb(110, 255, 225, 166), .65f)
            { LineJoin = LineJoin.Round };
            g.DrawPath(edgePen, path);
        }
        using var bmp = new Bitmap(WidthPx, HeightPx, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            // Alpha=1 is visually transparent, but still hit-testable by Windows.
            // It also helps right-click and drag work between the glyph strokes.
            g.Clear(Color.FromArgb(1, 0, 0, 0));
            // No rectangular badge / pill / outline: the taskbar stays visible.
            // Add a restrained golden halo only immediately around the numeral.
            // The text image itself has true alpha, so no card covers taskbar icons.
            using (var haloAttrs = new ImageAttributes())
            {
                var tint = new ColorMatrix { Matrix33 = 0.12f };
                haloAttrs.SetColorMatrix(tint);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                // Subtle shadow makes white digits legible on light taskbars.
                g.DrawImage(high, new Rectangle(0, 1, WidthPx, HeightPx),
                    0, 0, high.Width, high.Height, GraphicsUnit.Pixel, haloAttrs);
            }
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(high, new Rectangle(0, 0, WidthPx, HeightPx));
        }
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr previous = SelectObject(memDc, hBmp);
        try
        {
            var pos = new PointNative { X = Left, Y = Top };
            var size = new SizeNative { CX = WidthPx, CY = HeightPx };
            var source = new PointNative();
            var blend = new BlendFunction { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref pos, ref size, memDc, ref source, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(memDc, previous);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hoverTimer.Dispose();
            _dismissTimer.Dispose();
            _backgroundTimer.Dispose();
            _popup.Dispose();
        }
        base.Dispose(disposing);
    }

    private void LoadSettings()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_configPath));
            if (settings is null) return;
            EnabledOnScreen = settings.Visible;
            _rightOffset = settings.RightOffset;
            _bottomOffset = settings.BottomOffset;
        }
        catch { /* Default to secondary monitor. */ }
    }
    private void SaveSettings()
    {
        if (!_allowSave) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            File.WriteAllText(_configPath, JsonSerializer.Serialize(new Settings
            {
                Visible = EnabledOnScreen,
                RightOffset = _rightOffset, BottomOffset = _bottomOffset
            }));
        }
        catch { /* Settings cannot be written. */ }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SizeNative { public int CX, CY; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr dstDc, ref PointNative dstPos, ref SizeNative size,
        IntPtr srcDc, ref PointNative srcPos, int colorKey, ref BlendFunction blend, int flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
