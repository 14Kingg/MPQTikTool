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
    private readonly ObservableCollection<ArchiveEntry> _visibleEntries = new();
    private List<ArchiveEntry> _allEntries = new();

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".lua", ".j", ".ai", ".fdf", ".ini", ".slk", ".toc", ".xml", ".json", ".md"
    };

    public MainWindow()
    {
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
        PreviewInfo.Text = $"{entry.Category}  |  {entry.DisplaySize}  |  Flags: 0x{entry.Flags:X8}";
        try
        {
            if (TextExtensions.Contains(entry.Extension))
            {
                var bytes = _archive.ReadAllBytes(entry);
                TextPreview.Text = DecodeText(bytes);
                TextPreview.Visibility = Visibility.Visible;
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

            FallbackPreview.Text = entry.Extension == ".blp"
                ? "BLP 纹理：已识别，专用解码器正在规划中。当前可先导出原始文件。"
                : entry.Extension is ".mdx" or ".mdl"
                    ? "Warcraft III 模型：已识别，3D 模型查看器将在后续版本接入。"
                    : "该格式暂不支持内嵌预览。可以使用“导出选中”保存原始资源。";
            FallbackPreview.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            FallbackPreview.Text = $"预览失败：{ex.Message}";
            FallbackPreview.Visibility = Visibility.Visible;
        }
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch { return Encoding.Latin1.GetString(bytes); }
    }

    private void ClearPreview()
    {
        TextPreview.Visibility = Visibility.Collapsed;
        ImageScroll.Visibility = Visibility.Collapsed;
        FallbackPreview.Visibility = Visibility.Collapsed;
        ImagePreview.Source = null;
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
        var succeeded = 0;
        var failed = new List<string>();
        foreach (var entry in _allEntries)
        {
            try
            {
                var relative = entry.Name.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(Path.Combine(root, relative));
                if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    failed.Add(entry.Name + " (非法路径)");
                    continue;
                }
                _archive.Extract(entry, destination);
                succeeded++;
            }
            catch (Exception ex) { failed.Add(entry.Name + " (" + ex.Message + ")"); }
        }

        StatusText.Text = $"导出完成：成功 {succeeded}，失败 {failed.Count}";
        if (failed.Count > 0)
            MessageBox.Show($"成功：{succeeded}\n失败：{failed.Count}\n\n" + string.Join("\n", failed.Take(20)),
                "批量导出结果", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show($"已导出 {succeeded} 个文件。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    protected override void OnClosed(EventArgs e)
    {
        _archive.Dispose();
        base.OnClosed(e);
    }
}
