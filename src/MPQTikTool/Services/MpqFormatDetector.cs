using System.Buffers.Binary;
using System.IO;
using System.Text;
using MPQTikTool.Models;

namespace MPQTikTool.Services;

/// <summary>Content-based resource detection; filenames are optional in protected maps.</summary>
public static class MpqFormatDetector
{
    public static ResourceKind Sniff(ReadOnlySpan<byte> head, string? recoveredName = null)
    {
        var ext = recoveredName is null ? string.Empty : Path.GetExtension(recoveredName).ToLowerInvariant();
        if (head.Length >= 4)
        {
            if (StartsWith(head, "BLP1"u8)) return ResourceKind.Blp1;
            if (StartsWith(head, "BLP2"u8)) return ResourceKind.Blp2;
            if (StartsWith(head, "MDLX"u8)) return ResourceKind.Mdx;
            if (StartsWith(head, "\x89PNG"u8)) return ResourceKind.Png;
            if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return ResourceKind.Jpeg;
            if (StartsWith(head, "BM"u8)) return ResourceKind.Bmp;
            if (StartsWith(head, "GIF8"u8)) return ResourceKind.Gif;
            if (StartsWith(head, "II*\0"u8) || StartsWith(head, "MM\0*"u8)) return ResourceKind.Tiff;
            if (StartsWith(head, "RIFF"u8) && head.Length >= 12 && StartsWith(head[8..], "WAVE"u8)) return ResourceKind.Wav;
            if (StartsWith(head, "OggS"u8)) return ResourceKind.Ogg;
            if (StartsWith(head, "ID3"u8) || (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0)) return ResourceKind.Mp3;
            if (StartsWith(head, "W3do"u8) || StartsWith(head, "W3E!"u8) || StartsWith(head, "W3et"u8) ||
                StartsWith(head, "MP3W"u8) || StartsWith(head, "3RAW"u8)) return ResourceKind.W3Binary;
        }

        if (LooksLikeTga(head, ext)) return ResourceKind.Tga;
        if (LooksLikeText(head)) return ResourceKind.Text;
        if (ext is ".w3u" or ".w3t" or ".w3a" or ".w3b" or ".w3d" or ".w3q" or ".w3i" or ".w3e" or
            ".w3r" or ".w3c" or ".w3s" or ".w3o" or ".wpm" or ".doo" or ".wct" or ".wtg" or ".wts" or ".imp")
            return ResourceKind.W3Binary;
        return ResourceKind.Unknown;
    }

    public static string SuggestedExtension(ResourceKind kind, ReadOnlySpan<byte> head)
    {
        switch (kind)
        {
            case ResourceKind.Blp1:
            case ResourceKind.Blp2: return ".blp";
            case ResourceKind.Mdx: return ".mdx";
            case ResourceKind.Png: return ".png";
            case ResourceKind.Jpeg: return ".jpg";
            case ResourceKind.Bmp: return ".bmp";
            case ResourceKind.Gif: return ".gif";
            case ResourceKind.Tiff: return ".tif";
            case ResourceKind.Tga: return ".tga";
            case ResourceKind.Wav: return ".wav";
            case ResourceKind.Mp3: return ".mp3";
            case ResourceKind.Ogg: return ".ogg";
            case ResourceKind.W3Binary: return ".bin";
            case ResourceKind.Text:
                var text = Encoding.Latin1.GetString(head[..Math.Min(head.Length, 4096)]);
                if (text.Contains("function ", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("globals", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("endfunction", StringComparison.OrdinalIgnoreCase)) return ".j";
                if (text.TrimStart().StartsWith('[')) return ".ini";
                if (text.Contains("ID")) return ".slk";
                return ".txt";
            default: return ".bin";
        }
    }

    /// <summary>Stable, unique path for blocks whose original name could not be recovered.</summary>
    public static string BuildSyntheticName(int blockIndex, uint hash1, ResourceKind kind, string extension)
    {
        var folder = kind switch
        {
            ResourceKind.Blp1 or ResourceKind.Blp2 or ResourceKind.Png or ResourceKind.Jpeg or
            ResourceKind.Bmp or ResourceKind.Gif or ResourceKind.Tiff or ResourceKind.Tga => "textures",
            ResourceKind.Mdx => "models",
            ResourceKind.Wav or ResourceKind.Mp3 or ResourceKind.Ogg => "audio",
            ResourceKind.Text => "text",
            ResourceKind.W3Binary => "w3data",
            _ => "other"
        };
        return $"_unnamed/{folder}/B{blockIndex:D5}_{hash1 & 0xFFFFFF:X6}{extension}";
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> magic) =>
        data.Length >= magic.Length && data[..magic.Length].SequenceEqual(magic);

    private static bool LooksLikeTga(ReadOnlySpan<byte> head, string ext)
    {
        if (head.Length < 18) return false;
        var type = head[2];
        if (type is not (1 or 2 or 3 or 9 or 10 or 11)) return false;
        var width = BinaryPrimitives.ReadUInt16LittleEndian(head[12..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(head[14..]);
        var bpp = head[16];
        if (width is 0 or > 8192 || height is 0 or > 8192) return false;
        if (bpp is not (8 or 15 or 16 or 24 or 32)) return false;
        return ext == ".tga" || head[1] <= 1;
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        if (head.Length < 4) return false;
        var sample = head[..Math.Min(head.Length, 512)];
        var printable = 0;
        foreach (var b in sample)
        {
            if (b is 9 or 10 or 13 || b >= 32 && b < 127) printable++;
            else if (b == 0) return false;
        }
        return printable * 100 / sample.Length >= 92;
    }
}
