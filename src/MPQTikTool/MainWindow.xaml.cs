using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MPQTikTool.Models;
using MPQTikTool.Services;

namespace MPQTikTool;

public partial class MainWindow : Window
{
    private readonly MpqArchiveService _archive = new();
    private string? _audioTempPath;
    private readonly ObservableCollection<ArchiveEntry> _visibleEntries = new();
    private List<ArchiveEntry> _allEntries = new();

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".lua", ".j", ".jass", ".ai", ".fdf", ".ini", ".slk", ".toc", ".xml", ".json", ".md", ".mdl"
    };

    public MainWindow()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        InitializeComponent();
        EntriesGrid.ItemsSource = _visibleEntries;
    }

    private void OpenMap_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Warcraft III 地图",
            Filter = "Warcraft III 地图 (*.w3x;*.w3m)|*.w3x;*.w3m|MPQ 档案 (*.mpq)|*.mpq|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _allEntries = _archive.Open(dialog.FileName).ToList();
            MapPathText.Text = dialog.FileName;
            ApplyFilter();
            StatusText.Text = $"已读取 {_allEntries.Count} 个资源";
            ClearPreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "打开地图失败", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "打开失败";
        }
    }

    private void LoadListFile_Click(object sender, RoutedEventArgs e)
    {
        if (!_archive.IsOpen)
        {
            MessageBox.Show("请先打开地图，再加载外部文件名库。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择 Warcraft III 外部文件名库",
            Filter = "Listfile (*.txt;*.list)|*.txt;*.list|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _archive.AddExternalListFile(dialog.FileName);
            _allEntries = _archive.EnumerateNamedFiles().ToList();
            ApplyFilter();
            StatusText.Text = $"外部文件名库加载完成，当前识别 {_allEntries.Count} 个具名资源";
            MessageBox.Show($"已加载文件名库。当前可识别 {_allEntries.Count} 个具名资源。\n\n注意：文件名库只能匹配已有哈希项，不能保证恢复所有匿名资源。", "文件名库加载完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "加载文件名库失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var category = (CategoryBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部类型";
        var filtered = _allEntries.Where(x =>
            (category == "全部类型" || x.Category == category) &&
            (string.IsNullOrEmpty(query) ||
             x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             x.Extension.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             x.Category.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        _visibleEntries.Clear();
        foreach (var entry in filtered) _visibleEntries.Add(entry);
        CountText.Text = $"{filtered.Count} / {_allEntries.Count} 个资源";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) ApplyFilter();
    }

    private void EntriesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EntriesGrid.SelectedItem is ArchiveEntry entry) PreviewEntry(entry);
    }

    private void PreviewEntry(ArchiveEntry entry)
    {
        ClearPreview();
        PreviewName.Text = entry.Name;
        PreviewInfo.Text = $"{entry.Category}  |  {entry.DisplaySize}  |  压缩: {entry.CompressedSize:N0} B  |  Locale: {entry.Locale}  |  Flags: 0x{entry.Flags:X8}";
        try
        {
            if (TextExtensions.Contains(entry.Extension))
            {
                var bytes = _archive.ReadAllBytes(entry);
                TextPreview.Text = DecodeText(bytes);
                TextPreview.Visibility = Visibility.Visible;
                return;
            }

            if (entry.Extension == ".mdx")
            {
                var bytes = _archive.ReadAllBytes(entry);
                TextPreview.Text = MdxInspector.Describe(bytes);
                TextPreview.Visibility = Visibility.Visible;
                return;
            }

            if (entry.Extension is ".wav" or ".mp3")
            {
                var bytes = _archive.ReadAllBytes(entry);
                _audioTempPath = Path.Combine(Path.GetTempPath(), "MPQTikTool", Guid.NewGuid().ToString("N") + entry.Extension);
                Directory.CreateDirectory(Path.GetDirectoryName(_audioTempPath)!);
                File.WriteAllBytes(_audioTempPath, bytes);
                AudioPlayer.Source = new Uri(_audioTempPath);
                AudioPanel.Visibility = Visibility.Visible;
                AudioStatus.Text = "音频已加载，点击播放";
                return;
            }

            if (entry.Extension == ".blp")
            {
                var bytes = _archive.ReadAllBytes(entry);
                ImagePreview.Source = BlpDecoder.Decode(bytes);
                ImageScroll.Visibility = Visibility.Visible;
                return;
            }

            if (new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" }.Contains(entry.Extension))
            {
                var bytes = _archive.ReadAllBytes(entry);
                using var stream = new MemoryStream(bytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                ImagePreview.Source = bitmap;
                ImageScroll.Visibility = Visibility.Visible;
                return;
            }

            var raw = _archive.ReadAllBytes(entry);
            TextPreview.Text = BuildHexPreview(raw);
            TextPreview.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            FallbackPreview.Text = $"预览失败：{ex.Message}";
            FallbackPreview.Visibility = Visibility.Visible;
        }
    }

    private static string BuildHexPreview(byte[] bytes)
    {
        const int maxBytes = 4096;
        var length = Math.Min(bytes.Length, maxBytes);
        var sb = new StringBuilder();
        sb.AppendLine($"二进制资源预览（前 {length:N0} / {bytes.Length:N0} bytes）");
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
        if (bytes.Length > maxBytes) sb.AppendLine("\n... 已截断，导出选中可保存完整原始文件。");
        return sb.ToString();
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch { return Encoding.GetEncoding(936).GetString(bytes); }
    }

    private void ClearPreview()
    {
        TextPreview.Visibility = Visibility.Collapsed;
        ImageScroll.Visibility = Visibility.Collapsed;
        FallbackPreview.Visibility = Visibility.Collapsed;
        ImagePreview.Source = null;
        AudioPlayer.Stop();
        AudioPlayer.Source = null;
        AudioPanel.Visibility = Visibility.Collapsed;
        if (_audioTempPath is not null)
        {
            try { File.Delete(_audioTempPath); } catch { }
            _audioTempPath = null;
        }
        TextPreview.Clear();
        PreviewName.Text = "资源预览";
        PreviewInfo.Text = "";
        FallbackPreview.Text = "选择左侧资源以查看预览";
    }

    private void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        if (EntriesGrid.SelectedItem is not ArchiveEntry entry)
        {
            MessageBox.Show("请先选择一个资源。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog { FileName = Path.GetFileName(entry.Name), Title = "导出资源" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _archive.Extract(entry, dialog.FileName);
            StatusText.Text = $"已导出：{entry.Name}";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ExportAll_Click(object sender, RoutedEventArgs e)
    {
        if (!_archive.IsOpen || _allEntries.Count == 0)
        {
            MessageBox.Show("请先打开包含资源的地图。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "选择完整导出目录",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        var root = Path.GetFullPath(dialog.FolderName);
        var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var succeeded = 0;
        long exportedBytes = 0;
        var failed = new List<string>();
        var manifest = new List<string>
        {
            "Status,ArchivePath,Size,CompressedSize,Locale,Flags,Message"
        };
        foreach (var entry in _allEntries)
        {
            try
            {
                var relative = entry.Name.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(Path.Combine(root, relative));
                if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    failed.Add(entry.Name + " (非法路径)");
                    manifest.Add(Csv("REJECTED", entry.Name, entry.Size.ToString(), entry.CompressedSize.ToString(),
                        entry.Locale.ToString(), $"0x{entry.Flags:X8}", "拒绝写出：路径越界"));
                    continue;
                }
                _archive.Extract(entry, destination);
                succeeded++;
                exportedBytes += entry.Size;
                manifest.Add(Csv("OK", relative, entry.Size.ToString(), entry.CompressedSize.ToString(),
                    entry.Locale.ToString(), $"0x{entry.Flags:X8}", ""));
            }
            catch (Exception ex)
            {
                failed.Add(entry.Name + " (" + ex.Message + ")");
                manifest.Add(Csv("FAILED", entry.Name, entry.Size.ToString(), entry.CompressedSize.ToString(),
                    entry.Locale.ToString(), $"0x{entry.Flags:X8}", ex.Message));
            }
        }

        try
        {
            File.WriteAllLines(Path.Combine(root, "_MPQTikTool_ExportManifest.csv"), manifest, new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(root, "_MPQTikTool_ExportErrors.txt"),
                failed.Count == 0 ? "全部已知文件均导出成功。" : string.Join(Environment.NewLine, failed),
                new UTF8Encoding(true));
        }
        catch (Exception ex) { failed.Add("写入导出报告失败：" + ex.Message); }

        StatusText.Text = $"导出完成：成功 {succeeded}，失败 {failed.Count}，{exportedBytes:N0} bytes";
        if (failed.Count > 0)
            MessageBox.Show($"成功：{succeeded}\n失败：{failed.Count}\n已导出：{exportedBytes:N0} bytes\n\n完整清单和失败原因已写入导出目录。\n\n" + string.Join("\n", failed.Take(12)),
                "批量导出结果", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show($"已导出 {succeeded} 个文件（{exportedBytes:N0} bytes）。\n导出目录已附带 CSV 清单和结果报告。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private static string Csv(params string[] fields) =>
        string.Join(",", fields.Select(value => "\"" + value.Replace("\"", "\"\"") + "\""));

    protected override void OnClosed(EventArgs e)
    {
        _archive.Dispose();
        base.OnClosed(e);
    }
}
