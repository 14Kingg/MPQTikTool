using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MPQTikTool.Services;

/// <summary>Minimal TGA decoder (8/15/16/24/32 bpp, uncompressed and RLE).</summary>
public static class TgaDecoder
{
    public static BitmapSource Decode(byte[] data)
    {
        if (data.Length < 18) throw new InvalidDataException("TGA 文件太短。");
        var idLength = data[0];
        var colorMapType = data[1];
        var imageType = data[2];
        var width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14, 2));
        var bpp = data[16];
        var descriptor = data[17];
        if (width is 0 or > 8192 || height is 0 or > 8192) throw new InvalidDataException("TGA 尺寸无效。");
        if (colorMapType != 0) throw new NotSupportedException("暂不支持带调色板的 TGA。");
        var bytesPerPixel = bpp / 8;
        if (bpp is not (8 or 15 or 16 or 24 or 32)) throw new NotSupportedException($"暂不支持 {bpp} bpp TGA。");

        var start = 18 + idLength;
        var pixelCount = width * height;
        var raw = new byte[pixelCount * bytesPerPixel];
        if (imageType is 2 or 3)
        {
            if (start + raw.Length > data.Length) throw new InvalidDataException("TGA 像素数据不完整。");
            data.AsSpan(start, raw.Length).CopyTo(raw);
        }
        else if (imageType is 10 or 11)
        {
            DecodeRle(data, start, raw, bytesPerPixel);
        }
        else throw new NotSupportedException($"暂不支持 TGA imageType={imageType}。");

        var pixels = new byte[pixelCount * 4];
        var topDown = (descriptor & 0x20) != 0;
        for (var y = 0; y < height; y++)
        {
            var srcRow = topDown ? y : height - 1 - y;
            for (var x = 0; x < width; x++)
            {
                var si = (srcRow * width + x) * bytesPerPixel;
                var di = (y * width + x) * 4;
                switch (bpp)
                {
                    case 8:
                        pixels[di] = pixels[di + 1] = pixels[di + 2] = raw[si];
                        pixels[di + 3] = 255;
                        break;
                    case 24:
                    case 32:
                        pixels[di] = raw[si];
                        pixels[di + 1] = raw[si + 1];
                        pixels[di + 2] = raw[si + 2];
                        pixels[di + 3] = bpp == 32 ? raw[si + 3] : (byte)255;
                        break;
                    default: // 15/16 bpp: 5-5-5 or 5-6-5
                        var value = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(si, 2));
                        if (bpp == 16)
                        {
                            pixels[di] = (byte)(((value >> 11) & 31) * 255 / 31);
                            pixels[di + 1] = (byte)(((value >> 5) & 63) * 255 / 63);
                            pixels[di + 2] = (byte)((value & 31) * 255 / 31);
                        }
                        else
                        {
                            pixels[di] = (byte)(((value >> 10) & 31) * 255 / 31);
                            pixels[di + 1] = (byte)(((value >> 5) & 31) * 255 / 31);
                            pixels[di + 2] = (byte)((value & 31) * 255 / 31);
                        }
                        pixels[di + 3] = 255;
                        break;
                }
            }
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DecodeRle(byte[] data, int start, byte[] raw, int bytesPerPixel)
    {
        var cursor = start;
        var written = 0;
        while (written < raw.Length && cursor < data.Length)
        {
            var packet = data[cursor++];
            var count = (packet & 0x7F) + 1;
            var length = count * bytesPerPixel;
            if (written + length > raw.Length) break;
            if ((packet & 0x80) != 0)
            {
                if (cursor + bytesPerPixel > data.Length) break;
                for (var i = 0; i < count; i++)
                {
                    data.AsSpan(cursor, bytesPerPixel).CopyTo(raw.AsSpan(written));
                    written += bytesPerPixel;
                }
                cursor += bytesPerPixel;
            }
            else
            {
                if (cursor + length > data.Length) break;
                data.AsSpan(cursor, length).CopyTo(raw.AsSpan(written));
                cursor += length;
                written += length;
            }
        }
    }
}
