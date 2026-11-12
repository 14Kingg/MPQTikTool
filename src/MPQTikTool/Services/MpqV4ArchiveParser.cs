using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MPQTikTool.Services;

/// <summary>
/// Strict reader for the MPQ format-v4 header and HET/BET extended tables.
/// Table bytes are validated before they are decrypted or interpreted.
/// </summary>
public static class MpqV4ArchiveParser
{
    public const int HeaderLength = 0xD0;
    private const uint HetSignature = 0x1A544548;
    private const uint BetSignature = 0x1A544542;
    private const int HetHeaderBodySize = 32;
    private const int BetHeaderBodySize = 76;
    private const int MaxExtendedTableSize = 0x00100000;

    public sealed record Header(
        uint HeaderSize, ulong ArchiveSize, ushort FormatVersion, ushort SectorShift,
        ulong HiBlockTableOffset, ushort HashTableOffsetHigh, ushort BlockTableOffsetHigh,
        ulong BetTableOffset, ulong HetTableOffset, ulong HashTableStoredSize,
        ulong BlockTableStoredSize, ulong HiBlockTableStoredSize,
        ulong HetTableStoredSize, ulong BetTableStoredSize, uint RawChunkSize,
        string BlockTableMd5, string HashTableMd5, string HiBlockTableMd5,
        string BetTableMd5, string HetTableMd5, string HeaderMd5,
        bool HeaderMd5Valid);

    public sealed record BlockEntry(int Index, ulong FileOffset, uint FileSize,
        uint CompressedSize, uint Flags, ulong? NameHash);

    public sealed record TableInfo(string Kind, ulong Offset, ulong StoredSize,
        uint Version, uint DeclaredDataSize, uint TableSize, string Md5,
        bool Md5Valid, bool IsCompressed, bool IsEncrypted);

    public sealed record Result(Header MpqHeader, IReadOnlyList<BlockEntry> Blocks,
        TableInfo? HetInfo, TableInfo? BetInfo, IReadOnlyList<string> Diagnostics);

