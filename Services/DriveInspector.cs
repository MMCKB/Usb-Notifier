using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UsbFlashToast.Models;
using UsbFlashToast.Native;

namespace UsbFlashToast.Services;

/// <summary>读取 U 盘的容量、文件系统与物理设备信息（WMI + Win32）。</summary>
internal static class DriveInspector
{
    private static readonly object MapLock = new();
    private static Dictionary<string, DiskDescriptor> _diskMap = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _mapStamp = DateTime.MinValue;

    private sealed record DiskDescriptor(
        string Model, string InterfaceType, string MediaType, string Serial,
        string PnpDeviceId, long Size, int Partitions);

    /// <summary>当前挂载的、可移动的 USB 存储盘符（含 USB 移动硬盘）。</summary>
    public static List<string> EnumerateUsbLetters()
    {
        var result = new List<string>();
        var map = GetDiskMap();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.Removable)
            {
                result.Add(drive.Name.TrimEnd('\\'));
                continue;
            }
            if (drive.DriveType == DriveType.Fixed)
            {
                string letter = drive.Name.TrimEnd('\\');
                if (map.TryGetValue(letter, out var d) && IsUsbDescriptor(d))
                    result.Add(letter);
            }
        }
        return result;
    }

    /// <summary>
    /// 按「物理设备」归并的 U 盘列表：一块 U 盘即使有多个分区，也只返回一条。
    /// 容量 = 各分区之和；Primary 分区用于打开与展示。
    /// </summary>
    public static List<UsbDriveInfo> EnumerateUsbDevices()
    {
        var map = GetDiskMap();
        var letters = EnumerateUsbLetters();

        // 用 PNPDeviceID 作为物理设备键；拿不到描述符时退化为按盘符独立成组（旧行为）
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var descByKey = new Dictionary<string, DiskDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (string letter in letters)
        {
            string key;
            if (map.TryGetValue(letter, out var desc) && !string.IsNullOrEmpty(desc.PnpDeviceId))
            {
                key = desc.PnpDeviceId;
                descByKey[key] = desc;
            }
            else
            {
                key = "LETTER:" + letter;
            }

            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<string>();
                groups[key] = list;
            }
            if (!list.Contains(letter)) list.Add(letter);
        }

        var devices = new List<UsbDriveInfo>();
        foreach (var kv in groups)
        {
            var deviceLetters = kv.Value
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            descByKey.TryGetValue(kv.Key, out var d);
            var info = BuildDeviceInfo(kv.Key, deviceLetters, d);
            if (info is not null) devices.Add(info);
        }

        return devices
            .OrderBy(x => x.Letter, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>同一物理设备上的所有盘符（用于弹出时整体清理）。</summary>
    public static List<string> GetDeviceLetters(string letter)
    {
        letter = letter.TrimEnd('\\');
        var map = GetDiskMap();
        if (map.TryGetValue(letter, out var desc) && !string.IsNullOrEmpty(desc.PnpDeviceId))
        {
            var same = map
                .Where(kv => kv.Value.PnpDeviceId.Equals(desc.PnpDeviceId, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (same.Count > 0) return same;
        }
        return new List<string> { letter };
    }

    /// <summary>把一组同属一个物理设备的盘符，聚合成一条设备信息。</summary>
    private static UsbDriveInfo? BuildDeviceInfo(string key, List<string> letters, DiskDescriptor? desc)
    {
        var partitions = new List<PartitionInfo>();
        foreach (string l in letters)
        {
            var p = InspectPartition(l);
            if (p is not null) partitions.Add(p);
        }
        if (partitions.Count == 0) return null;

        // 首选分区：第一个已就绪的；都不可用时取第一个
        var ready = partitions.Where(p => p.IsReady).ToList();
        var primary = ready.FirstOrDefault() ?? partitions[0];
        primary.IsPrimary = true;

        long total = 0, free = 0, used = 0;
        foreach (var p in ready)
        {
            total += p.TotalBytes;
            free += p.FreeBytes;
            used += p.UsedBytes;
        }

        var info = new UsbDriveInfo
        {
            DeviceKey = key,
            Letter = primary.Letter,
            RootPath = primary.RootPath,
            VolumeLabel = primary.VolumeLabel,
            FileSystem = MergeFileSystem(ready),
            IsReady = ready.Count > 0,
            IsRemovable = primary.IsRemovable,
            TotalBytes = total,
            FreeBytes = free,
            UsedBytes = used,
            UsedRatio = total > 0 ? (double)used / total : 0,
            Partitions = partitions,
        };

        // 簇/扇区信息取首选分区
        if (Win32.GetDiskFreeSpaceW(primary.RootPath, out uint spc, out uint bps, out _, out _))
        {
            info.SectorsPerCluster = spc;
            info.BytesPerSector = bps;
        }

        if (desc is not null)
        {
            info.Model = desc.Model;
            info.InterfaceType = desc.InterfaceType;
            info.MediaType = desc.MediaType;
            info.SerialNumber = desc.Serial;
            info.PnpDeviceId = desc.PnpDeviceId;
            info.PhysicalSize = desc.Size;
            info.PartitionLayout = partitions.Count > 1
                ? $"{partitions.Count} 个分区（{info.LettersText}）"
                : "单分区";
            info.IsUsbDevice = IsUsbDescriptor(desc);

            // 物理容量明显大于各分区之和 → 存在未分配空间
            if (desc.Size > total * 1.05 && total > 0)
                info.PartitionLayout += " ，含未分配空间";
        }
        else
        {
            info.PartitionLayout = partitions.Count > 1
                ? $"{partitions.Count} 个分区（{info.LettersText}）"
                : "单分区";
        }

        if (string.IsNullOrEmpty(info.SerialNumber) && !string.IsNullOrEmpty(primary.Letter))
        {
            try
            {
                using var mo = new ManagementObject($"Win32_LogicalDisk.DeviceID='{primary.Letter}'");
                info.SerialNumber = mo["VolumeSerialNumber"]?.ToString() ?? string.Empty;
            }
            catch { /* ignore */ }
        }

        return info;
    }

    /// <summary>多个分区文件系统不一致时显示为“混合”。</summary>
    private static string MergeFileSystem(List<PartitionInfo> ready)
    {
        var kinds = ready
            .Select(p => p.FileSystem)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (kinds.Count == 0) return string.Empty;
        if (kinds.Count == 1) return kinds[0];
        return "混合（" + string.Join(" / ", kinds) + "）";
    }

    /// <summary>读取单个分区（卷）的容量等信息。</summary>
    private static PartitionInfo? InspectPartition(string letter)
    {
        letter = letter.TrimEnd('\\');
        string root = letter + "\\";

        DriveInfo drive;
        try { drive = new DriveInfo(letter); }
        catch { return null; }

        var p = new PartitionInfo { Letter = letter, RootPath = root };

        try
        {
            p.IsReady = drive.IsReady;
            p.IsRemovable = drive.DriveType == DriveType.Removable;
            if (drive.IsReady)
            {
                p.VolumeLabel = drive.VolumeLabel ?? string.Empty;
                p.FileSystem = drive.DriveFormat ?? string.Empty;
                p.TotalBytes = drive.TotalSize;
                p.FreeBytes = drive.TotalFreeSpace;
                p.UsedBytes = p.TotalBytes - p.FreeBytes;
                p.UsedRatio = p.TotalBytes > 0 ? (double)p.UsedBytes / p.TotalBytes : 0;
            }
        }
        catch (IOException) { p.IsReady = false; }
        catch (UnauthorizedAccessException) { p.IsReady = false; }
        catch (ArgumentException) { p.IsReady = false; }

        return p;
    }

    public static bool IsUsbLetter(string letter)
    {
        letter = letter.TrimEnd('\\');
        try
        {
            var info = new DriveInfo(letter);
            if (info.DriveType == DriveType.Removable) return true;
        }
        catch { /* ignore */ }

        var map = GetDiskMap();
        return map.TryGetValue(letter, out var d) && IsUsbDescriptor(d);
    }

    public static Task<UsbDriveInfo?> InspectAsync(string letter, bool includeDeviceInfo = true)
        => Task.Run(() => Inspect(letter, includeDeviceInfo));

    public static UsbDriveInfo? Inspect(string letter, bool includeDeviceInfo = true)
    {
        letter = letter.TrimEnd('\\');
        string root = letter + "\\";

        DriveInfo drive;
        try { drive = new DriveInfo(letter); }
        catch { return null; }

        var info = new UsbDriveInfo
        {
            Letter = letter,
            RootPath = root,
            DetectedAt = DateTime.Now,
        };

        try
        {
            info.IsReady = drive.IsReady;
            info.IsRemovable = drive.DriveType == DriveType.Removable;
            if (drive.IsReady)
            {
                info.VolumeLabel = drive.VolumeLabel ?? string.Empty;
                info.FileSystem = drive.DriveFormat ?? string.Empty;
                info.TotalBytes = drive.TotalSize;
                info.FreeBytes = drive.TotalFreeSpace;
                info.UsedBytes = info.TotalBytes - info.FreeBytes;
                info.UsedRatio = info.TotalBytes > 0 ? (double)info.UsedBytes / info.TotalBytes : 0;
            }
        }
        catch (IOException) { info.IsReady = false; }
        catch (UnauthorizedAccessException) { info.IsReady = false; }

        if (Win32.GetDiskFreeSpaceW(root, out uint spc, out uint bps, out _, out _))
        {
            info.SectorsPerCluster = spc;
            info.BytesPerSector = bps;
        }

        if (includeDeviceInfo)
        {
            if (GetDiskMap().TryGetValue(letter, out var desc))
            {
                info.Model = desc.Model;
                info.InterfaceType = desc.InterfaceType;
                info.MediaType = desc.MediaType;
                info.SerialNumber = desc.Serial;
                info.PnpDeviceId = desc.PnpDeviceId;
                info.PhysicalSize = desc.Size;
                info.PartitionLayout = desc.Partitions > 1 ? $"{desc.Partitions} 个分区" : "单分区";
                info.IsUsbDevice = IsUsbDescriptor(desc);
            }
            if (string.IsNullOrEmpty(info.SerialNumber))
            {
                try
                {
                    using var mo = new ManagementObject($"Win32_LogicalDisk.DeviceID='{letter}'");
                    info.SerialNumber = mo["VolumeSerialNumber"]?.ToString() ?? string.Empty;
                }
                catch { /* ignore */ }
            }
        }

        // 与 EnumerateUsbDevices 保持一致的字段，便于上层统一按“设备”处理
        info.DeviceKey = !string.IsNullOrEmpty(info.PnpDeviceId)
            ? info.PnpDeviceId
            : "LETTER:" + letter;
        info.Partitions = new List<PartitionInfo>
        {
            new()
            {
                Letter = letter,
                RootPath = root,
                VolumeLabel = info.VolumeLabel,
                FileSystem = info.FileSystem,
                IsReady = info.IsReady,
                IsRemovable = info.IsRemovable,
                TotalBytes = info.TotalBytes,
                FreeBytes = info.FreeBytes,
                UsedBytes = info.UsedBytes,
                UsedRatio = info.UsedRatio,
                IsPrimary = true,
            }
        };

        return info;
    }

    // ---------------- 安全弹出 ----------------

    public static Task<(bool Ok, string Message)> EjectAsync(string letter)
        => Task.Run(() => Eject(letter));

    public static (bool Ok, string Message) Eject(string letter)
    {
        letter = letter.TrimEnd('\\');
        var info = Inspect(letter);

        if (!string.IsNullOrEmpty(info?.PnpDeviceId))
        {
            int rc = Win32.CM_Locate_DevNodeW(out IntPtr devInst, info.PnpDeviceId, Win32.CM_LOCATE_DEVNODE_NORMAL);
            if (rc == Win32.CR_SUCCESS)
            {
                var vetoName = new StringBuilder(512);
                rc = Win32.CM_Request_Device_EjectW(devInst, out Win32.PnpVetoType veto, vetoName, vetoName.Capacity, 0);
                if (rc == Win32.CR_SUCCESS)
                    return (true, "可以安全地拔出设备了。");

                string detail = veto switch
                {
                    Win32.PnpVetoType.OutstandingOpen or Win32.PnpVetoType.PendingClose =>
                        "有程序正在使用该设备上的文件。",
                    Win32.PnpVetoType.InsufficientRights => "权限不足，请以当前用户关闭占用程序后重试。",
                    _ => $"系统拒绝弹出（{veto}）。"
                };
                string? name = vetoName.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    detail += $" 占用者：{name}";
                return (false, detail);
            }
        }

        return EjectByIoctl(letter);
    }

    private static (bool Ok, string Message) EjectByIoctl(string letter)
    {
        string path = @"\\.\" + letter;
        IntPtr handle = Win32.CreateFileW(path, Win32.GENERIC_READ,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero,
            Win32.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1))
            return (false, "无法打开卷，设备可能已被占用。");

        try
        {
            if (!Win32.DeviceIoControl(handle, Win32.FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                return (false, "有程序正在使用设备，请先关闭相关文件后重试。");

            Win32.DeviceIoControl(handle, Win32.FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            bool ejected = Win32.DeviceIoControl(handle, Win32.IOCTL_STORAGE_EJECT_MEDIA,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            return ejected
                ? (true, "可以安全地拔出设备了。")
                : (false, "设备已卸载，但未返回弹出成功，请确认指示灯后拔出。");
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    // ---------------- WMI ----------------

    private static bool IsUsbDescriptor(DiskDescriptor d)
    {
        if (d.InterfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase)) return true;
        return d.PnpDeviceId.Contains("USBSTOR", StringComparison.OrdinalIgnoreCase)
            || d.PnpDeviceId.Contains("USB\\", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, DiskDescriptor> GetDiskMap()
    {
        lock (MapLock)
        {
            if ((DateTime.Now - _mapStamp) < TimeSpan.FromSeconds(20) && _diskMap.Count > 0)
                return _diskMap;

            var map = new Dictionary<string, DiskDescriptor>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var disks = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
                foreach (ManagementObject disk in disks.Get())
                {
                    var desc = new DiskDescriptor(
                        disk["Model"]?.ToString()?.Trim() ?? string.Empty,
                        disk["InterfaceType"]?.ToString()?.Trim() ?? string.Empty,
                        disk["MediaType"]?.ToString()?.Trim() ?? string.Empty,
                        disk["SerialNumber"]?.ToString()?.Trim() ?? string.Empty,
                        disk["PNPDeviceID"]?.ToString() ?? string.Empty,
                        TryLong(disk["Size"]),
                        TryInt(disk["Partitions"]));

                    foreach (string letter in LettersOfDisk(disk))
                        map[letter] = desc;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("WMI disk enumeration failed: " + ex.Message);
            }

            _diskMap = map;
            _mapStamp = DateTime.Now;
            return map;
        }
    }

    private static IEnumerable<string> LettersOfDisk(ManagementObject disk)
    {
        var letters = new List<string>();
        try
        {
            var partitions = new ManagementObjectSearcher(
                new RelatedObjectQuery(disk.Path.RelativePath, "Win32_DiskDriveToDiskPartition"));
            foreach (ManagementObject partition in partitions.Get())
            {
                var logicals = new ManagementObjectSearcher(
                    new RelatedObjectQuery(partition.Path.RelativePath, "Win32_LogicalDiskToPartition"));
                foreach (ManagementObject logical in logicals.Get())
                {
                    string? id = logical["DeviceID"]?.ToString();
                    if (!string.IsNullOrEmpty(id))
                        letters.Add(id.TrimEnd('\\'));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("WMI partition walk failed: " + ex.Message);
        }
        return letters;
    }

    private static long TryLong(object? value)
    {
        try { return Convert.ToInt64(value); } catch { return 0; }
    }

    private static int TryInt(object? value)
    {
        try { return Convert.ToInt32(value); } catch { return 0; }
    }

    internal static void InvalidateCache()
    {
        lock (MapLock) { _mapStamp = DateTime.MinValue; }
    }
}
