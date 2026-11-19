using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SkiaSharp;

namespace ImagesToPdf;

public class ImageItem : INotifyPropertyChanged
{
    public string FilePath { get; init; } = "";
    public string Name => System.IO.Path.GetFileName(FilePath);
    public int Width { get; init; }
    public int Height { get; init; }
    public string Dims => $"{Width} × {Height}";
    public BitmapSource? Thumb { get; init; }
    int _index;
    public int Index { get => _index; set { _index = value; PropertyChanged?.Invoke(this, new(nameof(Index))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int StrCmpLogicalW(string a, string b);

    static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".jfif", ".webp", ".bmp", ".gif", ".ico", ".tif", ".tiff" };

    readonly ObservableCollection<ImageItem> items = new();
    Point dragStart; ImageItem? dragItem;

    public MainWindow()
    {
        InitializeComponent();
        List.ItemsSource = items;
        items.CollectionChanged += (_, _) => { Renumber(); UpdateInfo(); };
    }

    // ---------- loading ----------
    static SKBitmap? LoadBitmap(string p)
    {
        try { var b = SKBitmap.Decode(p); if (b != null) return b; } catch { }
        try // fallback (e.g. TIFF) through WPF decoders
        {
            var dec = BitmapDecoder.Create(new Uri(p), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(dec.Frames[0]);
            using var ms = new MemoryStream(); enc.Save(ms);
            return SKBitmap.Decode(ms.ToArray());
        }
        catch { return null; }
    }

    static BitmapSource MakeThumb(SKBitmap b)
    {
        float s = Math.Min(1f, Math.Min(96f / b.Width, 96f / b.Height));
        int w = Math.Max(1, (int)(b.Width * s)), h = Math.Max(1, (int)(b.Height * s));
        using var r = b.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using var img = SKImage.FromBitmap(r);
        using var d = img.Encode(SKEncodedImageFormat.Png, 100);
        var bi = new BitmapImage();
        bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = new MemoryStream(d.ToArray()); bi.EndInit(); bi.Freeze();
        return bi;
    }

    static ImageItem? Build(string f)
    {
        using var b = LoadBitmap(f);
        if (b == null) return null;
        return new ImageItem { FilePath = f, Width = b.Width, Height = b.Height, Thumb = MakeThumb(b) };
    }

    async Task AddPaths(IEnumerable<string> paths)
    {
        var files = paths
            .SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p) : new[] { p })
            .Where(f => Exts.Contains(System.IO.Path.GetExtension(f)))
            .Where(f => !items.Any(i => i.FilePath.Equals(f, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int skipped = 0, n = 0;
        foreach (var f in files)
        {
            Status.Text = $"Loading {++n}/{files.Count}…";
            var it = await Task.Run(() => Build(f));
            if (it == null) skipped++; else items.Add(it);
        }
        if (AutoSort.IsChecked == true) SortItems();
        Status.Text = skipped > 0 ? $"{skipped} file(s) could not be read." : "";
    }

    // ---------- ordering ----------
    void SortItems()
    {
        var sorted = items.OrderBy(i => i.Name, Comparer<string>.Create(StrCmpLogicalW)).ToList();
        items.Clear();
        foreach (var i in sorted) items.Add(i);
    }

    void Renumber() { for (int i = 0; i < items.Count; i++) items[i].Index = i + 1; Hint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed; }

    void MoveSelected(int delta)
    {
        var sel = List.SelectedItems.Cast<ImageItem>().OrderBy(items.IndexOf).ToList();
        if (sel.Count == 0) return;
        if (delta < 0) foreach (var s in sel) { int i = items.IndexOf(s); if (i > 0 && !sel.Contains(items[i - 1])) items.Move(i, i - 1); }
        else foreach (var s in Enumerable.Reverse(sel)) { int i = items.IndexOf(s); if (i < items.Count - 1 && !sel.Contains(items[i + 1])) items.Move(i, i + 1); }
    }

    // ---------- drag & drop ----------
    void List_PreviewMouseDown(object s, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(null);
        dragItem = (ItemsControl.ContainerFromElement(List, (DependencyObject)e.OriginalSource) as ListBoxItem)?.DataContext as ImageItem;
    }

    void List_PreviewMouseMove(object s, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragItem == null) return;
        var d = e.GetPosition(null) - dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = dragItem; dragItem = null;
        DragDrop.DoDragDrop(List, new DataObject(typeof(ImageItem), item), DragDropEffects.Move);
    }

    async void List_Drop(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(ImageItem)))
        {
            var src = (ImageItem)e.Data.GetData(typeof(ImageItem))!;
            var target = (ItemsControl.ContainerFromElement(List, (DependencyObject)e.OriginalSource) as ListBoxItem)?.DataContext as ImageItem;
            int from = items.IndexOf(src), to = target == null ? items.Count - 1 : items.IndexOf(target);
            if (from >= 0 && to >= 0 && from != to) { items.Move(from, to); List.SelectedItem = src; }
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            await AddPaths((string[])e.Data.GetData(DataFormats.FileDrop)!);
    }

    void List_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Delete) Remove_Click(s, e); }

