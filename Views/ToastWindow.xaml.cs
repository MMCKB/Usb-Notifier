using System;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using UsbFlashToast.Models;
using UsbFlashToast.Native;
using UsbFlashToast.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace UsbFlashToast.Views;

/// <summary>屏幕右下角滑出的 Fluent 通知卡片。</summary>
public sealed partial class ToastWindow : Window
{
    private const double ToastWidth = 384;
    private const double DriveHeight = 200;
    private const double NoticeHeight = 128;
    private const double RemovedHeight = 164;
    private const int AutoCloseMs = 9000;
    private const int SlideInMs = 520;
    private const int SlideOutMs = 420;
    private const int RemovedNoticeMs = 4600;

    private readonly AppWindow _apw;
    private readonly OverlappedPresenter _presenter;
    private readonly IntPtr _hwnd;
    private readonly DispatcherTimer _closeTimer;
    private readonly DispatcherTimer _animTimer;

    private UsbDriveInfo? _info;
    private double _height = DriveHeight;
    private int _slot;
    private bool _shown;
    private bool _closing;

    private int _fromX, _fromY, _toX, _toY, _animMs;
    private DateTime _animStart;
    private Action? _animDone;

    public string Letter => _info?.Letter ?? string.Empty;

    public event EventHandler<string>? ToastClosed;

    public ToastWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        WindowId id = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _apw = AppWindow.GetFromWindowId(id);
        _presenter = (OverlappedPresenter)_apw.Presenter;

        _presenter.SetBorderAndTitleBar(false, false);
        _presenter.IsResizable = false;
        _presenter.IsMinimizable = false;
        _presenter.IsMaximizable = false;
        _presenter.IsAlwaysOnTop = true;
        _apw.IsShownInSwitchers = false;

        // 工具窗口：不占用任务栏；并要求 DWM 圆角
        // 注：SystemBackdrop 在圆角窗口边缘会留出未绘制区，该区域由 DWM 绘制、位于 XAML 之下，
        // 因此让卡片铺满整个窗口（Padding=0）并以统一半透明填充覆盖，即可消除四周的白圈。
        IntPtr exStyle = Win32.GetWindowLongPtr64(_hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr64(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(exStyle.ToInt64() | Win32.WS_EX_TOOLWINDOW));
        int round = Win32.DWMWCP_ROUND;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);

