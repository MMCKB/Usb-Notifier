using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UsbFlashToast.Models;

namespace UsbFlashToast.Services;

/// <summary>后台扫描 U 盘内容：分类统计、最大文件、最近文件。</summary>
internal static class ContentScanner
{
    private enum Cat
    {
        Video, Audio, Image, Document, Archive, Program,
        Code, Font, Design3D, Database, Subtitle, DiskImage, Other
    }

    private static readonly Dictionary<string, Cat> ExtMap = BuildMap();

    /// <summary>无扩展名/特殊文件名按名称归类（Dockerfile、Makefile、LICENSE 等）。</summary>
    private static readonly Dictionary<string, Cat> NameMap = BuildNameMap();

    private static readonly (Cat Cat, string Name, string Glyph)[] Meta =
    {
        (Cat.Video, "视频", "\uE714"),
        (Cat.Audio, "音乐", "\uE8D6"),
        (Cat.Image, "图片", "\uEB9F"),
        (Cat.Document, "文档", "\uE8A5"),
        (Cat.Archive, "压缩包", "\uE7B8"),
        (Cat.Program, "程序与安装包", "\uECAA"),
        (Cat.Code, "代码", "\uE943"),
        (Cat.Font, "字体", "\uE8D2"),
        (Cat.Design3D, "设计与 3D", "\uE7AC"),
        (Cat.Database, "数据库", "\uE964"),
        (Cat.Subtitle, "字幕", "\uE8B1"),
        (Cat.DiskImage, "磁盘镜像", "\uEDA2"),
        (Cat.Other, "其他", "\uE7C3"),
    };

