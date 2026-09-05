using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsbFlashToast.Services;

public enum ToastBackdrop
{
    Acrylic,
    AcrylicThin,
    Smoke,
    Frosted,
    Mica,
    MicaAlt,
    Solid,
    Transparent
}

/// <summary>点击主窗口关闭按钮时的默认行为。</summary>
public enum CloseAction
{
    /// <summary>每次询问用户。</summary>
    Ask,
    /// <summary>直接退出应用。</summary>
    Exit,
    /// <summary>隐藏到托盘。</summary>
    Hide,
}

public class AppSettings
{
    public ToastBackdrop ToastBackdrop { get; set; } = ToastBackdrop.Acrylic;
    public bool StartupEnabled { get; set; }
    public CloseAction CloseAction { get; set; } = CloseAction.Ask;
}

internal static class SettingsService
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsbFlashToast");

    private static readonly string PathFile = Path.Combine(Dir, "settings.json");

    private static readonly object Lock = new();
    private static AppSettings _cached = new();
    private static DateTime _lastRead = DateTime.MinValue;

    public static event Action? Changed;

    public static AppSettings Current
    {
        get
        {
            lock (Lock)
            {
                if (DateTime.Now - _lastRead > TimeSpan.FromSeconds(1))
                {
                    _cached = ReadFromDisk();
                    _lastRead = DateTime.Now;
                }
                return _cached;
            }
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Lock)
        {
            _cached = settings;
            WriteToDisk(settings);
        }
        Changed?.Invoke();
    }

    private static AppSettings ReadFromDisk()
    {
        try
        {
            if (File.Exists(PathFile))
            {
                var json = File.ReadAllText(PathFile);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var s = JsonSerializer.Deserialize(json, SettingsContext.Default.AppSettings);
                    if (s is not null) return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("读取设置失败", ex);
        }
        return new AppSettings();
    }

    private static void WriteToDisk(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(settings, SettingsContext.Default.AppSettings);
            File.WriteAllText(PathFile, json);
        }
        catch (Exception ex)
        {
            Log.Write("保存设置失败", ex);
        }
    }

    public static void SyncStartupFromRegistry()
    {
        var s = Current;
        s.StartupEnabled = StartupService.IsEnabled;
        Save(s);
    }
}

[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsContext : JsonSerializerContext { }
