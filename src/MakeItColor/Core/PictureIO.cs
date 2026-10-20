using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MakeItColor.Core;

/// <summary>Opens and saves photos with Windows' own codecs (WIC): JPEG, PNG, BMP, TIFF, GIF, and WebP / HEIC when installed.</summary>
internal static class PictureIO
{
    public static readonly string[] PhotoExtensions = { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".tif", ".tiff", ".gif", ".webp", ".heic", ".heif", ".jxr", ".wdp" };

    public static readonly string[] VideoExtensions = { ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".mpg", ".mpeg", ".m2ts", ".mts", ".ts", ".flv", ".3gp", ".ogv", ".vob", ".mxf" };

    public static bool IsPhoto(string path) => PhotoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads a photo upright (the EXIF orientation applied) as 8-bit BGRA.</summary>
    public static Picture Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        var hasAlpha = frame.Format == PixelFormats.Bgra32 || frame.Format == PixelFormats.Pbgra32 || frame.Format == PixelFormats.Prgba64
                       || frame.Format == PixelFormats.Rgba64 || frame.Format == PixelFormats.Rgba128Float || frame.Format == PixelFormats.Prgba128Float;
        frame = Upright(frame, Orientation(decoder.Frames[0]));
        if (frame.Format != PixelFormats.Bgra32) frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var picture = new Picture(frame.PixelWidth, frame.PixelHeight) { HasAlpha = hasAlpha };
        frame.CopyPixels(picture.Pixels, picture.Stride, 0);
        if (!hasAlpha)
            for (var i = 3; i < picture.Pixels.Length; i += 4) picture.Pixels[i] = 255;
        return picture;
    }

    /// <summary>EXIF orientation 1-8 (1 when there is none).</summary>
    private static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta)
                foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}", "System.Photo.Orientation" })
                    if (meta.ContainsQuery(query) && meta.GetQuery(query) is { } value)
                        return Convert.ToInt32(value);
        }
        catch
        {
            // formats without metadata
        }
        return 1;
    }

    private static BitmapSource Upright(BitmapSource source, int orientation)
    {
        if (orientation is < 2 or > 8) return source;
        var transform = new TransformGroup();
        // EXIF: 2 mirror, 3 rotate 180, 4 mirror + 180, 5 mirror + 270 (transpose), 6 rotate 90, 7 mirror + 90, 8 rotate 270.
        if (orientation is 2 or 4 or 5 or 7) transform.Children.Add(new ScaleTransform(-1, 1));
        var angle = orientation switch { 3 or 4 => 180, 6 or 7 => 90, 5 or 8 => 270, _ => 0 };
        if (angle != 0) transform.Children.Add(new RotateTransform(angle));
        var result = new TransformedBitmap(source, transform);
        result.Freeze();
        return result;
    }

    /// <summary>Saves as PNG (lossless, keeps transparency) or JPEG (quality 95).</summary>
    public static void Save(Picture picture, string path, PhotoFormat format)
    {
        BitmapEncoder encoder = format == PhotoFormat.Jpeg
            ? new JpegBitmapEncoder { QualityLevel = 95 }
            : new PngBitmapEncoder();
        var source = ToBitmap(picture);
        if (format == PhotoFormat.Jpeg || !picture.HasAlpha) source = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        encoder.Frames.Add(BitmapFrame.Create(source));
        var temp = path + ".part";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) encoder.Save(file);
        File.Move(temp, path, true);
    }

    public static BitmapSource ToBitmap(Picture picture)
    {
        var bitmap = BitmapSource.Create(picture.Width, picture.Height, 96, 96, PixelFormats.Bgra32, null, picture.Pixels, picture.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A small copy for the screen (the long side at most <paramref name="max"/> pixels).</summary>
    public static BitmapSource Preview(Picture picture, int max)
    {
        BitmapSource bitmap = ToBitmap(picture);
        var scale = Math.Min(1.0, (double)max / Math.Max(picture.Width, picture.Height));
        if (scale < 1)
        {
            bitmap = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
            bitmap.Freeze();
        }
        return bitmap;
    }

    /// <summary>"photo (color).png" next to the original, not overwriting anything.</summary>
    public static string SuggestOutput(string input, string extension, string? folder = null)
    {
        var dir = folder ?? Path.GetDirectoryName(input) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var name = Path.GetFileNameWithoutExtension(input) + " (color)";
        var path = Path.Combine(dir, name + extension);
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{name} {i}{extension}");
        return path;
    }
}
