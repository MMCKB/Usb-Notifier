using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UsbFlashToast.Models;
using UsbFlashToast.Native;
using UsbFlashToast.Services;
using UsbFlashToast.Views;

namespace UsbFlashToast;

public partial class App : Application
{
    private const int CmdOverview = 1001;
    private const int CmdSettings = 1002;
    private const int CmdExit = 1003;
    private const int CmdDriveExplorerBase = 4000;
    private const int CmdDriveEjectBase = 5000;

    private BackgroundHost? _host;
    private readonly HashSet<string> _knownDrives = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>物理设备 → 该设备上的所有盘符（多分区时会有多个）。</summary>
    private readonly Dictionary<string, List<string>> _knownDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _ejectedTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ToastWindow> _toasts = new();
    /// <summary>托盘菜单里“打开”项对应的盘符（按设备、分区顺序）。</summary>
    private readonly List<string> _trayPartitionOrder = new();
    /// <summary>托盘菜单里“弹出”项对应的设备（每个设备一组盘符）。</summary>
    private readonly List<List<string>> _trayDeviceLetters = new();
    private OverviewWindow? _overview;

    internal static DispatcherQueue UiQueue { get; private set; } = null!;
    internal static App Instance => (App)Current;

    private const int EjectCooldownSeconds = 4;

    public App()
    {
        InitializeComponent();

        Log.Clear();
        Log.Write($"启动，命令行：{string.Join(' ', Environment.GetCommandLineArgs())}");

        UnhandledException += (_, e) =>
        {
            Log.Write("UI 未处理异常", e.Exception);
            e.Handled = true;      // 尽量不让应用直接消失
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write("AppDomain 未处理异常", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write("未观察的任务异常", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            Log.Write("OnLaunched 开始");
            UiQueue = DispatcherQueue.GetForCurrentThread();

            // 初始快照：启动时已插入的设备不弹通知（按物理设备记录，多分区算一个）
            foreach (var dev in DriveInspector.EnumerateUsbDevices())
            {
                var letters = dev.Partitions.Select(p => p.Letter).ToList();
                _knownDevices[dev.DeviceKey] = letters;
                foreach (var l in letters) _knownDrives.Add(l);
            }

            _host = new BackgroundHost();
            _host.StorageChanged += () => UiQueue.TryEnqueue(RefreshAsync);
            _host.TrayActivated += () => UiQueue.TryEnqueue(() => ShowOverview(null));
            _host.TrayCommand += cmd => UiQueue.TryEnqueue(() => OnTrayCommand(cmd));
            _host.Start();

            UpdateTray();
            SettingsService.SyncStartupFromRegistry();

            bool silent = Environment.GetCommandLineArgs().Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
            if (!silent)
                ShowOverview(null);

            if (Environment.GetCommandLineArgs().Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase)))
            {
                // 视觉验证用：无 U 盘时手动弹出一个示例通知
                var demo = new UsbDriveInfo
                {
                    Letter = "E:",
                    RootPath = "E:\\",
                    VolumeLabel = "我的U盘",
                    FileSystem = "exFAT",
                    IsReady = true,
                    IsRemovable = true,
                    TotalBytes = 64L * 1024 * 1024 * 1024,
                    FreeBytes = 20L * 1024 * 1024 * 1024,
                    UsedBytes = 44L * 1024 * 1024 * 1024,
                    UsedRatio = 0.6875,
                    PhysicalSize = 64L * 1024 * 1024 * 1024,
                };
                ShowToast(demo);

                // 视觉验证用：1.2 秒后触发「U 盘已拔出」线条动画
                if (Environment.GetCommandLineArgs().Any(a => a.Equals("--demo-removed", StringComparison.OrdinalIgnoreCase)))
                {
                    _ = Task.Delay(1200).ContinueWith(_ => UiQueue.TryEnqueue(() =>
                    {
                        lock (_toasts)
                        {
                            var t = _toasts.FirstOrDefault(x => x.Letter == "E:");
                            t?.ShowRemoved();
                        }
                    }));
                }
            }

            Log.Write("OnLaunched 完成");
        }
        catch (Exception ex)
        {
            Log.Write("OnLaunched 异常", ex);
            throw;
        }
    }

    // ---------------- 设备变化 ----------------

