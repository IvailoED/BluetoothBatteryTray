using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BluetoothBatteryTray;

// Direct Shell_NotifyIcon interface. NOTIFYICON_VERSION_4 without NIF_SHOWTIP
// suppresses the standard Windows tooltip, unlike NotifyIcon.Text = " ".
internal sealed class NativeTrayIcon : NativeWindow, IDisposable
{
    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;
    private const uint NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_INFO = 0x00000010;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const int WM_APP_TRAY = 0x8000 + 73;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT = 0x0400;
    private bool _added;
    private bool _disposed;
    private Icon _icon;
    private readonly ContextMenuStrip _menu;
    public event Action? MouseMove;
    public event Action? DoubleClick;
    public event Action? RightClick;
    public event Action? LeftClick;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    public NativeTrayIcon(Icon icon, ContextMenuStrip menu)
    {
        _icon = icon;
        _menu = menu;
        CreateHandle(new CreateParams { Caption = "BluetoothBatteryTray_NotificationHost" });
        var data = NewData(NIF_MESSAGE | NIF_ICON);
        if (!Shell_NotifyIcon(NIM_ADD, ref data))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create notification icon.");
        _added = true;
        // Crucial: NIF_SHOWTIP is deliberately NOT set in any request.
        data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        if (!Shell_NotifyIcon(NIM_SETVERSION, ref data))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enable native notification icon version 4.");
    }

    private NOTIFYICONDATA NewData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon.Handle,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    public void ShowBalloon(string title, string message)
    {
        if (_disposed) return;
        var data = NewData(NIF_INFO);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = message.Length > 255 ? message[..255] : message;
        data.dwInfoFlags = 1; // NIIF_INFO
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void SetIcon(Icon icon)
    {
        if (_disposed) return;
        _icon = icon;
        var data = NewData(NIF_ICON);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_APP_TRAY)
        {
            // In NOTIFYICON_VERSION_4, event ID is in LOWORD(lParam).
            int notification = unchecked((int)((long)m.LParam & 0xFFFF));
            switch (notification)
            {
                case WM_MOUSEMOVE: MouseMove?.Invoke(); break;
                case WM_LBUTTONDBLCLK: DoubleClick?.Invoke(); break;
                // Version 4 sends WM_CONTEXTMENU for a right-click.
                // Handling WM_RBUTTONUP too can toggle the popup twice.
                case WM_CONTEXTMENU:
                    RightClick?.Invoke();
                    break;
                case NIN_SELECT: LeftClick?.Invoke(); break;
            }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_added)
        {
            var data = NewData(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        DestroyHandle();
    }
}
