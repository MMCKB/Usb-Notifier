using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UsbFlashToast.Native;

namespace UsbFlashToast.Services;

/// <summary>
/// 后台消息宿主：一个 message-only 窗口 + 独立消息线程，负责
/// 1) 接收 WM_DEVICECHANGE 设备插拔通知（去抖后触发事件）
/// 2) 承载系统托盘图标与右键菜单
/// </summary>
internal sealed class BackgroundHost : IDisposable
{
    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly Dictionary<IntPtr, BackgroundHost> Live = new();

    private const uint WmTray = Win32.WM_USER + 1;
    private const uint WmShowMenu = Win32.WM_USER + 2;
    private const uint WmUpdateTray = Win32.WM_USER + 3;
    private const uint TrayUid = 0x5555;

    private readonly Win32.WndProcDelegate _wndProc;
    private readonly object _sync = new();
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _handle;
    private IntPtr _hNotify;
    private IntPtr _icon;
    private string _tip = "U 盘助手";
    private uint _taskbarCreated;
    private CancellationTokenSource? _debounce;
    private IReadOnlyList<TrayItem> _menuItems = Array.Empty<TrayItem>();
    private bool _disposed;
    private System.Threading.Timer? _pollTimer;
    private HashSet<string> _lastLetters = new(StringComparer.OrdinalIgnoreCase);

    public readonly record struct TrayItem(int Id, string Text, bool Checked, bool Separator);

    public IntPtr Handle => _handle;

    /// <summary>存储设备发生变化（已去抖，且盘符已就绪）。</summary>
    public event Action? StorageChanged;

    /// <summary>托盘图标被双击。</summary>
    public event Action? TrayActivated;

    /// <summary>托盘菜单命令。</summary>
    public event Action<int>? TrayCommand;

    public BackgroundHost()
    {
        _wndProc = WndProc;
    }