        ApplyBackdrop();
        SettingsService.Changed += OnSettingsChanged;

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoCloseMs) };
        _closeTimer.Tick += (_, _) => Dismiss(false);

        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _animTimer.Tick += OnAnimTick;

        Root.PointerEntered += (_, _) =>
        {
            if (!_closing) _closeTimer.Stop();
        };
        Root.PointerExited += (_, _) =>
        {
            if (!_closing && _shown) _closeTimer.Start();
        };
        Closed += (_, _) =>
        {
            SettingsService.Changed -= OnSettingsChanged;
            ToastClosed?.Invoke(this, Letter);
        };
    }

    public ToastWindow(UsbDriveInfo info) : this()
    {
        _info = info;
        _height = DriveHeight;
        BindDrive(info);
    }

    public static ToastWindow CreateNotice(string title, string message,
        InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        var toast = new ToastWindow { _height = NoticeHeight };
        toast.ShowNotice(title, message, severity);
        return toast;
    }

    private void BindDrive(UsbDriveInfo info)
    {
        DrivePanel.Visibility = Visibility.Visible;
        NoticePanel.Visibility = Visibility.Collapsed;

        TitleText.Text = info.DisplayName;
        SubtitleText.Text = $"{info.DeviceKind} · {info.FileSystem} · 共 {Format.Bytes(info.TotalBytes)}";

        UsageBar.Value = Math.Clamp(info.UsedRatio * 100, 0, 100);
        if (info.UsedRatio > 0.9)
            UsageBar.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 209, 52, 56));
        else if (info.UsedRatio > 0.75)
            UsageBar.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 202, 138, 4));

        UsageText.Text = $"已用 {Format.Bytes(info.UsedBytes)} / {Format.Bytes(info.TotalBytes)}";
        FreeText.Text = $"可用 {Format.Bytes(info.FreeBytes)}";
        EjectButton.IsEnabled = true;
    }

    public void ShowNotice(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        bool wasDriveVisible = DrivePanel.Visibility == Visibility.Visible;

        NoticeBar.Title = title;
        NoticeBar.Message = message;
        NoticeBar.Severity = severity;
        _height = NoticeHeight;

        if (_shown)
        {
            var (_, _, w, h, _, _) = ComputeTarget(_slot);
            _apw.Resize(new SizeInt32(w, h));
        }

        // 交叉淡入淡出切换面板
        if (wasDriveVisible)
        {
            CrossfadeContent(NoticePanel);
        }
        else
        {
            DrivePanel.Visibility = Visibility.Collapsed;
            NoticePanel.Visibility = Visibility.Visible;
            NoticePanel.Opacity = 1;
        }
    }

    // ---------------- 展示与定位 ----------------

    public void Present(int slot)
    {
        _slot = slot;
        var (x, y, w, h, right, _) = ComputeTarget(slot);
        Log.Write($"Toast Present slot={slot} size={w}x{h} target=({x},{y})");

        _apw.Resize(new SizeInt32(w, h));

        // 先放到右外侧较远处，滑入 + 淡入同时进行，观感顺滑
        int offX = right + (int)Math.Round(150 * GetScale());
        _apw.Move(new PointInt32(offX, y));

        Activate();
        _shown = true;
        Root.Opacity = 0;

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(300)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fadeIn, Root);
        Storyboard.SetTargetProperty(fadeIn, "Opacity");
        var fadeSb = new Storyboard();
        fadeSb.Children.Add(fadeIn);
        fadeSb.Begin();

        StartMove(offX, y, x, y, SlideInMs);
        _closeTimer.Start();
    }

    public void MoveToSlot(int slot)
    {
        _slot = slot;
        if (!_shown || _closing) return;

        var (x, y, _, _, _, _) = ComputeTarget(slot);
        PointInt32 cur = _apw.Position;
        if (cur.Y == y) return;
        StartMove(cur.X, cur.Y, x, y, 260);
    }

    private void CrossfadeContent(UIElement targetPanel)
    {
        var current = DrivePanel.Visibility == Visibility.Visible ? (UIElement)DrivePanel : NoticePanel;
        if (current == targetPanel) return;

        var sb = new Storyboard();
        var fadeOut = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(fadeOut, current);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");

        sb.Children.Add(fadeOut);
        sb.Completed += (_, _) =>
        {
            current.Visibility = Visibility.Collapsed;
            targetPanel.Opacity = 0;
            targetPanel.Visibility = Visibility.Visible;

            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeIn, targetPanel);
            Storyboard.SetTargetProperty(fadeIn, "Opacity");
            var sbi = new Storyboard();
            sbi.Children.Add(fadeIn);
            sbi.Begin();
        };
        sb.Begin();
    }

    public void Dismiss(bool immediate)
    {
        if (_closing) return;
        _closing = true;
        _closeTimer.Stop();

        if (immediate || !_shown)
        {
            Close();
            return;
        }

        // 退场：向右外侧滑回（约 150px 屏外），同时整体淡出
        var (_, _, _, _, right, _) = ComputeTarget(_slot);
        int offX = right + (int)Math.Round(150 * GetScale());
        PointInt32 cur = _apw.Position;

        var sb = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(SlideOutMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(fade, Root);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);
        sb.Begin();

        StartMove(cur.X, cur.Y, offX, cur.Y, SlideOutMs, Close);
    }

    /// <summary>
    /// 设备在弹窗仍可见时被拔出：切到「U 盘已拔出」线条动画（U 盘从 USB 口拔开），
    /// 停留数秒后自动关闭，不再静默消失。
    /// </summary>
    public void ShowRemoved(string? letterOverride = null)
    {
        Log.Write($"ShowRemoved 开始 closing={_closing} shown={_shown} info={_info?.Letter}");
        if (_closing) return;
        _closeTimer.Stop();

        // 加高窗口以容纳动画，并回正到右下角目标位置（内容淡化期间完成，不显突兀）
        _height = RemovedHeight;
        var (x, y, w, h, _, _) = ComputeTarget(_slot);
        _apw.Resize(new SizeInt32(w, h));
        _apw.Move(new PointInt32(x, y));

        string letter = letterOverride ?? _info?.Letter ?? string.Empty;
        RemovedText.Text = string.IsNullOrEmpty(letter) ? "U 盘已拔出" : $"U 盘 {letter} 已拔出";

        bool wasDrive = DrivePanel.Visibility == Visibility.Visible;
        if (wasDrive)
        {
            // 插入信息 → 拔出动画：交叉淡化平滑过渡（出 180ms / 入 240ms）
            CrossfadeContent(RemovedPanel);
        }
        else
        {
            DrivePanel.Visibility = Visibility.Collapsed;
            NoticePanel.Visibility = Visibility.Collapsed;
            RemovedPanel.Opacity = 1;
            RemovedPanel.Visibility = Visibility.Visible;
        }

        // 拔出动画等内容淡入完成后再开始（wasDrive 时 Crossfade 共 420ms）
        PlayRemovedAnimation(wasDrive ? 430 : 280);
        Log.Write("ShowRemoved 动画已启动");

        _closeTimer.Interval = TimeSpan.FromMilliseconds(RemovedNoticeMs);
        _closeTimer.Start();
    }

    /// <summary>弹窗已消失时拔出：新建一个只含拔出动画的通知窗。</summary>
    public static ToastWindow CreateRemoved(string letter)
    {
        var toast = new ToastWindow { _height = RemovedHeight };
        toast.DrivePanel.Visibility = Visibility.Collapsed;
        toast.RemovedText.Text = string.IsNullOrEmpty(letter) ? "U 盘已拔出" : $"U 盘 {letter} 已拔出";
        toast.RemovedPanel.Opacity = 1;
        toast.RemovedPanel.Visibility = Visibility.Visible;
        return toast;
    }

    /// <summary>拔出动画：U 盘沿导轨向右拔出 + 速度线闪现，纯线条无色块。</summary>
    private void PlayRemovedAnimation(int beginAtMs)
    {
        var sb = new Storyboard();

        // 1) U 盘向右滑出（先加速后减速），接头离开接口
        var slide = new DoubleAnimation
        {
            From = 0,
            To = 62,
            Duration = new Duration(TimeSpan.FromMilliseconds(620)),
            BeginTime = TimeSpan.FromMilliseconds(beginAtMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slide, UsbSlide);
        Storyboard.SetTargetProperty(slide, "X");
        sb.Children.Add(slide);

        // 2) 速度线闪现（自动反转淡入淡出），营造"用力一拔"的动感
        foreach (var line in new[] { SpeedLine1, SpeedLine2 })
        {
            var flash = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(190)),
                BeginTime = TimeSpan.FromMilliseconds(beginAtMs + 40),
                AutoReverse = true
            };
            Storyboard.SetTarget(flash, line);
            Storyboard.SetTargetProperty(flash, "Opacity");
            sb.Children.Add(flash);
        }

        // 3) 拔出完成后整体轻微停留，靠关闭计时器收尾
        sb.Begin();
    }

    private (int x, int y, int w, int h, int right, int bottom) ComputeTarget(int slot)
    {
        var area = DisplayArea.GetFromWindowId(_apw.Id, DisplayAreaFallback.Nearest)?.WorkArea;
        RectInt32 work;
        if (area.HasValue) work = area.Value;
        else work = new RectInt32(0, 0, 1920, 1040);

        double scale = GetScale();

        int w = (int)Math.Round(ToastWidth * scale);
        int h = (int)Math.Round(_height * scale);
        int margin = (int)Math.Round(12 * scale);
        int gap = (int)Math.Round(10 * scale);

        int right = work.X + work.Width;
        int bottom = work.Y + work.Height;

        int x = right - w - margin;
        int y = bottom - h - margin - slot * (h + gap);
        if (y < work.Y + margin) y = work.Y + margin;
        return (x, y, w, h, right, bottom);
    }

    private double GetScale()
    {
        try
        {
            uint dpi = Win32.GetDpiForWindow(_hwnd);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch { return 1.0; }
    }

    // ---------------- 背景材质 ----------------

    private void ApplyBackdrop()
    {
        var backdrop = SettingsService.Current.ToastBackdrop;
        bool dark = Root.ActualTheme == ElementTheme.Dark;

        BackdropHelper.Apply(this, Root, BackdropOverlay, dark);

        // 卡片铺满整窗并统一填充，遮住圆角边缘未被 SystemBackdrop 绘制到的那一圈（白圈）。
        // 填充是半透明的，材质仍可从下方透出。
        byte alpha = (byte)Math.Clamp(255 * FillAlpha(backdrop), 0, 255);
        Card.Background = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(alpha, 32, 32, 32)
            : Windows.UI.Color.FromArgb(alpha, 255, 255, 255));
    }

    /// <summary>各材质的填充不透明度：越接近 1 越实，越接近 0 越透。</summary>
    private static double FillAlpha(ToastBackdrop backdrop) => backdrop switch
    {
        ToastBackdrop.Solid => 1.0,
        ToastBackdrop.Mica => 0.94,
        ToastBackdrop.MicaAlt => 0.92,
        ToastBackdrop.Acrylic => 0.88,
        ToastBackdrop.AcrylicThin => 0.78,
        ToastBackdrop.Smoke => 0.74,
        ToastBackdrop.Frosted => 0.68,
        ToastBackdrop.Transparent => 0.45,
        _ => 0.88,
    };

    private void OnSettingsChanged()
    {
        DispatcherQueue.TryEnqueue(ApplyBackdrop);
    }

    // ---------------- 窗口位移动画 ----------------

    private void StartMove(int fromX, int fromY, int toX, int toY, int ms, Action? done = null)
    {
        _fromX = fromX; _fromY = fromY;
        _toX = toX; _toY = toY;
        _animMs = Math.Max(1, ms);
        _animStart = DateTime.Now;
        _animDone = done;
        _animTimer.Start();
    }

    private void OnAnimTick(object? sender, object e)
    {
        double t = (DateTime.Now - _animStart).TotalMilliseconds / _animMs;
        if (t >= 1) t = 1;
        double ease = 1 - Math.Pow(1 - t, 3);

        int x = (int)Math.Round(_fromX + (_toX - _fromX) * ease);
        int y = (int)Math.Round(_fromY + (_toY - _fromY) * ease);
        _apw.Move(new PointInt32(x, y));

        if (t >= 1)
        {
            _animTimer.Stop();
            var done = _animDone;
            _animDone = null;
            done?.Invoke();
        }
    }

    // ---------------- 交互 ----------------

    private void OnCloseClick(object sender, RoutedEventArgs e) => Dismiss(false);

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (_info is null) return;
        App.OpenInExplorer(_info.RootPath);
        Dismiss(false);
    }

    private void OnOverviewClick(object sender, RoutedEventArgs e)
    {
        if (_info is null) return;
        App.Instance.ShowOverview(_info.Letter);
        Dismiss(false);
    }

    private async void OnEjectClick(object sender, RoutedEventArgs e)
    {
        if (_info is null) return;
        string letter = _info.Letter;
        EjectButton.IsEnabled = false;
        _closeTimer.Stop();

        var (ok, message) = await DriveInspector.EjectAsync(letter).ConfigureAwait(true);
        if (_closing) return;

        ShowNotice(ok ? "已安全弹出" : "无法弹出设备", message,
            ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning);

        if (ok)
        {
            App.Instance.MarkEjected(letter);
            _ = Task.Delay(2600).ContinueWith(_ =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _closing = true;
                    Close();
                });
            });
        }
        else
        {
            _closeTimer.Start();
        }
    }
}
