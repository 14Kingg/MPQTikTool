# MPQ Tik Tool

通用 Warcraft III 地图资源浏览器（Windows x64）。面向 `.w3x` / `.w3m` / `.mpq`：
**加载任意地图 → 预览任意资源 → 导出单个 / 筛选 / 全部资源 → 复用导出结果**。

不再依赖 StormLib 原生 DLL。MPQ 读取由仓库内纯 C# 引擎 `src/MPQTikTool/Services/MpqArchive.cs` 完成。

## 下载与运行

1. 打开仓库 **Releases**，下载 `MPQTikTool-win-x64.zip`。
2. 解压后双击 `MPQTikTool.exe`（.NET 8 self-contained，无需安装 SDK）。

也可以在本地直接运行：

```powershell
dotnet run --project .\src\MPQTikTool\MPQTikTool.csproj
```

## 为什么换成自研 MPQ 引擎

很多平台版地图（例如 KK 平台版本）**没有内置 `(listfile)`**，哈希表 locale 还被改成 `0xFF000000`，
文件名被抹掉。StormLib 的 `SFileFindFirstFile` / `SFileOpenFileEx` 是按名字枚举和打开的，
这类地图只能列出极少数已知文件，导出全部资源根本做不到。

本工具按 **block-table 索引** 寻址，而不是按名字：

- 解析 W3X 头 / MPQ 用户数据头，自动定位 `MPQ\x1A`；当前传统 Hash/Block Table 解析覆盖 MPQ v1/v2/v3 的常见布局，**尚未实现 MPQ v4 的 HET/BET 表解析**。不能把 v4 当成已完整支持。
- 解密 hash table / block table，枚举**每一个块**，包含没有名字、被改名、locale 异常的块。
- zlib / raw deflate 扇区解压、压缩掩码、单块文件、CRC 扇区表。
- 加密文件：有原名时按 Storm 文件名哈希推导密钥；没有原名时，对带扇区偏移表的加密文件，可利用表头已知明文尝试推导密钥；加密单块文件在缺少原名时仍无法通用恢复。
- 名称恢复三级策略：内置标准名 → 导入外部 listfile（哈希匹配）→ 从 JASS/SLK/INI/MDX 内容中
  挖掘路径并按哈希匹配。剩余块给稳定的推断名 `_unnamed/<类型>/B<块号>_<哈希><扩展名>`。
- 类型识别按**文件头内容嗅探**，不依赖文件名：BLP1/BLP2、MDX、TGA、PNG/JPG/BMP/GIF/TIF、
  WAV/MP3/OGG、文本、W3 二进制、十六进制兜底。

## 功能

| 功能 | 说明 |
| --- | --- |
| 打开地图 | 支持任意 w3x/w3m/mpq；自动定位 MPQ 头；显示格式、块数、扇区大小 |
| 资源列表 | 名称 / 原名 or 推断 / 类型 / 格式 / 大小 / 块索引，可搜索、按类型与名称来源筛选 |
| 任意资源预览 | 图片（BLP1 JPEG/调色板、BLP2、TGA、常见格式）、MDX Chunk 结构、文本、音频、十六进制；可切换「自动 / 文本 / 十六进制」 |
| 分析资源 | 识别全部块的类型；从地图内容恢复文件名；「深度恢复文件名」扩大扫描范围 |
| 导出选中 | 单选直接另存为；多选导出到目录 |
| 导出筛选结果 | 只导出当前列表里的资源 |
| 导出全部资源 | 导出**所有块**（含匿名块），流式解压、进度显示、可取消，防路径穿越 |
| **还原地图** | 修复被保护的 hash 表 locale、写回 `(listfile)`、剔除损坏的空 WE 文件，生成 World Editor 可打开的 `.w3x`，并输出还原工程包与不可逆项报告 |
| 复用清单 | 每次批量导出生成 `_MPQTikTool_ExportManifest.csv`、`_MPQTikTool_Manifest.json`、`_MPQTikTool_ExportErrors.txt`，含原名/推断别名、类型、大小、Flags、Locale、双哈希、块索引、成功失败原因 |

## 目标样本实测

`不思议作战源文件KK平台版本.w3x`（570,233,086 B，17,389 个块，无 `(listfile)`，2 个加密块）：

| 项目 | 结果 |
| --- | --- |
| 打开 / 建表 | 0.08 s |
| 全部块类型识别 | 3.7 s |
| 内容恢复文件名 | 2,195 个（+22 个标准名），17 s |
| 全量导出校验 | **17,389 块 / 748,452,978 B / 0 失败** |
| 资源构成 | BLP1 13,821、文本 2,481、MDX 850、TGA 132、MP3 27、WAV 6 等 |
| 加密块 | `war3map.j` 等 2 个块无需文件名即可解密 |
| 图片预览 | BLP1 JPEG / 调色板 / TGA 均解码出正常图像 |

## 还原地图（deprotect）

「打开地图 → 分析资源 → **还原地图**」会生成一份新的 `.w3x`，并在同目录生成 `<名字>_restore/` 工程包：

```
map/                 22 个 WE 关键文件（war3map.*），按原名导出，可直接导入新地图
scripts/             平台 Lua 字节码 / 加密 JASS 包 / 生成的 JASS 占位脚本
listfile.txt         写回还原版地图的完整 (listfile)
name-map.csv         全部块：块号、原名或推断名、类型、大小、双哈希、flags、locale
restore-report.md    还原结论、被清空文件清单、不可逆项、重建步骤
restore-report.json  同上，便于程序处理
```

