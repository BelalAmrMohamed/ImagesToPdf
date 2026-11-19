using System.IO;
using ImagesToPdf.Models;
using SkiaSharp;

namespace ImagesToPdf.Services;

public enum ImageFitMode { Contain, Cover }

public sealed record PageDimensions(int PixelWidth, int PixelHeight, float PointWidth, float PointHeight);
public sealed record ExportOptions(
    PageDimensions Page,
    ImageFitMode FitMode,
    SKColor Background,
    bool HighQuality,
    bool IncludeAllFrames,
    int PageCount,
    float ContentInsetFraction);
public sealed record ExportProgress(int CompletedPages, int TotalPages, string CurrentFile);

public static class PdfExportService
{
    public static readonly (string Name, double WidthInches, double HeightInches)[] StandardPageSizes =
    {
        ("A4 Portrait", 8.2677165354, 11.6929133858),
        ("A4 Landscape", 11.6929133858, 8.2677165354),
        ("Letter Portrait", 8.5, 11.0),
        ("Letter Landscape", 11.0, 8.5),
        ("Square (8 × 8 in)", 8.0, 8.0),
        ("16:9 (12 × 6.75 in)", 12.0, 6.75),
        ("4:3 (10 × 7.5 in)", 10.0, 7.5)
    };

    public static PageDimensions GetPageDimensions(IReadOnlyList<ImageItem> items, int pageSizeIndex, int resolutionIndex, int dpi)
    {
        if (items.Count == 0) throw new InvalidOperationException("Add at least one image first.");
        dpi = Math.Clamp(dpi, 72, 600);

        if (pageSizeIndex > 0)
        {
            var size = StandardPageSizes[pageSizeIndex - 1];
            int width = Math.Max(1, (int)Math.Round(size.WidthInches * dpi));
            int height = Math.Max(1, (int)Math.Round(size.HeightInches * dpi));
            return new PageDimensions(width, height, (float)(size.WidthInches * 72), (float)(size.HeightInches * 72));
        }

        // The first image establishes the one shared output aspect ratio.
        double ratioWidth = items[0].Width;
        double ratioHeight = items[0].Height;
        double ratio = ratioWidth / ratioHeight;
        var equivalentSizes = items
            .Select(item => Math.Min(item.Width / ratio, item.Height))
            .OrderBy(value => value)
            .ToArray();

        double targetHeight = resolutionIndex switch
        {
            1 => Median(equivalentSizes),
            2 => equivalentSizes.Average(),
            3 => equivalentSizes[0],
            _ => equivalentSizes[^1]
        };
        targetHeight = Math.Max(1, targetHeight);

        int targetWidth = Math.Max(1, (int)Math.Round(targetHeight * ratio));
        int targetH = Math.Max(1, (int)Math.Round(targetHeight));
        // A defensive ceiling avoids accidental multi-gigabyte allocations from pathological input dimensions.
        const long maxPixels = 50_000_000;
        if ((long)targetWidth * targetH > maxPixels)
        {
            double shrink = Math.Sqrt(maxPixels / ((double)targetWidth * targetH));
            targetWidth = Math.Max(1, (int)Math.Floor(targetWidth * shrink));
            targetH = Math.Max(1, (int)Math.Floor(targetH * shrink));
        }

        return new PageDimensions(targetWidth, targetH, targetWidth * 72f / dpi, targetH * 72f / dpi);
    }

    public static int CountPages(IEnumerable<ImageItem> items, bool includeAllFrames) =>
        items.Sum(item => includeAllFrames ? Math.Max(1, item.FrameCount) : 1);

