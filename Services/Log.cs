using System;
using System.IO;

namespace UsbFlashToast.Services;

/// <summary>轻量文件日志，用于排查启动/运行期异常。日志位于 %LOCALAPPDATA%\UsbFlashToast\log.txt</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string DirPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsbFlashToast");
    private static readonly string FilePath = Path.Combine(DirPath, "log.txt");

    public static string Location => FilePath;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirPath);
                File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志绝不能影响主流程
        }
    }

    public static void Write(string tag, Exception ex)
        => Write($"{tag}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    public static void Clear()
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirPath);
                File.WriteAllText(FilePath, $"[{DateTime.Now:HH:mm:ss.fff}] === 会话开始 ==={Environment.NewLine}");
            }
        }
        catch { /* ignore */ }
    }
}
