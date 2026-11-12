using System.Buffers.Binary;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using MPQTikTool.Services;


Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var checks = new List<(string Name, Action Run)>
{
    ("MPQ hash determinism", () =>
    {
        var a = MpqHash.HashString("(hash table)", 3);
        var b = MpqHash.HashString("(hash table)", 3);
        Assert(a == b, "same input must produce same hash");
    }),
    ("MPQ slash normalization", () =>
    {
        Assert(MpqHash.HashString("war3mapImported\\test.blp", 1) ==
               MpqHash.HashString("war3mapImported/test.blp", 1), "slash variants must hash equally");
    }),
    ("MPQ cipher encrypt/decrypt round-trip", () =>
    {
        var plain = RandomNumberGenerator.GetBytes(4096);
        var aligned = plain.Length / 4 * 4;
        Array.Resize(ref plain, aligned);
        var cipher = (byte[])plain.Clone();
        MpqHash.EncryptInPlace(cipher, 0x12345678);
        Assert(!cipher.AsSpan().SequenceEqual(plain), "ciphertext should differ from plaintext");
        MpqHash.DecryptInPlace(cipher, 0x12345678);
        Assert(cipher.AsSpan().SequenceEqual(plain), "decrypted bytes must equal original");
    }),
    ("MPQ cipher streaming state", () =>
    {
        var plain = RandomNumberGenerator.GetBytes(8192);
        Array.Resize(ref plain, plain.Length / 4 * 4);
        var whole = (byte[])plain.Clone();
        MpqHash.EncryptInPlace(whole, 0x87654321);
        var streamed = (byte[])whole.Clone();
        uint seed1 = 0x87654321, seed2 = 0xEEEEEEEE;
        MpqHash.DecryptInPlace(streamed.AsSpan(0, 2048), ref seed1, ref seed2);
        MpqHash.DecryptInPlace(streamed.AsSpan(2048, 4096), ref seed1, ref seed2);
        MpqHash.DecryptInPlace(streamed.AsSpan(6144), ref seed1, ref seed2);
        Assert(streamed.AsSpan().SequenceEqual(plain), "chunked decryption must match whole-file decryption");
    }),
    ("MPQ cipher rejects no aligned bytes silently", () =>
    {
        var data = new byte[] { 1, 2, 3 };
        MpqHash.DecryptInPlace(data, 0x11111111);
        Assert(data.AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }), "sub-DWORD tail remains untouched by cipher primitive");
    }),
    ("MPQ CRC32 standard vector", () =>
    {
        var actual = MpqHash.ComputeCrc32(Encoding.ASCII.GetBytes("123456789"));
        Assert(actual == 0xCBF43926u, $"CRC32 vector mismatch: {actual:X8}");
    }),
    ("MPQ sector CRC validates decompressed bytes", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "MPQTikTool.SelfTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "sector-crc.mpq");
            var payload = RandomNumberGenerator.GetBytes(64);
            CreateSectorCrcMpq(file, payload, corruptCrc: false);
            using var archive = MpqArchive.Open(file);
            var restored = archive.ReadAllBytes(archive.Entries.Single());
            Assert(restored.AsSpan().SequenceEqual(payload), "sector data must round-trip");
            Assert(archive.Entries.Single().CrcStatus == "通过（1/1）", "sector checksum must pass");
        }
        finally { Directory.Delete(root, true); }
    }),
    ("MPQ sector CRC mismatch is surfaced", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "MPQTikTool.SelfTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "sector-crc-bad.mpq");
            var payload = RandomNumberGenerator.GetBytes(64);
            CreateSectorCrcMpq(file, payload, corruptCrc: true);
            using var archive = MpqArchive.Open(file);
            _ = archive.ReadAllBytes(archive.Entries.Single());
            Assert(archive.Entries.Single().CrcStatus.StartsWith("失败：", StringComparison.Ordinal),
                "checksum mismatch must be reported, not silently accepted");
        }
        finally { Directory.Delete(root, true); }
    }),
    ("MPQ v4 header MD5 and HET/BET bit tables", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "MPQTikTool.SelfTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "v4-het-bet.mpq");
            CreateV4HetBetMpq(file, corruptHeaderMd5: false, corruptHetMd5: false, compressTables: false);
            var parsed = MpqV4ArchiveParser.Read(file);
            Assert(parsed.MpqHeader.HeaderMd5Valid, "v4 header MD5 must validate");
            Assert(parsed.HetInfo is { Md5Valid: true } && parsed.BetInfo is { Md5Valid: true },
                "HET and BET raw MD5 values must validate");
            Assert(parsed.Blocks.Count == 1, "BET must reconstruct one block");
            var block = parsed.Blocks[0];
            Assert(block.FileOffset == 0x162 && block.FileSize == 5 && block.CompressedSize == 5,
                $"BET bit fields must decode file offset and sizes; actual={block.FileOffset:X}/{block.FileSize}/{block.CompressedSize}");
            Assert(block.Flags == 0x81000000u, "BET flag index must resolve to file flags");
            Assert(block.NameHash == MpqHash.HashJenkins64("test.bin", 16), "HET/BET name hash fragments must be merged");
        }
        finally { Directory.Delete(root, true); }
    }),
    ("MPQ v4 compressed and encrypted HET/BET tables", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "MPQTikTool.SelfTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "v4-compressed.mpq");
            CreateV4HetBetMpq(file, corruptHeaderMd5: false, corruptHetMd5: false, compressTables: true);
            var parsed = MpqV4ArchiveParser.Read(file);
            Assert(parsed.HetInfo?.IsCompressed == true && parsed.BetInfo?.IsCompressed == true,
                "both extended tables must be decompressed");
            Assert(parsed.Blocks.Single().NameHash == MpqHash.HashJenkins64("test.bin", 16), "decrypted/decompressed tables must preserve name hash");
        }
        finally { Directory.Delete(root, true); }
    }),
    ("MPQ v4 rejects bad header and HET MD5", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "MPQTikTool.SelfTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var badHeader = Path.Combine(root, "bad-header.mpq");
            CreateV4HetBetMpq(badHeader, corruptHeaderMd5: true, corruptHetMd5: false, compressTables: false);
            var headerRejected = false;
            try { _ = MpqV4ArchiveParser.Read(badHeader); }
            catch (InvalidDataException ex) { headerRejected = ex.Message.Contains("Header MD5"); }
            Assert(headerRejected, "bad header MD5 must be rejected");

            var badHet = Path.Combine(root, "bad-het.mpq");
            CreateV4HetBetMpq(badHet, corruptHeaderMd5: false, corruptHetMd5: true, compressTables: false);
            var hetRejected = false;
            try { _ = MpqV4ArchiveParser.Read(badHet); }
            catch (InvalidDataException ex) { hetRejected = ex.Message.Contains("HET 原始表 MD5"); }
            Assert(hetRejected, "bad HET raw MD5 must be rejected");
        }
        finally { Directory.Delete(root, true); }
    }),
    ("MPQ v4 archive open, HET name recovery and raw export", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "MPQTikTool.SelfTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "v4-open.mpq");
            var list = Path.Combine(root, "listfile.txt");
            var payload = Encoding.ASCII.GetBytes("hello");
            CreateV4HetBetMpq(file, corruptHeaderMd5: false, corruptHetMd5: false, compressTables: false);
            File.WriteAllText(list, "test.bin\n", Encoding.ASCII);
            using var archive = MpqArchive.Open(file);
            Assert(archive.FormatDescription.Contains("MPQ v4"), "archive must report v4 format");
            Assert(archive.AddListFile(list) == 1, "listfile must resolve the HET Jenkins hash");
            Assert(archive.Entries.Single().OriginalName == "test.bin", "recovered filename must be attached to BET block");
            Assert(archive.ReadAllBytes(archive.Entries.Single()).AsSpan().SequenceEqual(payload),
                "v4 BET block must export original bytes");
        }
        finally { Directory.Delete(root, true); }
    }),
    ("MPQ v1 table and raw block round-trip", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "MPQTikTool.SelfTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "fixture.mpq");
            var payload = RandomNumberGenerator.GetBytes(777);
            CreateMiniMpq(file, "test.bin", payload, encrypted: false, storedPayloadLength: payload.Length);
            using var archive = MpqArchive.Open(file);
            var restored = archive.ReadAllBytes(archive.Entries.Single());
            Assert(restored.AsSpan().SequenceEqual(payload), "raw block must match fixture payload");
        }
        finally { Directory.Delete(root, true); }
    }),
    ("MPQ encrypted raw block resolves key from listfile", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "MPQTikTool.SelfTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "encrypted.mpq");
            var list = Path.Combine(root, "listfile.txt");
            var payload = RandomNumberGenerator.GetBytes(1024);
            CreateMiniMpq(file, "test.bin", payload, encrypted: true, storedPayloadLength: payload.Length);
            File.WriteAllText(list, "test.bin\n", Encoding.ASCII);
            using var archive = MpqArchive.Open(file);
            Assert(archive.AddListFile(list) == 1, "listfile should resolve the entry name");
            var restored = archive.ReadAllBytes(archive.Entries.Single());
            Assert(restored.AsSpan().SequenceEqual(payload), "decrypted block must match fixture payload");
        }
        finally { Directory.Delete(root, true); }
    }),
    ("MPQ truncated raw block is rejected", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "MPQTikTool.SelfTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "truncated.mpq");
            var payload = RandomNumberGenerator.GetBytes(64);
            CreateMiniMpq(file, "test.bin", payload, encrypted: false, storedPayloadLength: payload.Length - 7);
            using var archive = MpqArchive.Open(file);
            var rejected = false;
            try { _ = archive.ReadAllBytes(archive.Entries.Single()); }
            catch (EndOfStreamException) { rejected = true; }
            Assert(rejected, "truncated block must not be reported as a successful export");
        }
        finally { Directory.Delete(root, true); }
    })
};

