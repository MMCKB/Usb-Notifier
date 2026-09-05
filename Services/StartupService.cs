using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace UsbFlashToast.Services;

/// <summary>开机自启（写入 HKCU Run 项，无需管理员权限、无需打包）。</summary>
internal static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UsbFlashToast";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                var value = key?.GetValue(ValueName) as string;
                return !string.IsNullOrEmpty(value);
            }
            catch { return false; }
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled)
            {
                string exe = ExecutablePath;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
                key.SetValue(ValueName, $"\"{exe}\" --silent");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
            return true;
        }
        catch { return false; }
    }
}
