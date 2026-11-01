namespace GitKay.Core

open System.Threading

/// The process-wide cancellation source for long-lived hosts. It is construction of a raw
/// <c>CancellationTokenSource</c>, so it lives in a capability-role file and the rest of the core only sees its token.
module HostCancellation =

    let private source = new CancellationTokenSource()

    /// Cancelled when the application host stops.
    let token = source.Token

    /// Requests cancellation of every long-lived host.
    let cancel () = source.Cancel()