var failures = new List<string>();
foreach (var check in checks)
{
    try { check.Run(); Console.WriteLine("PASS " + check.Name); }
    catch (Exception ex) { failures.Add(check.Name + ": " + ex.Message); Console.WriteLine("FAIL " + failures[^1]); }
}
Console.WriteLine($"RESULT {checks.Count - failures.Count}/{checks.Count} checks passed");
return failures.Count == 0 ? 0 : 1;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}


static void CreateV4HetBetMpq(string path, bool corruptHeaderMd5, bool corruptHetMd5, bool compressTables)
{
    const int headerSize = 0xD0;
    const ulong hetOffset = headerSize;
    var hetBody = new byte[34];
    Write32(hetBody, 0, 34);  // table size
    Write32(hetBody, 4, 1);   // occupied entries
    Write32(hetBody, 8, 1);   // total slots
    Write32(hetBody, 12, 16); // name hash bits
    Write32(hetBody, 16, 1);  // total index bits
    Write32(hetBody, 20, 0);  // extra index bits
    Write32(hetBody, 24, 1);  // effective index bits
    Write32(hetBody, 28, 1);  // index table bytes
    var fixtureNameHash = MpqHash.HashJenkins64("test.bin", 16);
    hetBody[32] = (byte)(fixtureNameHash >> 8); // HET NameHash1
    hetBody[33] = 0;          // BET index 0

    var betBody = new byte[88];
    Write32(betBody, 0, 88);
    Write32(betBody, 4, 1);
    Write32(betBody, 8, 0x10);
    Write32(betBody, 12, 49);
    Write32(betBody, 16, 0);  // file pos bit index
    Write32(betBody, 20, 32); // file size bit index
    Write32(betBody, 24, 40); // compressed size bit index
    Write32(betBody, 28, 48); // flag index bit index
    Write32(betBody, 32, 49);
    Write32(betBody, 36, 32);
    Write32(betBody, 40, 8);
    Write32(betBody, 44, 8);
    Write32(betBody, 48, 1);
    Write32(betBody, 52, 0);
    Write32(betBody, 56, 8);  // NameHash2 stride
    Write32(betBody, 60, 0);
    Write32(betBody, 64, 8);  // NameHash2 effective bits
    Write32(betBody, 68, 1);  // NameHash2 array bytes
    Write32(betBody, 72, 1);  // flag count
    Write32(betBody, 76, 0x81000000); // flags[0]
    var filePos = (uint)(headerSize + 46 + 100);
    WriteBits(betBody.AsSpan(80, 7), 0, 32, filePos);
    WriteBits(betBody.AsSpan(80, 7), 32, 8, 5);
    WriteBits(betBody.AsSpan(80, 7), 40, 8, 5);
    WriteBits(betBody.AsSpan(80, 7), 48, 1, 0);
    betBody[87] = (byte)fixtureNameHash; // NameHash2

    var het = BuildExtTable(0x1A544548, "(hash table)", hetBody, compressTables);
    var bet = BuildExtTable(0x1A544542, "(block table)", betBody, compressTables);
    // The fixture uses raw table storage sizes in the header.
    var betOffset = hetOffset + (ulong)het.Length;
    // Rebuild until the compressed BET length and the encoded data offset stabilize.
    for (var attempt = 0; attempt < 4; attempt++)
    {
        var nextFilePos = checked((uint)(headerSize + het.Length + bet.Length));
        if (nextFilePos == filePos) break;
        filePos = nextFilePos;
        WriteBits(betBody.AsSpan(80, 7), 0, 32, filePos);
        bet = BuildExtTable(0x1A544542, "(block table)", betBody, compressTables);
    }
    if (filePos != checked((uint)(headerSize + het.Length + bet.Length)))
        throw new InvalidOperationException("Compressed BET fixture offset did not stabilize.");

    var payload = Encoding.ASCII.GetBytes("hello");
    var output = new byte[checked((int)filePos + payload.Length)];
    var header = output.AsSpan(0, headerSize);
    new byte[] { (byte)'M', (byte)'P', (byte)'Q', 0x1A }.AsSpan().CopyTo(header);
    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), headerSize);
    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, 4), (uint)output.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(12, 2), 3);
    BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(14, 2), 3);
    BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(0x2C, 8), (ulong)output.Length);
    BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(0x34, 8), betOffset);
    BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(0x3C, 8), hetOffset);
    BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(0x5C, 8), (ulong)het.Length);
    BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(0x64, 8), (ulong)bet.Length);
    het.CopyTo(output, (int)hetOffset);
    bet.CopyTo(output, (int)betOffset);
    payload.CopyTo(output, (int)filePos);

    MD5.HashData(het).AsSpan().CopyTo(header.Slice(0xB0, 16));
    MD5.HashData(bet).AsSpan().CopyTo(header.Slice(0xA0, 16));
    if (corruptHetMd5) header[0xB0] ^= 0x80;
    var headerMd5 = MD5.HashData(header[..0xC0]);
    headerMd5.AsSpan().CopyTo(header.Slice(0xC0, 16));
    if (corruptHeaderMd5) header[0xC0] ^= 0x01;
    File.WriteAllBytes(path, output);
}

