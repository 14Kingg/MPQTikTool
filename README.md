# MPQ Tik Tool

A Windows desktop asset browser for Warcraft III maps (.w3x/.w3m), inspired by Unity AssetStudio.

## Current milestone

- Open MPQ-based Warcraft III map archives through StormLib.
- Enumerate archive entries (including entries discovered through the archive's file table).
- Search and filter the resource list.
- Export one file or all listed files while preserving archive paths.
- Preview common text formats and standard image formats supported by WPF.
- Show a clear fallback for formats that need dedicated decoders (BLP/MDX/MDL, etc.).

## Requirements

- Windows 10/11 x64
- .NET 8 SDK
- StormLib x64 native binary named `StormLib.dll`

Place `StormLib.dll` beside the built executable (or somewhere on the DLL search path). StormLib is an external native dependency and is not bundled in this repository. Obtain/build it from the official StormLib project and comply with its license.

## Build

```powershell
dotnet restore
dotnet build .\src\MPQTikTool\MPQTikTool.csproj -c Release
dotnet run --project .\src\MPQTikTool\MPQTikTool.csproj
```

## Notes

An MPQ archive is not a ZIP file. This application delegates MPQ hash/block tables, compression, encryption and archive access to StormLib. Some maps omit `(listfile)`; in that case StormLib's wildcard enumeration can only expose names it can discover. A later milestone will add optional external listfiles and dedicated Warcraft III decoders.

## Roadmap

1. MPQ archive browsing and extraction (current foundation).
2. BLP texture decoder and richer image metadata.
3. MDL/MDX model preview with camera, animation and texture inspection.
4. Audio playback, object/data table viewers and dependency inspection.
5. Batch export profiles, duplicate detection and map diagnostics.
