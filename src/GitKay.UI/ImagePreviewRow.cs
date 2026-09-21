using System;
using Avalonia.Media.Imaging;

namespace GitKay.UI;

/// <summary>
/// An image file shown as itself in the diff pane. There is no comparison here: a changed image is two pictures,
/// and telling them apart is the reader's job, not a line-by-line diff's. Deleted images show the side that existed.
/// </summary>
public sealed class ImagePreviewRowProjection : IDiffRowProjection {
    public ImagePreviewRowProjection(Bitmap image, string path, int byteCount, bool isOldSide, DiffFileProjection file) {
        Image = image;
        Path = path;
        ByteCount = byteCount;
        IsOldSide = isOldSide;
        File = file;
    }

    /// <summary>The file this picture belongs to, which holds the zoom across re-projections of the rows.</summary>
    public DiffFileProjection File { get; }

    /// <summary>0 fits the picture to the pane; otherwise a scale against its own pixels.</summary>
    public double Zoom { get => File.PreviewImageZoom; set => File.PreviewImageZoom = value; }



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
            var caption = $"{parts}  ·  {GitKay.Core.Presentation.Sizes.describeBytes(ByteCount)}";
            // Only once it is not showing the whole picture at its fitted size: a percentage on every image is noise.
            return Zoom > 0 ? $"{caption}  ·  {Zoom * 100:0}%" : caption;
        }
    }

}
