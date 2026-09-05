using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace UsbFlashToast.Services;

/// <summary>
/// 统一的背景材质应用逻辑，供弹窗与主窗口共用，确保设置里的 8 种材质在两类窗口上表现一致。
/// </summary>
internal static class BackdropHelper
{
    public static void Apply(Window window, Panel root, Shape? overlay, bool isDark)
    {
        try
        {
            var backdrop = SettingsService.Current.ToastBackdrop;
            window.SystemBackdrop = null;
            root.Background = null;
            if (overlay is not null)
            {
                overlay.Fill = null;
                overlay.Opacity = 0;
            }

            switch (backdrop)
            {
                case ToastBackdrop.Mica:
                    window.SystemBackdrop = new MicaBackdrop();
                    break;
                case ToastBackdrop.MicaAlt:
                    window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                    break;
                case ToastBackdrop.Solid:
                    root.Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
                    break;
                case ToastBackdrop.Transparent:
                    root.Background = new SolidColorBrush(isDark
                        ? Color.FromArgb(180, 32, 32, 32)
                        : Color.FromArgb(180, 243, 243, 243));
                    break;
                case ToastBackdrop.AcrylicThin:
                    window.SystemBackdrop = new DesktopAcrylicBackdrop();
                    if (overlay is not null) overlay.Fill = GetOverlayBrush(0.55, isDark);
                    break;
                case ToastBackdrop.Smoke:
                    window.SystemBackdrop = new DesktopAcrylicBackdrop();
                    if (overlay is not null) overlay.Fill = GetOverlayBrush(0.30, true);
                    break;
                case ToastBackdrop.Frosted:
                    window.SystemBackdrop = new DesktopAcrylicBackdrop();
                    if (overlay is not null) overlay.Fill = GetOverlayBrush(0.22, false);
                    break;
                case ToastBackdrop.Acrylic:
                default:
                    window.SystemBackdrop = new DesktopAcrylicBackdrop();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write("BackdropHelper.Apply 失败", ex);
            try { root.Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"]; }
            catch { }
        }
    }

    public static Brush GetOverlayBrush(double baseOpacity, bool dark)
    {
        try
        {
            byte alpha = (byte)System.Math.Clamp(255 * baseOpacity, 0, 255);
            return dark
                ? new SolidColorBrush(Color.FromArgb(alpha, 20, 20, 20))
                : new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));
        }
        catch
        {
            return new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        }
    }
}
