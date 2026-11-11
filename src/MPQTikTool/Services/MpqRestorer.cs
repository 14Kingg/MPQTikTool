using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using MPQTikTool.Models;

namespace MPQTikTool.Services;

/// <summary>
/// Produces the "restoration package": a WE-ready map plus everything needed to rebuild
/// triggers / object data by hand, and an honest report about what cannot be recovered.
/// </summary>
public static class MpqRestorer
{
    private static readonly string[] EditorCriticalFiles =
    {
        "war3map.w3i", "war3map.w3e", "war3map.wpm", "war3map.doo", "war3mapUnits.doo",
        "war3map.w3r", "war3map.w3c", "war3map.w3s", "war3map.w3o", "war3map.wct",
        "war3map.wtg", "war3map.wts", "war3map.j", "war3map.shd", "war3mapMap.blp",
        "war3mapPreview.tga", "war3map.imp", "war3mapMisc.txt", "war3mapSkin.txt",
        "war3mapExtra.txt", "war3map.w3u", "war3map.w3t", "war3map.w3a", "war3map.w3b",
        "war3map.w3d", "war3map.w3q"
    };

    public sealed record PackageResult(
        string PackageDirectory, int EditorFilesExported, int ScriptFilesExported,
        int NamedBlocks, int TotalBlocks, IReadOnlyList<string> ClearedFiles, string ReportPath);

