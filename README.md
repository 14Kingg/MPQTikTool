# MPQ Tik Tool

Warcraft III 地图资源浏览器（Windows x64），面向 W3X/W3M/MPQ 地图资源检查与导出。

## 下载与运行

1. 打开仓库的 **Releases** 页面，下载 `MPQTikTool-win-x64.zip`。
2. 解压 ZIP。
3. 双击 `MPQTikTool.exe`。

Release 包为 .NET 8 self-contained x64 发布包，已随包携带 StormLib 及其运行时 DLL；目标电脑不需要单独安装 .NET SDK 或手工配置 StormLib。

## 当前功能

- 打开 Warcraft III `.w3x` / `.w3m` 和标准 MPQ 档案。
- 使用 StormLib 枚举 MPQ 文件表，保留地图内资源路径。
- 文件名、扩展名、资源类型搜索与筛选。
- 单文件导出、整图批量导出（保留目录结构并阻止路径穿越）。
- 文本预览：TXT、LUA、JASS、AI、FDF、INI、SLK、TOC、XML、JSON、MD、MDL。
- 常见图片预览：PNG、JPG、BMP、GIF、TIF。
- BLP2 纹理预览：调色板、DXT1、DXT3、DXT5、BGRA mip level 0。
- 无专用预览器的二进制资源可直接原样导出。

## 当前限制

- BLP1/JPEG BLP 变体尚未解码。
- MDX 目前仍未提供真正的 3D 模型渲染、骨骼动画和材质预览；可导出原始模型文件。
- 音频播放、物编数据结构化编辑、资源依赖图、地图内部加密资源的专用处理尚未完成。
- 地图缺少 `(listfile)` 时，StormLib 能否给出可识别文件名取决于 MPQ 内部表和伪文件名能力；不能保证恢复原始文件名。未知资源仍应通过批量导出保留。
- 加密且需要特定文件名作为解密密钥的资源，若无法推导文件名，可能无法正确提取。

## 本地构建

需要 Windows 10/11 x64、.NET 8 SDK、Visual Studio C++ Build Tools（仅构建 StormLib 原生依赖时需要）。

```powershell
dotnet restore .\src\MPQTikTool\MPQTikTool.csproj
dotnet build .\src\MPQTikTool\MPQTikTool.csproj -c Release
dotnet run --project .\src\MPQTikTool\MPQTikTool.csproj
```

## GitHub Actions

- 推送到 `main` 或 PR 会执行 Windows x64 编译。
- 构建任务会安装 x64 StormLib，发布 self-contained 应用并将 StormLib 及依赖 DLL 放入 ZIP。
- `workflow_dispatch` 可手动运行构建并发布预览版。
- 每次推送到 `main` 且构建成功后，都会自动创建一个 GitHub Preview Release，并上传 `MPQTikTool-win-x64.zip`。

## 第三方组件

MPQ 读写由 [StormLib](https://github.com/ladislav-zezula/StormLib) 提供。StormLib 使用其上游许可证；发布包会附带上游许可证文本。
