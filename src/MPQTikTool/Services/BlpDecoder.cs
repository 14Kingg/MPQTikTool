using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MPQTikTool.Services;

/// <summary>
/// Decoder for Warcraft III BLP1 (JPEG / paletted) and BLP2 (palette / DXT / BGRA) textures, mip level 0.
/// BLP1 is the dominant texture format in classic maps, so it is required for "preview anything".
/// </summary>
public static class BlpDecoder
{
    private const int HeaderSize = 156;

    public static BitmapSource Decode(byte[] data)
    {
        if (data.Length < HeaderSize) throw new InvalidDataException("BLP 文件太短。");
        if (data.AsSpan(0, 4).SequenceEqual("BLP1"u8)) return DecodeBlp1(data);
        if (data.AsSpan(0, 4).SequenceEqual("BLP2"u8)) return DecodeBlp2(data);
        throw new InvalidDataException("不是 BLP1/BLP2 纹理。");
    }

    private static BitmapSource DecodeBlp1(byte[] data)
    {
        var compression = Read32(data, 4);
        var alphaBits = Read32(data, 8);
        var width = Read32(data, 12);
        var height = Read32(data, 16);
        var mipOffset = Read32(data, 0x1C);
        var mipSize = Read32(data, 0x5C);
        if (width is 0 or > 8192 || height is 0 or > 8192)
            throw new InvalidDataException("BLP1 尺寸无效。");
        if ((ulong)mipOffset + mipSize > (ulong)data.Length || mipSize == 0)
            throw new InvalidDataException("BLP1 mip 数据越界。");

        var w = (int)width;
        var h = (int)height;
        return compression == 0
            ? DecodeBlp1Jpeg(data, w, h, (int)mipOffset, (int)mipSize, (int)alphaBits)
            : DecodeBlp1Paletted(data, w, h, (int)mipOffset, (int)mipSize, (int)alphaBits);
    }

    private static BitmapSource DecodeBlp1Jpeg(byte[] file, int w, int h, int mipOffset, int mipSize, int alphaBits)
    {
        var headerSize = Read32(file, HeaderSize);
        if (headerSize == 0 || (long)HeaderSize + 4 + headerSize > file.Length)
            throw new InvalidDataException("BLP1 JPEG 公共头无效。");
        var header = file.AsSpan(HeaderSize + 4, (int)headerSize);
        var mip = file.AsSpan(mipOffset, mipSize);

        // Some repacked BLP1 files embed a complete JPEG in the mip instead of a shared header.
        if (mip.Length >= 2 && mip[0] == 0xFF && mip[1] == 0xD8)
        {
            var standalone = mip.ToArray();
            var standaloneEoi = FindEoi(standalone, 2);
            return LoadBitmap(standalone.AsSpan(0, standaloneEoi >= 0 ? standaloneEoi + 2 : standalone.Length));
        }

        // BLP1 stores JFIF tables up to SOS in a shared header; each mip continues that stream.
        var jpeg = new byte[header.Length + mip.Length + 2];
        header.CopyTo(jpeg);
        mip.CopyTo(jpeg.AsSpan(header.Length));
        var end = FindEoi(jpeg, header.Length);
        int jpegLength;
        if (end >= 0)
        {
            jpegLength = end + 2;
        }
        else
        {
            jpeg[header.Length + mip.Length] = 0xFF;
            jpeg[header.Length + mip.Length + 1] = 0xD9;
            jpegLength = header.Length + mip.Length + 2;
        }

        var source = LoadBitmap(jpeg.AsSpan(0, jpegLength));
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var bw = converted.PixelWidth;
        var bh = converted.PixelHeight;
        var pixels = new byte[checked(bw * bh * 4)];
        converted.CopyPixels(pixels, bw * 4, 0);

        // Some BLP1 JPEG files append an 8-bit alpha plane after the JPEG EOI marker.
        if (end >= 0 && alphaBits == 8)
        {
            var alphaOffset = end + 2;
            var alphaCount = w * h;
            if (alphaOffset + alphaCount <= jpeg.Length && alphaCount == bw * bh)
            {
                for (var i = 0; i < alphaCount; i++) pixels[i * 4 + 3] = jpeg[alphaOffset + i];
            }
        }
        return MakeBitmap(bw, bh, pixels);
    }

    private static int FindEoi(byte[] data, int start)
    {
        for (var i = Math.Max(start, 1); i + 1 < data.Length; i++)
            if (data[i] == 0xFF && data[i + 1] == 0xD9) return i;
        return -1;
    }