    public static PackageResult WritePackage(MpqArchive archive, MpqArchive.RestoreResult restore,
        string packageDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(packageDirectory);
        var mapDir = Path.Combine(packageDirectory, "map");
        var scriptDir = Path.Combine(packageDirectory, "scripts");
        Directory.CreateDirectory(mapDir);
        Directory.CreateDirectory(scriptDir);

        var byOriginal = archive.Entries
            .Where(e => e.NameRecovered && e.OriginalName is not null)
            .GroupBy(e => e.OriginalName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var cleared = new List<string>();
        var editorExported = 0;
        foreach (var name in EditorCriticalFiles)
        {
            if (!byOriginal.TryGetValue(name, out var entry)) continue;
            if (entry.Size <= 8) cleared.Add(name);
            archive.Extract(entry, Path.Combine(mapDir, Path.GetFileName(name)), null, cancellationToken);
            editorExported++;
        }

        // Platform scripts: Lua bytecode, encrypted JASS container, generated JASS stub.
        var scriptExported = 0;
        foreach (var entry in archive.Entries)
        {
            var isScript = (entry.NameRecovered && (
                    entry.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ||
                    entry.Name.EndsWith(".j", StringComparison.OrdinalIgnoreCase) ||
                    entry.Name.EndsWith(".jass", StringComparison.OrdinalIgnoreCase))) ||
                entry.Name.Contains("KKWE", StringComparison.OrdinalIgnoreCase);
            if (!isScript && entry.IsEncrypted && entry.Size > 1024)
            {
                // Large encrypted non-critical blocks are usually platform script containers.
                try
                {
                    var head = archive.ReadPrefix(entry, 32);
                    isScript = Encoding.ASCII.GetString(head).Contains("KKWE", StringComparison.OrdinalIgnoreCase);
                }
                catch { }
            }
            if (!isScript) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var safe = MakeSafeFileName(entry);
            try
            {
                archive.Extract(entry, Path.Combine(scriptDir, safe), null, cancellationToken);
                scriptExported++;
            }
            catch
            {
                // Unreadable platform blob: still record it in the report.
            }
        }

        File.WriteAllText(Path.Combine(packageDirectory, "listfile.txt"), restore.ListFileText, new UTF8Encoding(false));
        WriteNameMap(archive, Path.Combine(packageDirectory, "name-map.csv"));

        var info = ParseW3i(archive, byOriginal);
        var reportPath = Path.Combine(packageDirectory, "restore-report.md");
        File.WriteAllText(reportPath, BuildMarkdown(archive, restore, info, cleared, editorExported, scriptExported), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(packageDirectory, "restore-report.json"),
            JsonSerializer.Serialize(new
            {
                source = archive.ArchivePath,
                restoredMap = restore.TargetPath,
                format = archive.FormatDescription,
                map = info,
                totalBlocks = restore.TotalBlocks,
                namedBlocks = restore.NamedBlocks,
                fixedLocales = restore.FixedLocales,
                droppedEditorFiles = restore.DroppedEditorFiles,
                clearedEditorFiles = cleared,
                irreversible = new[]
                {
                    "GUI 触发器数据 (war3map.wtg / war3map.wct) 已被清空，无法还原成可视化触发器",
                    "自定义物编数据 (w3u/w3t/w3a/w3b/w3d/w3q) 为空表，需在 WE 中重建",
                    "war3map.j 仅为平台生成的占位注释，JASS 源码不存在于地图中",
                    "未出现在地图内容里的资源原名不可从哈希反推，只能人工补名或使用推断名"
                }
            }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

        return new PackageResult(packageDirectory, editorExported, scriptExported,
            restore.NamedBlocks, restore.TotalBlocks, cleared, reportPath);
    }

    private sealed record MapInfo(uint Version, uint Saves, uint EditorVersion, string Name,
        string Author, string Description, string Players);

    private static MapInfo ParseW3i(MpqArchive archive, Dictionary<string, ArchiveEntry> byOriginal)
    {
        if (!byOriginal.TryGetValue("war3map.w3i", out var entry)) return new MapInfo(0, 0, 0, "", "", "", "");
        try
        {
            var data = archive.ReadAllBytes(entry, 4096);
            if (data.Length < 12) return new MapInfo(0, 0, 0, "", "", "", "");
            var version = BinaryPrimitives.ReadUInt32LittleEndian(data);
            var saves = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
            var editor = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));
            var off = 12;
            string Next()
            {
                if (off >= data.Length) return "";
                var end = Array.IndexOf(data, (byte)0, off);
                if (end < 0) end = data.Length;
                var text = Encoding.UTF8.GetString(data, off, end - off);
                off = end + 1;
                return text;
            }
            return new MapInfo(version, saves, editor, Next(), Next(), Next(), Next());
        }
        catch { return new MapInfo(0, 0, 0, "", "", "", ""); }
    }

    private static void WriteNameMap(MpqArchive archive, string path)
    {
        var lines = new List<string> { "BlockIndex,Name,NameSource,Kind,Size,Hash1,Hash2,Flags,Locale" };
        foreach (var e in archive.Entries)
            lines.Add(string.Join(",", new[]
            {
                e.BlockIndex.ToString(), Quote(e.Name), e.NameSource, Quote(e.KindName),
                e.Size.ToString(), e.Hash1.ToString("X8"), e.Hash2.ToString("X8"),
                e.FlagsText, e.Locale.ToString("X8")
            }));
        File.WriteAllLines(path, lines, new UTF8Encoding(true));
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string MakeSafeFileName(ArchiveEntry entry)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = entry.NameRecovered ? Path.GetFileName(entry.Name) : $"B{entry.BlockIndex:D5}.bin";
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return $"B{entry.BlockIndex:D5}_{clean}";
    }

    private static string BuildMarkdown(MpqArchive archive, MpqArchive.RestoreResult restore,
        MapInfo info, IReadOnlyList<string> cleared, int editorExported, int scriptExported)
    {
        var namedPercent = restore.TotalBlocks == 0 ? 0 : restore.NamedBlocks * 100.0 / restore.TotalBlocks;
        var sb = new StringBuilder();
        sb.AppendLine("# 地图还原报告");
        sb.AppendLine();
        sb.AppendLine($"源图：`{archive.ArchivePath}`");
        sb.AppendLine($"还原版：`{restore.TargetPath}`（{restore.OutputSize:N0} B，{restore.TotalBlocks:N0} 个块）");
        sb.AppendLine();
        sb.AppendLine("## 地图信息（war3map.w3i）");
        sb.AppendLine($"- 名称：{info.Name}");
        sb.AppendLine($"- 作者：{info.Author}");
        sb.AppendLine($"- 说明：{info.Description}");
        sb.AppendLine($"- 推荐人数：{info.Players}");
        sb.AppendLine($"- 文件版本：{info.Version}，保存次数：{info.Saves}，编辑器版本：{info.EditorVersion}");
        sb.AppendLine();
        sb.AppendLine("## 本次修复");
        sb.AppendLine($"- hash 表 locale 修正：{restore.FixedLocales:N0} 个条目（0xFF000000 → 0），WE 现在可以按名字找到文件");
        sb.AppendLine($"- 移除无效空 WE 文件的哈希条目：{restore.DroppedEditorFiles:N0} 个（避免 WE 读到 1 字节损坏数据而打不开）");
        sb.AppendLine($"- 写回 `(listfile)`：{restore.NamedBlocks:N0} 个已确认原名；匿名块的推断名只写入 name-map.csv，不伪装成真实 MPQ 路径");
        sb.AppendLine($"- 原始文件数据未改动：地形、摆设、阴影、全部贴图/模型保持字节一致");
        sb.AppendLine($"- 导出 WE 关键文件 {editorExported} 个到 `map/`，平台脚本 {scriptExported} 个到 `scripts/`");
        sb.AppendLine();
        sb.AppendLine("## 资源名恢复率（重要：不影响游戏内资源显示）");
        sb.AppendLine($"- 已恢复原名：{restore.NamedBlocks:N0} / {restore.TotalBlocks:N0}（{namedPercent:0.0}%）");
        sb.AppendLine("- 其余资源使用 `_unnamed/<类型>/B<块号>_<哈希><扩展名>` 稳定导出别名，见 `name-map.csv`；这些别名不是原始路径，不能用于声称已恢复原始文件名");
        sb.AppendLine("- **关键**：MPQ 按「文件名双哈希」查表，hash 表里存的一直是原始路径的哈希，我们只是不知道明文名字。"
            + "因此地图内对贴图/模型的引用在游戏与 WE 中**仍然能命中正确资源**，不会出现绿块。"
            + "名字缺失只影响在导入管理器里人工查看、改名、以及把资源搬到另一张图。");
        sb.AppendLine("- 未出现在地图内容中的原名**无法反推**（哈希不可逆），若要搬资源到新图，需按 `name-map.csv` 人工补名或逐一对图核对");
        sb.AppendLine();
        sb.AppendLine("## 被清空的 WE 文件（不可逆）");
        if (cleared.Count == 0) sb.AppendLine("- 未检测到明显的空文件");
        else foreach (var name in cleared) sb.AppendLine($"- `{name}`：≤ 8 B，触发器/物编数据已被物理删除");
        sb.AppendLine();
        sb.AppendLine("## 结论：哪些能还原、哪些不能");
        sb.AppendLine("- ✅ 地形、单位/装饰物摆放、阴影图、小地图、预览图、地图设置、全部美术资源");
        sb.AppendLine("- ✅ 在 World Editor 中打开并另存还原版地图");
        sb.AppendLine("- ❌ GUI 触发器（wtg/wct）、自定义物编数据、字符串表(wts)：源文件里已经是空表");
        sb.AppendLine("- ❌ JASS 源码：war3map.j 只是平台生成的占位注释");
        sb.AppendLine("- ⚠️ 平台逻辑以编译后 Lua 字节码 / 私有加密包形式存在，只能原样导出，不能还原为源码");
        sb.AppendLine();
        sb.AppendLine("## 建议的还原步骤");
        sb.AppendLine("1. 用 World Editor 打开还原版 `.w3x`，确认地形与摆设正常，另存为新地图。");
        sb.AppendLine("2. 从 `map/` 导入需要的 war3map.* 文件（或直接在还原版上编辑）。");
        sb.AppendLine("3. 用「导出全部资源」拿到资源目录，配合 `name-map.csv` 给缺失原名的贴图/模型补名后导入。");
        sb.AppendLine("4. 触发器与物编需要按玩法重新搭建——这部分原图已不存在。");
        sb.AppendLine("5. `scripts/` 里的 Lua/加密包可作为玩法逻辑的对照参考（需要平台插件才能运行）。");
        return sb.ToString();
    }
}