static byte[] BuildExtTable(uint signature, string keyName, byte[] body, bool compress)
{
    byte[] storedBody;
    if (compress)
    {
        using var compressed = new MemoryStream();
        compressed.WriteByte(0x02); // MPQ ZLIB compression mask
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(body);
        storedBody = compressed.ToArray();
    }
    else storedBody = (byte[])body.Clone();

    MpqHash.EncryptInPlace(storedBody, MpqHash.HashString(keyName, 3));
    var table = new byte[12 + storedBody.Length];
    Write32(table, 0, signature);
    Write32(table, 4, 1);
    Write32(table, 8, (uint)body.Length);
    storedBody.CopyTo(table, 12);
    return table;
}

static void Write32(Span<byte> data, int offset, uint value) =>
    BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(offset, 4), value);

static void WriteBits(Span<byte> data, int bitOffset, int bitCount, ulong value)
{
    for (var bit = 0; bit < bitCount; bit++)
        if ((value & (1UL << bit)) != 0)
            data[(bitOffset + bit) >> 3] |= (byte)(1 << ((bitOffset + bit) & 7));
}

static void CreateSectorCrcMpq(string path, byte[] payload, bool corruptCrc)
{
    const int headerSize = 32, hashPos = 32, blockPos = 48, dataPos = 64;
    var filename = "sector.bin";
    var hashTable = new byte[16];
    var (hash1, hash2) = MpqHash.HashName(filename);
    BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(0, 4), hash1);
    BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(4, 4), hash2);
    BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(8, 4), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(12, 4), 0);
    MpqHash.EncryptInPlace(hashTable, MpqHash.HashString("(hash table)", 3));

    var stored = new byte[12 + payload.Length + 4];
    BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(0, 4), 12);
    BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(4, 4), (uint)(12 + payload.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(8, 4), (uint)stored.Length);
    payload.CopyTo(stored, 12);
    var crc = MpqHash.ComputeCrc32(payload) ^ (corruptCrc ? 0xFFFFFFFFu : 0u);
    BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(12 + payload.Length, 4), crc);

    var blockTable = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(blockTable.AsSpan(0, 4), dataPos);
    BinaryPrimitives.WriteUInt32LittleEndian(blockTable.AsSpan(4, 4), (uint)stored.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(blockTable.AsSpan(8, 4), (uint)payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(blockTable.AsSpan(12, 4), 0x80000000u | 0x00000200u | 0x04000000u);
    MpqHash.EncryptInPlace(blockTable, MpqHash.HashString("(block table)", 3));

    var totalLength = dataPos + stored.Length;
    var header = new byte[headerSize];
    new byte[] { (byte)'M', (byte)'P', (byte)'Q', 0x1A }.CopyTo(header, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), headerSize);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), (uint)totalLength);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12, 2), 0);
    header[14] = 3;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), hashPos);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20, 4), blockPos);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24, 4), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28, 4), 1);

    using var output = File.Create(path);
    output.Write(header);
    output.Write(hashTable);
    output.Write(blockTable);
    output.Write(stored);
}

