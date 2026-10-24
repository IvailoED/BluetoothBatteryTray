using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace BluetoothBatteryTray;

internal sealed class AppPreferences
{
    private static readonly string ConfigFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BluetoothBatteryTray", "preferences.json");
    public bool Notifications { get; set; } = true;
    public int[] Alerts { get; set; } = [20, 10, 5];
    public int PollSeconds { get; set; } = 30;
    public int OverlayWidth { get; set; } = 62;
    public int FontWeight { get; set; } = 5;
    public string ColorMode { get; set; } = "Dynamic";
    public bool CompactMode { get; set; } = false;
    public string IndicatorStyle { get; set; } = "Percentage Only";
    public Dictionary<string, DeviceProfile> DeviceProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public static AppPreferences Load()
    {
        try
        {
            var x = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(ConfigFile)) ?? new();
            x.Normalize(); return x;
        }
        catch { return new(); }
    }
    public void Normalize()
    {
        DeviceProfiles ??= new(StringComparer.OrdinalIgnoreCase);
        PollSeconds = Math.Clamp(PollSeconds, 10, 300);
        OverlayWidth = Math.Clamp(OverlayWidth, 36, 100);
        FontWeight = Math.Clamp(FontWeight, 1, 10);
        Alerts = Alerts.Where(a => a > 0 && a < 100).Distinct().OrderByDescending(a => a).ToArray();
    }
    public void Save()
    {
        Normalize(); Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed class PreferencesForm : Form
{
    private readonly AppPreferences _prefs;
    private readonly CheckBox _notifications = new() { Text = "Enable low-battery notifications", AutoSize = true };
    private readonly CheckBox _alert20 = new() { Text = "20%", AutoSize = true };
    private readonly CheckBox _alert10 = new() { Text = "10%", AutoSize = true };
    private readonly CheckBox _alert5 = new() { Text = "5%", AutoSize = true };
    private readonly NumericUpDown _poll = new() { Minimum = 10, Maximum = 300, Increment = 5, Width = 100 };
    private readonly NumericUpDown _size = new() { Minimum = 36, Maximum = 100, Width = 100 };
    private readonly NumericUpDown _weight = new() { Minimum = 1, Maximum = 10, Width = 100 };
    private readonly ComboBox _color = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175 };
    public PreferencesForm(AppPreferences prefs)
    {
        _prefs = prefs;
        Text = "BluetoothBatteryTray | Settings";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 400); MinimumSize = new Size(480, 400);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        BackColor = Color.FromArgb(28, 30, 34); ForeColor = Color.White;
        var stack = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 2, RowCount = 9 };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        for (int i=0;i<9;i++) stack.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 8 ? 55 : 38));
        Controls.Add(stack);
        void AddLabel(string text, int row) => stack.Controls.Add(new Label { Text = text, AutoSize = true, ForeColor = Color.White, Anchor = AnchorStyles.Left }, 0, row);
        _notifications.Checked = prefs.Notifications;
        stack.Controls.Add(_notifications, 0, 0); stack.SetColumnSpan(_notifications, 2);
        AddLabel("Notification thresholds", 1);
        var thresholds = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        foreach (var c in new [] { _alert20, _alert10, _alert5 }) thresholds.Controls.Add(c);
        _alert20.Checked = prefs.Alerts.Contains(20); _alert10.Checked = prefs.Alerts.Contains(10); _alert5.Checked = prefs.Alerts.Contains(5);
        stack.Controls.Add(thresholds, 1, 1);
        AddLabel("Refresh interval (seconds)", 2); _poll.Value = prefs.PollSeconds; stack.Controls.Add(_poll, 1, 2);
        AddLabel("Secondary overlay width (px)", 3); _size.Value = prefs.OverlayWidth; stack.Controls.Add(_size, 1, 3);
        AddLabel("Digit weight (1–10)", 4); _weight.Value = prefs.FontWeight; stack.Controls.Add(_weight, 1, 4);
        AddLabel("Indicator color", 5);
        _color.Items.AddRange(["Dynamic", "White"]); _color.SelectedItem = prefs.ColorMode == "White" ? "White" : "Dynamic";
        stack.Controls.Add(_color, 1, 5);
        var info = new Label { Text = "Auto scan: Bluetooth / Hands-Free / HID devices with battery information exposed by Windows. Unsupported devices cannot report a percentage.", AutoSize = true, MaximumSize = new Size(425, 0), ForeColor = Color.Silver };
        stack.Controls.Add(info, 0, 6); stack.SetColumnSpan(info, 2);
        var save = new Button { Text = "Save settings", Width = 145, Height = 32 };
        save.Click += (_, _) =>
        {
            prefs.Notifications = _notifications.Checked;
            prefs.Alerts = new[] { (_alert20.Checked,20), (_alert10.Checked,10), (_alert5.Checked,5) }.Where(x=>x.Item1).Select(x=>x.Item2).ToArray();
            prefs.PollSeconds = (int)_poll.Value; prefs.OverlayWidth = (int)_size.Value;
            prefs.FontWeight = (int)_weight.Value; prefs.ColorMode = _color.SelectedItem?.ToString() ?? "Dynamic";
            DialogResult = DialogResult.OK; Close();
        };
        stack.Controls.Add(save, 1, 8);
    }
}

