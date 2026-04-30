namespace GitKay.Core

module Graph =

    type LaneInfo =
        {
            Lane: int
            Color: int // Index or Hex
        }

    type GraphNode =
        {
            Hash: string
            Lane: int
            Connections: Connection list
        }
    and Connection =
        {
            TargetHash: string
            TargetLane: int
            IsMerge: bool
        }

    type LaneSegment =
        {
            Lane: int
            TargetLane: int
            IsCommit: bool
            Color: int
        }

    type CommitGraphInfo =
        {
            Commit: Models.Commit
            Lane: int
            Segments: LaneSegment list
        }

    let calculateLanes (commits: Models.Commit list) =
        let mutable activeLanes = [] // List of hash strings
        
        let infos = 
            commits |> List.map (fun commit ->
                // 1. Find or assign lane for the current commit
                let currentLane = 
                    match activeLanes |> List.tryFindIndex (fun h -> h = commit.Hash) with
                    | Some idx -> idx
                    | None -> 
                        activeLanes <- activeLanes @ [commit.Hash]
                        activeLanes.Length - 1
                
                // 2. Capture segments for THIS row (leading to the NEXT row)
                // We need to know where each active lane is going.
                
                // Remove current commit from active lanes for the next row
                let lanesWithoutCurrent = 
                    activeLanes 
                    |> List.mapi (fun i h -> if i = currentLane then None else Some h)
                    |> List.choose id
                
                // Add parents to active lanes for next row if not already there
                let mutable nextActiveLanes = lanesWithoutCurrent
                commit.Parents |> List.iter (fun p ->
                    if not (List.contains p nextActiveLanes) then
                        nextActiveLanes <- nextActiveLanes @ [p]
                )

                // Define segments that carry lanes from THIS row to the NEXT row
                let segments = 
                    activeLanes |> List.mapi (fun i hash ->
                        if i = currentLane then
                            // Current commit connects to all its parents
                            commit.Parents |> List.map (fun pHash ->
                                let targetIdx = nextActiveLanes |> List.findIndex (fun h -> h = pHash)
                                { Lane = i; TargetLane = targetIdx; IsCommit = true; Color = i }
                            )
                        else
                            // This lane is just passing through
                            let targetIdx = nextActiveLanes |> List.findIndex (fun h -> h = hash)
                            [{ Lane = i; TargetLane = targetIdx; IsCommit = false; Color = i }]
                    ) |> List.concat

                let info = { Commit = commit; Lane = currentLane; Segments = segments }
                
                // Update activeLanes for the next iteration
                activeLanes <- nextActiveLanes
                info
            )
        infos
