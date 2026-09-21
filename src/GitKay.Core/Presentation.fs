namespace GitKay.Core

open System

/// Pure presentation arithmetic: the decisions a view makes before it draws anything. None of it needs a window,
/// so none of it belongs in one — see "Where Code Lives" in AGENTS.md.
module Presentation =

    /// Sizes as a reader wants them, rather than as a count of bytes.
    module Sizes =
        let describeBytes (count: int64) =
            if count < 1024L then $"%d{count} B"
            elif count < 1024L * 1024L then $"%.1f{float count / 1024.0} KB".Replace(".0 KB", " KB")
            else $"%.2f{float count / (1024.0 * 1024.0)} MB".Replace(".00 MB", " MB")

    /// <summary>What a zoom gesture asks for. "Fit" is a state, not a scale, so it is a case rather than a number.</summary>
    type ZoomChange =
        | ZoomIn
        | ZoomOut
        | ZoomToFit

    /// One image shown at a size the reader chose, or fitted to the room there is.
    module ImageView =
        /// <summary>The smallest and largest an image goes, and how far one step of zoom moves it.</summary>
        let minZoom, maxZoom, zoomStep = 0.1, 16.0, 1.25

        /// <summary>
        /// Fitted to the available width, but never blown up past its own pixels: an enlarged image says something
        /// about the file that is not true.
        /// </summary>
        let fitScale (available: float) (pixelWidth: int) =
            if pixelWidth <= 0 then 1.0 else min 1.0 (max 1.0 available / float pixelWidth)

        /// <summary>The scale an image is drawn at: its own zoom, or the fitted one when it has none.</summary>
        let scale (zoom: float) (available: float) (pixelWidth: int) =
            if zoom > 0.0 then zoom else fitScale available pixelWidth

        /// <summary>Where one step of zoom lands. Fitting is returned as zero, which every caller reads as "fit".</summary>
        let step (change: ZoomChange) (current: float) =
            match change with
            | ZoomToFit -> 0.0
            | ZoomIn -> Math.Clamp(current * zoomStep, minZoom, maxZoom)
            | ZoomOut -> Math.Clamp(current / zoomStep, minZoom, maxZoom)

        /// <summary>The drawn size of an image at a scale.</summary>
        let drawnSize (scale: float) (pixelWidth: int, pixelHeight: int) =
            if pixelWidth <= 0 || pixelHeight <= 0 then 0.0, 0.0
            else float pixelWidth * scale, float pixelHeight * scale

    /// A changed-file row: a name that takes what is left, and trailing items pinned to the right.
    module FileRow =
        /// <summary>
        /// How much wider than it strictly needs to be a row must get before a dropped item comes back. Without it
        /// the show and hide widths are the same pixel, and a splitter resting there flickers the item in and out.
        /// </summary>
        let hysteresis = 12.0

        /// <summary>
        /// Whether the item that gives way first still fits. It costs a little more to bring back than to keep, so
        /// a width hovering on the boundary settles instead of flickering.
        /// </summary>
        let showsCollapsible (available: float) (nameWidth: float) (fixedWidth: float) (collapsibleWidth: float) (shownNow: bool) =
            if Double.IsInfinity available then true
            else
                let needed = nameWidth + fixedWidth + collapsibleWidth + (if shownNow then 0.0 else hysteresis)
                needed <= available

    /// The five-block change indicator beside a changed file.
    module ChangeBlocks =
        let count = 5

        /// <summary>
        /// How many of the blocks are green and how many red, scaled for small changes the way GitHub does it: a
        /// one-line change fills one block, not the whole bar. Any side with changes keeps at least one block, so a
        /// lopsided change never reads as one-sided.
        /// </summary>
        let split (added: int) (removed: int) =
            let total = added + removed
            if total <= 0 then 0, 0
            else
                let filled = min count total
                let green = int (Math.Round(float filled * float added / float total))
                let green = if added > 0 && green = 0 then 1 else green
                let red = filled - green
                let green, red = if removed > 0 && red = 0 then filled - 1, 1 else green, red
                max 0 green, max 0 red

    /// Colours mixed for chrome, where the result must be opaque so content cannot show through it.
    module Colour =
        /// <summary>Lays one colour over another at an alpha, returning the opaque result.</summary>
        let blend (under: byte * byte * byte) (over: byte * byte * byte) (alpha: byte) =
            let mix (a: byte) (b: byte) = byte ((int a * (255 - int alpha) + int b * int alpha) / 255)
            let ur, ug, ub = under
            let orr, og, ob = over
            mix ur orr, mix ug og, mix ub ob
