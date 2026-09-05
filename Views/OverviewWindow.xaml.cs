using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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

/// <summary>U 盘概览主窗口：容量、设备信息、内容构成。</summary>
public sealed partial class OverviewWindow : Window
{
    private readonly ObservableCollection<UsbDriveInfo> _drives = new();
    private readonly Dictionary<string, ScanResult> _scanCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IntPtr _hwnd;
    private readonly AppWindow _apw;
    private readonly OverlappedPresenter _presenter;
    private CancellationTokenSource? _scanCts;
    private UsbDriveInfo? _selected;
    private bool _loaded;
    private bool _settingsInitialized;

    public OverviewWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _apw = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _presenter = (OverlappedPresenter)_apw.Presenter;

        Title = "弹盘通";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _apw.Changed += OnAppWindowChanged;
        UpdateTitleBarPadding();

        // 拦截关闭：子类化 WM_CLOSE（unpackaged 下比 AppWindow.Closing 可靠），
        // 由 HandleCloseRequestAsync 弹出"退出 / 隐藏到托盘 / 取消"询问。
        HookClose();

        // 显式设置窗口图标，确保任务栏/Alt-Tab 能正确显示应用图标
        try
        {
            string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "usb.ico");
            if (File.Exists(ico)) _apw.SetIcon(ico);
        }
        catch (Exception ex)
        {
            Log.Write("设置窗口图标失败", ex);
        }

        try
        {
            ApplyBackdrop();
            SettingsService.Changed += OnSettingsChanged;
        }
        catch (Exception ex)
        {
            Log.Write("主窗口初始化背景材质失败", ex);
        }

        double scale = GetScale();
        int w = (int)Math.Round(1080 * scale);
        int h = (int)Math.Round(720 * scale);
        var work = Win32.GetPrimaryWorkArea();
        _apw.Resize(new SizeInt32(Math.Min(w, work.Right - work.Left - 40), Math.Min(h, work.Bottom - work.Top - 40)));
        _apw.Move(new PointInt32(
            work.Left + (work.Right - work.Left - _apw.Size.Width) / 2,
            work.Top + (work.Bottom - work.Top - _apw.Size.Height) / 2));

        DriveList.ItemsSource = _drives;

        // Window 本身没有 Loaded 事件，用根元素代替
        RootGrid.Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await ReloadAsync();
            InitSettingsFlyout();
        };

        Closed += (_, _) =>
        {
            SettingsService.Changed -= OnSettingsChanged;
            _scanCts?.Cancel();
            _scanCts?.Dispose();
        };
    }

    // ---------------- 设备集合 ----------------

    public async Task ReloadAsync()
    {
        var letters = await Task.Run(() => DriveInspector.EnumerateUsbLetters()).ConfigureAwait(true);
        var infos = new List<UsbDriveInfo>();
        foreach (var letter in letters)
        {
            var info = await DriveInspector.InspectAsync(letter).ConfigureAwait(true);
            if (info is { IsReady: true }) infos.Add(info);
        }

        // 移除已拔出的
        for (int i = _drives.Count - 1; i >= 0; i--)
        {
            if (!infos.Any(x => x.Letter.Equals(_drives[i].Letter, StringComparison.OrdinalIgnoreCase)))
                _drives.RemoveAt(i);
        }

        foreach (var info in infos)
            AddOrUpdateDrive(info, autoSelect: false);

        EmptyHint.Visibility = _drives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_selected is null && _drives.Count > 0)
            SelectDrive(_drives[0].Letter);
        else if (_selected is not null)
            UpdateDetail(_selected);
        else
            ShowEmptyDetail();
    }

    public void AddOrUpdateDrive(UsbDriveInfo info) => AddOrUpdateDrive(info, autoSelect: true);

    private void AddOrUpdateDrive(UsbDriveInfo info, bool autoSelect)
    {
        int index = -1;
        for (int i = 0; i < _drives.Count; i++)
        {
            if (_drives[i].Letter.Equals(info.Letter, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        }

        if (index >= 0) _drives[index] = info;
        else _drives.Add(info);

        EmptyHint.Visibility = _drives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        bool isSelected = _selected is not null &&
                          _selected.Letter.Equals(info.Letter, StringComparison.OrdinalIgnoreCase);
        if (isSelected)
        {
            _selected = info;
            UpdateDetail(info);
        }
        else if (autoSelect && _selected is null)
        {
            SelectDrive(info.Letter);
        }
    }

    public void RemoveDrive(string letter)
    {
        for (int i = _drives.Count - 1; i >= 0; i--)
        {
            if (_drives[i].Letter.Equals(letter, StringComparison.OrdinalIgnoreCase))
                _drives.RemoveAt(i);
        }
        _scanCache.Remove(letter);

        EmptyHint.Visibility = _drives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_selected is not null && _selected.Letter.Equals(letter, StringComparison.OrdinalIgnoreCase))
        {
            _selected = null;
            if (_drives.Count > 0) SelectDrive(_drives[0].Letter);
            else ShowEmptyDetail();
        }
    }

    public void SelectDrive(string letter)
    {
        var target = _drives.FirstOrDefault(d => d.Letter.Equals(letter, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            AddOrUpdateDrive(DriveInspector.Inspect(letter) ?? new UsbDriveInfo { Letter = letter }, autoSelect: false);
            target = _drives.FirstOrDefault(d => d.Letter.Equals(letter, StringComparison.OrdinalIgnoreCase));
            if (target is null) return;
        }

        _selected = target;
        DriveList.SelectedItem = target;
        UpdateDetail(target);
    }

    internal void BringToFront()
    {
        _closeConfirmed = false;   // 重新显示后，下次关闭重新询问（除非已"不再提醒"）
        Activate();
        Win32.SetForegroundWindow(_hwnd);
    }

    /// <summary>由 App 在用户确认关闭后调用，放行 WM_CLOSE（关闭询问不再拦截）。</summary>
    internal void MarkClosing()
    {
        _closeConfirmed = true;
    }

    /// <summary>由 App 在用户选择"隐藏到托盘"时调用。</summary>
    internal void HideToTray()
    {
        _closeConfirmed = true;
        _apw.Hide();
    }

    // ---------------- 标题栏 / 设置 ----------------

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange || args.DidPositionChange)
            UpdateTitleBarPadding();
    }

    private void UpdateTitleBarPadding()
    {
        try
        {
            // 左右 Inset 已是 DIP（逻辑像素），直接用于 Grid 列宽
            double left = _apw.TitleBar.LeftInset;
            double right = _apw.TitleBar.RightInset;
            LeftPaddingColumn.Width = new GridLength(Math.Max(0, left));
            RightPaddingColumn.Width = new GridLength(Math.Max(0, right));
        }
        catch { }
    }

    internal void ShowSettings()
    {
        InitSettingsFlyout();
        SettingsFlyout.ShowAt(SettingsButton);
    }

    private void InitSettingsFlyout()
    {
        if (BackdropRadioButtons is null) return;
        if (_settingsInitialized) return;
        _settingsInitialized = true;

        var settings = SettingsService.Current;
        string tag = settings.ToastBackdrop.ToString();
        foreach (var item in BackdropRadioButtons.Items)
        {
            if (item is RadioButton rb && rb.Tag?.ToString() == tag)
            {
                rb.IsChecked = true;
                break;
            }
        }
        BackdropRadioButtons.SelectionChanged += OnBackdropSelectionChanged;

        StartupToggle.IsOn = settings.StartupEnabled;
        StartupToggle.Toggled += OnStartupToggled;

        CloseActionCombo.SelectedIndex = (int)settings.CloseAction;  // 0=Ask, 1=Exit, 2=Hide
    }

    private void OnBackdropSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BackdropRadioButtons.SelectedItem is not RadioButton rb) return;
        if (Enum.TryParse<ToastBackdrop>(rb.Tag?.ToString(), out var backdrop))
        {
            var s = SettingsService.Current;
            s.ToastBackdrop = backdrop;
            SettingsService.Save(s);
        }
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        bool enabled = StartupToggle.IsOn;
        StartupService.SetEnabled(enabled);
        var s = SettingsService.Current;
        s.StartupEnabled = enabled;
        SettingsService.Save(s);

        ShowHealth(enabled
            ? "已开启开机自动启动：登录系统后会在后台静默运行，插入 U 盘即弹出通知。"
            : "已关闭开机自动启动。", InfoBarSeverity.Informational);
    }

    private void OnCloseActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CloseActionCombo.SelectedIndex < 0) return;
        var s = SettingsService.Current;
        s.CloseAction = CloseActionCombo.SelectedIndex switch
        {
            1 => CloseAction.Exit,
            2 => CloseAction.Hide,
            _ => CloseAction.Ask,
        };
        SettingsService.Save(s);
    }

    // ---------------- 关闭询问（WM_CLOSE 子类化兜底，unpackaged 可靠） ----------------

    private Win32.WndProcDelegate? _wndProcHook;
    private IntPtr _oldWndProc;
    private bool _closeConfirmed;

    private void HookClose()
    {
        try
        {
            _wndProcHook = WndProcHook;
            _oldWndProc = Win32.SetWindowLongPtr64(_hwnd, Win32.GWL_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_wndProcHook));
        }
        catch (Exception ex)
        {
            Log.Write("子类化关闭窗口失败", ex);
        }
    }

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_CLOSE && !_closeConfirmed)
        {
            // 拦截关闭：取消系统默认关闭，转而走询问流程（在 UI 线程显示对话框）
            _ = DispatcherQueue.TryEnqueue(async () => await HandleCloseRequestAsync());
            return IntPtr.Zero;   // 不调用默认过程 → 阻止关闭
        }
        return Win32.CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    private async Task HandleCloseRequestAsync()
    {
        var settings = SettingsService.Current;
        switch (settings.CloseAction)
        {
            case CloseAction.Exit:
                App.Instance.ConfirmExit();
                return;
            case CloseAction.Hide:
                App.Instance.ConfirmHideToTray();
                return;
            default:
                var (shouldExit, shouldHide) = await ShowCloseDialogAsync();
                if (!shouldExit && !shouldHide) return;   // 取消 → 保持窗口打开
                if (shouldExit) App.Instance.ConfirmExit();
                else App.Instance.ConfirmHideToTray();
                break;
        }
    }

    private async Task<(bool Exit, bool Hide)> ShowCloseDialogAsync()
    {
        var cb = new CheckBox
        {
            Content = "不再询问（记住此次选择）",
            Margin = new Thickness(0, 12, 0, 0),
        };

        var dialog = new ContentDialog
        {
            Title = "关闭弹盘通",
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "你希望接下来如何处理？「隐藏到托盘」可在托盘图标右键菜单中恢复窗口。",
                    },
                    cb,
                },
            },
            PrimaryButtonText = "退出应用",
            SecondaryButtonText = "隐藏到托盘",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };

        var result = await dialog.ShowAsync();

        // 把所选项同步成"记住"的行为（仅当用户勾了复选框）
        if (cb.IsChecked == true)
        {
            var s = SettingsService.Current;
            if (result == ContentDialogResult.Primary) s.CloseAction = CloseAction.Exit;
            else if (result == ContentDialogResult.Secondary) s.CloseAction = CloseAction.Hide;
            else s.CloseAction = CloseAction.Ask;
            SettingsService.Save(s);
            CloseActionCombo.SelectedIndex = (int)s.CloseAction;
        }

        return (result == ContentDialogResult.Primary, result == ContentDialogResult.Secondary);
    }

    private void OnSettingsFlyoutOpened(object sender, object e)
    {
        if (SettingsPanel is null) return;
        var transform = new ScaleTransform { ScaleX = 0.95, ScaleY = 0.95, CenterX = 0.5, CenterY = 0 };
        SettingsPanel.RenderTransform = transform;
        SettingsPanel.Opacity = 0;

        var backEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        var sb = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            EasingFunction = backEase
        };
        var scaleX = new DoubleAnimation
        {
            From = 0.95,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(280)),
            EasingFunction = backEase
        };
        var scaleY = new DoubleAnimation
        {
            From = 0.95,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(280)),
            EasingFunction = backEase
        };
        Storyboard.SetTarget(fade, SettingsPanel);
        Storyboard.SetTargetProperty(fade, "Opacity");
        Storyboard.SetTarget(scaleX, transform);
        Storyboard.SetTargetProperty(scaleX, "ScaleX");
        Storyboard.SetTarget(scaleY, transform);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");
        sb.Children.Add(fade);
        sb.Children.Add(scaleX);
        sb.Children.Add(scaleY);
        sb.Begin();
    }

    // ---------------- 背景材质 ----------------

    private void ApplyBackdrop()
    {
        BackdropHelper.Apply(this, RootGrid, BackdropOverlay, RootGrid.ActualTheme == ElementTheme.Dark);
    }

    private void OnSettingsChanged()
    {
        DispatcherQueue.TryEnqueue(ApplyBackdrop);
    }

    // ---------------- 详情 ----------------

    private void ShowEmptyDetail()
    {
        DetailEmptyHint.Visibility = Visibility.Visible;
        HeaderCard.Visibility = Visibility.Collapsed;
        CapacityCard.Visibility = Visibility.Collapsed;
        PropertyCard.Visibility = Visibility.Collapsed;
        ContentCard.Visibility = Visibility.Collapsed;
        HealthBar.IsOpen = false;
    }

    private void UpdateDetail(UsbDriveInfo info)
    {
        DetailEmptyHint.Visibility = Visibility.Collapsed;
        bool firstShow = HeaderCard.Visibility == Visibility.Collapsed;
        HeaderCard.Visibility = Visibility.Visible;
        CapacityCard.Visibility = Visibility.Visible;
        PropertyCard.Visibility = Visibility.Visible;

        if (firstShow)
        {
            AnimateCardEntrance(HeaderCard, 0);
            AnimateCardEntrance(CapacityCard, 60);
            AnimateCardEntrance(PropertyCard, 120);
        }

        DriveTitle.Text = string.IsNullOrWhiteSpace(info.VolumeLabel)
            ? $"可移动磁盘 ({info.Letter})"
            : info.VolumeLabel;
        DriveSubtitle.Text = $"{info.Letter} · {info.DeviceKind} · {info.FileSystem} · 共 {Format.Bytes(info.TotalBytes)}";

        CapacityBar.Value = Math.Clamp(info.UsedRatioPercent, 0, 100);
        CapacityBar.Foreground = info.UsedRatio > 0.9
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 209, 52, 56))
            : (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        CapacitySummary.Text = $"{Format.Bytes(info.FreeBytes)} 可用 / 共 {Format.Bytes(info.TotalBytes)}";
        UsedLegend.Text = $"已用 {Format.Bytes(info.UsedBytes)}（{Format.Percent(info.UsedRatio)}）";
        FreeLegend.Text = $"可用 {Format.Bytes(info.FreeBytes)}";

        PropLabel.Text = string.IsNullOrWhiteSpace(info.VolumeLabel) ? "（无）" : info.VolumeLabel;
        PropFileSystem.Text = info.FileSystem;
        PropTotal.Text = $"{Format.Bytes(info.TotalBytes)}（{info.TotalBytes:#,##0} 字节）";
        PropCluster.Text = info.SectorsPerCluster > 0 && info.BytesPerSector > 0
            ? Format.Bytes((long)info.SectorsPerCluster * info.BytesPerSector)
            : "—";
        PropSector.Text = info.BytesPerSector > 0 ? $"{info.BytesPerSector} 字节" : "—";
        PropSerial.Text = string.IsNullOrWhiteSpace(info.SerialNumber) ? "—" : info.SerialNumber;
        PropModel.Text = string.IsNullOrWhiteSpace(info.Model) ? "—" : info.Model;
        PropInterface.Text = string.IsNullOrWhiteSpace(info.InterfaceType) ? "USB" : info.InterfaceType;
        PropPartition.Text = string.IsNullOrWhiteSpace(info.PartitionLayout) ? "—" : info.PartitionLayout;
        PropDevicePath.Text = string.IsNullOrWhiteSpace(info.PnpDeviceId) ? "—" : info.PnpDeviceId;

        UpdateHealth(info);

        if (_scanCache.TryGetValue(info.Letter, out var cached))
        {
            ShowScanResult(cached);
        }
        else
        {
            ContentCard.Visibility = Visibility.Visible;
            CategoryList.ItemsSource = null;
            LargestFiles.ItemsSource = null;
            LargestFiles.Visibility = Visibility.Collapsed;
            LargestTitle.Visibility = Visibility.Collapsed;
            ScanSummary.Text = "点击“分析内容”查看文件构成";
            _ = StartScanAsync(info);
        }
    }

    private void UpdateHealth(UsbDriveInfo info)
    {
        if (info.FreeBytes < 512L * 1024 * 1024 && info.TotalBytes > 0)
        {
            ShowHealth("可用空间不足 512 MB，建议清理后再拷入文件。", InfoBarSeverity.Warning);
            return;
        }
        if (info.UsedRatio > 0.95)
        {
            ShowHealth("空间即将耗尽，剩余不足 5%。", InfoBarSeverity.Error);
            return;
        }
        if (info.UsedRatio > 0.85)
        {
            ShowHealth($"已用 {Format.Percent(info.UsedRatio)}，空间较为紧张。", InfoBarSeverity.Warning);
            return;
        }
        if (info.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase))
        {
            ShowHealth("FAT32 不支持单个超过 4 GB 的文件，拷贝大文件时请改用 exFAT 或 NTFS。", InfoBarSeverity.Informational);
            return;
        }
        HealthBar.IsOpen = false;
    }

    private void ShowHealth(string message, InfoBarSeverity severity)
    {
        HealthBar.Message = message;
        HealthBar.Severity = severity;
        HealthBar.IsOpen = true;
    }

    // ---------------- 内容分析 ----------------

    private async Task StartScanAsync(UsbDriveInfo info)
    {
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;

        ScanProgress.Visibility = Visibility.Visible;
        ScanButton.IsEnabled = false;
        ScanSummary.Text = "正在分析…";

        var progress = new Progress<int>(n => ScanSummary.Text = $"已扫描 {n} 个文件…");

        ScanResult result;
        try
        {
            result = await ContentScanner.ScanAsync(info.RootPath, progress).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("scan failed: " + ex.Message);
            result = new ScanResult();
        }

        if (cts.IsCancellationRequested) return;

        _scanCache[info.Letter] = result;
        ScanProgress.Visibility = Visibility.Collapsed;
        ScanButton.IsEnabled = true;

        if (_selected is not null && _selected.Letter.Equals(info.Letter, StringComparison.OrdinalIgnoreCase))
            ShowScanResult(result);
    }

    private void ShowScanResult(ScanResult result)
    {
        bool firstShow = ContentCard.Visibility == Visibility.Collapsed;
        ContentCard.Visibility = Visibility.Visible;
        CategoryList.ItemsSource = result.Categories;
        LargestFiles.ItemsSource = result.LargestFiles;
        LargestFiles.Visibility = result.LargestFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LargestTitle.Visibility = result.LargestFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        string summary = result.FileCount == 0
            ? "这个 U 盘是空的"
            : $"{result.FileCount:#,##0} 个文件 · {result.DirCount:#,##0} 个文件夹 · 共 {Format.Bytes(result.TotalBytes)} · 耗时 {result.Duration.TotalSeconds:0.0} 秒";
        if (result.Truncated) summary += "（已截断）";
        ScanSummary.Text = summary;

        if (firstShow)
            AnimateCardEntrance(ContentCard, 0);

        if (result.TotalBytes == 0 && result.FileCount == 0)
            ShowHealth("未发现可访问的文件，U 盘可能是空的或存在隐藏分区。", InfoBarSeverity.Informational);
    }

    // ---------------- 卡片入场动效 ----------------

    private void AnimateCardEntrance(FrameworkElement? element, int delayMs)
    {
        if (element is null) return;
        element.Opacity = 0;
        var transform = new CompositeTransform { TranslateY = 12, ScaleX = 0.98, ScaleY = 0.98 };
        element.RenderTransform = transform;

        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        var sb = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(320)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease
        };
        var move = new DoubleAnimation
        {
            From = 12,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(320)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease
        };
        var scaleX = new DoubleAnimation
        {
            From = 0.98,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(320)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease
        };
        var scaleY = new DoubleAnimation
        {
            From = 0.98,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(320)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        Storyboard.SetTarget(move, transform);
        Storyboard.SetTargetProperty(move, "TranslateY");
        Storyboard.SetTarget(scaleX, transform);
        Storyboard.SetTargetProperty(scaleX, "ScaleX");
        Storyboard.SetTarget(scaleY, transform);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");
        sb.Children.Add(fade);
        sb.Children.Add(move);
        sb.Children.Add(scaleX);
        sb.Children.Add(scaleY);
        sb.Begin();
    }

    // ---------------- 交互 ----------------

    private void OnDriveSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveList.SelectedItem is not UsbDriveInfo info) return;
        if (_selected is not null && _selected.Letter.Equals(info.Letter, StringComparison.OrdinalIgnoreCase)) return;
        _selected = info;
        UpdateDetail(info);
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) App.OpenInExplorer(_selected.RootPath);
    }

    private void OnScanClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        _scanCache.Remove(_selected.Letter);
        _ = StartScanAsync(_selected);
    }

    private async void OnEjectClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var info = _selected;
        EjectButton.IsEnabled = false;
        var (ok, message) = await DriveInspector.EjectAsync(info.Letter).ConfigureAwait(true);
        EjectButton.IsEnabled = true;

        ShowHealth(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        if (ok)
        {
            // 安全弹出后立即从列表移除并标记冷却期；不要立即 ReloadAsync，否则 Windows
            // 还没完成移除时盘符仍会被枚举回来，导致“设置内还显示 U 盘”。
            DriveInspector.InvalidateCache();
            App.Instance.MarkEjected(info.Letter);
            RemoveDrive(info.Letter);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        DriveInspector.InvalidateCache();
        await ReloadAsync().ConfigureAwait(true);
        RefreshButton.IsEnabled = true;
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        _apw.Hide();
    }

    private void OnFileItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FileEntry entry)
            App.RevealInExplorer(entry.FullPath);
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
}