    private static Dictionary<string, Cat> BuildNameMap()
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["dockerfile"] = Cat.Code,
            ["makefile"] = Cat.Code,
            ["cmakelists.txt"] = Cat.Code,
            ["rakefile"] = Cat.Code,
            ["gemfile"] = Cat.Code,
            ["vagrantfile"] = Cat.Code,
            ["procfile"] = Cat.Code,
            ["license"] = Cat.Document,
            ["license.txt"] = Cat.Document,
            ["readme"] = Cat.Document,
            ["readme.md"] = Cat.Document,
            ["changelog"] = Cat.Document,
            ["authors"] = Cat.Document,
            ["contributing"] = Cat.Document,
            ["todo"] = Cat.Document,
        };

    private static Dictionary<string, Cat> BuildMap()
    {
        var map = new Dictionary<string, Cat>(StringComparer.OrdinalIgnoreCase);
        void Add(Cat c, params string[] exts)
        {
            foreach (var e in exts) map["." + e.TrimStart('.')] = c;
        }

        Add(Cat.Video, "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "rmvb", "rm", "ts", "m2ts",
            "mpg", "mpeg", "3gp", "m4v", "vob", "mts", "asf", "ogv", "divx", "f4v", "mxf", "mk3d",
            "hdmov", "mpv", "dat", "vro", "evo", "tod", "mod", "m4p");
        Add(Cat.Audio, "mp3", "wav", "flac", "aac", "ogg", "wma", "m4a", "ape", "opus", "aiff", "mid", "midi",
            "mka", "dsf", "dff", "ac3", "dts", "amr", "au", "ra", "weba", "mp2", "mpa", "oga", "spx",
            "caf", "aifc", "aif", "m4b", "m4r", "tta", "wv");
        Add(Cat.Image, "jpg", "jpeg", "png", "gif", "bmp", "webp", "heic", "heif", "tif", "tiff",
            "raw", "cr2", "cr3", "nef", "arw", "svg", "ico", "emf", "wmf",
            "avif", "jxl", "jp2", "j2k", "jpf", "orf", "rw2", "pef", "sr2", "dng", "erf", "k25", "kdc",
            "x3f", "3fr", "mef", "mrw", "nrw", "ptx", "r3d", "raf", "srw", "tga", "exr", "hdr",
            "apng", "pic", "cur", "ani", "wbmp", "pbm", "pgm", "ppm", "pnm", "iff", "lbm", "pcx", "dcx");
        Add(Cat.Design3D, "psd", "psb", "ai", "eps", "epsf", "cdr", "indd", "sketch", "fig", "xd", "xcf",
            "kra", "blend", "blend1", "fbx", "obj", "stl", "3mf", "max", "c4d", "step", "stp", "igs",
            "iges", "dwg", "dxf", "skp", "3ds", "dae", "ply", "gltf", "glb", "usdz", "usd", "sldprt",
            "sldasm", "prt", "asm", "ipt", "iam", "rvt", "gcode");
        Add(Cat.Document, "doc", "docx", "docm", "dot", "dotx", "dotm", "xls", "xlsx", "xlsm", "xlsb",
            "ppt", "pptx", "pptm", "pps", "ppsx", "pdf", "txt", "md", "markdown", "csv", "rtf", "rtfd",
            "odt", "ods", "odg", "odp", "epub", "mobi", "azw", "azw3", "fb2", "djvu", "chm", "xml",
            "log", "ini", "cfg", "conf", "xmind", "vsd", "vsdx", "vsdm", "one", "pages", "numbers",
            "key", "wps", "et", "dps", "ofd", "tex", "bib", "nb", "ipynb", "mmap", "xps", "oxps");
        Add(Cat.Archive, "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "cab", "lz", "lzma", "zst",
            "tgz", "tbz", "tbz2", "txz", "zipx", "arj", "lzh", "ace", "z", "br", "cbr", "cbz",
            "r00", "001", "jar", "war", "ear", "pax", "cpio", "shar", "s7z", "ha", "lha", "sfx");
        Add(Cat.DiskImage, "iso", "vhd", "vhdx", "vmdk", "vdi", "qcow2", "img", "wim", "esd", "isz",
            "nrg", "mds", "ccd", "cue", "toast", "dmgpart");
        Add(Cat.Program, "exe", "msi", "dll", "apk", "dmg", "pkg", "deb", "rpm", "appx", "msix",
            "sys", "bin", "ipa", "appimage", "com", "scr", "paf", "nupkg", "vsix", "xap", "run",
            "whl", "egg", "gem", "crx", "xpi", "msm", "msp", "ocx", "drv", "efi", "aab", "apks");
        Add(Cat.Font, "ttf", "otf", "woff", "woff2", "eot", "fon", "ttc", "pfb", "pfm", "afm",
            "dfont", "suit", "pfa", "bdf", "pcf", "snf");
        Add(Cat.Database, "db", "sqlite", "sqlite3", "db3", "mdb", "accdb", "dbf", "frm", "myd",
            "myi", "ibd", "ndf", "ldf", "mdf", "odb", "sqlite-journal", "fp7", "dbf2");
        Add(Cat.Subtitle, "srt", "ass", "ssa", "vtt", "idx", "smi", "sami", "scc", "lrc", "sbv",
            "ttml", "dfxp", "sub");
        Add(Cat.Code, "cs", "cpp", "cxx", "c++", "c", "cc", "h", "hpp", "hxx", "h++", "py", "pyw", "pyi",
            "js", "jsx", "mjs", "cjs", "ts", "tsx", "java", "kt", "kts", "go", "rs", "rb", "php",
            "html", "htm", "css", "scss", "sass", "less", "styl", "sh", "bash", "zsh", "bat", "cmd",
            "ps1", "psm1", "swift", "vue", "svelte", "lua", "pl", "pm", "gradle", "yaml", "yml",
            "toml", "sln", "csproj", "vbproj", "fsproj", "vcxproj",
            "m", "mm", "scala", "sbt", "dart", "r", "jl", "nim", "zig", "v", "asm", "s", "groovy",
            "coffee", "proto", "graphql", "gql", "tf", "hcl", "cmake", "make", "mk", "mkfile",
            "json", "jsonc", "json5", "sql", "dockerignore", "gitignore", "gitattributes", "editorconfig",
            "env", "lock", "sum", "pubspec", "pom", "properties", "reg", "inf", "iss", "nsi",
            "vb", "vba", "fs", "fsx", "clj", "cljs", "el", "lisp", "ml", "hs", "erl", "ex", "exs");
        return map;
    }

    public static Task<ScanResult> ScanAsync(string root, IProgress<int>? progress = null,
        int maxFiles = 150_000, int timeoutSeconds = 20)
        => Task.Run(() => Scan(root, progress, maxFiles, timeoutSeconds));

    public static ScanResult Scan(string root, IProgress<int>? progress, int maxFiles, int timeoutSeconds)
    {
        var start = DateTime.Now;
        var result = new ScanResult();
        var sizes = new long[Meta.Length];
        var counts = new long[Meta.Length];
        var largest = new List<FileEntry>(16);
        var recent = new List<FileEntry>(16);
        int reported = 0;

        var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
        var stack = new Stack<string>();
        stack.Push(root);

        long fileCount = 0, dirCount = 0, totalBytes = 0;
        bool truncated = false;

        try
        {
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                if (DateTime.Now > deadline) { truncated = true; break; }

                IEnumerable<FileSystemInfo> entries;
                try
                {
                    var di = new DirectoryInfo(dir);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    entries = di.EnumerateFileSystemInfos("*", new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = false,
                        AttributesToSkip = 0
                    });
                }
                catch { continue; }

                dirCount++;
                foreach (var entry in entries)
                {
                    if (DateTime.Now > deadline) { truncated = true; break; }
                    if (fileCount >= maxFiles) { truncated = true; break; }

                    try
                    {
                        if ((entry.Attributes & FileAttributes.Directory) != 0)
                        {
                            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            stack.Push(entry.FullName);
                            continue;
                        }

                        long size = ((FileInfo)entry).Length;
                        string ext = Path.GetExtension(entry.Name);
                        var cat = Cat.Other;
                        if (ext.Length > 0)
                        {
                            if (!ExtMap.TryGetValue(ext, out cat)) cat = Cat.Other;
                        }
                        else if (NameMap.TryGetValue(entry.Name, out var named))
                        {
                            // 无扩展名（Dockerfile / Makefile / LICENSE 等）按文件名归类
                            cat = named;
                        }
                        int idx = (int)cat;
                        sizes[idx] += size;
                        counts[idx]++;
                        totalBytes += size;
                        fileCount++;

                        if (largest.Count < 10)
                        {
                            Insert(largest, entry, size, bySize: true);
                        }
                        else if (size > largest[^1].Size)
                        {
                            largest.RemoveAt(largest.Count - 1);
                            Insert(largest, entry, size, bySize: true);
                        }

                        if (recent.Count < 8)
                        {
                            Insert(recent, entry, size, bySize: false);
                        }
                        else if (entry.LastWriteTime > recent[^1].Modified)
                        {
                            recent.RemoveAt(recent.Count - 1);
                            Insert(recent, entry, size, bySize: false);
                        }

                        if (++reported % 250 == 0) progress?.Report(reported);
                    }
                    catch { /* 跳过无权限或损坏的项 */ }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("scan aborted: " + ex.Message);
        }

        result.FileCount = fileCount;
        result.DirCount = dirCount;
        result.TotalBytes = totalBytes;
        result.Truncated = truncated;
        result.Duration = DateTime.Now - start;

        for (int i = 0; i < Meta.Length; i++)
        {
            if (counts[i] == 0 && sizes[i] == 0) continue;
            result.Categories.Add(new CategoryStat
            {
                Name = Meta[i].Name,
                Glyph = Meta[i].Glyph,
                Bytes = sizes[i],
                Count = counts[i]
            });
        }
        result.Categories = result.Categories.OrderByDescending(c => c.Bytes).ToList();

        long max = result.Categories.Count > 0 ? result.Categories[0].Bytes : 0;
        foreach (var c in result.Categories)
        {
            c.Ratio = max > 0 ? (double)c.Bytes / max : 0;
            c.ShareOfTotal = totalBytes > 0 ? (double)c.Bytes / totalBytes : 0;
        }

        result.LargestFiles = largest;
        result.RecentFiles = recent.OrderByDescending(f => f.Modified).ToList();
        return result;
    }

    private static void Insert(List<FileEntry> list, FileSystemInfo entry, long size, bool bySize)
    {
        var item = new FileEntry
        {
            Name = entry.Name,
            FullPath = entry.FullName,
            Size = size,
            Modified = entry.LastWriteTime
        };
        int pos = list.Count;
        if (bySize)
        {
            while (pos > 0 && list[pos - 1].Size < size) pos--;
        }
        else
        {
            while (pos > 0 && list[pos - 1].Modified < item.Modified) pos--;
        }
        list.Insert(pos, item);
    }
}