    // ---------- buttons ----------
    async void AddImages_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = "Images|*.png;*.jpg;*.jpeg;*.jfif;*.webp;*.bmp;*.gif;*.ico;*.tif;*.tiff|All files|*.*" };
        if (dlg.ShowDialog() == true) await AddPaths(dlg.FileNames);
    }

    async void AddFolder_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog() == true) await AddPaths(new[] { dlg.FolderName });
    }

    void Up_Click(object s, RoutedEventArgs e) => MoveSelected(-1);
    void Down_Click(object s, RoutedEventArgs e) => MoveSelected(1);
    void Remove_Click(object s, RoutedEventArgs e) { foreach (var i in List.SelectedItems.Cast<ImageItem>().ToList()) items.Remove(i); }
    void Clear_Click(object s, RoutedEventArgs e) => items.Clear();
    void Sort_Click(object s, RoutedEventArgs e) => SortItems();
    void Setting_Changed(object s, SelectionChangedEventArgs e) => UpdateInfo();

    // ---------- resolution ----------
    (int w, int h) Target()
    {
        var l = items.ToList();
        var byArea = l.OrderBy(i => (long)i.Width * i.Height).ToList();
        switch (ResBox.SelectedIndex)
        {
            case 1: var m = byArea[l.Count / 2]; return (m.Width, m.Height);
            case 2:
                int w = (int)Math.Round(l.Average(i => i.Width));
                return (w, (int)Math.Round(w * l.Average(i => (double)i.Height / i.Width)));
            case 3: return (byArea[0].Width, byArea[0].Height);
            default: var t = byArea[^1]; return (t.Width, t.Height);
        }
    }

    void UpdateInfo()
    {
        if (ResBox == null || TargetInfo == null) return;
        if (items.Count == 0) { TargetInfo.Text = ""; return; }
        var (w, h) = Target();
        TargetInfo.Text = $"All pages: {w} × {h} px  ({items.Count} images)";
    }

    // ---------- PDF ----------
    static byte[] Render(SKBitmap src, int tw, int th, bool high)
    {
        float s = Math.Min((float)tw / src.Width, (float)th / src.Height);
        float dw = src.Width * s, dh = src.Height * s;
        var dest = SKRect.Create((tw - dw) / 2, (th - dh) / 2, dw, dh);
        var sampling = s < 1 ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
                     : high ? new SKSamplingOptions(SKCubicResampler.Mitchell)
                            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        using var surface = SKSurface.Create(new SKImageInfo(tw, th, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas; c.Clear(SKColors.White);
        using var img = SKImage.FromBitmap(src);
        c.DrawImage(img, dest, sampling);
        using var snap = surface.Snapshot();
        using var data = snap.Encode(SKEncodedImageFormat.Jpeg, high ? 95 : 80);
        return data.ToArray();
    }

    async void Create_Click(object s, RoutedEventArgs e)
    {
        if (items.Count == 0) { MessageBox.Show("Add some images first."); return; }
        var dlg = new SaveFileDialog { Filter = "PDF|*.pdf", FileName = "output.pdf" };
        if (dlg.ShowDialog() != true) return;

        var list = items.ToList(); var (tw, th) = Target(); bool high = QualityBox.SelectedIndex == 1; string path = dlg.FileName;
        CreateBtn.IsEnabled = false; Progress.Maximum = list.Count; Progress.Value = 0;
        var prog = new Progress<int>(v => { Progress.Value = v; Status.Text = $"Page {v}/{list.Count}…"; });
        try
        {
            await Task.Run(() =>
            {
                float pw = tw * 72f / 150f, ph = th * 72f / 150f; // pages at 150 DPI
                using var fs = File.Create(path);
                using var doc = SKDocument.CreatePdf(fs);
                int n = 0;
                foreach (var it in list)
                {
                    using var bmp = LoadBitmap(it.FilePath) ?? throw new IOException($"Cannot read {it.Name}");
                    var jpg = Render(bmp, tw, th, high);
                    using var img = SKImage.FromEncodedData(jpg);
                    var canvas = doc.BeginPage(pw, ph);
                    canvas.DrawImage(img, new SKRect(0, 0, pw, ph));
                    doc.EndPage();
                    ((IProgress<int>)prog).Report(++n);
                }
                doc.Close();
            });
            Status.Text = "Done ✔  " + path;
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex) { Status.Text = "Failed."; MessageBox.Show(ex.Message, "Error"); }
        finally { CreateBtn.IsEnabled = true; }
    }
}
