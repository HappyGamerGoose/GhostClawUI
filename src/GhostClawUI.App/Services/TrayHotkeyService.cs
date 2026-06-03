using System.Runtime.InteropServices;
using GhostClawUI.App.Views;
using Microsoft.UI.Xaml;

namespace GhostClawUI.App.Services;

internal sealed class TrayHotkeyService : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_COMMAND = 0x0111;
    private const int WM_HOTKEY = 0x0312;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int TrayMessage = WM_APP + 42;
    private const int HotkeyId = 9001;
    private const int MenuOpen = 100;
    private const int MenuSettings = 101;
    private const int MenuQuit = 102;
    private readonly MainWindow _window;
    private readonly Action _open;
    private readonly Action _settings;
    private readonly Action _quit;
    private readonly Action _quickPrompt;
    private readonly WndProc _wndProc;
    private readonly nint _oldWndProc;
    private nint _iconHandle;
    private bool _disposed;

    public TrayHotkeyService(MainWindow window, Action open, Action settings, Action quit, Action quickPrompt)
    {
        _window = window;
        _open = open;
        _settings = settings;
        _quit = quit;
        _quickPrompt = quickPrompt;
        _wndProc = WndProcHandler;
        _oldWndProc = SetWindowLongPtr(window.Hwnd, -4, Marshal.GetFunctionPointerForDelegate(_wndProc));
        TryAddIcon();
        RegisterHotKey(window.Hwnd, HotkeyId, 0x0001, 0x20);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterHotKey(_window.Hwnd, HotkeyId);
        DeleteIcon();
        if (_iconHandle != nint.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = nint.Zero;
        }

        if (_oldWndProc != nint.Zero)
        {
            SetWindowLongPtr(_window.Hwnd, -4, _oldWndProc);
        }
    }

    private nint WndProcHandler(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == TrayMessage)
        {
            var eventId = lParam.ToInt32();
            if (eventId == WM_LBUTTONDBLCLK)
            {
                _open();
                _window.Activate();
            }
            else if (eventId == WM_RBUTTONUP)
            {
                ShowMenu(hwnd);
            }
            return nint.Zero;
        }

        if (msg == WM_HOTKEY && wParam.ToUInt32() == HotkeyId)
        {
            _quickPrompt();
            return nint.Zero;
        }

        if (msg == WM_COMMAND)
        {
            switch (wParam.ToUInt32() & 0xffff)
            {
                case MenuOpen:
                    _open();
                    _window.Activate();
                    return nint.Zero;
                case MenuSettings:
                    _settings();
                    _window.Activate();
                    return nint.Zero;
                case MenuQuit:
                    _quit();
                    return nint.Zero;
            }
        }

        return CallWindowProc(_oldWndProc, hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(nint hwnd)
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, MenuOpen, "Open");
        AppendMenu(menu, 0, MenuSettings, "Settings");
        AppendMenu(menu, 0, MenuQuit, "Quit");
        GetCursorPos(out var point);
        SetForegroundWindow(hwnd);
        TrackPopupMenuEx(menu, 0x0100, point.X, point.Y, hwnd, nint.Zero);
        DestroyMenu(menu);
    }

    private void TryAddIcon()
    {
        var data = NewIconData();
        data.uFlags = 0x1 | 0x2 | 0x4;
        data.uCallbackMessage = TrayMessage;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        _iconHandle = File.Exists(iconPath)
            ? LoadImage(nint.Zero, iconPath, 1, 0, 0, 0x00000010 | 0x00000040)
            : nint.Zero;
        data.hIcon = _iconHandle != nint.Zero ? _iconHandle : LoadIcon(nint.Zero, new nint(32512));
        data.szTip = "GhostClawUI";
        Shell_NotifyIcon(0x0, ref data);
    }

    private void DeleteIcon()
    {
        var data = NewIconData();
        Shell_NotifyIcon(0x2, ref data);
    }

    private NOTIFYICONDATA NewIconData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Hwnd,
        uID = 1
    };

    private delegate nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIcon(nint hInstance, nint lpIconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint hMenu, uint uFlags, int uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hwnd, nint lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);
}



