using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Text;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace BluetoothBatteryTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new BatteryTrayContext());
    }
}

internal record Headset(string Name, int Battery, string Source = "PnP", string DeviceId = "");

internal sealed class BatteryTrayContext : ApplicationContext
{
    private const string AppName = "BluetoothBatteryTray";
    private readonly NativeTrayIcon _tray;
    private readonly System.Threading.SynchronizationContext _ui = System.Threading.SynchronizationContext.Current ?? new System.Windows.Forms.WindowsFormsSynchronizationContext();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _devicesMenu = new("Choose headset");
    private readonly ToolStripMenuItem _startupMenu = new("Start with Windows");
    private readonly Timer _timer = new() { Interval = 30_000 };
    private readonly Timer _startupRetryTimer = new() { Interval = 5_000 };
    private DateTime _startupRetryUntil;
    private readonly AppPreferences _prefs = AppPreferences.Load();
    private readonly BatteryHistory _history = new();
    private readonly Dictionary<string, int> _lastLevels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _alreadyWarned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OverlayForm> _overlays = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolStripMenuItem _overlayVisibleMenu = new("Show on secondary monitors");
    private readonly string _settingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "device.txt");
    private string _selected = "";
    private string _effectiveSelection = "";
    private readonly Dictionary<string, DateTime> _lastNotification = new(StringComparer.OrdinalIgnoreCase);
    private QuickSwitchForm? _quick;
    private readonly System.Windows.Forms.Timer _deviceEventDebounce = new() { Interval = 850 };
    private readonly System.Windows.Forms.Timer _hotkeyRetry = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Control _messageWindow = new();
    private readonly AppMessageFilter _messageFilter;
    private const int HotkeyId = 0xB771;
    private bool _hotkeyRegistered;
    private bool _knownInitialScan;
    private readonly HashSet<string> _knownHardware = new(StringComparer.OrdinalIgnoreCase);
    private List<Headset> _devices = new();
    private bool _scanning;
    private DashboardForm? _dashboard;
    private bool _exiting;
    private long _lastDashboardToggle;
    private Icon? _currentIcon;
    // Native tray icon hover: use the same custom popup as secondary overlays.
    private readonly BatteryInfoPopup _trayPopup = new();
    private readonly Timer _trayHoverTimer = new() { Interval = 80 };
    private long _trayHoverStarted = -1;
    private long _lastTrayMouseEvent = -1;
    private Point _trayMousePoint;


    public BatteryTrayContext()
    {
        _messageWindow.CreateControl();
        _messageFilter = new AppMessageFilter(() => { if (!_exiting) OpenDashboard(); }, () => { if (!_exiting) _deviceEventDebounce.Stop(); if (!_exiting) _deviceEventDebounce.Start(); });
        Application.AddMessageFilter(_messageFilter);
        _deviceEventDebounce.Tick += async (_, _) => { _deviceEventDebounce.Stop(); await RefreshAsync(); };
        _hotkeyRegistered = NativeHotkeys.RegisterHotKey(_messageWindow.Handle, HotkeyId, 0x0002 | 0x0001, (uint)Keys.B);
        _hotkeyRetry.Tick += (_, _) => { if (!_hotkeyRegistered) { _hotkeyRegistered = NativeHotkeys.RegisterHotKey(_messageWindow.Handle, HotkeyId, 0x0002 | 0x0001, (uint)Keys.B); if (_hotkeyRegistered) _hotkeyRetry.Stop(); } };
        if (!_hotkeyRegistered) _hotkeyRetry.Start();
        try { _selected = File.ReadAllText(_settingsFile).Trim(); } catch { /* First run. */ }
        _timer.Interval = _prefs.PollSeconds * 1000;

        _overlayVisibleMenu.CheckOnClick = true;
        _overlayVisibleMenu.Checked = ReadOverlayVisibility();
        _overlayVisibleMenu.CheckedChanged += (_, _) =>
        {
            SaveOverlayVisibility(_overlayVisibleMenu.Checked);
            ApplyAllOverlays();
        };
        _menu.Items.Add(_overlayVisibleMenu);
        _menu.Items.Add("Reset overlay positions", null, (_, _) =>
        {
            foreach (var overlay in _overlays.Values) overlay.ResetPosition();
            ApplyAllOverlays();
        });
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SyncOverlays();
        _menu.Items.Add(new ToolStripSeparator());
        _devicesMenu.Enabled = false;
        _menu.Items.Add(_devicesMenu);
        _menu.Items.Add("Refresh now", null, async (_, _) => await RefreshAsync());
        _menu.Items.Add("Dashboard / Devices...", null, (_, _) => OpenDashboard());
        _menu.Items.Add("Settings...", null, (_, _) =>
        {
            using var settings = new PreferencesForm(_prefs);
            if (settings.ShowDialog() == DialogResult.OK)
            {
                _prefs.Save();
                _timer.Interval = _prefs.PollSeconds * 1000;
                RefreshOverlaysStyle();
                Render();
            }
        });
        _menu.Items.Add("Battery history...", null, (_, _) =>
        {
            var win = new BatteryHistoryForm(_history, _selected);
            win.Show();
        });
        _menu.Items.Add("Export history CSV...", null, (_, _) =>
        {
            using var dlg = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = "battery-history.csv" };
            if (dlg.ShowDialog() == DialogResult.OK) _history.Export(dlg.FileName);
        });
        _menu.Items.Add(_startupMenu);
        _startupMenu.Checked = IsStartupEnabled();
        _startupMenu.Click += (_, _) => ToggleStartup();
        _menu.Items.Add("Open Bluetooth settings", null, (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
            catch { /* Settings unavailable. */ }
        });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _currentIcon = DrawIcon("?", Color.Gray);
        _tray = new NativeTrayIcon(_currentIcon, _menu);
        _tray.DoubleClick += () => OpenQuickSwitch();
        _tray.LeftClick += () => OpenQuickSwitch();
        _tray.RightClick += () => OpenDashboard();
        _tray.MouseMove += () =>
        {
            _lastTrayMouseEvent = Environment.TickCount64;
            _trayMousePoint = Cursor.Position;
            if (_trayHoverStarted < 0) _trayHoverStarted = _lastTrayMouseEvent;
        };
        _trayHoverTimer.Tick += (_, _) =>
        {
            if (_exiting) return;
            long now = Environment.TickCount64;
            // MouseMove is generated by the actual NotifyIcon. Do not use a
            // permanent popup overlay on the primary monitor/taskbar.
            bool near = _lastTrayMouseEvent >= 0
                && Math.Abs(Cursor.Position.X - _trayMousePoint.X) <= 12
                && Math.Abs(Cursor.Position.Y - _trayMousePoint.Y) <= 12;
            if (!near)
            {
                _trayHoverStarted = -1;
                if (_trayPopup.Visible) _trayPopup.Hide();
                return;
            }
            if (!_trayPopup.Visible && now - _trayHoverStarted >= 380)
            {
                _trayPopup.SetBatteryInfo(_lastTooltip);
                PositionTrayPopup();
                _trayPopup.Show();
            }
        };
        _trayHoverTimer.Start();

        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        // Query immediately; while there are no live battery readings, retry
        // every 5 seconds for up to 40 seconds. The configured poll interval
        // (10–300 seconds) remains unchanged for normal operation.
        _startupRetryUntil = DateTime.UtcNow.AddSeconds(40);
        _startupRetryTimer.Tick += async (_, _) =>
        {
            if (_exiting || _devices.Count > 0 || DateTime.UtcNow >= _startupRetryUntil)
            {
                _startupRetryTimer.Stop();
                return;
            }
            await RefreshAsync();
        };
        _startupRetryTimer.Start();
        ApplyAllOverlays();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_scanning || _exiting) return;
        _scanning = true;
        try
        {
            List<Headset> found = await Task.Run(QueryBatteryViaWindowsAsync);
            if (_exiting) return;
            // Polling automatically detects connect/disconnect and new battery reports.
            var newNames = found.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!_knownInitialScan) { _knownHardware.UnionWith(newNames); _knownInitialScan = true; }
            else { _knownHardware.UnionWith(newNames); }
            foreach (var d in found)
            {
                _lastSeen[d.Name] = DateTime.Now;
                _history.Record(d.Name, d.Battery);
                if (_lastLevels.TryGetValue(d.Name, out int prior) && d.Battery > prior + 4)
                    _alreadyWarned.RemoveWhere(k => k.StartsWith(d.Name + "|", StringComparison.OrdinalIgnoreCase));
                bool hadPrior = _lastLevels.TryGetValue(d.Name, out int batteryBefore);
                foreach (int threshold in _prefs.Alerts)
                {
                    string key = d.Name + "|" + threshold;
                    if (_prefs.Notifications && (_prefs.DeviceProfiles.GetValueOrDefault(d.Name)?.Notify ?? true) && d.Battery <= threshold && !_alreadyWarned.Contains(key) &&
                        (!hadPrior || batteryBefore > threshold))
                    {
                        _tray.ShowBalloon("Low Bluetooth battery", $"{d.Name}: {d.Battery}% (limit {threshold}%)");
                        _alreadyWarned.Add(key);
                    }
                }
                _lastLevels[d.Name] = d.Battery;
            }
            // Notify on actual state transitions, never on initial discovery.
            var previous = _devices.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var d in found)
                if (!previous.ContainsKey(d.Name) && _lastSeen.ContainsKey(d.Name))
                    StateNotification(d.Name, "Reconnected");
            foreach (var old in previous.Keys)
                if (!newNames.Contains(old)) StateNotification(old, "Disconnected");
            _devices = found;
            _effectiveSelection = found.Any(d => d.Name.Equals(_selected, StringComparison.OrdinalIgnoreCase))
                ? _selected : found.OrderByDescending(x => _prefs.DeviceProfiles.GetValueOrDefault(x.Name)?.Priority ?? 0).FirstOrDefault()?.Name ?? "";
            _quick?.SetDevices(_devices, _lastSeen.Keys.ToArray(), _effectiveSelection);

            if (found.Count > 0) _startupRetryTimer.Stop();
            if (string.IsNullOrWhiteSpace(_selected) && found.Count > 0) { _selected = found[0].Name; _effectiveSelection=_selected; }
            _devicesMenu.DropDownItems.Clear();
            _devicesMenu.Enabled = found.Count > 0 || _lastSeen.Count > 0;

            foreach (Headset device in found)
            {
                string capturedName = device.Name;
                var item = new ToolStripMenuItem($"{device.Name}  ({device.Battery}%)")
                {
                    Checked = string.Equals(_selected, device.Name, StringComparison.OrdinalIgnoreCase)
                };
                item.Click += (_, _) =>
                {
                    _selected = capturedName; _effectiveSelection = capturedName;
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
                        File.WriteAllText(_settingsFile, _selected);
                    }
                    catch { /* Cannot persist selection; current session still works. */ }
                    Render();
                    _dashboard?.UpdateDevices(_devices, _lastSeen.Keys.ToArray(), _selected);
                };
                _devicesMenu.DropDownItems.Add(item);
            }
            foreach (string missing in _lastSeen.Keys.Where(n => !newNames.Contains(n)).ToArray())
            {
                var mi = new ToolStripMenuItem(missing + "  (disconnected)");
                mi.Click += (_, _) => { _selected = missing; Render(); };
                _devicesMenu.DropDownItems.Add(mi);
            }
            Render();
            _dashboard?.UpdateDevices(_devices, _lastSeen.Keys.ToArray(), _selected);
        }
        catch (Exception ex)
        {
            if (!_exiting) SetIcon("!", Color.DarkOrange, "Battery check failed: " + ex.Message);
        }
        finally { _scanning = false; }
    }

    private void Render()
    {
        if (_exiting) return;
        Headset? selected = _devices.FirstOrDefault(d => d.Name.Equals(_effectiveSelection, StringComparison.OrdinalIgnoreCase));
        if (selected is null) selected = _devices.FirstOrDefault();
        if (selected is null)
        {
            bool disconnected = _lastSeen.ContainsKey(_selected);
            SetIcon(disconnected ? "--" : "?", Color.Gray,
                disconnected ? _selected + ": disconnected / battery unavailable" : "No compatible Bluetooth battery reported by Windows");
            return;
        }
        string digits = selected.Battery.ToString(CultureInfo.InvariantCulture);
        Color color = _prefs.ColorMode == "White" ? Color.White :
            selected.Battery < 15 ? Color.OrangeRed : selected.Battery < 30 ? Color.Gold : Color.White;
        SetIcon(digits, color, $"{selected.Name}: {selected.Battery}% · {selected.Source}");
        foreach (ToolStripItem toolItem in _devicesMenu.DropDownItems)
            if (toolItem is ToolStripMenuItem mi)
                mi.Checked = (mi.Text ?? string.Empty).StartsWith(selected.Name + "  (", StringComparison.OrdinalIgnoreCase);
    }

    private void StateNotification(string device, string state)
    {
        if (!_prefs.Notifications || !(_prefs.DeviceProfiles.GetValueOrDefault(device)?.Notify ?? true)) return;
        string key = device + "|" + state;
        if (_lastNotification.TryGetValue(key, out var last) && (DateTime.UtcNow-last).TotalMinutes < 5) return;
        _lastNotification[key] = DateTime.UtcNow;
        _tray.ShowBalloon("Device " + state, device);
    }

    private void OpenQuickSwitch()
    {
        if (_quick is null || _quick.IsDisposed)
        {
            _quick = new QuickSwitchForm(name =>
            {
                _selected = name;
                _effectiveSelection = name;
                try { Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!); File.WriteAllText(_settingsFile,name); } catch { }
                Render();
                _dashboard?.UpdateDevices(_devices, _lastSeen.Keys.ToArray(), _selected);
            });
        }
        if (_quick.Visible) { _quick.Hide(); return; }
        _quick.SetDevices(_devices, _lastSeen.Keys.ToArray(), _effectiveSelection);
        _quick.ShowNearCursor();
    }

    private void OpenDashboard()
    {
        // Ignore duplicate notification messages from a single click.
        long now = Environment.TickCount64;
        if (_lastDashboardToggle != 0 && now - _lastDashboardToggle < 350) return;
        _lastDashboardToggle = now;
        if (_dashboard is null || _dashboard.IsDisposed)
        {
            _dashboard = new DashboardForm(
                () => (_devices.ToArray(), _lastSeen.Keys.ToArray(), _selected),
                name =>
                {
                    _selected = name; _effectiveSelection = name;
                    try { Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!); File.WriteAllText(_settingsFile, name); } catch { }
                    Render();
                    _dashboard?.UpdateDevices(_devices, _lastSeen.Keys.ToArray(), _selected);
                },
                _prefs, _history,
                () => { _prefs.Save(); _timer.Interval = _prefs.PollSeconds * 1000; RefreshOverlaysStyle(); Render(); },
                () => IsStartupEnabled(),
                () => ToggleStartup(),
                () => _overlayVisibleMenu.Checked,
                b => { _overlayVisibleMenu.Checked = b; ApplyAllOverlays(); },
                () => { var f = new BatteryHistoryForm(_history, _selected); f.Show(); },
                () => { _ = RefreshAsync(); },
                () => { using var dlg = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = "battery-history.csv" }; if (dlg.ShowDialog() == DialogResult.OK) _history.Export(dlg.FileName); },
                () => { foreach (var overlay in _overlays.Values) overlay.ResetPosition(); ApplyAllOverlays(); },
                () => { try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); } catch { } },
                () => ExitThread());
            _dashboard.FormClosed += (_, _) => _dashboard = null;
            _dashboard.PositionNearCursor();
            _dashboard.ShowAnimated();
        }
        else { if (_dashboard.Visible) _dashboard.Hide(); else { _dashboard.PositionNearCursor(); _dashboard.ShowAnimated(); } }
        _dashboard?.UpdateDevices(_devices, _lastSeen.Keys.ToArray(), _selected);
    }

    private void SetIcon(string digits, Color color, string tooltip)
    {
        // Native 16px notification icons must retain fixed, controlled weight.
        // User thickness affects secondary monitors only.
        Icon next = DrawIcon(digits, color, _prefs.IndicatorStyle == "Battery Icon + Percentage");
        Icon? old = _currentIcon;
        _tray.SetIcon(next);
        _currentIcon = next;
        old?.Dispose();
        _trayPopup.SetBatteryInfo(tooltip);
        if (_trayPopup.Visible) PositionTrayPopup();
        _lastDigits = digits;
        _lastTooltip = tooltip;
        foreach (var overlay in _overlays.Values) overlay.SetBattery(digits, tooltip);
    }

    // High-resolution vector glyphs -> 16px tray icon, downsampled with antialiasing.
    // Windows limits notification icon dimensions; this maximizes apparent weight
    // and clarity within those 16 logical pixels.
    private static Icon DrawIcon(string digits, Color color, bool batteryMode = false)
    {
        const int size = 16;
        const int renderSize = 128;
        using var large = new Bitmap(renderSize, renderSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(large))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // True transparent tray icon; no dark tile or border behind digits.
            using var path = new GraphicsPath();
            using var family = new FontFamily("Arial");
            using var fmt = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Near,
                FormatFlags = StringFormatFlags.NoWrap
            };
            path.AddString(digits, family, (int)FontStyle.Bold, 105f,
                new PointF(0, 0), fmt);
            RectangleF bounds = path.GetBounds();
            float padX = digits.Length >= 3 ? 0.35f : 0.6f;
            float maxWidth = batteryMode ? 81f : renderSize - 2 * padX * 8;
            float maxHeight = renderSize - 13f;
            float scale = Math.Min(maxWidth / bounds.Width, maxHeight / bounds.Height);
            float scaledWidth = bounds.Width * scale;
            float scaledHeight = bounds.Height * scale;
            using var transform = new Matrix();
            transform.Scale(scale, scale);
            transform.Translate((batteryMode ? 43f + (81f - scaledWidth) / 2f : (renderSize - scaledWidth) / 2f) - bounds.Left * scale,
                (renderSize - scaledHeight) / 2f - bounds.Top * scale,
                MatrixOrder.Append);
            path.Transform(transform);
            using var stroke = new Pen(color, digits.Length >= 3 ? 2.2f : 3.4f)
            {
                LineJoin = LineJoin.Round
            };
            using var brush = new SolidBrush(color);
            g.DrawPath(stroke, path);
            g.FillPath(brush, path);
            if (batteryMode)
            {
                // Compact battery glyph at left. Draw at 128px and downsample to tray's 16px.
                using var batteryPen = new Pen(Color.FromArgb(230, 140, 250, 165), 8f) { LineJoin = LineJoin.Round };
                using var batteryBrush = new SolidBrush(Color.FromArgb(220, 140, 250, 165));
                using var fillBrush = new SolidBrush(Color.FromArgb(245, 140, 250, 165));
                using (var batteryPath = new GraphicsPath())
                {
                    batteryPath.AddArc(5, 39, 14, 14, 180, 90);
                    batteryPath.AddArc(21, 39, 14, 14, 270, 90);
                    batteryPath.AddArc(21, 74, 14, 14, 0, 90);
                    batteryPath.AddArc(5, 74, 14, 14, 90, 90);
                    batteryPath.CloseFigure();
                    g.DrawPath(batteryPen, batteryPath);
                }
                g.FillRectangle(batteryBrush, 35, 56, 6, 16);
                g.FillRectangle(fillBrush, 11, 55, 18, 27);
            }
        }
        using var small = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(small))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(large, new Rectangle(0, 0, size, size),
                new Rectangle(0, 0, renderSize, renderSize), GraphicsUnit.Pixel);
        }
        IntPtr handle = small.GetHicon();
        try
        {
            using Icon temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static async Task<List<Headset>> QueryBatteryViaWindowsAsync()
    {
        // Windows reports some Bluetooth Classic headset batteries on the
        // Hands-Free AG PnP node. This is an undocumented property, so not all
        // headphones/drivers report it. Does NOT fake a battery percentage.
        const string ps = """
            $ErrorActionPreference = 'SilentlyContinue'
            $key = '{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2'
            $audio = @(Get-PnpDevice -Class AudioEndpoint -PresentOnly -ErrorAction SilentlyContinue |
                Where-Object Status -eq 'OK' | Select-Object -ExpandProperty FriendlyName)
            # USB dongles, 2.4 GHz HID, keyboards and mice included. Only emit
            # real, numeric battery properties; never substitute a guessed value.
            Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {
                $_.Status -eq 'OK' -and $_.FriendlyName -and
                ($_.Class -in @('Bluetooth','HIDClass','Mouse','Keyboard','USB','Battery') -or
                 $_.FriendlyName -match 'Hands[- ]?Free AG')
            } | ForEach-Object {
                $d = $_
                if ($d.FriendlyName -match 'Hands[- ]?Free AG') {
                    $base = ([string]$d.FriendlyName -replace '(?i)\s*Hands[- ]?Free\s*AG.*$', '').Trim()
                    if (-not ($audio | Where-Object { $_ -like ('*' + $base + '*') })) { return }
                }
                $property = Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName $key -ErrorAction SilentlyContinue
                if ($null -eq $property -or $null -eq $property.Data) { return }
                $raw = $property.Data
                if ($raw -is [array]) { $raw = $raw[0] }
                $percent=0
                if ([int]::TryParse([string]$raw,[ref]$percent) -and $percent -ge 0 -and $percent -le 100) {
                    $name = ([string]$d.FriendlyName -replace '[\r\n\t]',' ')
                    $id = ([string]$d.InstanceId -replace '[\r\n\t]',' ')
                    Write-Output ($name + "`t" + $percent + "`t" + 'PnP/' + $d.Class + "`t" + $id)
                }
            }
            """;

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }
        };
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return new List<Headset>();
        }
        _ = await stderr;
        string text = await output;
        var results = new List<Headset>();
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Trim().Split('\t');
            if (parts.Length < 2 || !int.TryParse(parts[1], out int percent) || percent is < 0 or > 100) continue;
            results.Add(new Headset(parts[0], percent, parts.Length > 2 ? parts[2] : "PnP", parts.Length > 3 ? parts[3] : ""));
        }
        return results.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).OrderBy(d => d.Name).ToList();
    }

    private readonly string _overlayVisibilityFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "all-monitors.txt");

    private bool ReadOverlayVisibility()
    {
        try { return File.ReadAllText(_overlayVisibilityFile).Trim() != "false"; }
        catch { return true; }
    }

    private void SaveOverlayVisibility(bool visible)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_overlayVisibilityFile)!);
            File.WriteAllText(_overlayVisibilityFile, visible ? "true" : "false");
        }
        catch { }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_exiting) return;
        // The event can originate outside the UI thread.
        _ui.Post(_ => { if (!_exiting) SyncOverlays(); }, null);
    }

    private void SyncOverlays()
    {
        var monitors = Screen.AllScreens;
        var active = new HashSet<string>(monitors.Select(s => s.DeviceName), StringComparer.OrdinalIgnoreCase);
        foreach (var key in _overlays.Keys.Where(k => !active.Contains(k)).ToArray())
        {
            var obsolete = _overlays[key];
            _overlays.Remove(key);
            obsolete.Close();
            obsolete.Dispose();
        }
        foreach (var monitor in monitors)
        {
            if (_overlays.ContainsKey(monitor.DeviceName)) continue;
            var overlay = new OverlayForm(_menu, monitor.DeviceName);
            overlay.SetStyle(_prefs.OverlayWidth, _prefs.FontWeight, _prefs.ColorMode);
            overlay.SetIndicatorStyle(_prefs.IndicatorStyle);
            _overlays.Add(monitor.DeviceName, overlay);
            overlay.SetBattery(_lastDigits, _lastTooltip);
        }
        ApplyAllOverlays();
    }

    private void RefreshOverlaysStyle()
    {
        // Redraw the main tray icon as well as secondary overlays after changing style.
        Render();
        foreach (var overlay in _overlays.Values)
        {
            overlay.SetStyle(_prefs.OverlayWidth, _prefs.FontWeight, _prefs.ColorMode);
            overlay.SetIndicatorStyle(_prefs.IndicatorStyle);
        }
    }

    private string _lastDigits = "?";
    private string _lastTooltip = "Checking Bluetooth battery...";

    private void ApplyAllOverlays()
    {
        // The primary display already has the real NotifyIcon in Windows System Tray.
        // Never create an overlay on it: otherwise large digits overlap native tray icons.
        // Overlays belong only to non-primary displays.
        string? primaryId = Screen.PrimaryScreen?.DeviceName;
        foreach (var overlay in _overlays.Values)
        {
            bool isPrimary = string.Equals(overlay.MonitorId, primaryId,
                StringComparison.OrdinalIgnoreCase);
            overlay.EnabledOnScreen = _overlayVisibleMenu.Checked && !isPrimary;
            overlay.ApplyMonitor();
        }
    }

    private void PositionTrayPopup()
    {
        // Position near the notification area without obscuring the tray icon.
        Point p = _trayMousePoint;
        Rectangle area = Screen.FromPoint(p).Bounds;
        int x = Math.Clamp(p.X - _trayPopup.Width / 2,
            area.Left + 6, Math.Max(area.Left + 6, area.Right - _trayPopup.Width - 6));
        int above = p.Y - _trayPopup.Height - 24;
        int y = above >= area.Top ? above : Math.Min(area.Bottom - _trayPopup.Height - 6, p.Y + 28);
        _trayPopup.Location = new Point(x, y);
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue(AppName) is string;
        }
        catch { return false; }
    }

    private void ToggleStartup()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (key is null) return;
            if (IsStartupEnabled()) key.DeleteValue(AppName, throwOnMissingValue: false);
            else key.SetValue(AppName, "\"" + Environment.ProcessPath + "\"");
            _startupMenu.Checked = IsStartupEnabled();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Startup setting", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    protected override void ExitThreadCore()
    {
        _exiting = true;
        if (_hotkeyRegistered) NativeHotkeys.UnregisterHotKey(_messageWindow.Handle, HotkeyId);
        _hotkeyRetry.Stop(); _hotkeyRetry.Dispose();
        Application.RemoveMessageFilter(_messageFilter);
        _messageWindow.Dispose();
        _deviceEventDebounce.Stop(); _deviceEventDebounce.Dispose();
        _timer.Stop();
        _timer.Dispose();
        _trayHoverTimer.Stop();
        _trayHoverTimer.Dispose();
        _startupRetryTimer.Stop();
        _startupRetryTimer.Dispose();
        _trayPopup.Hide();
        _trayPopup.Dispose();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        foreach (var overlay in _overlays.Values) { overlay.Close(); overlay.Dispose(); }
        _overlays.Clear();
        _dashboard?.Close();
        _quick?.Close(); _quick?.Dispose();
        _tray.Dispose();
        _history.Dispose();
        _currentIcon?.Dispose();
        _menu.Dispose();
        base.ExitThreadCore();
    }
}

internal static class NativeHotkeys
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError=true)]
    internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError=true)]
    internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
internal sealed class AppMessageFilter : IMessageFilter
{
    private readonly Action hotkey, deviceChanged;
    public AppMessageFilter(Action hotkey, Action deviceChanged) { this.hotkey=hotkey; this.deviceChanged=deviceChanged; }
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam.ToInt32() == 0xB771) { hotkey(); return true; }
        if (m.Msg == 0x0219 || m.Msg == 0x001A) deviceChanged();
        return false;
    }
}
