namespace GitKay.Tests

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Headless
open GitKay.UI

/// <summary>
/// Renders a surface headlessly and reads its pixels back. Layout and contrast faults are invisible to ordinary
/// tests — they assert over projections, which are correct while the thing on screen is unreadable — so anything
/// about how a surface *looks* is checked here instead of being found by whoever is using the app.
/// </summary>
module Render =

    /// The shipped palette, so a measurement is of what ships rather than of the bare theme's fallbacks.
    let private palette =
        [ "GitKayWindowBrush", "#0D1117"; "GitKaySurfaceBrush", "#1B222C"; "GitKayRaisedBrush", "#222A35"
          "GitKayAccentBrush", "#58A6FF"; "GitKayBorderBrush", "#3C444D"; "GitKaySelectionBrush", "#27364A"
          "GitKayHoverBrush", "#21262D"; "GitKayHairlineBrush", "#2B333C"
          "GitKayTextBrush", "#E6EDF3"; "GitKaySecondaryTextBrush", "#9DA7B3"; "GitKayMutedTextBrush", "#6E7681"
          "GitKayAddedAccentBrush", "#3FB950"; "GitKayRemovedAccentBrush", "#F85149"
          "GitKayDiffStatNeutralBrush", "#30363D"; "GitKayFindMatchBrush", "#FFDF5D" ]

    /// <summary>A window around one control, carrying the real palette, rendered and captured.</summary>
    let capture (width: float) (height: float) (content: Control) =
        let window = Window(Width = width, Height = height, Content = content)
        for key, hex in palette do window.Resources.Add(key, SolidColorBrush(Color.Parse hex))
        window.Show()
        window.UpdateLayout()
        Avalonia.Threading.Dispatcher.UIThread.RunJobs()
        window, window.CaptureRenderedFrame()

    /// <summary>The pixels of a frame as (r, g, b), indexed by x and y.</summary>
    let private pixels (frame: WriteableBitmap) =
        use buffer = frame.Lock()
        let stride = buffer.RowBytes
        let bytes = Array.zeroCreate<byte> (stride * frame.PixelSize.Height)
        Runtime.InteropServices.Marshal.Copy(buffer.Address, bytes, 0, bytes.Length)
        fun x y ->
            let at = y * stride + x * 4
            bytes[at + 2], bytes[at + 1], bytes[at]

    let private luminance (r: byte, g: byte, b: byte) =
        0.299 * float r + 0.587 * float g + 0.114 * float b

    /// <summary>The darkest and lightest luminance in a rectangle of the frame.</summary>
    let luminanceRange (frame: WriteableBitmap) (x0, y0) (x1, y1) =
        let at = pixels frame
        let values = [ for x in x0 .. x1 - 1 do for y in y0 .. y1 - 1 -> luminance (at x y) ]
        List.min values, List.max values

    /// <summary>
    /// How much a band varies, which is what "is there anything drawn here" comes down to: flat background scores
    /// near zero however pretty the colours are.
    /// </summary>
    let contrast (frame: WriteableBitmap) (x0, y0) (x1, y1) =
        let darkest, lightest = luminanceRange frame (x0, y0) (x1, y1)
        lightest - darkest

    /// <summary>Whether a band is one flat colour — used to prove something is *not* drawn where it should not be.</summary>
    let isFlat (frame: WriteableBitmap) (x0, y0) (x1, y1) = contrast frame (x0, y0) (x1, y1) < 4.0

    /// <summary>The x of the first column in a row that differs from the row's leftmost pixel: where content starts.</summary>
    let firstContentColumn (frame: WriteableBitmap) (y: int) (x0: int) (x1: int) =
        let at = pixels frame
        let background = at x0 y
        [ x0 .. x1 - 1 ] |> List.tryFind (fun x -> at x y <> background)
