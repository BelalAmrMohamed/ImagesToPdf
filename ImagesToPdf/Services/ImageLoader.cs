using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using ImagesToPdf.Models;

namespace ImagesToPdf.Services;

public sealed record DecodedImageInfo(int Width, int Height, int FrameCount);

public static class ImageLoader
{
    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".webp", ".bmp", ".gif", ".ico", ".tif", ".tiff"
    };

    public static (ImageItem? Item, string? Error) TryBuildItem(string filePath)
    {
        try
        {
            var metadata = GetMetadata(filePath);
            using var bitmap = LoadFrame(filePath, 0);
            if (bitmap is null)
                return (null, "The image decoder returned no image data.");

            var item = new ImageItem
            {
                FilePath = Path.GetFullPath(filePath),
                Width = metadata.Width,
                Height = metadata.Height,
                FrameCount = metadata.FrameCount,
                Thumbnail = CreateThumbnail(bitmap)
            };
            return (item, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public static DecodedImageInfo GetMetadata(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
            if (decoder.Frames.Count == 0) throw new InvalidDataException("The image contains no readable frames.");
            var frame = decoder.Frames[0];
            int orientation = ReadExifOrientation(frame);
            bool swapsDimensions = orientation is 5 or 6 or 7 or 8;
            return new DecodedImageInfo(
                swapsDimensions ? frame.PixelHeight : frame.PixelWidth,
                swapsDimensions ? frame.PixelWidth : frame.PixelHeight,
                Math.Max(1, decoder.Frames.Count));
        }
        catch (Exception wicException)
        {
            // SkiaSharp supports formats (notably WebP on some Windows installations)
            // for which a Windows Imaging Component codec may not be installed.
            using var fallback = SKBitmap.Decode(filePath);
            if (fallback is not null)
                return new DecodedImageInfo(fallback.Width, fallback.Height, 1);
            throw new InvalidDataException("The image format is not supported or the file is corrupted.", wicException);
        }
    }

    public static SKBitmap? LoadFrame(string filePath, int frameIndex)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
            if (decoder.Frames.Count == 0) return null;

            var index = Math.Clamp(frameIndex, 0, decoder.Frames.Count - 1);
            BitmapSource source = decoder.Frames[index];
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = checked(converted.PixelWidth * 4);
            byte[] pixels = new byte[checked(stride * converted.PixelHeight)];
            converted.CopyPixels(pixels, stride, 0);

            var bitmap = new SKBitmap(new SKImageInfo(
                converted.PixelWidth, converted.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul), stride);
            if (bitmap.GetPixels() == IntPtr.Zero)
            {
                bitmap.Dispose();
                throw new InvalidOperationException("Could not allocate memory for the image.");
            }

            Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
            try
            {
                return ApplyExifOrientation(bitmap, ReadExifOrientation(decoder.Frames[index]));
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
        catch when (frameIndex == 0)
        {
            // Fallback for single-frame formats unsupported by the installed WIC codecs.
            return SKBitmap.Decode(filePath);
        }
    }

    private static int ReadExifOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery("/app1/ifd/{ushort=274}"))
            {
                var value = metadata.GetQuery("/app1/ifd/{ushort=274}");
                int orientation = Convert.ToInt32(value);
                return orientation is >= 1 and <= 8 ? orientation : 1;
            }
        }
        catch { }
        return 1;
    }

    private static SKBitmap ApplyExifOrientation(SKBitmap source, int orientation)
    {
        if (orientation == 1 || orientation is < 1 or > 8) return source;

        int sourceWidth = source.Width;
        int sourceHeight = source.Height;
        bool swapsDimensions = orientation is 5 or 6 or 7 or 8;
        int targetWidth = swapsDimensions ? sourceHeight : sourceWidth;
        int targetHeight = swapsDimensions ? sourceWidth : sourceHeight;
        int targetStride = checked(targetWidth * 4);
        byte[] sourcePixels = new byte[checked(source.RowBytes * sourceHeight)];
        byte[] targetPixels = new byte[checked(targetStride * targetHeight)];
        Marshal.Copy(source.GetPixels(), sourcePixels, 0, sourcePixels.Length);

        for (int y = 0; y < sourceHeight; y++)
        {
            for (int x = 0; x < sourceWidth; x++)
            {
                (int targetX, int targetY) = orientation switch
                {
                    2 => (sourceWidth - 1 - x, y),
                    3 => (sourceWidth - 1 - x, sourceHeight - 1 - y),
                    4 => (x, sourceHeight - 1 - y),
                    5 => (y, x),
                    6 => (sourceHeight - 1 - y, x),
                    7 => (sourceHeight - 1 - y, sourceWidth - 1 - x),
                    8 => (y, sourceWidth - 1 - x),
                    _ => (x, y)
                };

                int sourceOffset = y * source.RowBytes + x * 4;
                int targetOffset = targetY * targetStride + targetX * 4;
                Buffer.BlockCopy(sourcePixels, sourceOffset, targetPixels, targetOffset, 4);
            }
        }

        var oriented = new SKBitmap(
            new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul), targetStride);
        if (oriented.GetPixels() == IntPtr.Zero)
        {
            oriented.Dispose();
            throw new InvalidOperationException("Could not allocate memory for the rotated image.");
        }
        Marshal.Copy(targetPixels, 0, oriented.GetPixels(), targetPixels.Length);
        source.Dispose();
        return oriented;
    }

    private static BitmapSource CreateThumbnail(SKBitmap source)
    {
        float scale = Math.Min(1f, Math.Min(112f / source.Width, 112f / source.Height));
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var resized = source.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (resized is null) throw new InvalidOperationException("Could not create the image thumbnail.");
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(data.ToArray());
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
