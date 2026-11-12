using System.Buffers.Binary;
using System.Text;

namespace MPQTikTool.Services;

/// <summary>
/// Storm/MPQ hash and block encryption primitives.
/// Reusable, dependency-free implementation of the classic MPQ algorithms.
/// </summary>
internal static class MpqHash
{
    private const uint StormBufferSeed = 0x00100001;
    private static readonly uint[] CryptTable = BuildCryptTable();

    private static uint[] BuildCryptTable()
    {
        var table = new uint[0x500];
        unchecked
        {
            uint seed = StormBufferSeed;
            for (var i = 0; i < 0x100; i++)
            {
                for (var j = 0; j < 5; j++)
                {
                    seed = (seed * 125 + 3) % 0x2AAAAB;
                    var high = (seed & 0xFFFF) << 0x10;
                    seed = (seed * 125 + 3) % 0x2AAAAB;
                    var low = seed & 0xFFFF;
                    table[i + j * 0x100] = high | low;
                }
            }
        }
        return table;
    }

    /// <summary>Hashes raw filename bytes the same way StormLib does.</summary>
    public static uint HashBytes(ReadOnlySpan<byte> name, uint hashType)
    {
        unchecked
        {
            uint seed1 = 0x7FED7FED, seed2 = 0xEEEEEEEE;
            foreach (var raw in name)
            {
                var ch = raw == (byte)'/' ? (byte)'\\' : raw;
                if (ch >= (byte)'a' && ch <= (byte)'z') ch -= 32;
                seed1 = CryptTable[(hashType << 8) + ch] ^ (seed1 + seed2);
                seed2 = ch + seed1 + seed2 + (seed2 << 5) + 3;
            }
            return seed1;
        }
    }

    /// <summary>
    /// Hashes a managed filename. MPQ names are byte strings; GB18030 is used for
    /// non-ASCII characters because Warcraft III maps are overwhelmingly Chinese/ANSI.
    /// </summary>
    public static uint HashString(string name, uint hashType)
    {
        byte[] bytes;
        try
        {
            bytes = Encoding.GetEncoding(936).GetBytes(name);
        }
        catch (Exception)
        {
            bytes = Encoding.Latin1.GetBytes(name);
        }
        return HashBytes(bytes, hashType);
    }

    public static (uint Name1, uint Name2) HashNameBytes(ReadOnlySpan<byte> name) =>
        (HashBytes(name, 1), HashBytes(name, 2));

    public static (uint Name1, uint Name2) HashName(string name) =>
        (HashString(name, 1), HashString(name, 2));

    /// <summary>Decrypts a whole number of DWORDs in place (MPQ block cipher).</summary>
    public static void DecryptInPlace(Span<byte> data, uint key)
    {
        var seed1 = key;
        var seed2 = 0xEEEEEEEEu;
        DecryptInPlace(data, ref seed1, ref seed2);
    }

    /// <summary>
    /// Stateful variant for streaming encrypted raw files. Call successive chunks in order;
    /// every chunk except the final one must have a length divisible by four.
    /// </summary>
    public static void DecryptInPlace(Span<byte> data, ref uint seed1, ref uint seed2)
    {
        unchecked
        {
            var dwords = data.Length / 4;
            for (var i = 0; i < dwords; i++)
            {
                var slot = data.Slice(i * 4, 4);
                seed2 += CryptTable[0x400 + (seed1 & 0xFF)];
                var value = BinaryPrimitives.ReadUInt32LittleEndian(slot) ^ (seed1 + seed2);
                BinaryPrimitives.WriteUInt32LittleEndian(slot, value);
                seed1 = ((~seed1 << 21) + 0x11111111) | (seed1 >> 11);
                seed2 = value + seed2 + (seed2 << 5) + 3;
            }
        }
    }

    /// <summary>
    /// Encrypts a whole number of DWORDs in place. This is NOT the inverse of calling
    /// DecryptInPlace on plaintext: Storm's cipher advances seed2 with the plaintext
    /// during encryption and with the decrypted value during decryption.
    /// </summary>
    public static void EncryptInPlace(Span<byte> data, uint key)
    {
        unchecked
        {
            uint seed1 = key, seed2 = 0xEEEEEEEE;
            var dwords = data.Length / 4;
            for (var i = 0; i < dwords; i++)
            {
                var slot = data.Slice(i * 4, 4);
                seed2 += CryptTable[0x400 + (seed1 & 0xFF)];
                var plain = BinaryPrimitives.ReadUInt32LittleEndian(slot);
                BinaryPrimitives.WriteUInt32LittleEndian(slot, plain ^ (seed1 + seed2));
                seed1 = ((~seed1 << 21) + 0x11111111) | (seed1 >> 11);
                seed2 = plain + seed2 + (seed2 << 5) + 3;
            }
        }
    }

