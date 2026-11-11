namespace MPQTikTool.Models;

public sealed record ArchiveEntry(
    string Name,
    uint Size,
    uint Flags,
    uint CompressedSize,
    uint Locale)
{
    public string Extension => Path.GetExtension(Name).ToLowerInvariant();
    public string DisplaySize => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024d:0.##} KB",
        _ => $"{Size / (1024d * 1024d):0.##} MB"
    };

    public string Category => Extension switch
    {
        ".blp" or ".tga" or ".bmp" or ".png" or ".jpg" or ".jpeg" or ".dds" or ".gif" => "图片",
        ".mdx" or ".mdl" => "模型",
        ".wav" or ".mp3" or ".ogg" => "音频",
        ".lua" or ".j" or ".ai" or ".fdf" or ".txt" or ".ini" or ".slk" or ".toc" or ".xml" => "文本/脚本",
        ".w3u" or ".w3t" or ".w3a" or ".w3h" or ".w3q" or ".w3d" or ".w3b" => "物编数据",
        _ => "其他"
    };
}
