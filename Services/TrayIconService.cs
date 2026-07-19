using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AirPodsBattery.Services;

/// <summary>
/// Notification-area icon drawn on the fly: the current battery percentage as
/// crisp colour-coded text (green/amber/red, gray when stale), with a small
/// bolt dot while charging. Raw Shell_NotifyIcon interop — no third-party
/// dependencies, which keeps us safe on brand-new Windows App SDK versions.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    /// <summary>Left-click on the icon — show/hide the flyout.</summary>
    public event Action? FlyoutRequested;
    /// <summary>"Open dashboard" chosen from the context menu.</summary>
    public event Action? DashboardRequested;
    /// <summary>"Sync audio output" chosen from the context menu.</summary>
    public event Action? SyncRequested;
    /// <summary>"Start with Windows" toggled from the context menu.</summary>
    public event Action? StartupToggled;
    /// <summary>"Exit" chosen from the context menu.</summary>
    public event Action? ExitRequested;

    /// <summary>Check state shown next to "Start with Windows" (set by the app).</summary>
    public bool StartupChecked { get; set; }

    private const uint TrayCallbackMsg = 0x8001; // WM_APP + 1
    private const uint IconId = 1;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;

    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;

    private const uint CmdDashboard = 1, CmdExit = 2, CmdSync = 3, CmdStartup = 4;

    private readonly WndProcDelegate _wndProc; // field keeps the delegate alive for GC
    private readonly nint _hwnd;
    private readonly uint _taskbarCreatedMsg;
    private nint _hIcon;
    private string _tooltip = "AirPods Battery";
    private (int? Percent, bool Charging, bool Stale) _state = (null, false, true);

    public TrayIconService()
    {
        _wndProc = WndProc;
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = "AirPodsBatteryTrayWnd",
        };
        RegisterClassExW(ref wc);

        // Hidden top-level window that receives tray callbacks on the UI thread.
        _hwnd = CreateWindowExW(0, wc.lpszClassName, string.Empty, 0,
            0, 0, 0, 0, 0, 0, wc.hInstance, 0);

        AddIcon();
    }

    /// <summary>Redraws the badge and tooltip. Pass a null percent for "no reading".</summary>
    public void Update(int? percent, bool charging, bool stale, string tooltip)
    {
        _state = (percent, charging, stale);
        _tooltip = tooltip;

        nint oldIcon = _hIcon;
        _hIcon = CreateBadgeIcon(percent, charging, stale);

        var data = BuildIconData();
        Shell_NotifyIconW(NimModify, ref data);

        if (oldIcon != 0) DestroyIcon(oldIcon);
    }

    private void AddIcon()
    {
        if (_hIcon == 0)
            _hIcon = CreateBadgeIcon(_state.Percent, _state.Charging, _state.Stale);

        var data = BuildIconData();
        Shell_NotifyIconW(NimAdd, ref data);
    }

    private NOTIFYICONDATAW BuildIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = NifMessage | NifIcon | NifTip,
        uCallbackMessage = TrayCallbackMsg,
        hIcon = _hIcon,
        szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == TrayCallbackMsg)
        {
            switch ((uint)(lParam & 0xFFFF))
            {
                case WmLButtonUp: FlyoutRequested?.Invoke(); break;
                case WmRButtonUp: ShowContextMenu(); break;
            }
            return 0;
        }

        // Explorer restarted — the notification area was rebuilt, re-add our icon.
        if (msg == _taskbarCreatedMsg)
        {
            AddIcon();
            return 0;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        nint menu = CreatePopupMenu();
        AppendMenuW(menu, MfString, CmdSync, "Sync audio output");
        AppendMenuW(menu, MfString, CmdDashboard, "Open dashboard");
        AppendMenuW(menu, MfSeparator, 0, null);
        AppendMenuW(menu, MfString | (StartupChecked ? MfChecked : 0), CmdStartup, "Start with Windows");
        AppendMenuW(menu, MfSeparator, 0, null);
        AppendMenuW(menu, MfString, CmdExit, "Exit");

        GetCursorPos(out POINT pt);
        // Required so the menu dismisses when the user clicks elsewhere.
        SetForegroundWindow(_hwnd);
        int cmd = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton, pt.X, pt.Y, _hwnd, 0);
        DestroyMenu(menu);

        if (cmd == CmdDashboard) DashboardRequested?.Invoke();
        else if (cmd == CmdSync) SyncRequested?.Invoke();
        else if (cmd == CmdStartup) StartupToggled?.Invoke();
        else if (cmd == CmdExit) ExitRequested?.Invoke();
    }

    /// <summary>
    /// Renders the percentage as bold text sized to the system small-icon
    /// metric (already DPI-scaled), so the number stays sharp on any display.
    /// </summary>
    private static nint CreateBadgeIcon(int? percent, bool charging, bool stale)
    {
        int size = Math.Max(16, GetSystemMetrics(SmCxSmIcon));

        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        string text = percent?.ToString() ?? "–";
        Color color = stale || percent is null ? Color.FromArgb(156, 163, 175) // gray
            : percent >= 50 ? Color.FromArgb(74, 222, 128)                     // green
            : percent >= 20 ? Color.FromArgb(251, 191, 36)                     // amber
            : Color.FromArgb(248, 113, 113);                                   // red

        float em = size * (text.Length >= 3 ? 0.50f : text.Length == 2 ? 0.70f : 0.80f);
        using var font = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        var fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(text, font, brush, new RectangleF(0, -0.5f, size, size + 1f), fmt);

        if (charging && !stale)
        {
            // Small yellow "bolt dot" in the top-right corner.
            using var bolt = new SolidBrush(Color.FromArgb(250, 204, 21));
            float d = size * 0.28f;
            g.FillEllipse(bolt, size - d, 0, d, d);
        }

        return bmp.GetHicon(); // released via DestroyIcon in Update/Dispose
    }

    public void Dispose()
    {
        var data = BuildIconData();
        Shell_NotifyIconW(NimDelete, ref data);
        if (_hIcon != 0) DestroyIcon(_hIcon);
        DestroyWindow(_hwnd);
    }

    // ---- Win32 interop ------------------------------------------------------

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    private const int SmCxSmIcon = 49;
    private const uint MfString = 0x0, MfSeparator = 0x800, MfChecked = 0x8;
    private const uint TpmReturnCmd = 0x0100, TpmRightButton = 0x0002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);
}
