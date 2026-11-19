using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace ImagesToPdf.Models;

public sealed class ImageItem : INotifyPropertyChanged
{
    private int _index;
    private readonly Dictionary<int, int> _frameFitModeOverrides = new();

    public string FilePath { get; init; } = string.Empty;
    public string Name => Path.GetFileName(FilePath);
    public int Width { get; init; }
    public int Height { get; init; }
    public int FrameCount { get; init; } = 1;
    public BitmapSource? Thumbnail { get; init; }
    public string Dimensions => $"{Width:N0} × {Height:N0} px";
    public string FrameLabel => FrameCount > 1 ? $"{FrameCount} frames/pages" : "1 page";

    /// <summary>Returns a per-PDF-page override: null follows the global setting, 0 fits, 1 crops.</summary>
    public int? GetFrameFitModeOverride(int frameIndex) =>
        _frameFitModeOverrides.TryGetValue(frameIndex, out int value) ? value : null;

    public void SetFrameFitModeOverride(int frameIndex, int? value)
    {
        if (value is null) _frameFitModeOverrides.Remove(frameIndex);
        else _frameFitModeOverrides[frameIndex] = value.Value;
        OnPropertyChanged(nameof(FrameLabel));
    }

    public int Index
    {
        get => _index;
        set
        {
            if (_index == value) return;
            _index = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
