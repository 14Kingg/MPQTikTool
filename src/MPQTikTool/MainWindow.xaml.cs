using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MPQTikTool.Models;
using MPQTikTool.Services;

namespace MPQTikTool;

public partial class MainWindow : Window
{
    private MpqArchive? _archive;
    private readonly ObservableCollection<ArchiveEntry> _visibleEntries = new();
    private List<ArchiveEntry> _allEntries = new();
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _previewCts;
    private bool _busy;

    private ArchiveEntry? _previewEntry;
    private byte[]? _previewBytes;
    private string? _audioTempPath;

    public MainWindow()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        InitializeComponent();
        EntriesGrid.ItemsSource = _visibleEntries;
    }

    // ---------- open / analyse ----------

    private async void OpenMap_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Warcraft III 地图",
            Filter = "Warcraft III / MPQ (*.w3x;*.w3m;*.mpq)|*.w3x;*.w3m;*.mpq|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        await RunBusyAsync(async ct =>
        {
            var archive = await Task.Run(() => MpqArchive.Open(dialog.FileName), ct);
            _archive?.Dispose();
            _archive = archive;
            _allEntries = archive.Entries.ToList();
            ClearPreview();
            MapPathText.Text = dialog.FileName;
            if (archive.V4Info is { } v4)
            {
                var h = v4.MpqHeader;
                MpqHeaderInfoText.Text =
                    $"格式：MPQ v4（version={h.FormatVersion}） · HeaderSize=0x{h.HeaderSize:X} · ArchiveSize64={h.ArchiveSize:N0} B · SectorShift={h.SectorShift}\n" +
                    $"Hash：0x{h.HashTableOffset:X} / {h.HashTableStoredSize:N0} B · Block：0x{h.BlockTableOffset:X} / {h.BlockTableStoredSize:N0} B · HiBlock：0x{h.HiBlockTableOffset:X} / {h.HiBlockTableStoredSize:N0} B\n" +
                    $"HET：0x{h.HetTableOffset:X} / {h.HetTableStoredSize:N0} B · BET：0x{h.BetTableOffset:X} / {h.BetTableStoredSize:N0} B · RawChunkSize={h.RawChunkSize:N0}\n" +
                    $"Header MD5：{(h.HeaderMd5Valid ? "通过" : "失败")} · Hash MD5：{h.HashTableMd5} · Block MD5：{h.BlockTableMd5}\n" +
                    $"Header HET MD5：{h.HetTableMd5} · BET MD5：{h.BetTableMd5}";
                MpqV4TablesGrid.ItemsSource = new[] { v4.HetInfo, v4.BetInfo }.Where(t => t is not null).ToList();
                MpqV4BlocksGrid.ItemsSource = v4.Blocks;
            }
            else
            {
                MpqHeaderInfoText.Text = $"格式：{archive.FormatDescription} · 当前使用传统 Hash/Block Table 解析。";
                MpqV4TablesGrid.ItemsSource = Array.Empty<MpqV4ArchiveParser.TableInfo>();
                MpqV4BlocksGrid.ItemsSource = Array.Empty<MpqV4ArchiveParser.BlockEntry>();
            }
            ApplyFilter();
            StatusText.Text = $"已发现 {_allEntries.Count:N0} 个资源块 · {archive.FormatDescription}";
            await AnalyseAsync(ct, detectKinds: true, prefixCap: 96 * 1024);
        });
    }

    private void LoadListFile_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureArchive()) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择外部文件名库",
            Filter = "Listfile (*.txt;*.list)|*.txt;*.list|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        var path = dialog.FileName;

        _ = RunBusyAsync(async ct =>
        {
            var count = await Task.Run(() => _archive!.AddListFile(path), ct);
            ApplyFilter();
            StatusText.Text = $"文件名库匹配成功：新增 {count:N0} 个原名（当前原名 {_allEntries.Count(x => x.NameRecovered):N0} 个）";
        });
    }

    private void Analyze_Click(object sender, RoutedEventArgs e) =>
        _ = RunBusyAsync(ct => AnalyseAsync(ct, detectKinds: true, prefixCap: 96 * 1024));

    private void DeepAnalyze_Click(object sender, RoutedEventArgs e) =>
        _ = RunBusyAsync(ct => AnalyseAsync(ct, detectKinds: false, prefixCap: 512 * 1024));

    private async Task AnalyseAsync(CancellationToken ct, bool detectKinds, int prefixCap)
    {
        if (_archive is null) return;
        var archive = _archive;
        var recovered = 0;
        await Task.Run(() =>
        {
            if (detectKinds)
                archive.DetectKinds((cur, total) => ReportProgress("识别资源类型", cur, total), ct);
            recovered = archive.RecoverNamesFromContent(prefixCap,
                (cur, total) => ReportProgress("从地图内容恢复文件名", cur, total), ct);
        }, ct);

        ApplyFilter();
        StatusText.Text = $"分析完成：{archive.Entries.Count:N0} 个资源块，原名 {_allEntries.Count(x => x.NameRecovered):N0} 个，本轮新增 {recovered:N0} 个";
    }

    // ---------- filtering ----------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchBox?.Text?.Trim() ?? string.Empty;
        var category = (CategoryBox?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部类型";
        var source = (SourceBox?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部来源";

        var filtered = _allEntries.Where(x =>
            (category == "全部类型" || x.Category == category) &&
            (source == "全部来源" || x.NameSource == source) &&
            (query.Length == 0 ||
             x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             x.Extension.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             x.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             x.KindName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             x.BlockIndex.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        _visibleEntries.Clear();
        foreach (var entry in filtered) _visibleEntries.Add(entry);
        CountText.Text = $"{filtered.Count:N0} / {_allEntries.Count:N0} 个资源";
    }

    // ---------- preview ----------

    private async void EntriesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_busy || _archive is null) return;
        if (EntriesGrid.SelectedItem is ArchiveEntry entry) await PreviewEntryAsync(entry);
    }

    private void PreviewMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _previewBytes is null) return;
        RenderPreview(CurrentMode());
    }

    private string CurrentMode() =>
        (PreviewModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "自动";

    private async Task PreviewEntryAsync(ArchiveEntry entry)
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        ClearPreview();
        PreviewName.Text = entry.Name;
        PreviewInfo.Text = $"{entry.Category} · {entry.KindName} · {entry.DisplaySize} · 块 {entry.BlockIndex} · " +
                          $"Flags {entry.FlagsText} · Locale 0x{entry.Locale:X8} · Hash {entry.Hash1:X8}/{entry.Hash2:X8}" +
                          (entry.IsEncrypted ? " · 加密" : string.Empty) +
                          $"\n解密：{entry.DecryptionStatus} · 解压：{entry.DecompressionStatus} · CRC：{entry.CrcStatus}";

        var cap = PreviewCap(entry.Kind);
        byte[] bytes;
        try
        {
            bytes = await Task.Run(() => _archive!.ReadAllBytes(entry, cap), cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            FallbackPreview.Text = $"读取失败：{ex.Message}\n\n该资源仍可尝试导出（会写出解压后的原始数据）。";
            FallbackPreview.Visibility = Visibility.Visible;
            return;
        }
        if (cts.IsCancellationRequested) return;
        _previewEntry = entry;
        _previewBytes = bytes;
        RenderPreview(CurrentMode());
    }

    private static int PreviewCap(ResourceKind kind) => kind switch
    {
        ResourceKind.Blp1 or ResourceKind.Blp2 or ResourceKind.Png or ResourceKind.Jpeg or
        ResourceKind.Bmp or ResourceKind.Gif or ResourceKind.Tiff or ResourceKind.Tga or ResourceKind.Mdx => 32 * 1024 * 1024,
        ResourceKind.Wav or ResourceKind.Mp3 or ResourceKind.Ogg => 128 * 1024 * 1024,
        ResourceKind.Text => 4 * 1024 * 1024,
        _ => 128 * 1024
    };

    private void RenderPreview(string mode)
    {
        HidePreviewPanels();
        if (_previewEntry is null || _previewBytes is null) return;
        var entry = _previewEntry;
        var bytes = _previewBytes;
        var kind = entry.Kind;
        var effective = mode switch
        {
            "文本" => "文本",
            "十六进制" => "十六进制",
            _ => kind switch
            {
                ResourceKind.Blp1 or ResourceKind.Blp2 or ResourceKind.Png or ResourceKind.Jpeg or
                ResourceKind.Bmp or ResourceKind.Gif or ResourceKind.Tiff or ResourceKind.Tga => "图片",
                ResourceKind.Mdx => "模型",
                ResourceKind.Wav or ResourceKind.Mp3 or ResourceKind.Ogg => "音频",
                ResourceKind.Text => "文本",
                _ => "十六进制"
            }
        };

        try
        {
            switch (effective)
            {
                case "图片":
                    ImagePreview.Source = DecodeImage(kind, bytes);
                    ImageScroll.Visibility = Visibility.Visible;
                    return;
                case "模型":
                    TextPreview.Text = MdxInspector.Describe(bytes);
                    TextPreview.Visibility = Visibility.Visible;
                    return;
                case "音频":
                    ShowAudio(entry, bytes);
                    return;
                case "文本":
                    TextPreview.Text = DecodeText(bytes);
                    TextPreview.Visibility = Visibility.Visible;
                    return;
                default:
                    TextPreview.Text = BuildHexPreview(bytes, entry.Size);
                    TextPreview.Visibility = Visibility.Visible;
                    return;
            }
        }
        catch (Exception ex)
        {
            TextPreview.Text = $"[{effective}预览失败：{ex.Message}]\n\n" + BuildHexPreview(bytes, entry.Size);
            TextPreview.Visibility = Visibility.Visible;
        }
    }

    private static BitmapSource DecodeImage(ResourceKind kind, byte[] bytes)
    {
        return kind switch
        {
            ResourceKind.Blp1 or ResourceKind.Blp2 => BlpDecoder.Decode(bytes),
            ResourceKind.Tga => TgaDecoder.Decode(bytes),
            _ => DecodeStandardImage(bytes)
        };
    }

    private static BitmapSource DecodeStandardImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void ShowAudio(ArchiveEntry entry, byte[] bytes)
    {
        DeleteAudioTemp();
        _audioTempPath = Path.Combine(Path.GetTempPath(), "MPQTikTool",
            Guid.NewGuid().ToString("N") + (entry.Extension.Length > 1 ? entry.Extension : ".bin"));
        Directory.CreateDirectory(Path.GetDirectoryName(_audioTempPath)!);
        File.WriteAllBytes(_audioTempPath, bytes);
        AudioPlayer.Source = new Uri(_audioTempPath);
        AudioPanel.Visibility = Visibility.Visible;
        AudioStatus.Text = "音频已加载，点击播放（格式取决于系统解码器）";
    }

    private void HidePreviewPanels()
    {
        TextPreview.Visibility = Visibility.Collapsed;
        ImageScroll.Visibility = Visibility.Collapsed;
        FallbackPreview.Visibility = Visibility.Collapsed;
        AudioPanel.Visibility = Visibility.Collapsed;
        ImagePreview.Source = null;
        AudioPlayer.Stop();
        AudioPlayer.Source = null;
    }

    private void ClearPreview()
    {
        HidePreviewPanels();
        DeleteAudioTemp();
        TextPreview.Clear();
        _previewEntry = null;
        _previewBytes = null;
        PreviewName.Text = "资源预览";
        PreviewInfo.Text = string.Empty;
        FallbackPreview.Text = "选择左侧资源以查看预览";
    }

    private void DeleteAudioTemp()
    {
        if (_audioTempPath is null) return;
        try { File.Delete(_audioTempPath); } catch { }
        _audioTempPath = null;
    }

    private static string DecodeText(byte[] bytes)
    {
        string text;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        else
        {
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch { text = Encoding.GetEncoding(936).GetString(bytes); }
        }
        const int limit = 2_000_000;
        return text.Length > limit ? text[..limit] + "\n\n... 文本过长已截断，导出可获得完整内容。" : text;
    }

    private static string BuildHexPreview(byte[] bytes, uint totalSize)
    {
        const int maxBytes = 8192;
        var length = Math.Min(bytes.Length, maxBytes);
        var sb = new StringBuilder();
        sb.AppendLine($"二进制预览（前 {length:N0} / {totalSize:N0} bytes）");
        sb.AppendLine("偏移       00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F  | ASCII");
        for (var offset = 0; offset < length; offset += 16)
        {
            var count = Math.Min(16, length - offset);
            sb.Append(offset.ToString("X8")).Append("   ");
            for (var i = 0; i < 16; i++)
                sb.Append(i < count ? bytes[offset + i].ToString("X2") : "  ").Append(' ');
            sb.Append("| ");
            for (var i = 0; i < count; i++)
            {
                var b = bytes[offset + i];
                sb.Append(b is >= 32 and <= 126 ? (char)b : '.');
            }
            sb.AppendLine();
        }
        if (totalSize > length) sb.AppendLine("\n... 已截断，导出可获得完整原始文件。");
        return sb.ToString();
    }

    private void AudioPlay_Click(object sender, RoutedEventArgs e)
    {
        try { AudioPlayer.Play(); AudioStatus.Text = "正在播放"; }
        catch (Exception ex) { AudioStatus.Text = "播放失败：" + ex.Message; }
    }

    private void AudioPause_Click(object sender, RoutedEventArgs e)
    {
        AudioPlayer.Pause();
        AudioStatus.Text = "已暂停";
    }

    private void AudioStop_Click(object sender, RoutedEventArgs e)
    {
        AudioPlayer.Stop();
        AudioStatus.Text = "已停止";
    }

    // ---------- export ----------

    private void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureArchive()) return;
        var selected = EntriesGrid.SelectedItems.Cast<ArchiveEntry>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("请先在左侧选择一个或多个资源。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (selected.Count == 1)
        {
            var entry = selected[0];
            var dialog = new SaveFileDialog
            {
                Title = "导出资源",
                FileName = SafeFileName(Path.GetFileName(entry.Name)),
                Filter = "原始文件 (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                _archive!.Extract(entry, dialog.FileName);
                StatusText.Text = $"已导出：{entry.Name}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }
        if (!TryPickFolder("选择多个资源的导出目录", out var root)) return;
        _ = ExportBatchAsync(selected, root);
    }

    private void ExportFiltered_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureArchive() || _visibleEntries.Count == 0) return;
        if (!TryPickFolder("选择筛选结果的导出目录", out var root)) return;
        _ = ExportBatchAsync(_visibleEntries.ToList(), root);
    }

    private void ExportAll_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureArchive() || _allEntries.Count == 0) return;
        if (!TryPickFolder("选择完整导出目录", out var root)) return;
        _ = ExportBatchAsync(_allEntries.ToList(), root);
    }

    private bool TryPickFolder(string title, out string root)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (dialog.ShowDialog(this) == true)
        {
            root = Path.GetFullPath(dialog.FolderName);
            return true;
        }
        root = string.Empty;
        return false;
    }

    private sealed record ExportRow(string Status, string Name, string Source, string Kind, uint Size,
        uint CompressedSize, string Flags, uint Locale, uint Hash1, uint Hash2, int BlockIndex, string Message);

    private async Task ExportBatchAsync(IReadOnlyList<ArchiveEntry> entries, string root)
    {
        await RunBusyAsync(async ct =>
        {
            var archive = _archive!;
            var rows = new List<ExportRow>(entries.Count);
            var errors = new List<string>();
            var ok = 0;
            long exportedBytes = 0;
            var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;

            await Task.Run(() =>
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = entries[i];
                    var relative = SafeRelativePath(entry.Name);
                    var destination = Path.GetFullPath(Path.Combine(root, relative));
                    if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        rows.Add(new ExportRow("REJECTED", entry.Name, entry.NameSource, entry.KindName, entry.Size,
                            entry.CompressedSize, entry.FlagsText, entry.Locale, entry.Hash1, entry.Hash2, entry.BlockIndex,
                            "拒绝写出：路径越界"));
                        errors.Add(entry.Name + " (非法路径)");
                        continue;
                    }
                    try
                    {
                        archive.Extract(entry, destination, null, ct);
                        ok++;
                        exportedBytes += entry.Size;
                        rows.Add(new ExportRow("OK", relative, entry.NameSource, entry.KindName, entry.Size,
                            entry.CompressedSize, entry.FlagsText, entry.Locale, entry.Hash1, entry.Hash2, entry.BlockIndex, ""));
                    }
                    catch (Exception ex)
                    {
                        // Remove partial output so a failed file is never mistaken for a good one.
                        try { if (File.Exists(destination)) File.Delete(destination); } catch { }
                        rows.Add(new ExportRow("FAILED", entry.Name, entry.NameSource, entry.KindName, entry.Size,
                            entry.CompressedSize, entry.FlagsText, entry.Locale, entry.Hash1, entry.Hash2, entry.BlockIndex, ex.Message));
                        errors.Add(entry.Name + " (" + ex.Message + ")");
                    }
                    if ((i & 0x3F) == 0 || i + 1 == entries.Count)
                    {
                        ReportProgress("导出资源", i + 1, entries.Count);
                        var current = i + 1;
                        Dispatcher.InvokeAsync(() => StatusText.Text = $"导出中：{current:N0}/{entries.Count:N0}，成功 {ok:N0}");
                    }
                }
            }, ct);

            var csv = new List<string>
            {
                Csv("Status", "ArchivePath", "Source", "Kind", "Size", "CompressedSize", "Flags", "Locale", "Hash1", "Hash2", "BlockIndex", "Message")
            };
            foreach (var r in rows)
                csv.Add(Csv(r.Status, r.Name, r.Source, r.Kind, r.Size.ToString(), r.CompressedSize.ToString(),
                    r.Flags, r.Locale.ToString(), r.Hash1.ToString("X8"), r.Hash2.ToString("X8"), r.BlockIndex.ToString(), r.Message));
            File.WriteAllLines(Path.Combine(root, "_MPQTikTool_ExportManifest.csv"), csv, new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(root, "_MPQTikTool_ExportErrors.txt"),
                errors.Count == 0 ? "全部资源导出成功。" : string.Join(Environment.NewLine, errors), new UTF8Encoding(true));

            var manifest = new
            {
                source = archive.ArchivePath,
                format = archive.FormatDescription,
                exportedAt = DateTime.Now.ToString("s"),
                total = entries.Count,
                succeeded = ok,
                failed = errors.Count,
                exportedBytes,
                files = rows.Select(r => new
                {
                    name = r.Name,
                    originalName = r.Source == "原名" ? r.Name : null,
                    source = r.Source,
                    kind = r.Kind,
                    size = r.Size,
                    compressedSize = r.CompressedSize,
                    flags = r.Flags,
                    locale = r.Locale,
                    hash1 = r.Hash1.ToString("X8"),
                    hash2 = r.Hash2.ToString("X8"),
                    blockIndex = r.BlockIndex,
                    status = r.Status,
                    message = r.Message
                }).ToList()
            };
            File.WriteAllText(Path.Combine(root, "_MPQTikTool_Manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

            StatusText.Text = $"导出完成：成功 {ok:N0}，失败 {errors.Count:N0}，共 {exportedBytes:N0} bytes（含 CSV / JSON 清单）";
            if (errors.Count > 0)
                MessageBox.Show($"成功：{ok:N0}\n失败：{errors.Count:N0}\n\n失败原因见导出目录的 _MPQTikTool_ExportErrors.txt。\n\n" +
                                string.Join("\n", errors.Take(10)),
                    "批量导出结果", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private static string SafeRelativePath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var parts = name.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var clean = new string(part.Select(c => invalid.Contains(c) || c == ':' ? '_' : c).ToArray()).Trim();
                return clean is "" or "." or ".." ? "_" : clean;
            }).ToArray();
        return parts.Length == 0 ? "_unnamed.bin" : Path.Combine(parts);
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "resource.bin" : clean;
    }

    private static string Csv(params string[] fields) =>
        string.Join(",", fields.Select(value => "\"" + value.Replace("\"", "\"\"") + "\""));

    // ---------- shared plumbing ----------

    private bool EnsureArchive()
    {
        if (_archive is not null) return true;
        MessageBox.Show("请先打开一张地图。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> work)
    {
        if (_busy) return;
        _busy = true;
        SetBusyUi(true);
        _operationCts = new CancellationTokenSource();
        try
        {
            await work(_operationCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "操作已取消";
        }
        catch (Exception ex)
        {
            StatusText.Text = "操作失败";
            MessageBox.Show(ex.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            SetBusyUi(false);
            OperationProgress.Value = 0;
            ProgressText.Text = string.Empty;
        }
    }

    private void SetBusyUi(bool busy)
    {
        OpenButton.IsEnabled = !busy;
        ListFileButton.IsEnabled = !busy;
        AnalyzeButton.IsEnabled = !busy;
        DeepAnalyzeButton.IsEnabled = !busy;
        ExportSelectedButton.IsEnabled = !busy;
        ExportFilteredButton.IsEnabled = !busy;
        ExportAllButton.IsEnabled = !busy;
        RestoreButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        EntriesGrid.IsEnabled = !busy;
    }

    private void RestoreMap_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureArchive()) return;
        var sourceName = Path.GetFileNameWithoutExtension(_archive!.ArchivePath);
        var dialog = new SaveFileDialog
        {
            Title = "保存可编辑的还原版地图",
            Filter = "Warcraft III 地图 (*.w3x)|*.w3x",
            FileName = sourceName + ".restored.w3x"
        };
        if (dialog.ShowDialog() != true) return;

        var target = dialog.FileName;
        var packageDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target))!,
            Path.GetFileNameWithoutExtension(target) + "_restore");

        _ = RunBusyAsync(async ct =>
        {
            var archive = _archive!;
            await Task.Run(() => ReportProgress("还原地图（复制原始档案）", 0, 1), ct);
            var (restore, package, verified) = await Task.Run(() =>
            {
                var r = archive.RestoreTo(target, null, true, ct);
                var p = MpqRestorer.WritePackage(archive, r, packageDir, ct);
                int blocks;
                bool listFileFound;
                using (var check = MpqArchive.Open(target))
                {
                    blocks = check.Entries.Count;
                    listFileFound = check.Entries.Any(x => x.OriginalName == "(listfile)");
                }
                return (r, p, (blocks, listFileFound));
            }, ct);

            StatusText.Text = $"还原完成：{restore.FixedLocales:N0} 个 locale 已修复，{restore.NamedBlocks:N0} 个原名写回 listfile，产物 {restore.OutputSize:N0} B";
            var report = File.ReadAllText(package.ReportPath);
            var summary = string.Join("\n", report.Split('\n').Take(26));
            MessageBox.Show(
                $"还原版地图：{target}\n" +
                $"工程包：{package.PackageDirectory}\n\n" +
                $"修复 locale：{restore.FixedLocales:N0}\n" +
                $"已恢复原名：{restore.NamedBlocks:N0}/{restore.TotalBlocks:N0}\n" +
                $"被清空的 WE 文件：{package.ClearedFiles.Count} 个（触发器/物编不可逆）\n" +
                $"重开校验：{verified.blocks:N0} 个块，(listfile) {(verified.listFileFound ? "可读" : "未找到")}\n\n" +
                summary,
                "还原结果", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operationCts?.Cancel();

    private void ReportProgress(string label, int current, int total) =>
        Dispatcher.InvokeAsync(() =>
        {
            OperationProgress.Maximum = Math.Max(total, 1);
            OperationProgress.Value = current;
            ProgressText.Text = $"{label} {current:N0}/{total:N0}";
        });

    protected override void OnClosed(EventArgs e)
    {
        _operationCts?.Cancel();
        _previewCts?.Cancel();
        DeleteAudioTemp();
        _archive?.Dispose();
        base.OnClosed(e);
    }
}