    private static BitmapSource DecodeBlp1Paletted(byte[] file, int w, int h, int mipOffset, int mipSize, int alphaBits)
    {
        var paletteOffset = HeaderSize;
        if (paletteOffset + 1024 > file.Length) throw new InvalidDataException("BLP1 调色板缺失。");
        var count = checked(w * h);
        var alphaBytes = alphaBits switch
        {
            0 => 0,
            1 => (count + 7) / 8,
            4 => (count + 1) / 2,
            8 => count,
            _ => 0
        };
        if (mipSize < count + alphaBytes) throw new InvalidDataException("BLP1 调色板 mip 数据不完整。");

        var palette = file.AsSpan(paletteOffset, 1024);
        var indices = file.AsSpan(mipOffset, count);
        var alpha = file.AsSpan(mipOffset + count, alphaBytes);
        var pixels = new byte[count * 4];
        for (var i = 0; i < count; i++)
        {
            var pi = indices[i] * 4;
            var di = i * 4;
            pixels[di] = palette[pi];
            pixels[di + 1] = palette[pi + 1];
            pixels[di + 2] = palette[pi + 2];
            pixels[di + 3] = alphaBits switch
            {
                0 => 255,
                1 => (byte)(((alpha[i / 8] >> (i & 7)) & 1) * 255),
                4 => (byte)(((alpha[i / 2] >> ((i & 1) * 4)) & 15) * 17),
                8 => alpha[i],
                _ => 255
            };
        }
        return MakeBitmap(w, h, pixels);
    }

    private static BitmapSource LoadBitmap(ReadOnlySpan<byte> bytes)
    {
        var image = new BitmapImage();
        using var stream = new MemoryStream(bytes.ToArray(), false);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource DecodeBlp2(byte[] data)
    {
        // BLP2: magic(4), encoding(4), alphaDepth(1) @0x08, alphaEncoding(1) @0x09,
        // hasMips(1), pad(1), width(4) @0x0C, height(4) @0x10,
        // mipOffsets[16] @0x14, mipSizes[16] @0x54, palette(1024) @0x94.
        if (data.Length < 148 || !data.AsSpan(0, 4).SequenceEqual("BLP2"u8))
            throw new InvalidDataException("不是有效的 BLP2 文件。");

        var encoding = Read32(data, 4);
        var alphaDepth = data[8];
        var alphaEncoding = data[9];
        var width = Read32(data, 12);
        var height = Read32(data, 16);
        var offset = Read32(data, 20);
        var size = Read32(data, 84);
        if (width is 0 or > 8192 || height is 0 or > 8192 || offset == 0 || size == 0 ||
            (ulong)offset + size > (ulong)data.Length)
            throw new InvalidDataException("BLP2 头部尺寸或 mipmap 数据范围无效。");

        var w = checked((int)width);
        var h = checked((int)height);
        var pixels = new byte[checked(w * h * 4)];
        var source = data.AsSpan(checked((int)offset), checked((int)size));

        switch (encoding)
        {
            case 1: DecodePalette(data, source, pixels, w, h, alphaDepth); break;
            case 2: DecodeDxt(source, pixels, w, h, alphaEncoding); break;
            case 3:
                if (source.Length < pixels.Length) throw new InvalidDataException("BLP2 BGRA mipmap 数据不完整。");
                source[..pixels.Length].CopyTo(pixels);
                break;
            default: throw new NotSupportedException($"暂不支持 BLP2 encoding={encoding}。");
        }
        return MakeBitmap(w, h, pixels);
    }

    private static BitmapSource MakeBitmap(int width, int height, byte[] bgra)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
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
        Span<byte> alpha = stackalloc byte[16];
        for (var by = 0; by < blocksY; by++)
        for (var bx = 0; bx < blocksX; bx++)
        {
            alpha.Fill(255);
            if (alphaEncoding == 1) DecodeDxt3Alpha(src.Slice(cursor, 8), alpha);
            else if (alphaEncoding == 7) DecodeDxt5Alpha(src.Slice(cursor, 8), alpha);
            else if (alphaEncoding is not 0) throw new NotSupportedException($"暂不支持 DXT alphaEncoding={alphaEncoding}。");

            var colorOffset = cursor + (alphaEncoding == 0 ? 0 : 8);
            DecodeColors(src.Slice(colorOffset, 8), alphaEncoding == 0, out var colors, out var indices, out var transparentMode);
            for (var py = 0; py < 4; py++)
            for (var px = 0; px < 4; px++)
            {
                var x = bx * 4 + px; var y = by * 4 + py;
                if (x >= w || y >= h) continue;
                var p = py * 4 + px; var di = (y * w + x) * 4;
                var colorIndex = (int)((indices >> (p * 2)) & 3);
                var c = colors[colorIndex];
                dst[di] = c.B; dst[di + 1] = c.G; dst[di + 2] = c.R;
                dst[di + 3] = transparentMode && colorIndex == 3 ? (byte)0 : alpha[p];
            }
            cursor += blockBytes;
        }
    }

    private static void DecodeColors(ReadOnlySpan<byte> block, bool dxt1, out (byte R, byte G, byte B)[] colors, out uint indices, out bool transparentMode)
    {
        var c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        var c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        colors = new (byte, byte, byte)[4];
        colors[0] = Rgb565(c0); colors[1] = Rgb565(c1);
        transparentMode = dxt1 && c0 <= c1;
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

    private static uint Read32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
}
