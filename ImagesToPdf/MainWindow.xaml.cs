using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ImagesToPdf.Models;
using ImagesToPdf.Services;
using Microsoft.Win32;
using SkiaSharp;

namespace ImagesToPdf;

public partial class MainWindow : Window
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string left, string right);

    private readonly ObservableCollection<ImageItem> _items = new();
    private readonly AppSettings _settings;
    private Point _dragStart;
    private ImageItem? _dragItem;
    private CancellationTokenSource? _exportCancellation;
    private int _previewRequestId;
    private bool _isExporting;
    private bool _isImporting;
    private bool _updatingPreviewControls;
    private ImageItem? _lastPreviewItem;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsService.Load();

        ThemeService.Apply(_settings.ThemePreference);
        SourceInitialized += (_, _) => ThemeService.ApplyWindowChrome(this, _settings.ThemePreference);
        List.ItemsSource = _items;
        _items.CollectionChanged += (_, _) =>
        {
            RenumberItems();
            UpdateSummary();
        };

        ApplySavedSettings();
        UpdatePdfThemeHelp();
        RenumberItems();
        Loaded += async (_, _) => await RefreshPreviewAsync();
        // Keep the app theme synchronized when Windows switches between light and dark mode.
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        Closed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
            _exportCancellation?.Cancel();
            SaveSettings();
        };
    }

    // -------------------------------------------------------------------------
    // Preferences and page settings
    // -------------------------------------------------------------------------

    private void ApplySavedSettings()
    {
        ThemeBox.SelectedIndex = ThemeIndex(_settings.ThemePreference);
        PageSizeBox.SelectedIndex = ClampIndex(_settings.PageSizeIndex, PageSizeBox.Items.Count);
        ResolutionBox.SelectedIndex = ClampIndex(_settings.ResolutionIndex, ResolutionBox.Items.Count);
        DpiBox.SelectedIndex = ClampIndex(_settings.DpiIndex, DpiBox.Items.Count);
        FitModeBox.SelectedIndex = ClampIndex(_settings.FitModeIndex, FitModeBox.Items.Count);
        PdfThemeBox.SelectedIndex = ClampIndex(_settings.PdfThemeIndex, PdfThemeBox.Items.Count);
        QualityBox.SelectedIndex = ClampIndex(_settings.QualityIndex, QualityBox.Items.Count);
        RecursiveBox.IsChecked = _settings.RecursiveFolderImport;
        IncludeAllFramesBox.IsChecked = _settings.IncludeAllFrames;
        AutoSort.IsChecked = _settings.AutoSort;
    }

    private static int ClampIndex(int value, int itemCount) =>
        itemCount == 0 ? -1 : Math.Clamp(value, 0, itemCount - 1);

    private static int ThemeIndex(string preference) => preference switch
    {
        "Light" => 1,
        "Dark" => 2,
        _ => 0
    };

    private int GetDpi() => DpiBox.SelectedIndex switch
    {
        0 => 150,
        2 => 300,
        _ => 200
    };

    private ImageFitMode GetFitMode() => FitModeBox.SelectedIndex == 1
        ? ImageFitMode.Cover
        : ImageFitMode.Contain;

    private SKColor GetPdfBackground() => PdfThemeBox.SelectedIndex switch
    {
        1 => SKColor.Parse("#B51F43"), // Red PDF theme: visible outer page frame.
        2 => SKColor.Parse("#E8E9ED"),
        _ => SKColors.White
    };

    // A slim red frame makes the PDF theme visible even when every source image
    // already matches the output page's aspect ratio (the main use case).
    private float GetPdfThemeInsetFraction() => PdfThemeBox.SelectedIndex == 1 ? 0.025f : 0f;

    private void UpdatePdfThemeHelp()
    {
        if (PdfThemeHelp is null) return;
        PdfThemeHelp.Text = PdfThemeBox.SelectedIndex switch
        {
            1 => "Adds a visible red frame to every PDF page, with a clean white image area inside.",
            2 => "Uses a light-gray page background around images when Fit leaves uncovered space.",
            _ => "Uses a white page background around images when Fit leaves uncovered space."
        };
    }

    private PageDimensions GetPageDimensions() => PdfExportService.GetPageDimensions(
        _items.ToList(), PageSizeBox.SelectedIndex, ResolutionBox.SelectedIndex, GetDpi());

    private void SystemEvents_UserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (!string.Equals(_settings.ThemePreference, "System", StringComparison.OrdinalIgnoreCase)) return;
        if (e.Category is not (UserPreferenceCategory.Color or UserPreferenceCategory.General or
            UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Window)) return;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            ThemeService.Apply("System");
            ThemeService.ApplyWindowChrome(this, "System");
        }));
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ThemeBox.SelectedItem is not ComboBoxItem selected) return;
        var preference = selected.Tag?.ToString() ?? "System";
        ThemeService.Apply(preference);
        ThemeService.ApplyWindowChrome(this, preference);
        _settings.ThemePreference = preference;
        SaveSettings();
    }

    private async void Settings_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateSummary();
        SaveSettings();
        if (ReferenceEquals(sender, PdfThemeBox)) UpdatePdfThemeHelp();
        await RefreshPreviewAsync();
    }

    private async void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateSummary();
        SaveSettings();
        await RefreshPreviewAsync();
    }

    private void SaveSettings()
    {
        if (ThemeBox.SelectedItem is ComboBoxItem themeItem)
            _settings.ThemePreference = themeItem.Tag?.ToString() ?? "System";

        _settings.PageSizeIndex = Math.Max(0, PageSizeBox.SelectedIndex);
        _settings.ResolutionIndex = Math.Max(0, ResolutionBox.SelectedIndex);
        _settings.DpiIndex = Math.Max(0, DpiBox.SelectedIndex);
        _settings.FitModeIndex = Math.Max(0, FitModeBox.SelectedIndex);
        _settings.PdfThemeIndex = Math.Max(0, PdfThemeBox.SelectedIndex);
        _settings.QualityIndex = Math.Max(0, QualityBox.SelectedIndex);
        _settings.RecursiveFolderImport = RecursiveBox.IsChecked == true;
        _settings.IncludeAllFrames = IncludeAllFramesBox.IsChecked == true;
        _settings.AutoSort = AutoSort.IsChecked == true;
        SettingsService.Save(_settings);
    }

    // -------------------------------------------------------------------------
    // Import and image queue
    // -------------------------------------------------------------------------

    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        if (_isExporting || _isImporting) return;

        _isImporting = true;
        SetImportControlsEnabled(false);
        try
        {
            bool recursive = RecursiveBox.IsChecked == true;
            Status.Text = "Scanning for images…";
            var scanResult = await Task.Run(() =>
            {
                var discovered = CollectImageFiles(paths, recursive, out int inaccessible);
                return (Files: discovered, Inaccessible: inaccessible);
            });

            var known = new HashSet<string>(_items.Select(item => item.FilePath), StringComparer.OrdinalIgnoreCase);
            var files = scanResult.Files.Where(file => known.Add(file)).ToList();
            int inaccessibleDirectories = scanResult.Inaccessible;

            if (files.Count == 0)
            {
                Status.Text = inaccessibleDirectories > 0
                    ? $"No new supported images found. {inaccessibleDirectories} folder(s) could not be read."
                    : "No new supported images found.";
                return;
            }

            int skipped = 0;
            int loaded = 0;
            var importErrors = new List<string>();
            foreach (var file in files)
            {
                Status.Text = $"Reading {loaded + skipped + 1} of {files.Count}: {Path.GetFileName(file)}";
                var result = await Task.Run(() => ImageLoader.TryBuildItem(file));
                if (result.Item is null)
                {
                    skipped++;
                    importErrors.Add($"{Path.GetFileName(file)}: {result.Error ?? "Unknown decoding error."}");
                    continue;
                }

                _items.Add(result.Item);
                loaded++;
            }

            if (AutoSort.IsChecked == true) SortItems();
            Status.Text = $"Added {loaded} image file(s)." +
                          (skipped > 0 ? $" Skipped {skipped} unreadable file(s)." : string.Empty) +
                          (inaccessibleDirectories > 0 ? $" Could not access {inaccessibleDirectories} folder(s)." : string.Empty);

            if (importErrors.Count > 0)
            {
                var details = string.Join(Environment.NewLine, importErrors.Take(12));
                if (importErrors.Count > 12) details += Environment.NewLine + $"…and {importErrors.Count - 12} more file(s).";
                MessageBox.Show(this, details, "Some images could not be imported", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            Status.Text = "Import stopped. Successfully imported images were kept in the queue.";
            MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isImporting = false;
            SetImportControlsEnabled(true);
        }

        if (List.SelectedItem is null && _items.Count > 0)
            List.SelectedIndex = 0;
        UpdateSummary();
        await RefreshPreviewAsync();
    }

    private static List<string> CollectImageFiles(IEnumerable<string> paths, bool recursive, out int inaccessibleDirectories)
    {
        inaccessibleDirectories = 0;
        var result = new List<string>();

        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            try
            {
                if (File.Exists(path))
                {
                    if (ImageLoader.SupportedExtensions.Contains(Path.GetExtension(path)))
                        result.Add(Path.GetFullPath(path));
                    continue;
                }

                if (!Directory.Exists(path)) continue;
                var pending = new Stack<string>();
                pending.Push(Path.GetFullPath(path));

                while (pending.Count > 0)
                {
                    var current = pending.Pop();
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(current))
                        {
                            if (ImageLoader.SupportedExtensions.Contains(Path.GetExtension(file)))
                                result.Add(Path.GetFullPath(file));
                        }
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                    {
                        inaccessibleDirectories++;
                    }

                    if (!recursive) continue;
                    try
                    {
                        foreach (var directory in Directory.EnumerateDirectories(current))
                        {
                            try
                            {
                                var attributes = File.GetAttributes(directory);
                                if ((attributes & FileAttributes.ReparsePoint) == 0)
                                    pending.Push(directory);
                            }
                            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                            {
                                inaccessibleDirectories++;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                    {
                        inaccessibleDirectories++;
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                inaccessibleDirectories++;
            }
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file, Comparer<string>.Create(StrCmpLogicalW))
            .ToList();
    }

    private void SetImportControlsEnabled(bool enabled)
    {
        List.IsEnabled = enabled;
        AddImagesBtn.IsEnabled = enabled && !_isExporting;
        AddFolderBtn.IsEnabled = enabled && !_isExporting;
        MoveUpBtn.IsEnabled = enabled && !_isExporting;
        MoveDownBtn.IsEnabled = enabled && !_isExporting;
        RemoveBtn.IsEnabled = enabled && !_isExporting;
        SortBtn.IsEnabled = enabled && !_isExporting;
        ClearBtn.IsEnabled = enabled && !_isExporting;
        AutoSort.IsEnabled = enabled && !_isExporting;
        RecursiveBox.IsEnabled = enabled && !_isExporting;
        IncludeAllFramesBox.IsEnabled = enabled && !_isExporting;
        CreateBtn.IsEnabled = enabled && !_isExporting && _items.Count > 0;
    }

    private async void AddImages_Click(object sender, RoutedEventArgs e)
    {
        if (_isImporting || _isExporting) return;
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Supported images|*.png;*.jpg;*.jpeg;*.jfif;*.webp;*.bmp;*.gif;*.ico;*.tif;*.tiff|All files|*.*",
            Title = "Add images"
        };
        if (dialog.ShowDialog(this) == true)
            await AddPathsAsync(dialog.FileNames);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_isImporting || _isExporting) return;
        var dialog = new OpenFolderDialog { Title = "Choose an image folder", Multiselect = false };
        if (dialog.ShowDialog(this) == true)
            await AddPathsAsync(new[] { dialog.FolderName });
    }

    private void SortItems()
    {
        var selected = List.SelectedItems.Cast<ImageItem>().ToHashSet();
        var sorted = _items.OrderBy(item => item.Name, Comparer<string>.Create(StrCmpLogicalW)).ToList();
        _items.Clear();
        foreach (var item in sorted) _items.Add(item);
        RestoreSelection(selected);
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (!_isImporting && !_isExporting) SortItems();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_isImporting || _isExporting || _items.Count == 0) return;
        var answer = MessageBox.Show(this, "Remove all images from the queue?", "Clear image queue",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        _items.Clear();
        _lastPreviewItem = null;
        UpdateSummary();
        _ = RefreshPreviewAsync();
    }

    private void RenumberItems()
    {
        for (int index = 0; index < _items.Count; index++)
            _items[index].Index = index + 1;
        Hint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ImageCountText.Text = _items.Count == 0
            ? "No images added"
            : $"{_items.Count:N0} image file(s)";
        CreateBtn.IsEnabled = !_isExporting && !_isImporting && _items.Count > 0;
    }

    // -------------------------------------------------------------------------
    // Queue ordering and selection
    // -------------------------------------------------------------------------

    private void MoveSelected(int direction)
    {
        if (_isImporting || _isExporting) return;
        var selected = List.SelectedItems.Cast<ImageItem>().OrderBy(_items.IndexOf).ToList();
        if (selected.Count == 0) return;

        if (direction < 0)
        {
            foreach (var item in selected)
            {
                int index = _items.IndexOf(item);
                if (index > 0 && !selected.Contains(_items[index - 1]))
                    _items.Move(index, index - 1);
            }
        }
        else
        {
            foreach (var item in selected.AsEnumerable().Reverse())
            {
                int index = _items.IndexOf(item);
                if (index >= 0 && index < _items.Count - 1 && !selected.Contains(_items[index + 1]))
                    _items.Move(index, index + 1);
            }
        }

        RestoreSelection(selected.ToHashSet());
    }

    private void RestoreSelection(HashSet<ImageItem> selected)
    {
        List.SelectedItems.Clear();
        foreach (var item in _items)
            if (selected.Contains(item)) List.SelectedItems.Add(item);
    }

    private void Up_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_isImporting || _isExporting) return;
        var selected = List.SelectedItems.Cast<ImageItem>().ToList();
        foreach (var item in selected) _items.Remove(item);
        UpdateSummary();
        _ = RefreshPreviewAsync();
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            Remove_Click(sender, e);
            e.Handled = true;
        }
    }

    private void List_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = (ItemsControl.ContainerFromElement(List, (DependencyObject)e.OriginalSource) as ListBoxItem)?.DataContext as ImageItem;
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null || _isExporting) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var anchor = _dragItem;
        _dragItem = null;
        var draggedItems = List.SelectedItems.Cast<ImageItem>().ToList();
        if (!draggedItems.Contains(anchor))
        {
            List.SelectedItems.Clear();
            List.SelectedItem = anchor;
            draggedItems = new List<ImageItem> { anchor };
        }

        DragDrop.DoDragDrop(List, new DataObject(typeof(List<ImageItem>), draggedItems), DragDropEffects.Move);
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(List<ImageItem>)))
            e.Effects = DragDropEffects.Move;
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private async void List_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(List<ImageItem>)))
        {
            var dragged = (e.Data.GetData(typeof(List<ImageItem>)) as List<ImageItem>) ?? new List<ImageItem>();
            var target = (ItemsControl.ContainerFromElement(List, (DependencyObject)e.OriginalSource) as ListBoxItem)?.DataContext as ImageItem;
            MoveItemsToTarget(dragged, target);
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var droppedPaths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (droppedPaths is not null) await AddPathsAsync(droppedPaths);
        }
    }

    private void MoveItemsToTarget(List<ImageItem> dragged, ImageItem? target)
    {
        var moving = _items.Where(dragged.Contains).ToList();
        if (moving.Count == 0 || (target is not null && moving.Contains(target))) return;

        int insertionIndex = target is null ? _items.Count : _items.IndexOf(target);
        int removedBefore = moving.Count(item => _items.IndexOf(item) < insertionIndex);
        insertionIndex -= removedBefore;

        foreach (var item in moving) _items.Remove(item);
        insertionIndex = Math.Clamp(insertionIndex, 0, _items.Count);
        for (int index = 0; index < moving.Count; index++)
            _items.Insert(insertionIndex + index, moving[index]);

        RestoreSelection(moving.ToHashSet());
    }

    private async void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        await RefreshPreviewAsync();
    }

    // -------------------------------------------------------------------------
    // Preview and calculated output information
    // -------------------------------------------------------------------------

    private void PreviewFrame_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_updatingPreviewControls) _ = RefreshPreviewAsync();
    }

    private void PageFit_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _updatingPreviewControls) return;
        var item = List.SelectedItem as ImageItem ?? _items.FirstOrDefault();
        if (item is null) return;
        int frameIndex = Math.Max(0, PreviewFrameBox.SelectedIndex);
        item.SetFrameFitModeOverride(frameIndex, PageFitBox.SelectedIndex switch
        {
            1 => 0, // Fit this PDF page.
            2 => 1, // Crop this PDF page.
            _ => null // Follow the global setting.
        });
        _ = RefreshPreviewAsync();
    }

    private async Task RefreshPreviewAsync()
    {
        if (!IsLoaded) return;
        int requestId = ++_previewRequestId;
        var item = List.SelectedItem as ImageItem ?? _items.FirstOrDefault();
        if (item is null)
        {
            PreviewImage.Source = null;
            PreviewPlaceholder.Text = "Your normalized page preview will appear here";
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewTitle.Text = "Select an image to preview";
            PreviewInfo.Text = "The preview matches the exported page layout.";
            _updatingPreviewControls = true;
            PreviewFrameBox.ItemsSource = null;
            PreviewFrameBox.Visibility = Visibility.Collapsed;
            PageFitBox.SelectedIndex = 0;
            PageFitBox.IsEnabled = false;
            _updatingPreviewControls = false;
            _lastPreviewItem = null;
            return;
        }

        int visibleFrames = IncludeAllFramesBox.IsChecked == true ? Math.Max(1, item.FrameCount) : 1;
        int previousFrameIndex = ReferenceEquals(_lastPreviewItem, item) ? Math.Max(0, PreviewFrameBox.SelectedIndex) : 0;
        _updatingPreviewControls = true;
        PreviewFrameBox.ItemsSource = Enumerable.Range(1, visibleFrames).Select(number => number.ToString()).ToList();
        PreviewFrameBox.Visibility = visibleFrames > 1 ? Visibility.Visible : Visibility.Collapsed;
        PreviewFrameBox.SelectedIndex = Math.Clamp(previousFrameIndex, 0, visibleFrames - 1);
        int frameIndex = Math.Clamp(PreviewFrameBox.SelectedIndex, 0, visibleFrames - 1);
        PageFitBox.SelectedIndex = item.GetFrameFitModeOverride(frameIndex) switch { 0 => 1, 1 => 2, _ => 0 };
        PageFitBox.IsEnabled = true;
        _updatingPreviewControls = false;
        _lastPreviewItem = item;
        PreviewTitle.Text = item.Name + (visibleFrames > 1 ? $"  ·  frame {frameIndex + 1}" : string.Empty);
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        PreviewInfo.Text = "Rendering preview…";

        try
        {
            var page = GetPageDimensions();
            var previewPage = GetPreviewDimensions(page, 1500);
            var fit = item.GetFrameFitModeOverride(frameIndex) switch
            {
                0 => ImageFitMode.Contain,
                1 => ImageFitMode.Cover,
                _ => GetFitMode()
            };
            var background = GetPdfBackground();
            var themeInset = GetPdfThemeInsetFraction();
            var imageBytes = await Task.Run(() =>
            {
                using var bitmap = ImageLoader.LoadFrame(item.FilePath, frameIndex)
                    ?? throw new IOException("The selected image could not be decoded.");
                return PdfExportService.RenderPreviewPng(bitmap, previewPage, fit, background, themeInset);
            });

            if (requestId != _previewRequestId) return;
            var preview = new BitmapImage();
            using (var stream = new MemoryStream(imageBytes))
            {
                preview.BeginInit();
                preview.CacheOption = BitmapCacheOption.OnLoad;
                preview.StreamSource = stream;
                preview.EndInit();
                preview.Freeze();
            }
            PreviewImage.Source = preview;
            PreviewInfo.Text = $"Output: {page.PixelWidth:N0} × {page.PixelHeight:N0} px  ·  {page.PointWidth / 72:0.##} × {page.PointHeight / 72:0.##} in";
        }
        catch (Exception ex)
        {
            if (requestId != _previewRequestId) return;
            PreviewImage.Source = null;
            PreviewPlaceholder.Text = "Preview unavailable";
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewInfo.Text = ex.Message;
        }
    }

    private static PageDimensions GetPreviewDimensions(PageDimensions page, int maximumEdge)
    {
        double scale = Math.Min(1.0, maximumEdge / (double)Math.Max(page.PixelWidth, page.PixelHeight));
        return new PageDimensions(
            Math.Max(1, (int)Math.Round(page.PixelWidth * scale)),
            Math.Max(1, (int)Math.Round(page.PixelHeight * scale)),
            page.PointWidth, page.PointHeight);
    }

    private void UpdateSummary()
    {
        if (TargetInfo is null || PageCountText is null) return;
        ResolutionBox.IsEnabled = PageSizeBox.SelectedIndex == 0 && !_isExporting;
        if (_items.Count == 0)
        {
            TargetInfo.Text = "Add images to calculate the output size.";
            PageCountText.Text = "0 PDF pages";
            SummaryInfo.Text = "All pages will share one aspect ratio and resolution.";
            AspectWarning.Text = string.Empty;
            AspectWarning.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            PageFitBox.IsEnabled = false;
            PreviewFrameBox.IsEnabled = false;
            ResolutionHelp.Text = PageSizeBox.SelectedIndex == 0
                ? "Every page is normalized to the same pixel dimensions."
                : "Standard paper dimensions are fixed; DPI determines the pixel resolution.";
            return;
        }

        try
        {
            var page = GetPageDimensions();
            int dpi = GetDpi();
            int pageCount = PdfExportService.CountPages(_items, IncludeAllFramesBox.IsChecked == true);
            TargetInfo.Text = $"{page.PixelWidth:N0} × {page.PixelHeight:N0} px  ·  {dpi} DPI";
            PageCountText.Text = $"{pageCount:N0} PDF page(s)";
            SummaryInfo.Text = $"One page size for all images · {page.PointWidth / 72:0.##} × {page.PointHeight / 72:0.##} in";
            ResolutionBox.IsEnabled = PageSizeBox.SelectedIndex == 0;
            ResolutionHelp.Text = PageSizeBox.SelectedIndex == 0
                ? "Every page is normalized to the same pixel dimensions."
                : "Standard paper dimensions are fixed; DPI determines the pixel resolution.";

            var firstRatio = (double)_items[0].Width / _items[0].Height;
            bool hasMixedRatios = _items.Any(item => Math.Abs(((double)item.Width / item.Height) - firstRatio) > 0.005);
            AspectWarning.Text = hasMixedRatios
                ? (PageSizeBox.SelectedIndex == 0
                    ? "Mixed aspect ratios: Original uses the first image's ratio. Choose a standard size if needed, then preview Fit or Crop for each page."
                    : "Mixed aspect ratios: every page uses the selected standard size. Preview each page and choose Fit or Crop.")
                : "All source images share the same aspect ratio. Resolution differences will be normalized.";
            AspectWarning.SetResourceReference(TextBlock.ForegroundProperty, hasMixedRatios ? "Danger" : "TextSecondary");
            PreviewFrameBox.IsEnabled = IncludeAllFramesBox.IsChecked == true;
            PageFitBox.IsEnabled = true;
        }
        catch (Exception ex)
        {
            TargetInfo.Text = ex.Message;
        }
    }

    // -------------------------------------------------------------------------
    // PDF export
    // -------------------------------------------------------------------------

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_isImporting || _isExporting) return;
        if (_items.Count == 0)
        {
            MessageBox.Show(this, "Add some images first.", "Images to PDF", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "PDF document|*.pdf",
            FileName = "images.pdf",
            DefaultExt = ".pdf",
            AddExtension = true,
            OverwritePrompt = true,
            Title = "Save PDF"
        };
        if (dialog.ShowDialog(this) != true) return;

        var snapshot = _items.ToList();
        var page = GetPageDimensions();
        int pageCount = PdfExportService.CountPages(snapshot, IncludeAllFramesBox.IsChecked == true);
        var options = new ExportOptions(
            page,
            GetFitMode(),
            GetPdfBackground(),
            QualityBox.SelectedIndex == 1,
            IncludeAllFramesBox.IsChecked == true,
            pageCount,
            GetPdfThemeInsetFraction());

        var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        _isExporting = true;
        SetExportingState(true);
        Progress.Minimum = 0;
        Progress.Maximum = pageCount;
        Progress.Value = 0;
        Status.Text = $"Preparing {pageCount:N0} page(s)…";

        var progress = new Progress<ExportProgress>(value =>
        {
            Progress.Value = value.CompletedPages;
            Status.Text = $"Creating page {value.CompletedPages:N0}/{value.TotalPages:N0}  ·  {value.CurrentFile}";
        });

        try
        {
            await Task.Run(() => PdfExportService.Export(snapshot, dialog.FileName, options, progress, cancellation.Token));
            Status.Text = $"PDF created successfully: {dialog.FileName}";
            MessageBox.Show(this, $"Your PDF was created successfully.\n\n{pageCount:N0} page(s)\n{page.PixelWidth:N0} × {page.PixelHeight:N0} px per page",
                "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dialog.FileName}\"") { UseShellExecute = true });
            }
            catch { }
        }
        catch (OperationCanceledException)
        {
            Status.Text = "Export cancelled. No partial PDF was kept.";
        }
        catch (Exception ex)
        {
            Status.Text = "Export failed. The destination file was not replaced.";
            MessageBox.Show(this, ex.Message, "PDF export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            cancellation.Dispose();
            _exportCancellation = null;
            _isExporting = false;
            SetExportingState(false);
            UpdateSummary();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();

    private void SetExportingState(bool exporting)
    {
        CreateBtn.Visibility = exporting ? Visibility.Collapsed : Visibility.Visible;
        CreateBtn.IsEnabled = !exporting && !_isImporting && _items.Count > 0;
        CancelBtn.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
        List.IsEnabled = !exporting;
        AddImagesBtn.IsEnabled = !exporting;
        AddFolderBtn.IsEnabled = !exporting;
        MoveUpBtn.IsEnabled = !exporting;
        MoveDownBtn.IsEnabled = !exporting;
        RemoveBtn.IsEnabled = !exporting;
        AutoSort.IsEnabled = !exporting;
        PageSizeBox.IsEnabled = !exporting;
        FitModeBox.IsEnabled = !exporting;
        PdfThemeBox.IsEnabled = !exporting;
        ResolutionBox.IsEnabled = !exporting && PageSizeBox.SelectedIndex == 0;
        DpiBox.IsEnabled = !exporting;
        QualityBox.IsEnabled = !exporting;
        IncludeAllFramesBox.IsEnabled = !exporting;
        RecursiveBox.IsEnabled = !exporting;
        PreviewFrameBox.IsEnabled = !exporting && IncludeAllFramesBox.IsChecked == true;
        PageFitBox.IsEnabled = !exporting;
        SortBtn.IsEnabled = !exporting;
        ClearBtn.IsEnabled = !exporting;
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_isExporting && e.Key == Key.Escape)
        {
            _exportCancellation?.Cancel();
            e.Handled = true;
            return;
        }
        if (_isImporting || _isExporting) return;

        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (!control) return;

        if (e.Key == Key.O && shift)
        {
            AddFolder_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.O)
        {
            AddImages_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.A)
        {
            List.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            MoveSelected(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            MoveSelected(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Create_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

}
