namespace GitKay.Core

open System
open Axial
open Axial.PlatformService

/// The single time source for application timing. Every duration the UI reports — click-to-diff, a command's
/// round trip, the diagnostics breadcrumbs — is measured against Axial's explicit, mockable IClock rather than
/// System.Diagnostics.Stopwatch. Marks are monotonic tick counts, so the existing int64 plumbing and message
/// shapes carry a mark from where an action started to where its result arrived without a type change.
module Timing =

    // One process-wide live clock, so any two marks are comparable.
    let private clock : IClock = Clock.live

    /// A monotonic mark to measure a duration from later.
    let mark () : int64 = clock.Elapsed().Ticks

    /// The time between an earlier mark and now.
    let elapsedFrom (startedAt: int64) : TimeSpan = TimeSpan.FromTicks(clock.Elapsed().Ticks - startedAt)

    /// The time between two marks taken from the same clock.
    let elapsedBetween (startedAt: int64) (finishedAt: int64) : TimeSpan = TimeSpan.FromTicks(finishedAt - startedAt)

    /// The current instant, in the clock's own zone (UTC for the live clock).
    let now () : DateTimeOffset = clock.UtcNow()
