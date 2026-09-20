namespace GitKay.Kit

open System.Collections.Generic

type SequenceEdit<'a> =
    | Equal of 'a * 'a
    | Delete of 'a
    | Insert of 'a

[<RequireQualifiedAccess>]
module Myers =
    /// Myers' O(ND) shortest-edit-script diff. Equality is supplied by the caller so fingerprints can be compared cheaply.
    let diffBy (equals: 'a -> 'a -> bool) (oldItems: 'a array) (newItems: 'a array) =
        let n, m = oldItems.Length, newItems.Length
        let maximum = n + m
        if maximum = 0 then [] else
        let offset = maximum
        let v = Array.create (2 * maximum + 1) 0
        let trace = ResizeArray<int array>()
        let mutable distance = 0
        let mutable finished = false
        while distance <= maximum && not finished do
            trace.Add(Array.copy v)
            let mutable k = -distance
            while k <= distance && not finished do
                let index = offset + k
                let mutable x =
                    if k = -distance || (k <> distance && v[index - 1] < v[index + 1]) then v[index + 1]
                    else v[index - 1] + 1
                let mutable y = x - k
                while x < n && y < m && equals oldItems[x] newItems[y] do
                    x <- x + 1
                    y <- y + 1
                v[index] <- x
                if x >= n && y >= m then finished <- true
                k <- k + 2
            if not finished then distance <- distance + 1

        let edits = ResizeArray<SequenceEdit<'a>>()
        let mutable x, y = n, m
        for d = distance downto 1 do
            let previous = trace[d]
            let k = x - y
            let previousK =
                if k = -d || (k <> d && previous[offset + k - 1] < previous[offset + k + 1]) then k + 1
                else k - 1
            let previousX = previous[offset + previousK]
            let previousY = previousX - previousK
            while x > previousX && y > previousY do
                edits.Add(Equal(oldItems[x - 1], newItems[y - 1]))
                x <- x - 1; y <- y - 1
            if x = previousX then
                edits.Add(Insert newItems[y - 1]); y <- y - 1
            else
                edits.Add(Delete oldItems[x - 1]); x <- x - 1
        while x > 0 && y > 0 do
            edits.Add(Equal(oldItems[x - 1], newItems[y - 1])); x <- x - 1; y <- y - 1
        while x > 0 do edits.Add(Delete oldItems[x - 1]); x <- x - 1
        while y > 0 do edits.Add(Insert newItems[y - 1]); y <- y - 1
        edits |> Seq.rev |> Seq.toList

    let diff oldItems newItems = diffBy (=) oldItems newItems
