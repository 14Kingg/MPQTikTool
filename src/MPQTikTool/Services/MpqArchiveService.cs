using System.IO;
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
        return EnumerateNamedFiles();
    }

    /// <summary>
    /// Adds an external MPQ listfile to StormLib's name database. Only names whose
    /// hashes match entries already present in the archive can be recovered.
    /// </summary>
    public void AddExternalListFile(string listFilePath)
    {
        EnsureOpen();
        if (!File.Exists(listFilePath)) throw new FileNotFoundException("外部文件名库不存在。", listFilePath);
        var result = NativeMethods.SFileAddListFile(_archive, listFilePath);
        if (result != 0)
            throw new IOException($"加载外部文件名库失败（StormLib 错误码：{result}）。");
    }

    public IReadOnlyList<ArchiveEntry> EnumerateNamedFiles()
    {
        EnsureOpen();
        var entries = new List<ArchiveEntry>();
        var findHandle = NativeMethods.SFileFindFirstFile(_archive, "*", out var data, IntPtr.Zero);
        if (findHandle == IntPtr.Zero || findHandle == new IntPtr(-1))
            return entries;

        try
        {
            do
            {
                var name = data.FileName?.TrimEnd('\\0') ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name))
                    entries.Add(new ArchiveEntry(name, data.FileSize, data.FileFlags, data.CompressedSize, data.Locale));
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
        public IntPtr PlainName;
        public uint HashIndex;
        public uint BlockIndex;
        public uint FileSize;
        public uint FileFlags;
        public uint CompressedSize;
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

        [DllImport(Dll, CharSet = CharSet.Ansi, SetLastError = true, EntryPoint = "SFileAddListFile")]
        internal static extern uint SFileAddListFile(IntPtr archive, string listFileName);

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
