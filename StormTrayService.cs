using System;
using System.IO;
using System.Runtime.InteropServices;

namespace StormDsDecryptor;

public sealed class StormTrayService : IDisposable
{
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 2026;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_MEASUREITEM = 0x002C;
    private const int WM_DRAWITEM = 0x002B;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;
    private const int NIIF_INFO = 0x00000001;

    private const uint MF_OWNERDRAW = 0x00000100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint MIM_BACKGROUND = 0x00000002;
    private const uint MIM_APPLYTOSUBMENUS = 0x80000000;

    private const int CMD_RESTORE = 1001;
    private const int CMD_UPDATES = 1002;
    private const int CMD_ABOUT = 1003;
    private const int CMD_SEP = 1004;
    private const int CMD_EXIT = 1005;

    private const uint ODS_SELECTED = 0x0001;
    private const int DT_LEFT = 0x00000000;
    private const int DT_VCENTER = 0x00000004;
    private const int DT_SINGLELINE = 0x00000020;
    private const int TRANSPARENT = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEASUREITEMSTRUCT
    {
        public uint CtlType;
        public uint CtlID;
        public uint itemID;
        public uint itemWidth;
        public uint itemHeight;
        public IntPtr itemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DRAWITEMSTRUCT
    {
        public uint CtlType;
        public uint CtlID;
        public uint itemID;
        public uint itemAction;
        public uint itemState;
        public IntPtr hwndItem;
        public IntPtr hDC;
        public RECT rcItem;
        public IntPtr itemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MENUINFO
    {
        public uint cbSize;
        public uint fMask;
        public uint dwStyle;
        public uint cyMax;
        public IntPtr hbrBack;
        public uint dwContextHelpID;
        public UIntPtr dwMenuData;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATA lpdata);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, IntPtr lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetMenuInfo(IntPtr hMenu, ref MENUINFO lpcmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int nHeight, int nWidth, int nEscapement, int nOrientation, int fnWeight,
        uint fdwItalic, uint fdwUnderline, uint fdwStrikeOut, uint fdwCharSet, uint fdwOutputPrecision,
        uint fdwClipPrecision, uint fdwQuality, uint fdwPitchAndFamily, string lpszFace);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int iBkMode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr hdc, uint crColor);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
    private static extern int SetPreferredAppMode(int appMode);

    [DllImport("uxtheme.dll", EntryPoint = "#133", SetLastError = true)]
    private static extern bool AllowDarkModeForWindow(IntPtr hWnd, bool allow);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    private readonly IntPtr _hWnd;
    private readonly SubclassProc _subclassDelegate;
    private NOTIFYICONDATA _nid;
    private bool _isCreated;
    private bool _isDisposed;

    public bool IsQuittingFromTray { get; set; }
    public Action? OnRestoreRequested { get; set; }
    public Action? OnCheckUpdatesRequested { get; set; }
    public Action? OnAboutRequested { get; set; }
    public Action? OnExitRequested { get; set; }
    public Action<string[]>? OnFilesDropped { get; set; }

    public StormTrayService(IntPtr hWnd)
    {
        _hWnd = hWnd;
        _subclassDelegate = WindowSubclassProc;

        try
        {
            SetPreferredAppMode(2);
            AllowDarkModeForWindow(_hWnd, true);
        }
        catch { }

        SetWindowSubclass(_hWnd, _subclassDelegate, (UIntPtr)2026, IntPtr.Zero);
        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
            if (!File.Exists(iconPath))
                iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                hIcon = LoadImage(IntPtr.Zero, iconPath, 1, 16, 16, 0x00000010);
            }
        }
        catch { }

