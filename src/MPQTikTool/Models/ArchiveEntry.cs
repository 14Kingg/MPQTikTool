using System.IO;

namespace MPQTikTool.Models;

public enum ResourceKind
{
    Unknown,
    Text,
    Blp1,
    Blp2,
    Mdx,
    Png,
    Jpeg,
    Bmp,
    Gif,
    Tiff,
    Tga,
    Wav,
    Mp3,
    Ogg,
    W3Binary,
    Other
}

/// <summary>
/// One MPQ block. A block stays usable even when its original filename is unknown,
/// because every read/export is addressed by BlockIndex instead of by name.
/// </summary>
public sealed class ArchiveEntry
{
    public int BlockIndex { get; init; }
    public string Name { get; set; } = string.Empty;
    public string? OriginalName { get; set; }
    public bool NameRecovered => OriginalName is not null;

    public uint Size { get; init; }
    public uint CompressedSize { get; init; }
    public uint Flags { get; init; }
    public uint Locale { get; init; }
    public ulong FilePos { get; init; }
    public uint Hash1 { get; init; }
    public uint Hash2 { get; init; }
    public uint? FileKey { get; set; }

    public ResourceKind Kind { get; set; } = ResourceKind.Unknown;
    public string Extension => Path.GetExtension(Name).ToLowerInvariant();

    public string DisplaySize => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024d:0.##} KB",
        _ => $"{Size / (1024d * 1024d):0.##} MB"
    };

    public string NameSource => NameRecovered ? "原名" : "推断";

    public string FlagsText => $"0x{Flags:X8}";

    public bool IsEncrypted => (Flags & 0x00010000) != 0;

    public string Category => Kind switch
    {
        ResourceKind.Blp1 or ResourceKind.Blp2 or ResourceKind.Png or ResourceKind.Jpeg or
        ResourceKind.Bmp or ResourceKind.Gif or ResourceKind.Tiff or ResourceKind.Tga => "图片",
        ResourceKind.Mdx => "模型",
        ResourceKind.Wav or ResourceKind.Mp3 or ResourceKind.Ogg => "音频",
        ResourceKind.Text => "文本/脚本",
        ResourceKind.W3Binary => "地图/物编数据",
        _ => "其他"
    };

    public string KindName => Kind switch
    {
        ResourceKind.Blp1 => "BLP1 纹理",
        ResourceKind.Blp2 => "BLP2 纹理",
        ResourceKind.Mdx => "MDX 模型",
        ResourceKind.Png => "PNG",
        ResourceKind.Jpeg => "JPEG",
        ResourceKind.Bmp => "BMP",
        ResourceKind.Gif => "GIF",
        ResourceKind.Tiff => "TIFF",
        ResourceKind.Tga => "TGA",
        ResourceKind.Wav => "WAV",
        ResourceKind.Mp3 => "MP3",
        ResourceKind.Ogg => "OGG",
        ResourceKind.Text => "文本",
        ResourceKind.W3Binary => "W3 二进制",
        _ => "二进制"
    };
}