    public static Result Read(string path, long headerOffset = 0)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(stream, headerOffset);
    }

    public static Result Read(FileStream stream, long headerOffset)
    {
        if (headerOffset < 0 || headerOffset > stream.Length - HeaderLength)
            throw new InvalidDataException("MPQ v4 Header 超出文件范围。");

        var rawHeader = ReadExact(stream, headerOffset, HeaderLength);
        if (!rawHeader.AsSpan(0, 4).SequenceEqual(new byte[] { (byte)'M', (byte)'P', (byte)'Q', 0x1A }))
            throw new InvalidDataException("无效的 MPQ 签名。");

        uint U32(int p) => BinaryPrimitives.ReadUInt32LittleEndian(rawHeader.AsSpan(p, 4));
        ushort U16(int p) => BinaryPrimitives.ReadUInt16LittleEndian(rawHeader.AsSpan(p, 2));
        ulong U64(int p) => BinaryPrimitives.ReadUInt64LittleEndian(rawHeader.AsSpan(p, 8));

        var headerSize = U32(4);
        var version = U16(0x0C);
        if (version != 3 || headerSize != HeaderLength)
            throw new NotSupportedException($"该解析器要求 MPQ v4（version=3、headerSize=0xD0），实际 version={version}、headerSize=0x{headerSize:X}。");

        var archiveSize = U64(0x2C);
        if (archiveSize < HeaderLength || archiveSize > (ulong)(stream.Length - headerOffset))
            throw new InvalidDataException("MPQ v4 ArchiveSize64 超出文件范围。");

        var expectedHeaderMd5 = rawHeader.AsSpan(0xC0, 16);
        var actualHeaderMd5 = MD5.HashData(rawHeader.AsSpan(0, 0xC0));
        var headerMd5Valid = CryptographicOperations.FixedTimeEquals(expectedHeaderMd5, actualHeaderMd5);
        if (!headerMd5Valid)
            throw new InvalidDataException($"MPQ v4 Header MD5 校验失败：期望 {Convert.ToHexString(expectedHeaderMd5)}，实际 {Convert.ToHexString(actualHeaderMd5)}。");

        var header = new Header(
            headerSize, archiveSize, version, U16(0x0E),
            U64(0x20), U16(0x28), U16(0x2A),
            U64(0x34), U64(0x3C), U64(0x44), U64(0x4C),
            U64(0x54), U64(0x5C), U64(0x64), U32(0x6C),
            Convert.ToHexString(rawHeader.AsSpan(0x70, 16)),
            Convert.ToHexString(rawHeader.AsSpan(0x80, 16)),
            Convert.ToHexString(rawHeader.AsSpan(0x90, 16)),
            Convert.ToHexString(rawHeader.AsSpan(0xA0, 16)),
            Convert.ToHexString(rawHeader.AsSpan(0xB0, 16)),
            Convert.ToHexString(expectedHeaderMd5), true);

        var diagnostics = new List<string> { "MPQ v4 Header MD5：通过" };
        ulong parsedHetBitsMask = 0;
        TableInfo? hetInfo = null;
        TableInfo? betInfo = null;
        IReadOnlyList<BlockEntry> blocks = Array.Empty<BlockEntry>();
        Dictionary<int, ulong> nameHashes = new();

        if (header.HetTableOffset != 0 || header.HetTableStoredSize != 0)
        {
            var parsedHet = ReadHet(stream, headerOffset, header);
            hetInfo = parsedHet.Info;
            nameHashes = parsedHet.NameHashes;
            parsedHetBitsMask = parsedHet.NameHash2Bits == 64 ? ulong.MaxValue : ((1UL << parsedHet.NameHash2Bits) - 1);
            diagnostics.Add($"HET：{parsedHet.Info.TableSize} B，MD5 {(parsedHet.Info.Md5Valid ? "通过" : "失败")}，{nameHashes.Count} 个名称哈希索引");
        }

        if (header.BetTableOffset != 0 || header.BetTableStoredSize != 0)
        {
            var parsedBet = ReadBet(stream, headerOffset, header);
            betInfo = parsedBet.Info;
            var lowerMask = parsedHetBitsMask;
            blocks = parsedBet.Blocks.Select(b =>
                b with { NameHash = nameHashes.TryGetValue(b.Index, out var hash)
                    ? hash | ((b.NameHash ?? 0UL) & lowerMask)
                    : null }).ToArray();
            diagnostics.Add($"BET：{parsedBet.Info.TableSize} B，MD5 {(parsedBet.Info.Md5Valid ? "通过" : "失败")}，{blocks.Count} 个 Block 条目");
        }
        else if (hetInfo is not null)
        {
            throw new InvalidDataException("检测到 HET 表，但缺少对应 BET 表，无法重建 Block 条目。");
        }

        return new Result(header, blocks, hetInfo, betInfo, diagnostics);
    }

    private sealed record ParsedHet(TableInfo Info, Dictionary<int, ulong> NameHashes, int NameHash2Bits);
    private sealed record ParsedBet(TableInfo Info, IReadOnlyList<BlockEntry> Blocks);

    private static ParsedHet ReadHet(FileStream stream, long baseOffset, Header h)
    {
        var raw = ReadExtendedTable(stream, baseOffset, h.ArchiveSize, h.HetTableOffset, h.HetTableStoredSize,
            HetSignature, h.HetTableMd5, "(hash table)");
        var body = raw.Body;
        if (body.Length < HetHeaderBodySize) throw new InvalidDataException("HET Header 被截断。");
        uint U(int p) => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(p, 4));

        var tableSize = U(0);
        var entryCount = U(4);
        var totalCount = U(8);
        var nameHashBits = U(12);
        var indexBitsTotal = U(16);
        var indexBitsExtra = U(20);
        var indexBits = U(24);
        var indexTableSize = U(28);

        if (entryCount > totalCount || totalCount > 1_000_000 ||
            nameHashBits is < 8 or > 64 || indexBitsTotal is < 1 or > 32 ||
            indexBits > indexBitsTotal || indexBitsExtra > indexBitsTotal ||
            (ulong)HetHeaderBodySize + totalCount + indexTableSize != tableSize ||
            tableSize > body.Length)
            throw new InvalidDataException("HET 表头字段或数组长度不合法。");

        var expectedIndexBytes = ((ulong)totalCount * indexBitsTotal + 7) / 8;
        if (expectedIndexBytes != indexTableSize)
            throw new InvalidDataException("HET BET 索引位数组长度与声明不匹配。");

        var nameBytes = body.AsSpan(HetHeaderBodySize, checked((int)totalCount));
        var indexBytes = body.AsSpan(checked(HetHeaderBodySize + (int)totalCount), checked((int)indexTableSize));
        var names = new Dictionary<int, ulong>();
        var nameHash2Bits = nameHashBits - 8;
        for (var i = 0; i < totalCount; i++)
        {
            var hash1 = nameBytes[(int)i];
            if (hash1 == 0xFF) continue;
            var betIndex = ReadBits(indexBytes, checked((ulong)i * indexBitsTotal), checked((int)indexBits));
            if (betIndex > int.MaxValue) throw new InvalidDataException("HET BET 索引超出支持范围。");
            // The lower hash bits are filled from BET after that table is decoded.
            names[(int)betIndex] = (ulong)hash1 << checked((int)nameHash2Bits);
        }

        return new ParsedHet(
            new TableInfo("HET", h.HetTableOffset, h.HetTableStoredSize, raw.Version,
                raw.DeclaredDataSize, tableSize, raw.Md5, raw.Md5Present, raw.Md5Valid, raw.IsCompressed, true),
            names, checked((int)nameHash2Bits));
    }

    private static ParsedBet ReadBet(FileStream stream, long baseOffset, Header h)
    {
        var raw = ReadExtendedTable(stream, baseOffset, h.ArchiveSize, h.BetTableOffset, h.BetTableStoredSize,
            BetSignature, h.BetTableMd5, "(block table)");
        var body = raw.Body;
        if (body.Length < BetHeaderBodySize) throw new InvalidDataException("BET Header 被截断。");
        uint U(int p) => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(p, 4));

        var tableSize = U(0);
        var entryCount = U(4);
        var unknown08 = U(8);
        var entryBits = U(12);
        var posIndex = U(16);
        var sizeIndex = U(20);
        var cmpIndex = U(24);
        var flagIndex = U(28);
        var unknownIndex = U(32);
        var posBits = U(36);
        var sizeBits = U(40);
        var cmpBits = U(44);
        var flagBits = U(48);
        var unknownBits = U(52);
        var nameHashTotalBits = U(56);
        var nameHashExtraBits = U(60);
        var nameHashBits = U(64);
        var nameHashBytes = U(68);
        var flagCount = U(72);

        if (entryCount > 1_000_000 || entryBits == 0 || entryBits > 512 ||
            posBits > 64 || sizeBits > 32 || cmpBits > 32 || flagBits > 32 ||
            (ulong)posIndex + posBits > entryBits ||
            (ulong)sizeIndex + sizeBits > entryBits ||
            (ulong)cmpIndex + cmpBits > entryBits ||
            (ulong)flagIndex + flagBits > entryBits ||
            (ulong)unknownIndex + unknownBits > entryBits ||
            nameHashBits > nameHashTotalBits || nameHashExtraBits > nameHashTotalBits ||
            nameHashTotalBits > 64 || flagCount > 256)
            throw new InvalidDataException("BET 位字段定义不合法。");

        var flagBytes = checked((int)flagCount * 4);
        var fileBitsBytes = checked((int)(((ulong)entryCount * entryBits + 7) / 8));
        var nameBitsBytes = checked((int)(((ulong)entryCount * nameHashTotalBits + 7) / 8));
        var calculatedTableSize = checked((ulong)BetHeaderBodySize + (ulong)flagBytes + (ulong)fileBitsBytes + (ulong)nameBitsBytes);
        if (calculatedTableSize != tableSize || tableSize > body.Length || nameHashBytes != nameBitsBytes)
            throw new InvalidDataException("BET 表长度与 flags、文件位数组、NameHash2 数组不匹配。");

        var cursor = BetHeaderBodySize;
        var flags = new uint[flagCount];
        for (var i = 0; i < flags.Length; i++)
            flags[i] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(cursor + i * 4, 4));
        cursor += flagBytes;
        var fileBits = body.AsSpan(cursor, fileBitsBytes);
        cursor += fileBitsBytes;
        var nameBits = body.AsSpan(cursor, nameBitsBytes);

        var blocks = new BlockEntry[entryCount];
        for (var i = 0; i < blocks.Length; i++)
        {
            var bitBase = checked((ulong)i * entryBits);
            var filePos = ReadBits(fileBits, bitBase + posIndex, checked((int)posBits));
            var fileSize = checked((uint)ReadBits(fileBits, bitBase + sizeIndex, checked((int)sizeBits)));
            var compressedSize = checked((uint)ReadBits(fileBits, bitBase + cmpIndex, checked((int)cmpBits)));
            var flag = flagCount == 0 ? 0u : ReadBits(fileBits, bitBase + flagIndex, checked((int)flagBits)) is var flagSlot && flagSlot < flags.Length
                ? flags[(int)flagSlot] : throw new InvalidDataException($"BET Block {i} 的 FlagIndex 越界。");
            var nameHash2 = nameHashBits == 0 ? 0UL :
                ReadBits(nameBits, checked((ulong)i * nameHashTotalBits), checked((int)nameHashBits));
            blocks[i] = new BlockEntry(i, filePos, fileSize, compressedSize, flag, nameHash2);
        }

        return new ParsedBet(
            new TableInfo("BET", h.BetTableOffset, h.BetTableStoredSize, raw.Version,
                raw.DeclaredDataSize, tableSize, raw.Md5, raw.Md5Valid, raw.IsCompressed, true),
            blocks);
    }

    private sealed record ExtendedData(uint Version, uint DeclaredDataSize, byte[] Body,
        string Md5, bool Md5Present, bool Md5Valid, bool IsCompressed);

    private static ExtendedData ReadExtendedTable(FileStream stream, long baseOffset, ulong archiveSize,
        ulong relativeOffset, ulong storedSize, uint expectedSignature, string expectedMd5, string keyName)
    {
        if (relativeOffset < HeaderLength || storedSize < 12 || storedSize >= MaxExtendedTableSize ||
            relativeOffset > archiveSize || storedSize > archiveSize - relativeOffset)
            throw new InvalidDataException("HET/BET 表偏移或压缩长度超出 MPQ v4 ArchiveSize64。");
        var absolute = checked((ulong)baseOffset + relativeOffset);
        if (absolute > (ulong)stream.Length || storedSize > (ulong)stream.Length - absolute)
            throw new InvalidDataException("HET/BET 表超出文件范围。");
        var bytes = ReadExact(stream, checked((long)absolute), checked((int)storedSize));
        var md5 = Convert.ToHexString(MD5.HashData(bytes));
        var md5Present = !expectedMd5.All(c => c == '0');
        var md5Valid = !md5Present || string.Equals(md5, expectedMd5, StringComparison.OrdinalIgnoreCase);
        if (!md5Valid)
            throw new InvalidDataException($"{(expectedSignature == HetSignature ? "HET" : "BET")} 原始表 MD5 校验失败。");

        var signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        var declaredDataSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        if (signature != expectedSignature || version != 1 || declaredDataSize < (expectedSignature == HetSignature ? HetHeaderBodySize : BetHeaderBodySize))
            throw new InvalidDataException("HET/BET 扩展表签名、版本或 DataSize 不合法。");

        var encrypted = bytes.AsSpan(12).ToArray();
        MpqHash.DecryptInPlace(encrypted, MpqHash.HashString(keyName, 3));
        var isCompressed = (ulong)declaredDataSize + 12 > storedSize;
        byte[] body;
        if (!isCompressed)
        {
            if (encrypted.Length != declaredDataSize)
                throw new InvalidDataException("HET/BET 未压缩数据长度与 DataSize 不匹配。");
            body = encrypted;
        }
        else
        {
            if (encrypted.Length < 2 || encrypted[0] != 0x02)
                throw new NotSupportedException($"HET/BET 使用未支持的表压缩掩码 0x{(encrypted.Length == 0 ? 0 : encrypted[0]):X2}。");
            using var input = new MemoryStream(encrypted, 1, encrypted.Length - 1, false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(checked((int)declaredDataSize));
            zlib.CopyTo(output);
            if (output.Length != declaredDataSize)
                throw new InvalidDataException($"HET/BET 解压长度错误：期望 {declaredDataSize}，实际 {output.Length}。");
            body = output.ToArray();
        }

        return new ExtendedData(version, declaredDataSize, body, md5, md5Present, md5Valid, isCompressed);
    }

    private static ulong ReadBits(ReadOnlySpan<byte> bytes, ulong bitOffset, int bitCount)
    {
        if (bitCount == 0) return 0;
        if (bitCount > 64 || bitOffset > (ulong)bytes.Length * 8 ||
            (ulong)bitCount > (ulong)bytes.Length * 8 - bitOffset)
            throw new InvalidDataException("位字段读取超出数据范围。");
        ulong value = 0;
        for (var bit = 0; bit < bitCount; bit++)
        {
            var sourceBit = bitOffset + (uint)bit;
            if ((bytes[(int)(sourceBit >> 3)] & (1 << (int)(sourceBit & 7))) != 0)
                value |= 1UL << bit;
        }
        return value;
    }

    private static byte[] ReadExact(FileStream stream, long offset, int count)
    {
        if (count < 0 || offset < 0 || offset > stream.Length || count > stream.Length - offset)
            throw new EndOfStreamException("MPQ 数据块被截断。");
        var bytes = new byte[count];
        lock (stream)
        {
            stream.Position = offset;
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(bytes, total, count - total);
                if (read == 0) throw new EndOfStreamException("MPQ 数据块读取不完整。");
                total += read;
            }
        }
        return bytes;
    }
}