    /// <summary>
    /// Recovers the effective file key of an encrypted file without knowing its name.
    /// The first DWORD of the encrypted sector-offset table is always the table size,
    /// which gives enough known plaintext to solve the 32-bit key in 256 tries.
    /// </summary>
    /// <summary>Computes the 64-bit Jenkins lookup3 hash used by MPQ HET tables.</summary>
    public static ulong HashJenkins64(string name, uint bitSize)
    {
        if (bitSize is < 8 or > 64) throw new ArgumentOutOfRangeException(nameof(bitSize));
        var normalized = name.Replace('/', '\\').ToLowerInvariant();
        var bytes = Encoding.UTF8.GetBytes(normalized);
        uint pc = 2, pb = 1;
        HashLittle2(bytes, ref pc, ref pb);
        var hash = ((ulong)pb << 32) | pc;
        var highBit = 1UL << checked((int)bitSize - 1);
        var mask = bitSize == 64 ? ulong.MaxValue : (1UL << checked((int)bitSize)) - 1;
        return (hash & (mask ^ highBit)) | highBit;
    }

    private static uint RotateLeft(uint value, int bits) =>
        (value << bits) | (value >> (32 - bits));

    private static void JenkinsMix(ref uint a, ref uint b, ref uint c)
    {
        unchecked
        {
            a -= c; a ^= RotateLeft(c, 4); c += b;
            b -= a; b ^= RotateLeft(a, 6); a += c;
            c -= b; c ^= RotateLeft(b, 8); b += a;
            a -= c; a ^= RotateLeft(c, 16); c += b;
            b -= a; b ^= RotateLeft(a, 19); a += c;
            c -= b; c ^= RotateLeft(b, 4); b += a;
        }
    }

    private static void JenkinsFinal(ref uint a, ref uint b, ref uint c)
    {
        unchecked
        {
            c ^= b; c -= RotateLeft(b, 14);
            a ^= c; a -= RotateLeft(c, 11);
            b ^= a; b -= RotateLeft(a, 25);
            c ^= b; c -= RotateLeft(b, 16);
            a ^= c; a -= RotateLeft(c, 4);
            b ^= a; b -= RotateLeft(a, 14);
            c ^= b; c -= RotateLeft(b, 24);
        }
    }

    private static void HashLittle2(ReadOnlySpan<byte> key, ref uint pc, ref uint pb)
    {
        unchecked
        {
            uint a = 0xDEADBEEF + (uint)key.Length + pc;
            uint b = a;
            uint c = a + pb;
            var offset = 0;
            var remaining = key.Length;
            while (remaining > 12)
            {
                a += BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(offset, 4));
                b += BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(offset + 4, 4));
                c += BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(offset + 8, 4));
                JenkinsMix(ref a, ref b, ref c);
                offset += 12;
                remaining -= 12;
            }
            if (remaining == 0) { pc = c; pb = b; return; }
            for (var i = 0; i < remaining; i++)
            {
                var value = (uint)key[offset + i];
                if (i < 4) a += value << (8 * i);
                else if (i < 8) b += value << (8 * (i - 4));
                else c += value << (8 * (i - 8));
            }
            JenkinsFinal(ref a, ref b, ref c);
            pc = c;
            pb = b;
        }
    }

    /// <summary>Standard reflected CRC-32 (polynomial 0xEDB88320), as used by MPQ sector checksums.</summary>
    public static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    public static uint? RecoverFileKey(uint firstCipherDword, uint plainFirstDword, Func<uint, bool> validateTableKey)
    {
        unchecked
        {
            var mask = firstCipherDword ^ plainFirstDword;
            const uint initialSeed2 = 0xEEEEEEEE;
            for (var low = 0u; low < 256; low++)
            {
                var tableKey = mask - initialSeed2 - CryptTable[0x400 + low];
                if ((tableKey & 0xFF) != low) continue;
                if (!validateTableKey(tableKey)) continue;
                return tableKey + 1;
            }
        }
        return null;
    }
}
