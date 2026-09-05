using System;
using System.Collections.Generic;
using System.Linq;

namespace UsbFlashToast.Models;

/// <summary>U 盘上的一个分区（卷）。</summary>
public sealed class PartitionInfo
{
    public string Letter { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string VolumeLabel { get; set; } = string.Empty;
    public string FileSystem { get; set; } = string.Empty;
    public bool IsReady { get; set; }
    public bool IsRemovable { get; set; }

    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public long UsedBytes { get; set; }
    public double UsedRatio { get; set; }
    public double UsedRatioPercent => UsedRatio * 100;

    /// <summary>是否为设备的首选分区（点击“打开 U 盘”时打开它）。</summary>
    public bool IsPrimary { get; set; }

    /// <summary>该分区未分配的空间（物理容量大于分区容量时）。</summary>
    public bool HasUnallocatedHint { get; set; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(VolumeLabel) ? Letter : $"{Letter} {VolumeLabel}";
}

/// <summary>一次 U 盘快照：容量、文件系统与物理设备信息。</summary>
public sealed class UsbDriveInfo
{
    public string Letter { get; set; } = string.Empty;          // "E:"
    public string RootPath { get; set; } = string.Empty;        // "E:\"
    public string VolumeLabel { get; set; } = string.Empty;
    public string FileSystem { get; set; } = string.Empty;
    public bool IsReady { get; set; }
    public bool IsRemovable { get; set; }

    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public long UsedBytes { get; set; }
    public double UsedRatio { get; set; }
    public double UsedRatioPercent => UsedRatio * 100;

    public uint BytesPerSector { get; set; }
    public uint SectorsPerCluster { get; set; }

    // 物理设备信息（来自 WMI）
    public string Model { get; set; } = string.Empty;
    public string InterfaceType { get; set; } = string.Empty;   // USB / SCSI
    public string MediaType { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string PnpDeviceId { get; set; } = string.Empty;
    public string PartitionLayout { get; set; } = string.Empty;
    public long PhysicalSize { get; set; }
    public bool IsUsbDevice { get; set; }

    public DateTime DetectedAt { get; set; } = DateTime.Now;

    /// <summary>物理设备标识（PNPDeviceID）；无法判定时退化为 "LETTER:E:"。</summary>
    public string DeviceKey { get; set; } = string.Empty;

    /// <summary>该物理设备上的所有分区（单分区时只有一项）。</summary>
    public List<PartitionInfo> Partitions { get; set; } = new();

    public bool IsMultiPartition => Partitions.Count > 1;

    /// <summary>所有分区盘符，如 "E:, F:"。</summary>
    public string LettersText => Partitions.Count > 0
        ? string.Join(", ", Partitions.Select(p => p.Letter))
        : Letter;

    public string DisplayName
    {
        get
        {
            string letters = IsMultiPartition ? LettersText : Letter;
            return string.IsNullOrWhiteSpace(VolumeLabel)
                ? $"U 盘 ({letters})"
                : $"{VolumeLabel} ({letters})";
        }
    }

    public string DeviceKind
    {
        get
        {
            string m = (Model + " " + MediaType).ToUpperInvariant();
            if (m.Contains("CARD") || m.Contains("SD") || m.Contains("MMC")) return "存储卡 / 读卡器";
            if (PhysicalSize >= 500L * 1024 * 1024 * 1024) return "USB 移动硬盘";
            return "U 盘";
        }
    }

    public bool IsLowOnSpace => TotalBytes > 0 && FreeBytes < TotalBytes * 0.10;
}

/// <summary>按扩展名归类的统计项。</summary>
public sealed class CategoryStat
{
    public string Name { get; set; } = string.Empty;
    public string Glyph { get; set; } = "\uE8A5";
    public long Bytes { get; set; }
    public long Count { get; set; }

    /// <summary>占最大分类的比例（0~1），用于绘制条形。</summary>
    public double Ratio { get; set; }

    /// <summary>占全盘的比例（0~1）。</summary>
    public double ShareOfTotal { get; set; }

    public double RatioPercent => Ratio * 100;
}

public sealed class FileEntry
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime Modified { get; set; }
}

/// <summary>内容扫描结果。</summary>
public sealed class ScanResult
{
    public long FileCount { get; set; }
    public long DirCount { get; set; }
    public long TotalBytes { get; set; }
    public List<CategoryStat> Categories { get; set; } = new();
    public List<FileEntry> LargestFiles { get; set; } = new();
    public List<FileEntry> RecentFiles { get; set; } = new();
    public TimeSpan Duration { get; set; }
    public bool Truncated { get; set; }
    public bool HasHiddenSystemUsage { get; set; }
}

public static class Format
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Bytes(long value)
    {
        if (value <= 0) return "0 B";
        double size = value;
        int unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{size:0} B" : $"{size:0.##} {Units[unit]}";
    }

    public static string Gigabytes(long value) => $"{value / 1024.0 / 1024 / 1024:0.##} GB";

    public static string Percent(double ratio) => $"{ratio * 100:0.#}%";

    public static string TimeAgo(DateTime time)
    {
        var span = DateTime.Now - time;
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时前";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
        return time.ToString("yyyy-MM-dd");
    }
}