    public void Start()
    {
        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "UsbFlashToast.Background"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // 等待窗口创建完成
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _handle) == IntPtr.Zero && sw.ElapsedMilliseconds < 5000)
            Thread.Sleep(10);
    }

    private void MessageLoop()
    {
        _threadId = Win32.GetCurrentThreadId();

        var wndClass = new Win32.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = Win32.GetModuleHandleW(null),
            lpszClassName = "UsbFlashToast.BackgroundWindow",
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
        };
        Win32.RegisterClassExW(ref wndClass);

        // 隐藏的顶层窗口：可接收 WM_DEVICECHANGE 广播，也能作为托盘宿主
        _handle = Win32.CreateWindowExW(Win32.WS_EX_TOOLWINDOW, wndClass.lpszClassName, null, 0,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
        if (_handle == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("background window creation failed: " + Marshal.GetLastWin32Error());
            return;
        }

        lock (Live) Live[_handle] = this;

        // 注册卷设备接口通知
        var filter = new Win32.DEV_BROADCAST_DEVICEINTERFACE_W
        {
            dbch_size = Marshal.SizeOf<Win32.DEV_BROADCAST_DEVICEINTERFACE_W>(),
            dbch_devicetype = Win32.DBT_DEVTYP_DEVICEINTERFACE,
            dbch_classguid = Win32.GuidDevInterfaceVolume,
        };
        IntPtr filterPtr = Marshal.AllocHGlobal(filter.dbch_size);
        Marshal.StructureToPtr(filter, filterPtr, false);
        _hNotify = Win32.RegisterDeviceNotificationW(_handle, filterPtr, Win32.DEVICE_NOTIFY_WINDOW_HANDLE);
        Marshal.FreeHGlobal(filterPtr);
        if (_hNotify == IntPtr.Zero)
            System.Diagnostics.Debug.WriteLine("RegisterDeviceNotification failed: " + Marshal.GetLastWin32Error());

        _taskbarCreated = Win32.RegisterWindowMessageW("TaskbarCreated");
        // 初始图标由 App 设置后再添加，避免第一次 NIM_ADD 时空图标
        if (_icon != IntPtr.Zero) AddTrayIcon();

        // 轮询兜底：即使 WM_DEVICECHANGE 没收到的环境，也能在 2s 内发现插拔变化
        try
        {
            _lastLetters = new HashSet<string>(DriveInspector.EnumerateUsbLetters(), StringComparer.OrdinalIgnoreCase);
            _pollTimer = new System.Threading.Timer(_ => PollStorageChange(), null, 2000, 2000);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("poll timer init failed: " + ex.Message);
        }

        Win32.MSG msg;
        while (Win32.GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_DEVICECHANGE:
                int evt = wParam.ToInt32();
                if (evt is Win32.DBT_DEVICEARRIVAL or Win32.DBT_DEVICEREMOVECOMPLETE)
                    ScheduleStorageChanged();
                return new IntPtr(1);

            case WmTray:
                int mouseMsg = lParam.ToInt32();
                if (mouseMsg is Win32.WM_RBUTTONUP or Win32.WM_CONTEXTMENU)
                    ShowMenuNow();
                else if (mouseMsg == Win32.WM_LBUTTONDBLCLK)
                    TrayActivated?.Invoke();
                return IntPtr.Zero;

            case WmShowMenu:
                ShowMenuNow();
                return IntPtr.Zero;

            case WmUpdateTray:
                AddTrayIcon();
                return IntPtr.Zero;

            case Win32.WM_COMMAND:
                int cmd = wParam.ToInt32() & 0xFFFF;
                if (cmd != 0) TrayCommand?.Invoke(cmd);
                return IntPtr.Zero;

            case Win32.WM_DESTROY:
                RemoveTrayIcon();
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        if (_taskbarCreated != 0 && msg == _taskbarCreated)
        {
            _iconAdded = false; // Explorer 重启后托盘图标失效，需要重新 ADD
            AddTrayIcon();
            return IntPtr.Zero;
        }

        return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ---------------- 设备变更（去抖 + 等待盘符就绪） ----------------

    private void ScheduleStorageChanged()
    {
        lock (_sync)
        {
            _debounce?.Cancel();
            var cts = new CancellationTokenSource();
            _debounce = cts;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(450, cts.Token).ConfigureAwait(false);
                    await WaitUntilDrivesSettleAsync(cts.Token).ConfigureAwait(false);
                    if (cts.IsCancellationRequested) return;
                    DriveInspector.InvalidateCache();
                    StorageChanged?.Invoke();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("storage change handling failed: " + ex.Message);
                }
            });
        }
    }

    private static async Task WaitUntilDrivesSettleAsync(CancellationToken token)
    {
        // 刚插入时盘符可能尚未就绪，等待最多 4 秒
        for (int i = 0; i < 10; i++)
        {
            if (token.IsCancellationRequested) return;
            var letters = DriveInspector.EnumerateUsbLetters();
            if (letters.Count > 0)
            {
                bool allReady = true;
                foreach (var letter in letters)
                {
                    try
                    {
                        var di = new System.IO.DriveInfo(letter);
                        if (!di.IsReady) allReady = false;
                    }
                    catch { allReady = false; }
                }
                if (allReady) return;
            }
            await Task.Delay(400, token).ConfigureAwait(false);
        }
    }

    // ---------------- 轮询兜底（对比盘符集合） ----------------

    private void PollStorageChange()
    {
        if (_disposed || _handle == IntPtr.Zero) return;
        try
        {
            var letters = DriveInspector.EnumerateUsbLetters();
            bool changed = letters.Count != _lastLetters.Count;
            if (!changed)
            {
                foreach (var l in letters)
                    if (!_lastLetters.Contains(l)) { changed = true; break; }
            }
            _lastLetters = new HashSet<string>(letters, StringComparer.OrdinalIgnoreCase);

            if (changed)
            {
                Log.Write($"轮询检测到盘符变化：[{string.Join(",", letters)}]");
                StorageChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("poll storage check failed: " + ex.Message);
        }
    }

    // ---------------- 托盘 ----------------

    public void SetTrayIcon(IntPtr hIcon, string? tip = null)
    {
        lock (_sync)
        {
            if (_icon != IntPtr.Zero && _icon != hIcon && _ownedIcon)
                Win32.DestroyIcon(_icon);
            _icon = hIcon;
            if (!string.IsNullOrEmpty(tip)) _tip = tip!;
        }
        if (_handle != IntPtr.Zero)
            Win32.PostMessageW(_handle, WmUpdateTray, IntPtr.Zero, IntPtr.Zero);
    }

    private bool _ownedIcon;
    private bool _iconAdded;

    public void SetTrayIconFromIconHandle(IntPtr hIcon, bool owned, string? tip = null)
    {
        _ownedIcon = owned;
        SetTrayIcon(hIcon, tip);
    }

    public void SetTrayMenu(IReadOnlyList<TrayItem> items) => _menuItems = items;

    public void RequestMenu()
    {
        if (_handle != IntPtr.Zero)
            Win32.PostMessageW(_handle, WmShowMenu, IntPtr.Zero, IntPtr.Zero);
    }

    private void AddTrayIcon()
    {
        if (_handle == IntPtr.Zero) return;

        uint msg = _iconAdded ? (uint)Win32.NIM_MODIFY : (uint)Win32.NIM_ADD;
        var data = new Win32.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATAW>(),
            hWnd = _handle,
            uID = TrayUid,
            uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP,
            uCallbackMessage = WmTray,
            hIcon = _icon,
            szTip = _tip.Length > 127 ? _tip[..127] : _tip,
        };

        bool ok = Win32.Shell_NotifyIconW(msg, ref data);
        Log.Write($"Tray {(msg == Win32.NIM_ADD ? "ADD" : "MODIFY")} ok={ok} hIcon={_icon} tip={_tip}");

        if (ok && !_iconAdded)
        {
            _iconAdded = true;
            data.uTimeoutOrVersion = Win32.NOTIFYICON_VERSION_4;
            Win32.Shell_NotifyIconW(Win32.NIM_SETVERSION, ref data);
        }
    }

    private void ShowBalloon(string title, string text, uint icon)
    {
        if (_handle == IntPtr.Zero) return;
        var data = new Win32.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATAW>(),
            hWnd = _handle,
            uID = TrayUid,
            uFlags = Win32.NIF_INFO,
            szInfoTitle = title.Length > 63 ? title[..63] : title,
            szInfo = text.Length > 255 ? text[..255] : text,
            dwInfoFlags = icon,
            uTimeoutOrVersion = 10000,
        };
        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
    }

    private void RemoveTrayIcon()
    {
        if (_handle == IntPtr.Zero) return;
        var data = new Win32.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATAW>(),
            hWnd = _handle,
            uID = TrayUid,
        };
        Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref data);
    }

    private void ShowMenuNow()
    {
        if (_menuItems.Count == 0) return;
        IntPtr menu = Win32.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        int index = 0;
        foreach (var item in _menuItems)
        {
            uint flags = item.Separator ? Win32.MF_SEPARATOR : Win32.MF_STRING;
            int id = item.Id != 0 ? item.Id : ++index + 100;
            Win32.AppendMenuW(menu, flags, new UIntPtr((uint)id), item.Text);
        }

        Win32.GetCursorPos(out Win32.POINT pt);
        Win32.SetForegroundWindow(_handle);
        int cmd = Win32.TrackPopupMenuEx(menu,
            Win32.TPM_LEFTALIGN | Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD | Win32.TPM_NONOTIFY,
            pt.X, pt.Y, _handle, IntPtr.Zero);
        Win32.PostMessageW(_handle, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        Win32.DestroyMenu(menu);

        if (cmd > 0) TrayCommand?.Invoke(cmd);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pollTimer is not null) { _pollTimer.Dispose(); _pollTimer = null; }
        if (_hNotify != IntPtr.Zero) Win32.UnregisterDeviceNotification(_hNotify);
        if (_handle != IntPtr.Zero)
        {
            lock (Live) Live.Remove(_handle);
            Win32.PostMessageW(_handle, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
