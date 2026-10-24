using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace BluetoothBatteryTray;

// Drawn on a single surface: there are no opaque child controls that interrupt
// the animated Golden Rain background.
internal sealed class QuickSwitchForm : Form
{
    private readonly Action<string> _select;
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 50 };
    private bool _helpVisible;
    private readonly List<(string Name, int? Battery)> _items = new();
    private string _selected = "";
    private int _hover = -1;
    private long _shown;
    private readonly DateTime _born = DateTime.UtcNow;
    private Rectangle _helpRect;
    private readonly List<Rectangle> _deviceRects = new();

    public QuickSwitchForm(Action<string> select)
    {
        _select = select;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Size = new Size(345, 250);
        DoubleBuffered = true;
        Resize += (_, _) => ApplyRoundedWindow();
        ApplyRoundedWindow();
        BackColor = Color.FromArgb(11, 17, 30);
        ForeColor = Color.White;
        _animation.Tick += (_, _) => { if (Visible) Invalidate(); };
        Deactivate += (_, _) => { if (Environment.TickCount64 - _shown > 400) Hide(); };
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
    }

    public void SetDevices(IEnumerable<Headset> live, IEnumerable<string> known, string selected)
    {
        var readings = live.ToArray();
        _items.Clear();
        foreach (string name in readings.Select(x => x.Name).Concat(known).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var item = readings.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            _items.Add((name, item is null ? null : item.Battery));
        }
        _selected = selected;
        int desired = 115 + Math.Max(1, _items.Count) * 51 + 16;
        Height = Math.Clamp(desired, 190, 490);
        _hover = -1;
        Invalidate();
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void ApplyRoundedWindow()
    {
        if (Width < 2 || Height < 2) return;
        using var shape = Rounded(new RectangleF(0, 0, Width, Height), 18);
        var newRegion = new Region(shape);
        var oldRegion = Region;
        Region = newRegion;
        oldRegion?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var clipPath = Rounded(new RectangleF(0, 0, Width, Height), 18);
        g.SetClip(clipPath);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        Rectangle bounds = ClientRectangle;
        using (var gradient = new LinearGradientBrush(bounds, Color.FromArgb(16, 27, 44), Color.FromArgb(8, 13, 24), 63f))
            g.FillRectangle(gradient, bounds);

        // Radial golden glows, similar to the blurred CSS orbs of dashboard.html.
        double t = (DateTime.UtcNow - _born).TotalSeconds;
        for (int i = 0; i < 7; i++)
        {
            float x = (float)((i * 93.1 + 21 + Math.Sin(t * .23 + i) * 11) % Width);
            float y = (float)((i * 66.3 + 17 + Math.Cos(t * .19 + i) * 14) % Height);
            float size = 40 + (i * 13) % 34;
            for (int ring = 5; ring >= 1; ring--)
            {
                float r = size * ring / 5f;
                using var brush = new SolidBrush(Color.FromArgb(2 + (6-ring) * 2, 245, 194, 96));
                g.FillEllipse(brush, x-r/2, y-r/2, r, r);
            }
        }
        for (int i = 0; i < 31; i++)
        {
            float x = (i * 41.71f) % Width;
            float y = (float)((i * 83.17 + t * (15 + i%8 * 3)) % (Height + 140)) - 110;
            int len = 13 + i%6 * 11;
            using var line = new LinearGradientBrush(new PointF(x,y),new PointF(x,y+len), Color.FromArgb(0,238,191,104),Color.FromArgb(65,255,222,154));
            using var pen = new Pen(line, i%7==0 ? 1.2f : .6f);
            g.DrawLine(pen,x,y,x,y+len);
        }

        using var thin = new Pen(Color.FromArgb(83, 221, 185, 119), 1f);
        using (var border = Rounded(new RectangleF(.5f,.5f,Width-1,Height-1), 17)) g.DrawPath(thin,border);
        using var titleFont = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        using var itemFont = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        using var captionFont = new Font("Segoe UI", 8.5f, FontStyle.Regular);
        using var gold = new SolidBrush(Color.FromArgb(242, 196, 118));
        using var text = new SolidBrush(Color.White);
        using var muted = new SolidBrush(Color.FromArgb(166, 177, 193));
        g.DrawString("QUICK DEVICE SWITCH", titleFont, gold, 19, 20);
        _helpRect = new Rectangle(Width-42,19,22,22);
        using (var circle = Rounded(_helpRect, 11))
        {
            using var bg = new SolidBrush(Color.FromArgb(54, 45, 40, 49));
            g.FillPath(bg,circle); g.DrawPath(thin,circle);
        }
        // Draw the exclamation as vector shapes, independent of font metrics/DPI.
        float centerX = _helpRect.Left + _helpRect.Width / 2f;
        float centerY = _helpRect.Top + _helpRect.Height / 2f;
        using (var markPen = new Pen(Color.FromArgb(242, 196, 118), 1.8f))
        {
            markPen.StartCap = LineCap.Round;
            markPen.EndCap = LineCap.Round;
            g.DrawLine(markPen, centerX, centerY - 5f, centerX, centerY + 1f);
        }
        using (var dotBrush = new SolidBrush(Color.FromArgb(242, 196, 118)))
            g.FillEllipse(dotBrush, centerX - 1.05f, centerY + 4f, 2.1f, 2.1f);
        _deviceRects.Clear();
        if (_items.Count==0) g.DrawString("No devices with reported battery",itemFont,muted,19,85);
        for (int i=0;i<_items.Count;i++)
        {
            int y = 64 + i*51;
            if (y+42 > Height-10) break;
            var rect = new Rectangle(18,y,Width-36,43);
            _deviceRects.Add(rect);
            bool chosen = _items[i].Name.Equals(_selected,StringComparison.OrdinalIgnoreCase);
            using var path = Rounded(rect,12);
            using var fill = new SolidBrush(Color.FromArgb(_hover==i ? 94:66, 65, 76, 97));
            using var stroke = new Pen(Color.FromArgb(chosen?172:67,235,199,132));
            g.FillPath(fill,path);g.DrawPath(stroke,path);
            if (chosen) { using var marker = new SolidBrush(Color.FromArgb(235,196,123));g.FillEllipse(marker,27,y+19,5,5); }
            string name = _items[i].Name;
            string value = _items[i].Battery.HasValue ? _items[i].Battery.Value+"%" : "--";
            var maxNameWidth = Width-122;
            while (name.Length>2 && g.MeasureString(name,itemFont).Width>maxNameWidth) name=name[..^2]+"…";
            g.DrawString(name,itemFont,text,chosen?39:29,y+11);
            using var valueFont = new Font("Segoe UI",9.5f,FontStyle.Bold);
            float valueWidth=g.MeasureString(value,valueFont).Width;
            g.DrawString(value,valueFont,gold,Width-31-valueWidth,y+11);
        }
        g.DrawString("Choose the device shown in System Tray",captionFont,muted,19,Height-30);

        // Draw our own tooltip rather than a native ToolTip which escapes the popup
        // and renders as a long bright Windows rectangle.
        if (_helpVisible)
        {
            var help = new Rectangle(18, 52, Width - 36, 93);
            using var shadowPath = Rounded(new RectangleF(help.X,help.Y+3,help.Width,help.Height),13);
            using var shadowBrush = new SolidBrush(Color.FromArgb(100,0,0,0));
            g.FillPath(shadowBrush,shadowPath);
            using var helpPath = Rounded(help,13);
            using var helpBg = new SolidBrush(Color.FromArgb(248,31,38,53));
            using var helpStroke = new Pen(Color.FromArgb(165,224,185,109),1);
            g.FillPath(helpBg,helpPath);
            g.DrawPath(helpStroke,helpPath);
            using var bold = new Font("Segoe UI",9.3f,FontStyle.Bold);
            using var body = new Font("Segoe UI",8.8f,FontStyle.Regular);
            g.DrawString("QUICK DEVICE SWITCH",bold,gold,help.X+13,help.Y+11);
            g.DrawString("Choose which device's battery appears in the tray.\nDevices without battery data display --.",
                body,text,new RectangleF(help.X+13,help.Y+36,help.Width-26,help.Height-42));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int next = _deviceRects.FindIndex(r=>r.Contains(e.Location));
        if (next!=_hover) { _hover=next; Invalidate(); }
        bool showHelp = _helpRect.Contains(e.Location);
        if (_helpVisible != showHelp) { _helpVisible = showHelp; Invalidate(); }
        Cursor = showHelp ? Cursors.Help : next >= 0 ? Cursors.Hand : Cursors.Default;
    }
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_helpVisible) { _helpVisible = false; Invalidate(); }
    }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button!=MouseButtons.Left) return;
        int index = _deviceRects.FindIndex(r=>r.Contains(e.Location));
        if (index>=0 && index<_items.Count) { _select(_items[index].Name);Hide(); }
    }
    public void ShowNearCursor()
    {
        var area=Screen.FromPoint(Cursor.Position).WorkingArea;
        Location=new Point(Math.Clamp(Cursor.Position.X-Width/2,area.Left,area.Right-Width),
                           Math.Clamp(Cursor.Position.Y-Height-12,area.Top,area.Bottom-Height));
        _helpVisible=false;
        _shown=Environment.TickCount64;
        Show();Activate();_animation.Start();
    }
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) _animation.Stop();
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing) { _animation.Dispose(); }
        base.Dispose(disposing);
    }
}
