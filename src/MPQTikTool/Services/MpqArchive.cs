using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using MPQTikTool.Models;

namespace MPQTikTool.Services;

/// <summary>
/// Self-contained MPQ reader for Warcraft III maps (.w3x/.w3m) and raw MPQ archives.
///
/// Unlike a name-based API (StormLib SFileFindFirstFile / SFileOpenFileEx), this reader
/// addresses every file by its block-table index. That makes it possible to enumerate,
/// preview and export every resource in protected maps that have no (listfile), whose
/// names were stripped, or whose hash-table locale was tampered with.
///
/// No native DLL is required; the class can be reused from any .NET 8 project.
/// </summary>
public sealed class MpqArchive : IDisposable
{
    private const uint FlagImplode = 0x00000100;
    private const uint FlagCompress = 0x00000200;
    private const uint FlagEncrypted = 0x00010000;
    private const uint FlagFixKey = 0x00020000;
    private const uint FlagPatch = 0x00100000;
    private const uint FlagSingleUnit = 0x01000000;
    private const uint FlagDeleteMarker = 0x02000000;
    private const uint FlagSectorCrc = 0x04000000;
    private const uint FlagExists = 0x80000000;

    private const uint HashFree = 0xFFFFFFFF;
    private const uint HashDeleted = 0xFFFFFFFE;

    private readonly FileStream _stream;
    private readonly object _sync = new();
    private readonly long _base;
    private readonly uint _sectorSize;
    private readonly HeaderInfo _header;
    private readonly uint[] _rawHashTable;
    private readonly BlockInfo[] _rawBlocks;
    private readonly Dictionary<(uint, uint), List<ArchiveEntry>> _hashLookup = new();
    private readonly List<ArchiveEntry> _entries;

    public string ArchivePath { get; }
    public string FormatDescription { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;
    public long FileLength => _stream.Length;

    private MpqArchive(string path, FileStream stream, long headerOffset, HeaderInfo header,
        BlockInfo[] blocks, uint[] hashTable)
    {
        ArchivePath = path;
        _stream = stream;
        _base = headerOffset;
        _header = header;
        _rawHashTable = hashTable;
        _rawBlocks = blocks;
        _sectorSize = 512u << header.SectorShift;
        FormatDescription = $"MPQ v{header.Version + 1}，{header.BlockCount:N0} 个块，扇区 {_sectorSize:N0} B，头部偏移 0x{headerOffset:X}";

        _entries = new List<ArchiveEntry>((int)header.BlockCount);
        var hashByBlock = new Dictionary<int, (uint H1, uint H2, uint Locale)>();
        for (var i = 0; i < hashTable.Length; i += 4)
        {
            var h1 = hashTable[i];
            var h2 = hashTable[i + 1];
            var locale = hashTable[i + 2];
            var blockIndex = hashTable[i + 3];
            if (blockIndex == HashFree || blockIndex == HashDeleted || blockIndex >= header.BlockCount) continue;
            hashByBlock.TryAdd((int)blockIndex, (h1, h2, locale));
        }

        for (var i = 0; i < blocks.Length; i++)
        {
            var b = blocks[i];
            hashByBlock.TryGetValue(i, out var h);
            var entry = new ArchiveEntry
            {
                BlockIndex = i,
                Size = b.FileSize,
                CompressedSize = b.CompressedSize,
                Flags = b.Flags,
                Locale = h.Locale,
                FilePos = b.FilePos,
                Hash1 = h.H1,
                Hash2 = h.H2,
                Kind = ResourceKind.Unknown
            };
            entry.Name = MpqFormatDetector.BuildSyntheticName(i, h.H1, ResourceKind.Unknown, ".bin");
            _entries.Add(entry);

            var key = (h.H1, h.H2);
            if (!_hashLookup.TryGetValue(key, out var list))
            {
                list = new List<ArchiveEntry>();
                _hashLookup[key] = list;
            }
            list.Add(entry);
        }

        // Standard Warcraft III filenames are hash-matched regardless of the tampered locale.
        ApplyNames(StandardNames);
    }

    /// <summary>Opens any w3x/w3m/mpq, locating the MPQ header even behind a W3X or user-data header.</summary>
    public static MpqArchive Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("地图文件不存在。", path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        try
        {
            foreach (var candidate in FindHeaderCandidates(stream))
            {
                var header = TryReadHeader(stream, candidate);
                if (header is null) continue;
                // Header version 3 is MPQ format v4. Its HET/BET tables are not
                // interchangeable with the classic hash/block tables parsed below.
                if (header.Value.Version >= 3)
                    throw new NotSupportedException("检测到 MPQ v4。当前版本尚未实现 HET/BET 扩展表解析，已停止读取以避免把扩展表误当成传统表。");
                var blocks = ReadTables(stream, candidate, header.Value, out var hashTable);
                return new MpqArchive(path, stream, candidate, header.Value, blocks, hashTable);
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        stream.Dispose();
        throw new InvalidDataException("未找到有效的 MPQ 头。文件可能不是 Warcraft III 地图 / MPQ 档案，或已被非标准加密。");
    }

    private static List<long> FindHeaderCandidates(FileStream stream)
    {
        var candidates = new List<long>();
        var buffer = new byte[1 << 20];
        var limit = Math.Min(stream.Length, 128L * 1024 * 1024);
        for (var chunk = 0L; chunk < limit; chunk += buffer.Length)
        {
            stream.Position = chunk;
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - chunk));
            for (var off = 0; off + 4 <= read; off += 0x200)
            {
                if (buffer[off] != (byte)'M' || buffer[off + 1] != (byte)'P' || buffer[off + 2] != (byte)'Q') continue;
                var absolute = chunk + off;
                if (buffer[off + 3] == 0x1A)
                {
                    candidates.Add(absolute);
                }
                else if (buffer[off + 3] == 0x1B && off + 16 <= read)
                {
                    var headerOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(off + 8, 4));
                    if (headerOffset < 0x10000000) candidates.Add(absolute + headerOffset);
                }
            }
        }
        return candidates;
    }