internal sealed record BatterySample(DateTime Time, string Device, int Level);
internal sealed class BatteryHistory : IDisposable
{
    private readonly string _file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BluetoothBatteryTray", "battery-history.csv");
    private readonly List<BatterySample> _samples = new();
    public BatteryHistory()
    {
        try
        {
            foreach (string line in File.ReadLines(_file).Skip(1))
            {
                string[] a = line.Split('\t');
                if (a.Length == 3 && DateTime.TryParse(a[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) && int.TryParse(a[2], out int n))
                    _samples.Add(new(dt, a[1], n));
            }
        }
        catch { }
    }
    public IReadOnlyList<BatterySample> Get(string device) => _samples.Where(s => string.IsNullOrWhiteSpace(device) || s.Device.Equals(device, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Time).ToArray();
    public IReadOnlyList<string> Devices => _samples.Select(x=>x.Device).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x).ToArray();
    public void Record(string device, int value)
    {
        var last = _samples.LastOrDefault(s => s.Device.Equals(device, StringComparison.OrdinalIgnoreCase));
        if (last is not null && last.Level == value && (DateTime.Now - last.Time).TotalMinutes < 15) return;
        var sample = new BatterySample(DateTime.Now, device.Replace('\t',' '), value);
        _samples.Add(sample);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            if (!File.Exists(_file)) File.WriteAllText(_file, "Timestamp\tDevice\tBatteryPercent\n", Encoding.UTF8);
            File.AppendAllText(_file, $"{sample.Time:O}\t{sample.Device}\t{sample.Level}\n", Encoding.UTF8);
        }
        catch { }
    }
    public void Export(string filename)
    {
        static string Csv(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
        using var wr = new StreamWriter(filename, false, new UTF8Encoding(true));
        wr.WriteLine("Timestamp,Device,BatteryPercent");
        foreach(var r in _samples) wr.WriteLine($"{r.Time:O},{Csv(r.Device)},{r.Level}");
    }
    public void Dispose() { }
}

internal sealed class BatteryHistoryForm : Form
{
    private readonly BatteryHistory _history;
    private readonly ComboBox _device = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 290 };
    private readonly ComboBox _period = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly Label _stats = new() { AutoSize = true, ForeColor = Color.White };
    private readonly Panel _plot = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(35,38,43) };
    private IReadOnlyList<BatterySample> _points = [];
    public BatteryHistoryForm(BatteryHistory history, string selected)
    {
        _history = history;
        Text = "BluetoothBatteryTray | Battery history";
        StartPosition = FormStartPosition.CenterScreen; Size = new Size(800, 480);
        BackColor = Color.FromArgb(26,28,32); ForeColor = Color.White;
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 45, Padding = new Padding(12, 7, 0, 0) };
        header.Controls.Add(_device); _period.Items.AddRange(new object[] { "24 hours", "7 days", "30 days" });
        _period.SelectedIndex=0;
        header.Controls.Add(_period); header.Controls.Add(_stats);
        _period.SelectedIndexChanged += (_,_) => UpdateGraph();
        Controls.Add(_plot); Controls.Add(header);
        _device.Items.Add("All devices");
        foreach (var name in history.Devices) _device.Items.Add(name);
        _device.SelectedItem = history.Devices.Contains(selected) ? selected : "All devices";
        _device.SelectedIndexChanged += (_,_) => UpdateGraph();
        _plot.Paint += PaintGraph;
        UpdateGraph();
    }
    private void UpdateGraph()
    {
        var name = _device.SelectedItem?.ToString() ?? "All devices";
        int days=_period.SelectedIndex switch { 0=>1, 1=>7, _=>30 };
        _points = _history.Get(name == "All devices" ? "" : name)
            .Where(x=>x.Time>=DateTime.Now.AddDays(-days)).ToArray();
        var recent = _points.TakeLast(1000).ToArray();
        if (recent.Length < 2) { _stats.Text = "  Not enough history"; }
        else
        {
            var earliest = recent[0]; var latest = recent[^1];
            double hours = Math.Max(0.001, (latest.Time-earliest.Time).TotalHours);
            double rate = (earliest.Level - latest.Level)/hours;
            _stats.Text = $"  Samples: {recent.Length} | Latest: {latest.Level}% | Net drain: {rate:F1}%/hour";
        }
        _plot.Invalidate();
    }
    private void PaintGraph(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var area = new Rectangle(58, 24, Math.Max(10,_plot.Width-90), Math.Max(10,_plot.Height-85));
        using var grid = new Pen(Color.FromArgb(70,80,90));
        using var axisFont = new Font("Segoe UI", 9);
        for (int i=0;i<=4;i++)
        {
            int y=area.Top+i*area.Height/4;
            g.DrawLine(grid,area.Left,y,area.Right,y);
            g.DrawString($"{100-i*25}%",axisFont,Brushes.Silver,8,y-9);
        }
        var samples = _points.TakeLast(1000).ToArray();
        if (samples.Length < 2) return;
        long start=samples[0].Time.Ticks, end=samples[^1].Time.Ticks;
        if (start==end) end++;
        foreach(var group in samples.GroupBy(s=>s.Device))
        {
            var points = group.Select(s=>new PointF(area.Left+(float)((s.Time.Ticks-start)/(double)(end-start))*area.Width,
                area.Bottom-s.Level*area.Height/100f)).ToArray();
            if (points.Length>1) using(var pen=new Pen(Color.FromArgb(105,190,255),2.5f)) g.DrawLines(pen,points);
        }
        g.DrawString(samples[0].Time.ToString("dd.MM HH:mm"),axisFont,Brushes.Silver,area.Left,area.Bottom+12);
        g.DrawString(samples[^1].Time.ToString("dd.MM HH:mm"),axisFont,Brushes.Silver,area.Right-90,area.Bottom+12);
    }
}

internal sealed class DeviceProfile
{
    public string Alias { get; set; } = "";
    public int Priority { get; set; } = 0;
    public bool Notify { get; set; } = true;
}