还原器**不重新压缩任何文件**：复制原始档案 → 修 hash 表 locale（`0xFF000000` → `0`）→ 把失效的 1 字节 `wtg/wct/wts/w3r/w3c` 哈希条目标记为删除（WE 会生成干净的空文件）→ 追加 `(listfile)` 文件与新块表。所有原始数据字节不变。

### 目标样本的真实情况（先看清上限再动手）

`不思议作战源文件KK平台版本.w3x` 是 **KKWE 平台地图**，不是传统 JASS 图：

| 项目 | 状态 |
| --- | --- |
| 地形 / 摆设 / 阴影 / 小地图 / 预览图 / 地图设置 | ✅ 完整（w3e 260 KB、doo 126 KB、shd 590 KB、wpm 590 KB、w3i 761 B） |
| 美术资源 | ✅ 17,389 块全部可导出（BLP1 13,821、MDX 850、TGA 132 等） |
| GUI 触发器 / 物编 / 字符串表 | ❌ `wtg/wct/wts/w3r` 均 1 B，`w3c` 8 B，`w3u` 1.3 KB，`w3t/w3a/w3b/w3q` 12 B —— **已被物理清空，不可逆** |
| JASS 源码 | ❌ `war3map.j` 仅 116 B 的 `GENERATED WITH KKWE` 占位注释 |
| 平台逻辑 | ⚠️ `plugin_main2.lua` 是编译后的 Lua 字节码；138 KB 的 `KKWE PLUG-IN ENCRYPT JASS!` 是平台私有加密容器，只能原样导出 |
| 保护手法 | hash 表 17,387 个条目 locale 改成 `0xFF000000`，只有 `war3map.j` 与 KKWE 加密包保持 locale 0（游戏能读、WE/MPQ 工具找不到） |
| 资源原名 | 全字符串哈希爆破上限 2,219 / 17,389（12.8%）；其余无法反推 |

**结论**：这张图的**逻辑层数据本来就不在文件里**，任何工具都无法把它还原成带触发器的可编辑工程。能还原的是「WE 可打开的地图骨架 + 地形摆设 + 全部美术资源 + 平台脚本原件」，触发器和物编需要按玩法重建。

好消息是 MPQ 按文件名双哈希寻址，hash 表中保存的一直是**原始路径的哈希**——我们只是不知道明文名字。所以修复 locale 后，地图内对贴图/模型的引用在游戏和 WE 中仍然命中正确资源，不会变绿块；名字缺失只影响人工查看、改图和把资源搬到另一张地图。

## 复用引擎（给其他项目用）

`MpqArchive` 不依赖 WPF，可单独拷进任何 .NET 8 项目（配 `System.Text.Encoding.CodePages`）：

```csharp
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
using var archive = MpqArchive.Open(@"D:\maps\any.w3x");

archive.DetectKinds();                       // 按内容识别类型
archive.RecoverNamesFromContent();           // 从地图文本/MDX 恢复文件名
archive.AddListFile(@"D:\listfile.txt");     // 可选：外部文件名库

foreach (var entry in archive.Entries)
{
    byte[] head = archive.ReadPrefix(entry, 4096);   // 预览 / 嗅探
    // archive.Extract(entry, $@"D:\out\{entry.Name}"); // 导出
    // archive.CopyTo(entry, outputStream);            // 流式写出
}

var restore = archive.RestoreTo(@"D:\maps\restored.w3x");   // 修 locale + 写 (listfile)
MpqRestorer.WritePackage(archive, restore, @"D:\maps\restore_package");
```

## 当前限制

- MDX 只展示 Chunk 结构，不做 3D 渲染 / 骨骼动画。
- 音频播放依赖 Windows Media Foundation 已安装的解码器（OGG/部分 MP3 可能只能导出，不能播放）。
- BLP1 JPEG 的独立 alpha 平面仅在 EOI 后存在且尺寸匹配时叠加；BLP1 非标准 alphaBits 字段按无 alpha 处理。
- 加密的「单块（single-unit）」文件若既没有原名又无法导入 listfile，无法推导密钥（会写失败报告，不影响其他资源）。
- 压缩算法目前尚未覆盖 MPQ 全部压缩掩码组合；PKWARE implode、BZip2、Huffman、ADPCM、Sparse 等需要继续实现并用样本验证。MPQ patch 文件尚未实现；不支持或解压长度校验失败的块会进入失败报告。
- MPQ v4 的 HET/BET 扩展表尚未实现；还原器也只支持传统 32 位表布局和 4 GB 以内档案。
- 导出的是解密/解压后的原始文件内容，不是 MPQ 内部压缩扇区的原样复制。

## 本地构建

需要 Windows 10/11 x64 + .NET 8 SDK（不需要 Visual Studio C++ Build Tools 或 StormLib）。

```powershell
dotnet restore .\src\MPQTikTool\MPQTikTool.csproj
dotnet build .\src\MPQTikTool\MPQTikTool.csproj -c Release
```

## GitHub Actions

推送 `main` 或 PR 触发 Windows x64 编译；不再需要 vcpkg / StormLib。
每次推送 `main` 构建成功后自动创建 Preview Release 并上传 `MPQTikTool-win-x64.zip`。

## 第三方组件

无原生依赖、无第三方 NuGet 运行时组件（仅 `System.Text.Encoding.CodePages` 用于中文文件名解码）。
MPQ 格式参考 [StormLib](https://github.com/ladislav-zezula/StormLib) 的公开格式定义，但代码为独立实现。
