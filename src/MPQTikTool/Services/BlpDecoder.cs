using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MPQTikTool.Services;

/// <summary>Decoder for common Warcraft III BLP2 mip level zero images.</summary>
public static class BlpDecoder
{
    public static BitmapSource Decode(byte[] data)
    {
        if (data.Length < 148 || !data.AsSpan(0, 4).SequenceEqual("BLP2"u8))
            throw new InvalidDataException("不是有效的 BLP2 文件（当前解码器不支持 BLP1/JPEG 变体）。");

        var encoding = data[8];
        var alphaDepth = data[9];
        var alphaEncoding = data[10];
        var width = ReadUInt(data, 12);
        var height = ReadUInt(data, 16);
        var offset = ReadUInt(data, 20);
        var size = ReadUInt(data, 84);
        if (width is 0 or > 8192 || height is 0 or > 8192 || offset == 0 || size == 0 ||
            (ulong)offset + size > (ulong)data.Length)
            throw new InvalidDataException("BLP2 头部尺寸或 mipmap 数据范围无效。");

        var w = checked((int)width);
        var h = checked((int)height);
        var pixels = new byte[checked(w * h * 4)];
        var source = data.AsSpan(checked((int)offset), checked((int)size));

        switch (encoding)
        {
            case 1:
                DecodePalette(data, source, pixels, w, h, alphaDepth);
                break;
            case 2:
                DecodeDxt(source, pixels, w, h, alphaEncoding);
                break;
            case 3:
                if (source.Length < pixels.Length) throw new InvalidDataException("BLP2 BGRA mipmap 数据不完整。");
                source[..pixels.Length].CopyTo(pixels);
                break;
            default:
                throw new NotSupportedException($"暂不支持 BLP2 encoding={encoding}。");
        }

        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DecodePalette(byte[] file, ReadOnlySpan<byte> src, byte[] dst, int w, int h, int alphaDepth)
    {
        if (file.Length < 1172) throw new InvalidDataException("BLP2 调色板缺失。");
        var count = checked(w * h);
        var alphaBytes = alphaDepth switch { 0 => 0, 1 => (count + 7) / 8, 4 => (count + 1) / 2, 8 => count, _ => throw new NotSupportedException($"不支持 {alphaDepth} 位调色板 Alpha。") };
        if (src.Length < count + alphaBytes) throw new InvalidDataException("BLP2 调色板 mipmap 数据不完整。");
        var palette = file.AsSpan(148, 1024);
        for (var i = 0; i < count; i++)
        {
            var pi = src[i] * 4;
            var di = i * 4;
            dst[di] = palette[pi];
            dst[di + 1] = palette[pi + 1];
            dst[di + 2] = palette[pi + 2];
            dst[di + 3] = alphaDepth switch
            {
                0 => (byte)255,
                1 => (byte)(((src[count + i / 8] >> (i & 7)) & 1) * 255),
                4 => (byte)(((src[count + i / 2] >> ((i & 1) * 4)) & 15) * 17),
                8 => src[count + i],
                _ => 255
            };
        }
    }

    private static void DecodeDxt(ReadOnlySpan<byte> src, byte[] dst, int w, int h, int alphaEncoding)
    {
        var blockBytes = alphaEncoding == 0 ? 8 : 16;
        var blocksX = (w + 3) / 4;
        var blocksY = (h + 3) / 4;
        if (src.Length < blocksX * blocksY * blockBytes) throw new InvalidDataException("BLP2 DXT mipmap 数据不完整。");
        var cursor = 0;
        for (var by = 0; by < blocksY; by++)
        for (var bx = 0; bx < blocksX; bx++)
        {
            Span<byte> alpha = stackalloc byte[16];
            alpha.Fill(255);
            if (alphaEncoding == 1) DecodeDxt3Alpha(src.Slice(cursor, 8), alpha);
            else if (alphaEncoding == 7) DecodeDxt5Alpha(src.Slice(cursor, 8), alpha);
            else if (alphaEncoding is not 0) throw new NotSupportedException($"暂不支持 DXT alphaEncoding={alphaEncoding}。");

            var colorOffset = cursor + (alphaEncoding == 0 ? 0 : 8);
            DecodeColors(src.Slice(colorOffset, 8), alphaEncoding == 0, out var colors, out var indices);
            for (var py = 0; py < 4; py++)
            for (var px = 0; px < 4; px++)
            {
                var x = bx * 4 + px; var y = by * 4 + py;
                if (x >= w || y >= h) continue;
                var p = py * 4 + px; var di = (y * w + x) * 4;
                var c = colors[(indices >> (p * 2)) & 3];
                dst[di] = c.B; dst[di + 1] = c.G; dst[di + 2] = c.R;
                dst[di + 3] = alpha[p];
            }
            cursor += blockBytes;
        }
    }

    private static void DecodeColors(ReadOnlySpan<byte> block, bool dxt1, out (byte R, byte G, byte B)[] colors, out uint indices)
    {
        var c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        var c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        colors = new (byte, byte, byte)[4];
        colors[0] = Rgb565(c0); colors[1] = Rgb565(c1);
        if (c0 > c1 || !dxt1)
        {
            colors[2] = Mix(colors[0], colors[1], 2, 1, 3);
            colors[3] = Mix(colors[0], colors[1], 1, 2, 3);
        }
        else
        {
            colors[2] = Mix(colors[0], colors[1], 1, 1, 2);
            colors[3] = (0, 0, 0);
        }
        indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
    }

    private static (byte R, byte G, byte B) Rgb565(ushort c) =>
        ((byte)(((c >> 11) & 31) * 255 / 31), (byte)(((c >> 5) & 63) * 255 / 63), (byte)((c & 31) * 255 / 31));

    private static (byte R, byte G, byte B) Mix((byte R, byte G, byte B) a, (byte R, byte G, byte B) b, int wa, int wb, int d) =>
        ((byte)((a.R * wa + b.R * wb) / d), (byte)((a.G * wa + b.G * wb) / d), (byte)((a.B * wa + b.B * wb) / d));

    private static void DecodeDxt3Alpha(ReadOnlySpan<byte> block, Span<byte> alpha)
    {
        for (var i = 0; i < 16; i++)
        {
            var nibble = (block[i / 2] >> ((i & 1) * 4)) & 15;
            alpha[i] = (byte)(nibble * 17);
        }
    }

    private static void DecodeDxt5Alpha(ReadOnlySpan<byte> block, Span<byte> alpha)
    {
        var a0 = block[0]; var a1 = block[1];
        Span<byte> table = stackalloc byte[8]; table[0] = a0; table[1] = a1;
        if (a0 > a1)
            for (var i = 1; i <= 6; i++) table[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
        else
        {
            for (var i = 1; i <= 4; i++) table[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
            table[6] = 0; table[7] = 255;
        }
        ulong bits = 0;
        for (var i = 0; i < 6; i++) bits |= (ulong)block[i + 2] << (8 * i);
        for (var i = 0; i < 16; i++) alpha[i] = table[(int)((bits >> (3 * i)) & 7)];
    }

    private static uint ReadUInt(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
}