    private readonly record struct HeaderInfo(
        uint HeaderSize, uint ArchiveSize, ushort Version, byte SectorShift,
        long HashPos, long BlockPos, uint HashCount, uint BlockCount, long HiBlockPos);

    private static HeaderInfo? TryReadHeader(FileStream stream, long offset)
    {
        if (offset + 0x20 > stream.Length) return null;
        var raw = ReadAt(stream, offset, 0xD0);
        if (raw.Length < 0x20 || !raw.AsSpan(0, 4).SequenceEqual("MPQ\x1A"u8)) return null;

        uint Read32(int at) => at + 4 <= raw.Length ? BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at, 4)) : 0;
        ushort Read16(int at) => at + 2 <= raw.Length ? BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at, 2)) : (ushort)0;

        var headerSize = Read32(4);
        var version = Read16(0x0C);
        var shift = raw[0x0E];
        var hashCount = Read32(0x18);
        var blockCount = Read32(0x1C);
        if (headerSize < 0x20 || headerSize > 0xD0 || shift > 20) return null;
        if (hashCount == 0 || hashCount > (1u << 22) || blockCount == 0 || blockCount > (1u << 22)) return null;

        var hashPos = (long)Read32(0x10);
        var blockPos = (long)Read32(0x14);
        long hiBlockPos = 0;
        if (version >= 1 && headerSize >= 0x2C)
        {
            hashPos |= (long)Read16(0x28) << 32;
            blockPos |= (long)Read16(0x2A) << 32;
            if (headerSize >= 0x28)
                hiBlockPos = Read32(0x20) | ((long)Read32(0x24) << 32);
        }

        var available = stream.Length - offset;
        if (hashPos < 0 || blockPos < 0 || hashPos + (long)hashCount * 16 > available ||
            blockPos + (long)blockCount * 16 > available) return null;
        if (hiBlockPos != 0 && hiBlockPos + (long)blockCount * 2 > available) return null;

        return new HeaderInfo(headerSize, Read32(8), version, shift, hashPos, blockPos, hashCount, blockCount, hiBlockPos);
    }

    private readonly record struct BlockInfo(ulong FilePos, uint CompressedSize, uint FileSize, uint Flags);

    private static BlockInfo[] ReadTables(FileStream stream, long baseOffset, HeaderInfo header, out uint[] hashTable)
    {
        var hashBytes = ReadAt(stream, baseOffset + header.HashPos, checked((int)(header.HashCount * 16)));
        var blockBytes = ReadAt(stream, baseOffset + header.BlockPos, checked((int)(header.BlockCount * 16)));
        MpqHash.DecryptInPlace(hashBytes, MpqHash.HashString("(hash table)", 3));
        MpqHash.DecryptInPlace(blockBytes, MpqHash.HashString("(block table)", 3));

        hashTable = new uint[header.HashCount * 4];
        for (var i = 0; i < hashTable.Length; i++)
            hashTable[i] = BinaryPrimitives.ReadUInt32LittleEndian(hashBytes.AsSpan(i * 4, 4));

        var extHigh = new ushort[header.BlockCount];
        if (header.HiBlockPos != 0)
        {
            var ext = ReadAt(stream, baseOffset + header.HiBlockPos, checked((int)(header.BlockCount * 2)));
            for (var i = 0; i < extHigh.Length; i++)
                extHigh[i] = BinaryPrimitives.ReadUInt16LittleEndian(ext.AsSpan(i * 2, 2));
        }

        var blocks = new BlockInfo[header.BlockCount];
        for (var i = 0; i < blocks.Length; i++)
        {
            var at = i * 16;
            var pos = BinaryPrimitives.ReadUInt32LittleEndian(blockBytes.AsSpan(at, 4));
            var csize = BinaryPrimitives.ReadUInt32LittleEndian(blockBytes.AsSpan(at + 4, 4));
            var fsize = BinaryPrimitives.ReadUInt32LittleEndian(blockBytes.AsSpan(at + 8, 4));
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(blockBytes.AsSpan(at + 12, 4));
            blocks[i] = new BlockInfo(((ulong)extHigh[i] << 32) | pos, csize, fsize, flags);
        }
        return blocks;
    }

    private static byte[] ReadAt(FileStream stream, long offset, int count)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return Array.Empty<byte>();

        var buffer = new byte[count];
        var totalRead = 0;
        lock (stream)
        {
            if (offset >= stream.Length) return Array.Empty<byte>();
            stream.Position = offset;
            while (totalRead < count)
            {
                var read = stream.Read(buffer, totalRead, count - totalRead);
                if (read == 0) break;
                totalRead += read;
            }
        }

        // Preserve the actual byte count on a truncated archive. Callers can then
        // reject incomplete tables/blocks instead of consuming zero-filled tails.
        if (totalRead != buffer.Length) Array.Resize(ref buffer, totalRead);
        return buffer;
    }

    private byte[] ReadAt(long offset, int count) => ReadAt(_stream, offset, count);

    private long GetAbsoluteOffset(ulong archiveRelativeOffset)
    {
        if (archiveRelativeOffset > long.MaxValue)
            throw new InvalidDataException("MPQ 块偏移超过当前 .NET 文件流可寻址范围。");
        var absolute = checked(_base + (long)archiveRelativeOffset);
        if (absolute < _base || absolute > _stream.Length)
            throw new InvalidDataException($"MPQ 块偏移越界：0x{archiveRelativeOffset:X}。");
        return absolute;
    }

    /// <summary>Matches names against hash-table entries. Locale is intentionally ignored.</summary>
    private int ApplyNames(IEnumerable<string> candidates)
    {
        var changed = 0;
        foreach (var name in candidates)
        {
            var normalized = NormalizeName(name);
            if (normalized.Length is < 4 or > 240) continue;
            foreach (var variant in NameVariants(normalized))
            {
                var (h1, h2) = MpqHash.HashName(variant);
                if (!_hashLookup.TryGetValue((h1, h2), out var list)) continue;
                foreach (var entry in list)
                {
                    if (entry.NameRecovered) continue;
                    entry.OriginalName = variant;
                    entry.Name = variant;
                    entry.FileKey = null;
                    changed++;
                }
            }
        }
        return changed;
    }

    private static string NormalizeName(string name)
    {
        var value = name.Trim().Trim('"', '\'', '\0').Replace("\\\\", "\\").Replace('/', '\\');
        while (value.StartsWith('\\')) value = value[1..];
        return value;
    }

    /// <summary>
    /// Maps often reference a resource by bare filename while the hash table stores a
    /// war3mapImported\ path (or vice versa). Try both, plus a few common prefixes.
    /// </summary>
    private static IEnumerable<string> NameVariants(string name)
    {
        yield return name;
        var slash = name.LastIndexOf('\\');
        var bare = slash >= 0 ? name[(slash + 1)..] : name;
        if (bare != name && bare.Length >= 4) yield return bare;
        if (!name.StartsWith("war3mapImported\\", StringComparison.OrdinalIgnoreCase))
        {
            yield return "war3mapImported\\" + bare;
            yield return "war3mapImported\\" + name;
        }
        if (!name.StartsWith("Textures\\", StringComparison.OrdinalIgnoreCase))
            yield return "Textures\\" + bare;
        if (!name.StartsWith("UI\\", StringComparison.OrdinalIgnoreCase))
            yield return "UI\\" + bare;
    }

    /// <summary>Adds an external listfile (one path per line) and returns the number of new names.</summary>
    public int AddListFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("外部文件名库不存在。", path);
        var lines = new List<string>();
        using var reader = new StreamReader(path, Encoding.GetEncoding(936), true);
        while (reader.ReadLine() is { } line)
            if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
        return ApplyNames(lines);
    }

    private static readonly Regex PathRegex = new(
        @"[A-Za-z0-9_\\\/\-\.\(\)\[\] \u0080-\u00FF]{2,180}?\.(?:blp|mdx|mdl|tga|png|jpg|jpeg|wav|mp3|ogg|slk|txt|ini|j|jass|w3[uitqbadroe]|wtg|wts|wpm|doo|wct|imp|fdf|toc|xml|json|lua|md|dds|gif|bmp)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Recovers filenames by scanning decompressed map content (JASS/SLK/INI/MDX texture tables).
    /// Works without any external listfile and is safe to cancel.
    /// </summary>
    public int RecoverNamesFromContent(int maxPrefix = 96 * 1024, Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = _entries[i];
            var cap = entry.Kind is ResourceKind.Text or ResourceKind.Mdx or ResourceKind.W3Binary
                ? Math.Max(maxPrefix, 256 * 1024)
                : Math.Min(maxPrefix, 16 * 1024);
            try
            {
                var prefix = ReadPrefix(entry, (int)Math.Min(entry.Size, cap));
                var text = Encoding.Latin1.GetString(prefix);
                foreach (Match match in PathRegex.Matches(text))
                {
                    candidates.Add(match.Value);
                    var collapsed = match.Value.Replace("\\\\", "\\");
                    candidates.Add(collapsed);
                }
            }
            catch (Exception)
            {
                // Unreadable/encrypted blocks are still exportable, just not name sources.
            }
            if ((i & 0xFF) == 0) progress?.Invoke(i, _entries.Count);
        }
        progress?.Invoke(_entries.Count, _entries.Count);
        return ApplyNames(candidates);
    }

    /// <summary>Sniffs every block and rebuilds synthetic names with real extensions.</summary>
    public void DetectKinds(Action<int, int>? progress = null, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = _entries[i];
            byte[] head;
            try
            {
                head = ReadPrefix(entry, (int)Math.Min(entry.Size, 4096));
                entry.Kind = MpqFormatDetector.Sniff(head, entry.OriginalName);
            }
            catch (Exception)
            {
                head = Array.Empty<byte>();
                entry.Kind = ResourceKind.Unknown;
            }
            if (!entry.NameRecovered)
            {
                var ext = MpqFormatDetector.SuggestedExtension(entry.Kind, head);
                entry.Name = MpqFormatDetector.BuildSyntheticName(entry.BlockIndex, entry.Hash1, entry.Kind, ext);
            }
            if ((i & 0xFF) == 0) progress?.Invoke(i, _entries.Count);
        }
        progress?.Invoke(_entries.Count, _entries.Count);
    }

    private byte[] ReadPrefixSafe(ArchiveEntry entry, int count)
    {
        try { return ReadPrefix(entry, (int)Math.Min(entry.Size, count)); }
        catch { return Array.Empty<byte>(); }
    }

    public byte[] ReadPrefix(ArchiveEntry entry, int maxBytes)
    {
        var take = (int)Math.Min(entry.Size, Math.Max(0, maxBytes));
        using var memory = new MemoryStream(take);
        CopyTo(entry, memory, take);
        return memory.ToArray();
    }

    public byte[] ReadAllBytes(ArchiveEntry entry, int maxBytes = int.MaxValue)
    {
        var take = (int)Math.Min(entry.Size, Math.Max(0, maxBytes));
        using var memory = new MemoryStream(take);
        CopyTo(entry, memory, take);
        return memory.ToArray();
    }

    public void Extract(ArchiveEntry entry, string destination, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        CopyTo(entry, output, -1, progress, cancellationToken);
    }

    /// <summary>Streams decompressed file bytes. Only the sectors actually needed are read.</summary>
    public void CopyTo(ArchiveEntry entry, Stream destination, long take = -1,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (entry.Size == 0) return;
        if ((entry.Flags & FlagPatch) != 0)
            throw new NotSupportedException("暂不支持 MPQ patch 文件。");
        if ((entry.Flags & FlagDeleteMarker) != 0)
            throw new InvalidDataException("该块是删除标记，不含数据。");
        if ((entry.Flags & FlagImplode) != 0 && (entry.Flags & FlagCompress) == 0)
            throw new NotSupportedException("PKWARE implode 压缩尚未实现。");

        var wanted = take < 0 ? entry.Size : (uint)Math.Min(entry.Size, take);
        if (wanted == 0) return;

        if ((entry.Flags & (FlagCompress | FlagImplode)) == 0)
        {
            CopyRaw(entry, destination, wanted, progress, cancellationToken);
            return;
        }

        if ((entry.Flags & FlagSingleUnit) != 0)
        {
            CopySingleUnit(entry, destination, wanted, progress, cancellationToken);
            return;
        }

        CopySectored(entry, destination, wanted, progress, cancellationToken);
    }

    private void CopyRaw(ArchiveEntry entry, Stream destination, uint wanted,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var bufferSize = 1 << 16; // divisible by four for the MPQ block cipher
        var buffer = new byte[bufferSize];
        long remaining = wanted;
        var offset = GetAbsoluteOffset(entry.FilePos);
        var encrypted = IsEncrypted(entry);
        uint seed1 = 0, seed2 = 0xEEEEEEEE;
        if (encrypted)
        {
            seed1 = GetRawFileKey(entry);
        }

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, remaining);
            // Keep all intermediate chunks DWORD-aligned so cipher state carries forward.
            if (encrypted && remaining > count) count &= ~3;
            var chunk = ReadAt(offset, count);
            if (encrypted) MpqHash.DecryptInPlace(chunk, ref seed1, ref seed2);
            destination.Write(chunk, 0, chunk.Length);
            offset += chunk.Length;
            remaining -= chunk.Length;
            progress?.Report(wanted - remaining);
            if (chunk.Length != count)
                throw new EndOfStreamException($"块 {entry.BlockIndex} 数据被截断：需要读取 {count} B，实际读取 {chunk.Length} B。");
        }
    }

    private static uint GetRawFileKey(ArchiveEntry entry)
    {
        if (entry.OriginalName is not { Length: > 0 } name)
            throw new InvalidDataException($"块 {entry.BlockIndex} 是加密的未压缩文件，缺少原始文件名，无法安全推导密钥。");
        var normalized = name.Replace('/', '\\');
        var slash = normalized.LastIndexOf('\\');
        var basename = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        var key = MpqHash.HashString(basename, 3);
        if ((entry.Flags & FlagFixKey) != 0)
            key = unchecked((key + (uint)entry.FilePos) ^ entry.Size);
        return key;
    }

    private void CopySingleUnit(ArchiveEntry entry, Stream destination, uint wanted,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var raw = ReadAt(_base + (long)entry.FilePos, checked((int)entry.CompressedSize));
        if (IsEncrypted(entry))
        {
            var key = GetFileKey(entry, null);
            MpqHash.DecryptInPlace(raw, key);
        }
        byte[] data;
        if (entry.CompressedSize < entry.Size) data = Inflate(raw, checked((int)entry.Size));
        else data = raw;
        if (data.Length != entry.Size)
            throw new InvalidDataException($"单块解压长度不匹配：期望 {entry.Size} B，实际 {data.Length} B。");
        WriteChunk(data, destination, wanted, progress, cancellationToken);
    }

    private void CopySectored(ArchiveEntry entry, Stream destination, uint wanted,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var encrypted = IsEncrypted(entry);
        var sectorCount = (int)((entry.Size + _sectorSize - 1) / _sectorSize);
        var tableLength = sectorCount + ((entry.Flags & FlagSectorCrc) != 0 ? 2 : 1);

        var tableBytes = ReadAt(_base + (long)entry.FilePos, tableLength * 4);
        uint? key = null;
        if (encrypted)
        {
            key = GetFileKey(entry, tableBytes);
            var copy = (byte[])tableBytes.Clone();
            MpqHash.DecryptInPlace(copy, key.Value - 1);
            tableBytes = copy;
        }
        var offsets = new uint[tableLength];
        for (var i = 0; i < tableLength; i++)
            offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(tableBytes.AsSpan(i * 4, 4));
        if (offsets[0] < tableLength * 4 || offsets[^1] > entry.CompressedSize)
            throw new InvalidDataException($"块 {entry.BlockIndex} 的扇区表无效。");

        long written = 0;
        for (var sector = 0; sector < sectorCount && written < wanted; sector++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = offsets[sector];
            var end = offsets[sector + 1];
            if (end < start || end > entry.CompressedSize)
                throw new InvalidDataException($"块 {entry.BlockIndex} 扇区 {sector} 越界。");
            var expected = (int)Math.Min(_sectorSize, entry.Size - (uint)sector * _sectorSize);
            var data = ReadAt(checked(GetAbsoluteOffset(entry.FilePos) + start), checked((int)(end - start)));
            if (encrypted) MpqHash.DecryptInPlace(data, key!.Value + (uint)sector);
            if (data.Length < expected) data = Inflate(data, expected);
            if (data.Length != expected)
                throw new InvalidDataException($"块 {entry.BlockIndex} 扇区 {sector} 解压长度不匹配：期望 {expected} B，实际 {data.Length} B。");
            var need = (int)Math.Min(expected, wanted - written);
            if (need > 0)
            {
                destination.Write(data, 0, need);
                written += need;
                progress?.Report(written);
            }
        }
    }

    private static void WriteChunk(byte[] data, Stream destination, uint wanted,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = (int)Math.Min(data.Length, wanted);
        destination.Write(data, 0, count);
        progress?.Report(count);
    }

    private static bool IsEncrypted(ArchiveEntry entry) => (entry.Flags & FlagEncrypted) != 0;

    /// <summary>
    /// Obtains the effective file key: derive it from a recovered filename when possible,
    /// otherwise brute-force it from the known-plaintext first DWORD of the sector table.
    /// </summary>
    private uint GetFileKey(ArchiveEntry entry, byte[]? tableBytes)
    {
        if (entry.FileKey is uint cached) return cached;
        if (entry.NameRecovered && entry.OriginalName is { } name)
        {
            var plainName = name.Replace('/', '\\');
            var slash = plainName.LastIndexOf('\\');
            if (slash >= 0) plainName = plainName[(slash + 1)..];
            var baseKey = MpqHash.HashString(plainName, 3);
            var key = (entry.Flags & FlagFixKey) != 0
                ? unchecked((baseKey + (uint)entry.FilePos) ^ entry.Size)
                : baseKey;
            // Single-unit files have no sector table to validate against.
            if ((entry.Flags & FlagSingleUnit) != 0) return CacheKey(entry, key);
            if (ValidateTableKey(entry, key - 1)) return CacheKey(entry, key);
        }
        if ((entry.Flags & FlagSingleUnit) != 0)
            throw new InvalidDataException($"块 {entry.BlockIndex} 是加密的单块文件，需要原始文件名才能解密。");

        var sectorCount = (int)((entry.Size + _sectorSize - 1) / _sectorSize);
        var tableLength = sectorCount + ((entry.Flags & FlagSectorCrc) != 0 ? 2 : 1);
        var first = tableBytes is { Length: >= 4 }
            ? BinaryPrimitives.ReadUInt32LittleEndian(tableBytes)
            : BinaryPrimitives.ReadUInt32LittleEndian(ReadAt(_base + (long)entry.FilePos, 4));
        var found = MpqHash.RecoverFileKey(first, (uint)(tableLength * 4), t => ValidateTableKey(entry, t));
        if (found is uint effective) return CacheKey(entry, effective);
        throw new InvalidDataException($"无法推导块 {entry.BlockIndex} 的加密密钥（可尝试导入 listfile 恢复文件名）。");
    }

    private static uint CacheKey(ArchiveEntry entry, uint key)
    {
        entry.FileKey = key;
        return key;
    }

    private bool ValidateTableKey(ArchiveEntry entry, uint tableKey)
    {
        if ((entry.Flags & FlagSingleUnit) != 0 || entry.Size == 0) return false;
        var sectorCount = (int)((entry.Size + _sectorSize - 1) / _sectorSize);
        var tableLength = sectorCount + ((entry.Flags & FlagSectorCrc) != 0 ? 2 : 1);
        var bytes = (byte[])ReadAt(_base + (long)entry.FilePos, tableLength * 4).Clone();
        MpqHash.DecryptInPlace(bytes, tableKey);
        var first = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (first != tableLength * 4) return false;
        uint previous = first;
        for (var i = 1; i < tableLength; i++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4));
            if (value < previous || value > entry.CompressedSize) return false;
            previous = value;
        }
        return true;
    }

    /// <summary>
    /// MPQ sectors may use zlib, raw deflate, or a one-byte compression mask.
    /// Try every variant instead of relying on a single convention.
    /// </summary>
    private static byte[] Inflate(byte[] source, int expected)
    {
        var attempts = new (int Offset, bool ZLib)[]
        {
            (0, true), (1, true), (0, false), (1, false)
        };
        Exception? last = null;
        foreach (var (offset, zlib) in attempts)
        {
            if (offset >= source.Length) continue;
            try
            {
                using var input = new MemoryStream(source, offset, source.Length - offset, false);
                using var inflater = zlib
                    ? (Stream)new ZLibStream(input, CompressionMode.Decompress, true)
                    : new DeflateStream(input, CompressionMode.Decompress, true);
                using var output = new MemoryStream(expected);
                inflater.CopyTo(output);
                if (output.Length == expected) return output.ToArray();
                if (output.Length > expected)
                    throw new InvalidDataException($"解压长度异常：期望 {expected} B，实际 {output.Length} B。");
            }
            catch (Exception ex) { last = ex; }
        }
        throw new InvalidDataException("扇区解压失败：" + (last?.Message ?? "数据格式不支持"));
    }

    private static readonly string[] StandardNames =
    {
        "(listfile)", "(attributes)", "(signature)", "(patch_metadata)", "(user data)",
        "war3map.w3i", "war3map.w3e", "war3map.wpm", "war3map.doo", "war3mapUnits.doo",
        "war3map.w3r", "war3map.w3c", "war3map.w3s", "war3map.w3o", "war3map.wct",
        "war3map.wtg", "war3map.wts", "war3map.j", "war3map.lua", "war3map.imp",
        "war3map.w3u", "war3map.w3t", "war3map.w3a", "war3map.w3b", "war3map.w3d",
        "war3map.w3q", "war3mapMap.blp", "war3mapPreview.tga", "war3mapMisc.txt",
        "war3mapSkin.txt", "war3mapExtra.txt", "war3mapTemplate.j", "conversation.json",
        "Scripts\\war3map.j", "scripts\\war3map.j", "war3map.w3z", "war3map.imp",
        "ReplaceableTextures\\CommandButtons\\BTNSelectHeroOn.blp",
        "ReplaceableTextures\\WorldEditUI\\DoodadPlaceholder.blp"
    };

    // ---------- map restoration (writer) ----------

    public sealed record RestoreResult(
        string TargetPath, int OriginalBlocks, int TotalBlocks, int NamedBlocks,
        int FixedLocales, int DroppedEditorFiles, long OutputSize, string ListFileText);

    /// <summary>Builds the (listfile) content: every recovered name plus stable synthetic names.</summary>
    public string BuildListFile(IEnumerable<string>? extraNames = null)
    {
        // A listfile is a dictionary of real archive paths, not a display-name manifest.
        // Synthetic _unnamed/... aliases do not hash to the original MPQ hash entries,
        // so writing them here would advertise paths that cannot actually be opened.
        var names = new List<string> { "(listfile)" };
        foreach (var entry in _entries)
            if (entry.OriginalName is { Length: > 0 } original)
                names.Add(original);
        if (extraNames is not null) names.AddRange(extraNames);
        return string.Join("\r\n", names.Distinct(StringComparer.OrdinalIgnoreCase)) + "\r\n";
    }

    /// <summary>
    /// Rebuilds a World-Editor-friendly map without recompressing any file:
    ///  - resets tampered hash-table locales (0xFF000000 -&gt; 0) so WE/StormLib can find files by name
    ///  - inserts a real (listfile) containing all recovered names
    ///  - appends a new block table with one extra block for (listfile)
    /// All original file data stays byte-identical, so resources and terrain survive unchanged.
    /// </summary>
    public RestoreResult RestoreTo(string targetPath, IEnumerable<string>? extraNames = null,
        bool dropClearedEditorFiles = true, CancellationToken cancellationToken = default)
    {
        var listText = BuildListFile(extraNames);
        byte[] listBytes;
        try { listBytes = Encoding.GetEncoding(936).GetBytes(listText); }
        catch { listBytes = Encoding.UTF8.GetBytes(listText); }

        var hashCount = _header.HashCount;
        var blockCount = _header.BlockCount;
        long sourceLength;
        lock (_sync) sourceLength = _stream.Length;

        var listOffset = sourceLength - _base;                 // relative to MPQ header
        var blockTableOffset = listOffset + listBytes.Length;
        var newArchiveSize = blockTableOffset + (long)(blockCount + 1) * 16;
        if (newArchiveSize > uint.MaxValue || blockTableOffset > uint.MaxValue || listOffset > uint.MaxValue)
            throw new InvalidDataException("档案超过 4 GB，当前还原器只支持 32 位 MPQ 位置。");

        // A protected map often leaves 1-byte invalid wtg/wct/wts files. World Editor
        // chokes on them; removing their hash entries makes WE create clean empty ones.
        var editorEmptyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "war3map.wtg", "war3map.wct", "war3map.wts", "war3map.w3r", "war3map.w3c"
        };
        var droppedBlocks = new HashSet<uint>();
        if (dropClearedEditorFiles)
            foreach (var entry in _entries)
                if (entry.NameRecovered && entry.OriginalName is { } n && editorEmptyNames.Contains(n) && entry.Size <= 8)
                    droppedBlocks.Add((uint)entry.BlockIndex);

        // --- hash table: keep name hashes and block indices, neutralise locale ---
        var hash = (uint[])_rawHashTable.Clone();
        var fixedLocales = 0;
        var droppedEntries = 0;
        for (var i = 0; i < hash.Length; i += 4)
        {
            var block = hash[i + 3];
            if (block == HashFree || block == HashDeleted || block >= blockCount) continue;
            if (droppedBlocks.Contains(block)) { hash[i + 3] = HashDeleted; droppedEntries++; continue; }
            // Preserve legitimate locale IDs (e.g. 0x0409). Only normalize values
            // with reserved high bits set, such as the observed 0xFF000000 tampering.
            if ((hash[i + 2] & 0xFFFF0000) != 0)
            {
                hash[i + 2] = 0;
                fixedLocales++;
            }
        }
        var (lh1, lh2) = MpqHash.HashName("(listfile)");
        var slotCount = (int)hashCount;
        // MPQ uses hashType 0 for the initial hash-table slot, and 1/2 for the stored name hashes.
        var startSlot = (int)(MpqHash.HashString("(listfile)", 0) % (uint)slotCount);
        var inserted = false;
        for (var probe = 0; probe < slotCount; probe++)
        {
            var at = ((startSlot + probe) % slotCount) * 4;
            if (hash[at + 3] != HashFree && hash[at + 3] != HashDeleted) continue;
            hash[at] = lh1; hash[at + 1] = lh2; hash[at + 2] = 0; hash[at + 3] = blockCount;
            inserted = true;
            break;
        }
        if (!inserted) throw new InvalidDataException("哈希表没有空槽，无法插入 (listfile)。");

        var hashBytes = new byte[hash.Length * 4];
        for (var i = 0; i < hash.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(hashBytes.AsSpan(i * 4, 4), hash[i]);
        MpqHash.EncryptInPlace(hashBytes, MpqHash.HashString("(hash table)", 3));

        // --- block table: original entries untouched + (listfile) as plain single-unit file ---
        var blockBytes = new byte[(blockCount + 1) * 16];
        for (var i = 0; i < blockCount; i++)
        {
            var b = _rawBlocks[i];
            var at = i * 16;
            BinaryPrimitives.WriteUInt32LittleEndian(blockBytes.AsSpan(at, 4), (uint)b.FilePos);
            BinaryPrimitives.WriteUInt32LittleEndian(blockBytes.AsSpan(at + 4, 4), b.CompressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(blockBytes.AsSpan(at + 8, 4), b.FileSize);
            BinaryPrimitives.WriteUInt32LittleEndian(blockBytes.AsSpan(at + 12, 4), b.Flags);
        }
        var extra = (int)blockCount * 16;
        BinaryPrimitives.WriteUInt32LittleEndian(blockBytes.AsSpan(extra, 4), (uint)listOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(blockBytes.AsSpan(extra + 4, 4), (uint)listBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(blockBytes.AsSpan(extra + 8, 4), (uint)listBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(blockBytes.AsSpan(extra + 12, 4), FlagExists | FlagSingleUnit);
        MpqHash.EncryptInPlace(blockBytes, MpqHash.HashString("(block table)", 3));

        var headerSize = (int)Math.Min(_header.HeaderSize, 0xD0);
        var headerBytes = new byte[headerSize];
        lock (_sync)
        {
            _stream.Position = _base;
            _ = _stream.Read(headerBytes, 0, headerSize);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(headerBytes.AsSpan(0x08, 4), (uint)newArchiveSize);
        BinaryPrimitives.WriteUInt32LittleEndian(headerBytes.AsSpan(0x14, 4), (uint)blockTableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(headerBytes.AsSpan(0x1C, 4), blockCount + 1);
        if (headerSize >= 0x2C)
        {
            // New block table is in the low 4 GB; clear its high word. Hash table position is unchanged.
            BinaryPrimitives.WriteUInt16LittleEndian(headerBytes.AsSpan(0x2A, 2), 0);
        }
        if (headerSize >= 0x30)
        {
            // MPQ v3/v4 carry a 64-bit archive size.
            BinaryPrimitives.WriteUInt64LittleEndian(headerBytes.AsSpan(0x2C, 8), (ulong)newArchiveSize);
        }

        var buffer = new byte[1 << 20];
        using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
        {
            lock (_sync)
            {
                _stream.Position = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = _stream.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    output.Write(buffer, 0, read);
                }
            }
            output.Position = _base;
            output.Write(headerBytes, 0, headerSize);
            output.Position = _base + _header.HashPos;
            output.Write(hashBytes, 0, hashBytes.Length);
            output.Position = output.Length;
            output.Write(listBytes, 0, listBytes.Length);
            output.Write(blockBytes, 0, blockBytes.Length);
        }

        return new RestoreResult(targetPath, (int)blockCount, (int)blockCount + 1,
            _entries.Count(x => x.NameRecovered), fixedLocales, droppedEntries,
            new FileInfo(targetPath).Length, listText);
    }

    public void Dispose() => _stream.Dispose();
}
