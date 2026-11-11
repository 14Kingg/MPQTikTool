using System.Buffers.Binary;
using System.Security.Cryptography;
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
