using System.Runtime.InteropServices;
using System.Text;
using MPQTikTool.Models;

namespace MPQTikTool.Services;

/// <summary>
/// Thin StormLib adapter. Keep native MPQ details isolated from the UI.
/// Requires StormLib.dll (x64) at runtime.
/// </summary>
public sealed class MpqArchiveService : IDisposable
{
    private IntPtr _archive;
    public string? ArchivePath { get; private set; }
    public bool IsOpen => _archive != IntPtr.Zero;

    public IReadOnlyList<ArchiveEntry> Open(string path)
    {
        Close();
        if (!File.Exists(path)) throw new FileNotFoundException("地图文件不存在。", path);

        if (!NativeMethods.SFileOpenArchive(path, 0, 0, out _archive) || _archive == IntPtr.Zero)
            throw new InvalidOperationException($"StormLib 无法打开该地图（Win32 错误：{Marshal.GetLastWin32Error()}）。请确认文件是有效的 MPQ/W3X/W3M，且 StormLib.dll 可用。");

        ArchivePath = path;
        var entries = new List<ArchiveEntry>();
        var findHandle = NativeMethods.SFileFindFirstFile(_archive, "*", out var data, IntPtr.Zero);
        if (findHandle == IntPtr.Zero || findHandle == new IntPtr(-1))
            return entries;

        try
        {
            do
            {
                var name = data.FileName?.TrimEnd('\0') ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name) && name != "(listfile)")
                    entries.Add(new ArchiveEntry(name, data.FileSize, data.FileFlags, data.Compression, data.Locale));
            }
            while (NativeMethods.SFileFindNextFile(findHandle, out data));
        }
        finally
        {
            NativeMethods.SFileFindClose(findHandle);
        }

        return entries
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Extract(ArchiveEntry entry, string destinationPath)
    {
        EnsureOpen();
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (!NativeMethods.SFileExtractFile(_archive, entry.Name, destinationPath, 0))
            throw new IOException($"导出失败：{entry.Name}（Win32 错误：{Marshal.GetLastWin32Error()}）。文件可能加密、损坏或缺少解密密钥。");
    }

    public byte[] ReadAllBytes(ArchiveEntry entry)
    {
        var temp = Path.Combine(Path.GetTempPath(), "MPQTikTool", Guid.NewGuid().ToString("N") + entry.Extension);
        try
        {
            Extract(entry, temp);
            return File.ReadAllBytes(temp);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    private void EnsureOpen()
    {
        if (!IsOpen) throw new InvalidOperationException("请先打开一张地图。");
    }

    public void Close()
    {
        if (_archive != IntPtr.Zero)
        {
            NativeMethods.SFileCloseArchive(_archive);
            _archive = IntPtr.Zero;
        }
        ArchivePath = null;
    }

    public void Dispose() => Close();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct SFileFindData
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)] public string PlainName;
        public uint HashIndex;
        public uint BlockIndex;
        public uint FileSize;
        public uint FileFlags;
        public uint Compression;
        public uint FileTimeLow;
        public uint FileTimeHigh;
        public uint Locale;
    }

    private static class NativeMethods
    {
        private const string Dll = "StormLib.dll";

        [DllImport(Dll, CharSet = CharSet.Ansi, SetLastError = true, EntryPoint = "SFileOpenArchive")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SFileOpenArchive(string archiveName, uint priority, uint flags, out IntPtr archive);

        [DllImport(Dll, SetLastError = true, EntryPoint = "SFileCloseArchive")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SFileCloseArchive(IntPtr archive);

        [DllImport(Dll, CharSet = CharSet.Ansi, SetLastError = true, EntryPoint = "SFileFindFirstFile")]
        internal static extern IntPtr SFileFindFirstFile(IntPtr archive, string mask, out SFileFindData findData, IntPtr listFile);

        [DllImport(Dll, SetLastError = true, EntryPoint = "SFileFindNextFile")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SFileFindNextFile(IntPtr findHandle, out SFileFindData findData);

        [DllImport(Dll, SetLastError = true, EntryPoint = "SFileFindClose")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern void SFileFindClose(IntPtr findHandle);

        [DllImport(Dll, CharSet = CharSet.Ansi, SetLastError = true, EntryPoint = "SFileExtractFile")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SFileExtractFile(IntPtr archive, string archivedName, string localName, uint searchScope);
    }
}
