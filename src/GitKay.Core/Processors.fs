namespace GitKay.Core

open System

/// <summary>The processors available to the process, which sizes the diff and search worker pools. Reading it is a
/// read of the machine, so it lives here, in a capability file, and the pools take the number.</summary>
module Processors =

    /// The number of processors available to the process.
    let count = Environment.ProcessorCount
