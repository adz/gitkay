using System;
using Avalonia.Media.Imaging;

namespace GitKay.UI;

/// <summary>
/// An image file shown as itself in the diff pane. There is no comparison here: a changed image is two pictures,
/// and telling them apart is the reader's job, not a line-by-line diff's. Deleted images show the side that existed.
/// </summary>
public sealed class ImagePreviewRowProjection : IDiffRowProjection {
    public ImagePreviewRowProjection(Bitmap image, string path, int byteCount, bool isOldSide) {
        Image = image;
        Path = path;
        ByteCount = byteCount;
        IsOldSide = isOldSide;
    }

    public Bitmap Image { get; }
    public string Path { get; }
    public int ByteCount { get; }

    /// <summary>The file was deleted, so what is shown is how it last looked rather than how it looks now.</summary>
    public bool IsOldSide { get; }

    /// <summary>
    /// What can be read off the file itself without guessing: the decoder's own pixel size, the bytes on disk, and
    /// the format from the extension. Anything further — EXIF, colour profiles, orientation — needs a library that
    /// can be wrong about them, so it is left out rather than shown unreliably.
    /// </summary>
    public string Metadata {
        get {
            var format = System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
            var size = $"{Image.PixelSize.Width} × {Image.PixelSize.Height}";
            var parts = format.Length > 0 ? $"{format}  ·  {size}" : size;
            // Density is only worth showing when it is not the default the decoder assumes.
            var dpi = Math.Round(Image.Dpi.X);
            if (dpi > 0 && Math.Abs(dpi - 96) > 0.5) parts += $"  ·  {dpi:0} dpi";
            return $"{parts}  ·  {DescribeBytes(ByteCount)}";
        }
    }

    internal static string DescribeBytes(long count) =>
        count switch {
            < 1024 => $"{count} B",
            < 1024 * 1024 => $"{count / 1024.0:0.#} KB",
            _ => $"{count / (1024.0 * 1024.0):0.##} MB",
        };
}