        _nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hWnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = hIcon,
            szTip = $"STORM DS DECRYPTOR {UpdateManager.CurrentVersionString}"
        };

        _isCreated = Shell_NotifyIconW(NIM_ADD, ref _nid);
    }

    public void ShowBalloon(string title, string message)
    {
        if (!_isCreated) return;
        try
        {
            _nid.uFlags = NIF_INFO | NIF_MESSAGE | NIF_ICON | NIF_TIP;
            _nid.szInfoTitle = title;
            _nid.szInfo = message;
            _nid.dwInfoFlags = NIIF_INFO;
            _nid.uTimeoutOrVersion = 1500;
            Shell_NotifyIconW(NIM_MODIFY, ref _nid);
        }
        catch { }
    }

    private static string GetMenuText(uint cmdId)
    {
        return cmdId switch
        {
            CMD_RESTORE => LocalizationManager.Get("TrayShow"),
            CMD_UPDATES => LocalizationManager.Get("TrayCheckUpdates"),
            CMD_ABOUT => LocalizationManager.Get("TrayAbout"),
            CMD_EXIT => LocalizationManager.Get("TrayExit"),
            _ => string.Empty
        };
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, System.Text.StringBuilder? lpszFile, uint cch);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    private IntPtr WindowSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == 0x0233 /* WM_DROPFILES */)
        {
            IntPtr hDrop = wParam;
            try
            {
                uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                var list = new System.Collections.Generic.List<string>();
                for (uint i = 0; i < count; i++)
                {
                    var sb = new System.Text.StringBuilder(1024);
                    if (DragQueryFile(hDrop, i, sb, 1024) > 0)
                        list.Add(sb.ToString());
                }
                if (list.Count > 0)
                    OnFilesDropped?.Invoke(list.ToArray());
            }
            catch { }
            finally
            {
                DragFinish(hDrop);
            }
            return IntPtr.Zero;
        }

        if (uMsg == WM_TRAYICON)
        {
            int eventId = (int)(lParam.ToInt64() & 0xFFFF);
            if (eventId == WM_LBUTTONUP || eventId == WM_LBUTTONDBLCLK)
            {
                OnRestoreRequested?.Invoke();
                return IntPtr.Zero;
            }
            if (eventId == WM_RBUTTONUP || eventId == WM_CONTEXTMENU)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }
        }
        else if (uMsg == WM_MEASUREITEM)
        {
            var mis = Marshal.PtrToStructure<MEASUREITEMSTRUCT>(lParam);
            if (mis.CtlType == 1 /* ODT_MENU */)
            {
                if (mis.itemID == CMD_SEP)
                {
                    mis.itemWidth = 240;
                    mis.itemHeight = 9;
                }
                else
                {
                    mis.itemWidth = 250;
                    mis.itemHeight = 30;
                }
                Marshal.StructureToPtr(mis, lParam, false);
                return (IntPtr)1;
            }
        }
        else if (uMsg == WM_DRAWITEM)
        {
            var dis = Marshal.PtrToStructure<DRAWITEMSTRUCT>(lParam);
            if (dis.CtlType == 1 /* ODT_MENU */)
            {
                bool selected = (dis.itemState & ODS_SELECTED) != 0 && dis.itemID != CMD_SEP;
                // BGR colors: #141026 -> 0x00261014, #2E2652 -> 0x0052262E, #8B5CF6 -> 0x00F65C8B
                uint bgCol = selected ? 0x0052262Eu : 0x00261014u;
                IntPtr hBgBrush = CreateSolidBrush(bgCol);
                FillRect(dis.hDC, ref dis.rcItem, hBgBrush);
                DeleteObject(hBgBrush);

                if (dis.itemID == CMD_SEP)
                {
                    var sepRect = new RECT
                    {
                        Left = dis.rcItem.Left + 12,
                        Right = dis.rcItem.Right - 12,
                        Top = dis.rcItem.Top + 4,
                        Bottom = dis.rcItem.Top + 5
                    };
                    IntPtr hSepBrush = CreateSolidBrush(0x0058323Eu);
                    FillRect(dis.hDC, ref sepRect, hSepBrush);
                    DeleteObject(hSepBrush);
                }
                else
                {
                    string text = GetMenuText(dis.itemID);
                    int weight = dis.itemID == CMD_RESTORE ? 700 : 400;
                    IntPtr hFont = CreateFontW(-15, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
                    IntPtr hOldFont = SelectObject(dis.hDC, hFont);
                    SetBkMode(dis.hDC, TRANSPARENT);
                    SetTextColor(dis.hDC, selected ? 0x00FFFFFFu : 0x00F5F0F0u);

                    var textRect = new RECT
                    {
                        Left = dis.rcItem.Left + 16,
                        Top = dis.rcItem.Top,
                        Right = dis.rcItem.Right - 12,
                        Bottom = dis.rcItem.Bottom
                    };
                    DrawTextW(dis.hDC, text, text.Length, ref textRect, DT_LEFT | DT_VCENTER | DT_SINGLELINE);

                    SelectObject(dis.hDC, hOldFont);
                    DeleteObject(hFont);
                }
                return (IntPtr)1;
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void ShowContextMenu()
    {
        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        IntPtr hBrush = IntPtr.Zero;
        try
        {
            hBrush = CreateSolidBrush(0x00261014u); // #141026
            if (hBrush != IntPtr.Zero)
            {
                var mi = new MENUINFO
                {
                    cbSize = (uint)Marshal.SizeOf<MENUINFO>(),
                    fMask = MIM_BACKGROUND | MIM_APPLYTOSUBMENUS,
                    hbrBack = hBrush
                };
                SetMenuInfo(hMenu, ref mi);
            }

            AppendMenuW(hMenu, MF_OWNERDRAW, (UIntPtr)CMD_RESTORE, IntPtr.Zero);
            AppendMenuW(hMenu, MF_OWNERDRAW, (UIntPtr)CMD_UPDATES, IntPtr.Zero);
            AppendMenuW(hMenu, MF_OWNERDRAW, (UIntPtr)CMD_ABOUT, IntPtr.Zero);
            AppendMenuW(hMenu, MF_OWNERDRAW, (UIntPtr)CMD_SEP, IntPtr.Zero);
            AppendMenuW(hMenu, MF_OWNERDRAW, (UIntPtr)CMD_EXIT, IntPtr.Zero);

            GetCursorPos(out POINT pt);
            SetForegroundWindow(_hWnd);

            int cmd = TrackPopupMenuEx(hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, _hWnd, IntPtr.Zero);
            PostMessageW(_hWnd, 0, IntPtr.Zero, IntPtr.Zero);

            switch (cmd)
            {
                case CMD_RESTORE: OnRestoreRequested?.Invoke(); break;
                case CMD_UPDATES: OnCheckUpdatesRequested?.Invoke(); break;
                case CMD_ABOUT: OnAboutRequested?.Invoke(); break;
                case CMD_EXIT: OnExitRequested?.Invoke(); break;
            }
        }
        finally
        {
            if (hBrush != IntPtr.Zero) DeleteObject(hBrush);
            DestroyMenu(hMenu);
        }
    }

    public void RemoveIcon()
    {
        if (_isCreated)
        {
            Shell_NotifyIconW(NIM_DELETE, ref _nid);
            _isCreated = false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        RemoveIcon();
        RemoveWindowSubclass(_hWnd, _subclassDelegate, (UIntPtr)2026);
    }
}
