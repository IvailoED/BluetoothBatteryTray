using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace BluetoothBatteryTray;

internal sealed class DashboardForm : Form
{
    private readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(9, 13, 23) };
    private readonly Func<(Headset[] connected, string[] known, string selected)> get;
    private readonly Action<string> select;
    private readonly AppPreferences prefs;
    private readonly Action save, history, refresh, export, reset, bluetooth, exit, toggleStartup;
    private readonly BatteryHistory batteryHistory;
    private readonly Func<bool> getStartup, getSecondary;
    private readonly Action<bool> setSecondary;
    private bool ready;
    private long openedAt;
    private long lastInteractionAt;
    private readonly System.Windows.Forms.Timer dismissGuard = new() { Interval = 120 };
    private (Headset[] connected, string[] known, string selected)? queued;
    private readonly System.Windows.Forms.Timer fadeTimer = new() { Interval = 16 };

    public DashboardForm(Func<(Headset[] connected, string[] known, string selected)> get,
        Action<string> select, AppPreferences prefs, BatteryHistory batteryHistory, Action save, Func<bool> getStartup,
        Action toggleStartup, Func<bool> getSecondary, Action<bool> setSecondary,
        Action history, Action refresh, Action export, Action resetPositions,
        Action bluetoothSettings, Action exit)
    {
        this.get=get; this.select=select; this.prefs=prefs; this.save=save; this.batteryHistory=batteryHistory;
        this.getStartup=getStartup; this.toggleStartup=toggleStartup;
        this.getSecondary=getSecondary; this.setSecondary=setSecondary;
        this.history=history; this.refresh=refresh; this.export=export;
        reset=resetPositions; bluetooth=bluetoothSettings; this.exit=exit;
        Text="BluetoothBatteryTray";
        FormBorderStyle=FormBorderStyle.None;
        ShowInTaskbar=false;
        TopMost=true;
        StartPosition=FormStartPosition.Manual;
        ClientSize=new Size(510, 715);
        Resize += (_, _) => ApplyRoundedWindow();
        ApplyRoundedWindow();
        BackColor=Color.FromArgb(9,13,23);
        KeyPreview=true;
        KeyDown+=(_,e)=> { if(e.KeyCode==Keys.Escape) Hide(); };
        // Windows/WebView2 may briefly activate a different HWND while opening.
        // Check after the initial animation to avoid instantly hiding the popup.
        Deactivate += (_, _) =>
        {
            if (!Visible) return;
            if (Environment.TickCount64 - openedAt < 650 || Environment.TickCount64 - lastInteractionAt < 650) return;
            BeginInvoke(new Action(() =>
            {
                if (Visible && !ContainsFocus && Form.ActiveForm != this) Hide();
            }));
        };
        dismissGuard.Tick += (_, _) =>
        {
            if (!Visible) { dismissGuard.Stop(); return; }
            if (Environment.TickCount64 - openedAt < 650 || Environment.TickCount64 - lastInteractionAt < 650) return;
            dismissGuard.Stop();
            // If the popup lost focus during the opening animation, hide it now.
            if (!ContainsFocus && Form.ActiveForm != this) Hide();
        };
        VisibleChanged += (_, _) => { if (!Visible) { fadeTimer.Stop(); dismissGuard.Stop(); } };
        Controls.Add(web);
        web.CoreWebView2InitializationCompleted+=(_,e)=> {
            if(!e.IsSuccess) { MessageBox.Show("WebView2 could not start: "+e.InitializationException?.Message, "BluetoothBatteryTray"); return; }
            web.CoreWebView2.Settings.AreDevToolsEnabled=false;
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;
            web.CoreWebView2.Settings.IsStatusBarEnabled=false;
            web.CoreWebView2.WebMessageReceived+=OnMessage;
            var f=Path.Combine(AppContext.BaseDirectory,"dashboard.html");
            web.CoreWebView2.Navigate(new Uri(f).AbsoluteUri);
        };
        web.NavigationCompleted+=(_,e)=> { ready=e.IsSuccess; if(ready) { SendPrefs(); var data=queued ?? get(); UpdateDevices(data.connected,data.known,data.selected); } };
        fadeTimer.Tick+=(_,_)=> { Opacity=Math.Min(1,Opacity+0.14); if(Opacity>=1) fadeTimer.Stop(); };
        Shown+= async (_,_)=> { try { await web.EnsureCoreWebView2Async(); } catch(Exception ex) { MessageBox.Show("WebView2 Runtime is required: "+ex.Message); } };
    }
    // Clip the native WebView2 HWND to the same rounded outline as the CSS shell.
    // CSS border-radius by itself cannot round the native WinForms host window.
    private void ApplyRoundedWindow()
    {
        if (Width < 2 || Height < 2) return;
        int radius = 18;
        using var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(Width-d, 0, d, d, 270, 90);
        path.AddArc(Width-d, Height-d, d, d, 0, 90);
        path.AddArc(0, Height-d, d, d, 90, 90);
        path.CloseFigure();
        var newRegion = new Region(path);
        var oldRegion = Region;
        Region = newRegion;
        oldRegion?.Dispose();
    }

    private void Post(object value)
    {
        if(!ready || web.CoreWebView2==null) return;
        try { web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value)); } catch(ObjectDisposedException) { }
    }
    private void SendPrefs() => Post(new {
        type="settings", notifications=prefs.Notifications, alerts=prefs.Alerts,
        poll=prefs.PollSeconds, width=prefs.OverlayWidth, weight=prefs.FontWeight,
        color=prefs.ColorMode, indicatorStyle=prefs.IndicatorStyle, startup=getStartup(), secondary=getSecondary(), compact=prefs.CompactMode, profiles=prefs.DeviceProfiles
    });
    private void OnMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
    {
        try {
            using var doc=JsonDocument.Parse(args.WebMessageAsJson);
            var root=doc.RootElement;
            string action=root.GetProperty("action").GetString() ?? "";
            lastInteractionAt=Environment.TickCount64;
            switch(action) {
                case "select": select(root.GetProperty("name").GetString() ?? ""); break;
                case "analytics":
                    string device=root.GetProperty("name").GetString() ?? "";
                    int days=Math.Clamp(root.GetProperty("days").GetInt32(),1,30);
                    Post(new {type="analytics",name=device,days,stats=BatteryInsights.Calculate(batteryHistory.Get(device),days)});
                    break;
                case "compact":
                    prefs.CompactMode=root.GetProperty("value").GetBoolean(); prefs.Save(); SendPrefs(); break;
                case "profile":
                    string profileName=root.GetProperty("name").GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(profileName)) {
                        var profile=prefs.DeviceProfiles.GetValueOrDefault(profileName) ?? new DeviceProfile();
                        profile.Alias=root.GetProperty("alias").GetString() ?? "";
                        profile.Priority=Math.Clamp(root.GetProperty("priority").GetInt32(),0,100);
                        profile.Notify=root.GetProperty("notify").GetBoolean();
                        prefs.DeviceProfiles[profileName]=profile; prefs.Save(); SendPrefs();
                    }
                    break;
                case "refresh": refresh(); break;
                case "history": history(); break;
                case "export": export(); break;
                case "reset": reset(); break;
                case "bluetooth": bluetooth(); break;
                case "exit": exit(); break;
                case "close": Hide(); break;
                case "startup": toggleStartup(); SendPrefs(); break;
                case "secondary": setSecondary(root.GetProperty("value").GetBoolean()); break;
                case "save":
                    bool desiredStartup=root.GetProperty("startup").GetBoolean();
                    bool desiredSecondary=root.GetProperty("secondary").GetBoolean();
                    prefs.Notifications=root.GetProperty("notifications").GetBoolean();
                    prefs.Alerts=root.GetProperty("alerts").EnumerateArray().Select(x=>x.GetInt32()).ToArray();
                    prefs.PollSeconds=Math.Clamp(root.GetProperty("poll").GetInt32(),10,300);
                    prefs.OverlayWidth=Math.Clamp(root.GetProperty("width").GetInt32(),36,100);
                    prefs.FontWeight=Math.Clamp(root.GetProperty("weight").GetInt32(),1,10);
                    prefs.ColorMode=root.GetProperty("color").GetString()=="White" ? "White":"Dynamic";
                    if (root.TryGetProperty("indicatorStyle", out var indicator))
                        prefs.IndicatorStyle=indicator.GetString()=="Battery Icon + Percentage" ? "Battery Icon + Percentage" : "Percentage Only";
                    save();
                    if (desiredStartup != getStartup()) toggleStartup();
                    if (desiredSecondary != getSecondary()) setSecondary(desiredSecondary);
                    SendPrefs(); Post(new {type="saved"}); break;
            }
        } catch(Exception ex) { Post(new { type="error", message=ex.Message }); }
    }
    public void UpdateDevices(System.Collections.Generic.IReadOnlyList<Headset> current,
        System.Collections.Generic.IEnumerable<string> previouslySeen,string selected)
    {
        if(IsDisposed || Disposing) return;
        if(InvokeRequired) { BeginInvoke(new Action(()=>UpdateDevices(current,previouslySeen,selected))); return; }
        var names=current.Select(x=>x.Name).Concat(previouslySeen).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x).ToArray();
        queued=(current.ToArray(),names,selected);
        Post(new {type="devices",selected,profiles=prefs.DeviceProfiles,devices=names.Select(name => {
            var d=current.FirstOrDefault(x=>x.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
            var recent=batteryHistory.Get(name).LastOrDefault();
            return new {name, battery=d?.Battery, connected=d!=null, source=d?.Source ?? "Unavailable",
                deviceId=d?.DeviceId ?? "", lastSeen=recent?.Time.ToString("dd.MM.yyyy HH:mm:ss") ?? "Never"};
        }).ToArray(),updated=DateTime.Now.ToString("HH:mm:ss")});
    }
    public void PositionNearCursor()
    {
        var area=Screen.FromPoint(Cursor.Position).WorkingArea;
        int h=Math.Min(730,Math.Max(390,area.Height-18));
        Size=new Size(510,h);
        Location=new Point(Math.Clamp(Cursor.Position.X-Width+28,area.Left+8,Math.Max(area.Left+8,area.Right-Width-8)),
            Math.Clamp(Cursor.Position.Y-Height-10,area.Top+8,Math.Max(area.Top+8,area.Bottom-Height-8)));
    }
    public void ShowAnimated()
    {
        if(Visible) { Activate(); return; }
        PositionNearCursor(); openedAt=Environment.TickCount64; Opacity=0; Show(); Activate(); fadeTimer.Start(); dismissGuard.Start();
        SendPrefs(); var d=get(); UpdateDevices(d.connected,d.known,d.selected);
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing) { fadeTimer.Dispose(); dismissGuard.Dispose(); }
        base.Dispose(disposing);
    }
}