    public static SKBitmap RenderPage(SKBitmap source, PageDimensions page, ImageFitMode fitMode, SKColor background, bool highQuality, float contentInsetFraction = 0f)
    {
        var output = new SKBitmap(new SKImageInfo(page.PixelWidth, page.PixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (output.GetPixels() == IntPtr.Zero)
        {
            output.Dispose();
            throw new InvalidOperationException("Not enough memory to render this page. Try a lower resolution.");
        }
        using var canvas = new SKCanvas(output);
        canvas.Clear(background);

        float inset = Math.Clamp(contentInsetFraction, 0f, 0.12f);
        float insetX = page.PixelWidth * inset;
        float insetY = page.PixelHeight * inset;
        var contentBounds = new SKRect(insetX, insetY, page.PixelWidth - insetX, page.PixelHeight - insetY);
        if (inset > 0f)
        {
            // Red theme: keep the outer frame visible and render image content on white.
            using var contentPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawRect(contentBounds, contentPaint);
        }

        float contentWidth = contentBounds.Width;
        float contentHeight = contentBounds.Height;
        float scale = fitMode == ImageFitMode.Contain
            ? Math.Min(contentWidth / source.Width, contentHeight / source.Height)
            : Math.Max(contentWidth / source.Width, contentHeight / source.Height);

        float width = source.Width * scale;
        float height = source.Height * scale;
        var destination = SKRect.Create(contentBounds.MidX - width / 2f, contentBounds.MidY - height / 2f, width, height);
        var sampling = highQuality
            ? new SKSamplingOptions(SKCubicResampler.Mitchell)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

        using var image = SKImage.FromBitmap(source);
        canvas.Save();
        canvas.ClipRect(contentBounds);
        canvas.DrawImage(image, destination, sampling);
        canvas.Restore();
        canvas.Flush();
        return output;
    }

    public static byte[] RenderPreviewPng(SKBitmap source, PageDimensions page, ImageFitMode fitMode, SKColor background, float contentInsetFraction = 0f)
    {
        using var rendered = RenderPage(source, page, fitMode, background, highQuality: true, contentInsetFraction: contentInsetFraction);
        using var image = SKImage.FromBitmap(rendered);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static void Export(
        IReadOnlyList<ImageItem> items,
        string destinationPath,
        ExportOptions options,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using var document = SKDocument.CreatePdf(stream);
                if (document is null) throw new InvalidOperationException("SkiaSharp could not initialize the PDF document.");

                int completed = 0;
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int frameCount = options.IncludeAllFrames ? Math.Max(1, item.FrameCount) : 1;
                    for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var source = ImageLoader.LoadFrame(item.FilePath, frameIndex)
                            ?? throw new IOException($"Could not read {item.Name} (frame {frameIndex + 1}).");
                        var itemFitMode = item.GetFrameFitModeOverride(frameIndex) switch
                        {
                            0 => ImageFitMode.Contain,
                            1 => ImageFitMode.Cover,
                            _ => options.FitMode
                        };
                        using var rendered = RenderPage(source, options.Page, itemFitMode, options.Background, options.HighQuality, options.ContentInsetFraction);
                        using var image = SKImage.FromBitmap(rendered);
                        using var encoded = options.HighQuality
                            ? image.Encode(SKEncodedImageFormat.Png, 100)
                            : image.Encode(SKEncodedImageFormat.Jpeg, 90);

                        var canvas = document.BeginPage(options.Page.PointWidth, options.Page.PointHeight);
                        using var pageImage = SKImage.FromEncodedData(encoded)
                            ?? throw new InvalidDataException($"Could not encode {item.Name} for the PDF.");
                        canvas.DrawImage(pageImage, new SKRect(0, 0, options.Page.PointWidth, options.Page.PointHeight));
                        document.EndPage();

                        completed++;
                        progress?.Report(new ExportProgress(completed, options.PageCount, item.Name));
                    }
                }

                // SKDocument.Close finalizes the PDF stream; disposing the stream at the end
                // of this scope completes the file before validation and atomic replacement.
                document.Close();
            }

            cancellationToken.ThrowIfCancellationRequested();
            ValidatePdfFile(temporaryPath);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            throw;
        }
    }

    private static void ValidatePdfFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 8)
            throw new InvalidDataException("The PDF export did not produce a valid output file.");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[5];
        stream.ReadExactly(header);
        if (!header.SequenceEqual("%PDF-"u8))
            throw new InvalidDataException("The output file does not have a valid PDF header.");
    }

    private static double Median(double[] values)
    {
        int middle = values.Length / 2;
        return values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
    }
}
