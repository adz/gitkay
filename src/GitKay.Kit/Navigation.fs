namespace GitKay.Kit

/// <summary>Back and forward through visited places, like a browser: visiting somewhere new clears forward.</summary>
type Navigation<'place when 'place: equality> =
    { /// <summary>Earlier places, most recent first.</summary>
      Back: 'place list
      Current: 'place option
      /// <summary>Places stepped back from, most recent first.</summary>
      Forward: 'place list }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Navigation =
    [<Literal>]
    let private Limit = 200

    let empty<'place when 'place: equality> : Navigation<'place> = { Back = []; Current = None; Forward = [] }

    /// <summary>Records a visit; revisiting the current place changes nothing.</summary>
    let visit (place: 'place) (navigation: Navigation<'place>) =
        match navigation.Current with
        | Some current when current = place -> navigation
        | Some current -> { Back = current :: navigation.Back |> List.truncate Limit; Current = Some place; Forward = [] }
        | None -> { navigation with Current = Some place; Forward = [] }

    let private step (from: 'place list) (isAvailable: 'place -> bool) =
        // Places that no longer exist (a commit gone after a rewrite) are skipped and dropped.
        let rec go (remaining: 'place list) =
            match remaining with
            | [] -> None
            | place :: rest when isAvailable place -> Some(place, rest)
            | _ :: rest -> go rest
        go from

    /// <summary>The previous available place and the navigation after moving there, or None.</summary>
    let back (isAvailable: 'place -> bool) (navigation: Navigation<'place>) =
        step navigation.Back isAvailable
        |> Option.map (fun (place, rest) ->
            place, { Back = rest; Current = Some place; Forward = (Option.toList navigation.Current) @ navigation.Forward })

    /// <summary>The next available place and the navigation after moving there, or None.</summary>
    let forward (isAvailable: 'place -> bool) (navigation: Navigation<'place>) =
        step navigation.Forward isAvailable
        |> Option.map (fun (place, rest) ->
            place, { Back = (Option.toList navigation.Current) @ navigation.Back |> List.truncate Limit; Current = Some place; Forward = rest })

    let canGoBack (navigation: Navigation<'place>) = not navigation.Back.IsEmpty
    let canGoForward (navigation: Navigation<'place>) = not navigation.Forward.IsEmpty
