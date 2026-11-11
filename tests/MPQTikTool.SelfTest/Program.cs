using System.Security.Cryptography;
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