static void CreateMiniMpq(string path, string filename, byte[] payload, bool encrypted, int storedPayloadLength)
{
    const int headerSize = 32, hashPos = 32, blockPos = 48, dataPos = 64;
    var hashTable = new byte[16];
    var (hash1, hash2) = MpqHash.HashName(filename);
    BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(0, 4), hash1);
    BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(4, 4), hash2);
    BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(8, 4), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(12, 4), 0);
    MpqHash.EncryptInPlace(hashTable, MpqHash.HashString("(hash table)", 3));

    var flags = 0x80000000u | (encrypted ? 0x00010000u : 0u);
    var blockTable = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(blockTable.AsSpan(0, 4), dataPos);
    BinaryPrimitives.WriteUInt32LittleEndian(blockTable.AsSpan(4, 4), (uint)payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(blockTable.AsSpan(8, 4), (uint)payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(blockTable.AsSpan(12, 4), flags);
    MpqHash.EncryptInPlace(blockTable, MpqHash.HashString("(block table)", 3));

    var stored = payload.AsSpan(0, storedPayloadLength).ToArray();
    if (encrypted) MpqHash.EncryptInPlace(stored, MpqHash.HashString(Path.GetFileName(filename), 3));

    var totalLength = dataPos + stored.Length;
    var header = new byte[headerSize];
    new byte[] { (byte)'M', (byte)'P', (byte)'Q', 0x1A }.CopyTo(header, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), headerSize);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), (uint)totalLength);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12, 2), 0);
    header[14] = 3;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), hashPos);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20, 4), blockPos);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24, 4), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28, 4), 1);

    using var output = File.Create(path);
    output.Write(header);
    output.Write(hashTable);
    output.Write(blockTable);
    output.Write(stored);
}