    private void RefreshAsync()
    {
        try
        {
            // 清理已过冷却期的记录
            var now = DateTime.Now;
            var staleEjects = _ejectedTimestamps
                .Where(kv => (now - kv.Value).TotalSeconds > EjectCooldownSeconds)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var l in staleEjects) _ejectedTimestamps.Remove(l);

            // 盘符快照：即使 WMI 在拔出瞬间抛错，DriveInfo 仍能给出真实状态
            var currentLetters = new HashSet<string>(DriveInspector.EnumerateUsbLetters(), StringComparer.OrdinalIgnoreCase);
            var devices = DriveInspector.EnumerateUsbDevices();
            var currentKeys = new HashSet<string>(devices.Select(d => d.DeviceKey), StringComparer.OrdinalIgnoreCase);

            // ---- 移除已拔出的设备：整台设备一起清理，多分区不会残留 ----
            var goneKeys = _knownDevices.Keys.Where(k => !currentKeys.Contains(k)).ToList();
            foreach (string key in goneKeys)
            {
                var letters = _knownDevices[key];
                _knownDevices.Remove(key);
                foreach (string l in letters)
                {
                    _knownDrives.Remove(l);
                    // 拔出未安全弹出时盘符会“回光返照”几秒（仍枚举得到但卷不可读），
                    // 所以除盘符存在外还要求 IsReady，才能判定设备真的还在。
                    bool stillThere = currentLetters.Contains(l) && IsDriveReady(l);
                    RemoveToastsFor(l, showRemoved: !stillThere);
                    _overview?.RemoveDrive(l);
                    _ejectedTimestamps.Remove(l);
                }
            }

            // ---- 兜底：已记录盘符实际已不可用（拔出/卷失效），按盘符清理防残留 ----
            var orphanLetters = _knownDrives
                .Where(l => !currentLetters.Contains(l) || !IsDriveReady(l))
                .ToList();
            foreach (var l in orphanLetters)
            {
                _knownDrives.Remove(l);
                RemoveToastsFor(l, showRemoved: true);
                _overview?.RemoveDrive(l);
            }

            bool anyNew = false;
            foreach (var dev in devices)
            {
                var letters = dev.Partitions.Select(p => p.Letter).ToList();
                bool isNew = !_knownDevices.ContainsKey(dev.DeviceKey);
                _knownDevices[dev.DeviceKey] = letters;
                foreach (string l in letters) _knownDrives.Add(l);

                if (!isNew)
                {
                    _overview?.AddOrUpdateDrive(dev);
                    continue;
                }

                // 安全弹出后 Windows 可能仍短暂枚举到盘符，短暂忽略避免“已弹出却仍显示”
                if (letters.Any(l => _ejectedTimestamps.ContainsKey(l)))
                {
                    Log.Write($"忽略短暂重新枚举的设备 {dev.DeviceKey}（刚弹出）");
                    continue;
                }

                if (!dev.IsReady) continue;

                anyNew = true;
                _overview?.AddOrUpdateDrive(dev);
                ShowToast(dev);   // 一个物理设备只弹一条通知，即使它有多个分区
            }

            if (goneKeys.Count > 0 || anyNew || orphanLetters.Count > 0) UpdateTray();
        }
        catch (Exception ex)
        {
            // 不让异常中断刷新：下一轮（2s 轮询兜底）会再次尝试
            Log.Write("RefreshAsync 异常", ex);
        }
    }

    /// <summary>卷当前是否可读。拔出后的“回光返照”盘符 IsReady=false。</summary>
    private static bool IsDriveReady(string letter)
    {
        try { return new DriveInfo(letter).IsReady; }
        catch { return false; }
    }

    private void ShowToast(UsbDriveInfo info)
    {
        var toast = new ToastWindow(info);
        toast.ToastClosed += OnToastClosed;
        lock (_toasts) _toasts.Insert(0, toast);   // slot 0 = 最靠近右下角
        RepositionToasts();
        toast.Present(0);
    }

    private void OnToastClosed(object? sender, string letter)
    {
        lock (_toasts)
        {
            if (sender is ToastWindow t) _toasts.Remove(t);
        }
        RepositionToasts();
    }

    private void RemoveToastsFor(string letter, bool showRemoved = false)
    {
        List<ToastWindow> victims;
        lock (_toasts)
        {
            victims = _toasts.Where(t => t.Letter.Equals(letter, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var v in victims) _toasts.Remove(v);
        }
        Log.Write($"RemoveToastsFor {letter} showRemoved={showRemoved} 匹配弹窗 {victims.Count} 个");

        if (showRemoved && victims.Count == 0)
        {
            // 弹窗已消失：新弹一条拔出动画通知，保证拔出总有反馈
            var toast = ToastWindow.CreateRemoved(letter);
            toast.ToastClosed += OnToastClosed;
            lock (_toasts) _toasts.Insert(0, toast);
            RepositionToasts();
            toast.Present(0);
            toast.ShowRemoved(letter);
            return;
        }

        foreach (var v in victims)
        {
            v.ToastClosed -= OnToastClosed;
            if (showRemoved)
                v.ShowRemoved(letter);   // 原地切到拔出动画
            else
                v.Dismiss(immediate: false);
        }
        RepositionToasts();
    }

    private void RepositionToasts()
    {
        List<ToastWindow> snapshot;
        lock (_toasts) snapshot = _toasts.ToList();
        for (int i = 0; i < snapshot.Count; i++)
            snapshot[i].MoveToSlot(i);
    }

    // ---------------- 主窗口 ----------------

    internal void MarkEjected(string letter)
    {
        lock (_ejectedTimestamps) _ejectedTimestamps[letter] = DateTime.Now;
        if (_knownDrives.Contains(letter))
        {
            _knownDrives.Remove(letter);
            _overview?.RemoveDrive(letter);
            RemoveToastsFor(letter);
            UpdateTray();
        }
    }

    internal void ShowOverview(string? selectLetter)
    {
        if (_overview is null)
        {
            _overview = new OverviewWindow();
            _overview.Closed += (_, _) => _overview = null;
        }

        if (!string.IsNullOrEmpty(selectLetter))
            _overview.SelectDrive(selectLetter!);

        _overview.Activate();
        _overview.BringToFront();
    }

    internal static void OpenInExplorer(string path)
    {
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine("explorer launch failed: " + ex.Message);
        }
    }

    internal static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine("reveal failed: " + ex.Message);
        }
    }

    // ---------------- 托盘 ----------------

    private void UpdateTray()
    {
        if (_host is null) return;

        var letters = _knownDrives.Count > 0
            ? _knownDrives.ToList()
            : DriveInspector.EnumerateUsbLetters();

        lock (_trayPartitionOrder)
        {
            _trayPartitionOrder.Clear();
            _trayPartitionOrder.AddRange(letters);
        }

        var items = new List<BackgroundHost.TrayItem>();

        for (int i = 0; i < letters.Count && i < 8; i++)
        {
            var info = DriveInspector.Inspect(letters[i], includeDeviceInfo: false);
            string label = info is { IsReady: true } && !string.IsNullOrWhiteSpace(info.VolumeLabel)
                ? $"{letters[i]}  {info.VolumeLabel}"
                : letters[i];
            items.Add(new BackgroundHost.TrayItem(CmdDriveExplorerBase + i, $"打开 {label}", false, false));
            items.Add(new BackgroundHost.TrayItem(CmdDriveEjectBase + i, $"弹出 {letters[i]}", false, false));
        }

        if (letters.Count > 0) items.Add(new BackgroundHost.TrayItem(0, "", false, true));
        items.Add(new BackgroundHost.TrayItem(CmdOverview, "打开 U 盘概览", false, false));
        items.Add(new BackgroundHost.TrayItem(CmdSettings, "设置", false, false));
        items.Add(new BackgroundHost.TrayItem(0, "", false, true));
        items.Add(new BackgroundHost.TrayItem(CmdExit, "退出 U 盘助手", false, false));

        _host.SetTrayMenu(items);

        string tip = letters.Count == 0
            ? "U 盘助手 · 等待设备插入"
            : $"U 盘助手 · 已连接 {letters.Count} 个设备：{string.Join(" ", letters)}";

        IntPtr baseIcon = GetTrayIcon(letters);
        if (baseIcon != IntPtr.Zero)
        {
            IntPtr finalIcon = letters.Count > 0 ? DrawBadge(baseIcon, letters.Count) : baseIcon;
            if (finalIcon != baseIcon) Win32.DestroyIcon(baseIcon);
            _host.SetTrayIconFromIconHandle(finalIcon, owned: true, tip);
        }
    }

    /// <summary>在基础图标右下角绘制红色圆点 + 白色数量，生成带角标的托盘图标。</summary>
    private static IntPtr DrawBadge(IntPtr baseIcon, int count)
    {
        if (count <= 0) return baseIcon;
        if (!Win32.GetIconInfo(baseIcon, out Win32.ICONINFO ii)) return baseIcon;
        try
        {
            if (ii.hbmColor == IntPtr.Zero) return baseIcon;

            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            try
            {
                var bmi = new Win32.BITMAPINFO
                {
                    bmiHeader = new Win32.BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>() }
                };
                // 第一次调用：拿到尺寸/位深
                if (Win32.GetDIBits(screenDc, ii.hbmColor, 0, 0, IntPtr.Zero, ref bmi, Win32.DIB_RGB_COLORS) == 0)
                    return baseIcon;

                int w = bmi.bmiHeader.biWidth;
                int h = Math.Abs(bmi.bmiHeader.biHeight);
                int bpp = bmi.bmiHeader.biBitCount;
                if (w <= 0 || h <= 0) return baseIcon;

                int stride = ((w * Math.Max(bpp, 32) / 8 + 3) / 4) * 4;
                int size = stride * h;
                IntPtr bits = Marshal.AllocHGlobal(size);
                try
                {
                    bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>();
                    if (Win32.GetDIBits(screenDc, ii.hbmColor, 0, (uint)h, bits, ref bmi, Win32.DIB_RGB_COLORS) == 0)
                        return baseIcon;

                    // 在内存 DC 上绘制圆点 + 数字（仅写入 RGB，alpha 后续手动补齐）
                    int r = Math.Max(6, w / 3);
                    int cx = w - r / 2;
                    int cy = h - r / 2;

                    IntPtr memDc = Win32.CreateCompatibleDC(IntPtr.Zero);
                    IntPtr prev = Win32.SelectObject(memDc, ii.hbmColor);
                    try
                    {
                        var red = Win32.CreateSolidBrush(0x000000FF); // COLORREF: 纯红
                        var prevBrush = Win32.SelectObject(memDc, red);
                        Win32.Ellipse(memDc, cx - r, cy - r, cx + r, cy + r);
                        Win32.SelectObject(memDc, prevBrush);
                        Win32.DeleteObject(red);

                        string txt = count > 99 ? "99+" : count.ToString();
                        Win32.SetBkMode(memDc, Win32.TRANSPARENT_BK);
                        Win32.SetTextColor(memDc, 0x00FFFFFF); // 白
                        IntPtr font = Win32.CreateFontW(-(r * 3 / 4), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 0, 0, "Segoe UI");
                        var prevFont = Win32.SelectObject(memDc, font);
                        int tx = cx - (txt.Length * r * 3 / 8);
                        int ty = cy - r / 2;
                        Win32.TextOutW(memDc, tx, ty, txt, txt.Length);
                        Win32.SelectObject(memDc, prevFont);
                        Win32.DeleteObject(font);
                    }
                    finally
                    {
                        Win32.SelectObject(memDc, prev);
                        Win32.DeleteDC(memDc);
                    }

                    // 把绘制结果读回，对圆点区域内像素补齐 alpha=255（图标透明区绘制后 alpha 仍为 0）
                    if (bpp >= 32)
                    {
                        var header2 = new Win32.BITMAPINFO
                        {
                            bmiHeader = new Win32.BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>() }
                        };
                        if (Win32.GetDIBits(screenDc, ii.hbmColor, 0, (uint)h, bits, ref header2, Win32.DIB_RGB_COLORS) != 0)
                        {
                            int rr = r * r;
                            int bytesPerPixel = bpp / 8;
                            for (int y = 0; y < h; y++)
                            {
                                int bufRow = h - 1 - y; // DIB 自下而上
                                for (int x = 0; x < w; x++)
                                {
                                    int dx = x - cx;
                                    int dy = y - cy;
                                    if (dx * dx + dy * dy > rr) continue;
                                    int off = bufRow * stride + x * bytesPerPixel;
                                    Marshal.WriteByte(bits, off + 3, 255); // alpha
                                }
                            }
                            Win32.SetDIBits(screenDc, ii.hbmColor, 0, (uint)h, bits, ref header2, Win32.DIB_RGB_COLORS);
                        }
                    }
                    else
                    {
                        // 无 alpha 通道：直接回写绘制结果
                        var header2 = new Win32.BITMAPINFO
                        {
                            bmiHeader = new Win32.BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>() }
                        };
                        Win32.SetDIBits(screenDc, ii.hbmColor, 0, (uint)h, bits, ref header2, Win32.DIB_RGB_COLORS);
                    }

                    var newIcon = new Win32.ICONINFO
                    {
                        fIcon = ii.fIcon,
                        xHotspot = ii.xHotspot,
                        yHotspot = ii.yHotspot,
                        hbmMask = ii.hbmMask,
                        hbmColor = ii.hbmColor,
                    };
                    IntPtr result = Win32.CreateIconIndirect(ref newIcon);
                    return result != IntPtr.Zero ? result : baseIcon;
                }
                finally
                {
                    Marshal.FreeHGlobal(bits);
                }
            }
            finally
            {
                Win32.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
        catch (Exception ex)
        {
            Log.Write("绘制托盘角标失败", ex);
            return baseIcon;
        }
        finally
        {
            Win32.DeleteObject(ii.hbmColor);
            Win32.DeleteObject(ii.hbmMask);
        }
    }

    private static IntPtr GetTrayIcon(List<string> letters)
    {
        foreach (var letter in letters)
        {
            IntPtr h = GetShellIcon(letter + "\\");
            if (h != IntPtr.Zero) return h;
        }
        return LoadAppIcon();
    }

    private static IntPtr GetShellIcon(string path)
    {
        try
        {
            var shfi = new Win32.SHFILEINFOW();
            uint size = (uint)Marshal.SizeOf<Win32.SHFILEINFOW>();
            IntPtr result = Win32.SHGetFileInfoW(path, 0, ref shfi, size, Win32.SHGFI_ICON | Win32.SHGFI_TYPENAME);
            return result != IntPtr.Zero && shfi.hIcon != IntPtr.Zero ? shfi.hIcon : IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }

    private static IntPtr LoadAppIcon()
    {
        try
        {
            string exe = StartupService.ExecutablePath;
            if (!string.IsNullOrEmpty(exe))
            {
                // 先尝试从 exe 提取小图标
                if (Win32.ExtractIconExW(exe, 0, out _, out IntPtr small, 1) == 1 && small != IntPtr.Zero)
                    return small;
            }

            // 回退到 Assets/usb.ico
            string? dir = Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(dir))
            {
                string ico = Path.Combine(dir, "Assets", "usb.ico");
                if (File.Exists(ico))
                {
                    int cx = Win32.GetSystemMetrics(Win32.SM_CXSMICON);
                    int cy = Win32.GetSystemMetrics(Win32.SM_CYSMICON);
                    IntPtr h = Win32.LoadImageW(IntPtr.Zero, ico, Win32.IMAGE_ICON, cx, cy, Win32.LR_LOADFROMFILE);
                    if (h != IntPtr.Zero) return h;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("加载托盘图标失败", ex);
        }
        return IntPtr.Zero;
    }

    private void OnTrayCommand(int cmd)
    {
        if (cmd == CmdOverview) { ShowOverview(null); return; }
        if (cmd == CmdExit) { Shutdown(); return; }
        if (cmd == CmdSettings)
        {
            ShowOverview(null);
            _overview?.ShowSettings();
            return;
        }
        if (cmd >= CmdDriveExplorerBase && cmd < CmdDriveEjectBase)
        {
            string[] letters;
            lock (_trayPartitionOrder) letters = _trayPartitionOrder.ToArray();
            int index = cmd - CmdDriveExplorerBase;
            if (index >= 0 && index < letters.Length) OpenInExplorer(letters[index] + "\\");
            return;
        }
        if (cmd >= CmdDriveEjectBase)
        {
            string[] letters;
            lock (_trayPartitionOrder) letters = _trayPartitionOrder.ToArray();
            int index = cmd - CmdDriveEjectBase;
            if (index >= 0 && index < letters.Length) EjectDriveAsync(letters[index]);
        }
    }

    /// <summary>从托盘菜单直接安全弹出设备，并同步清理弹窗/概览/角标。</summary>
    internal async void EjectDriveAsync(string letter)
    {
        try
        {
            var (ok, message) = await DriveInspector.EjectAsync(letter).ConfigureAwait(true);
            if (ok)
            {
                MarkEjected(letter);
                DriveInspector.InvalidateCache();
                ShowNotice("已安全弹出", message, InfoBarSeverity.Success);
            }
            else
            {
                ShowNotice("无法弹出设备", message, InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Write("托盘弹出失败", ex);
        }
    }

    private void ShowNotice(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        var toast = ToastWindow.CreateNotice(title, message, severity);
        toast.ToastClosed += OnToastClosed;
        lock (_toasts) _toasts.Insert(0, toast);
        RepositionToasts();
        toast.Present(0);
    }

    private void Shutdown()
    {
        _host?.Dispose();
        _overview?.Close();
        Environment.Exit(0);
    }

    /// <summary>用户确认退出应用：先解除"再询问"逻辑，再走 Shutdown。</summary>
    internal void ConfirmExit()
    {
        _overview?.MarkClosing();
        Shutdown();
    }

    /// <summary>用户确认隐藏到托盘：仅隐藏窗口，应用继续在后台运行。</summary>
    internal void ConfirmHideToTray()
    {
        _overview?.HideToTray();
    }
}
