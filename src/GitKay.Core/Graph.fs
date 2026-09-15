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

    /// <summary>
    /// One line through a row: from <c>Lane</c> at the row's top (or, for a commit's edge, its node) to
    /// <c>TargetLane</c> at the row's bottom. <c>Color</c> identifies the branch line, stable along it.
    /// </summary>
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
            /// A child above leads into this commit, so a line runs from the row's top into its node.
            HasIncoming: bool
            /// The commit's branch line colour.
            Color: int
        }

    let calculateLanes (commits: Models.Commit list) =
        let emptyLane = ""
        let isOccupied hash = not (System.String.IsNullOrEmpty hash)

        let mutable activeLanes = System.Collections.Generic.List<string>()
        let mutable activeLaneIndexes = System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal)
        let infos = System.Collections.Generic.List<CommitGraphInfo>()
        // A line keeps its colour from the commit that started it down to where it ends; new lines take the next colour.
        let colors = System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal)
        let mutable nextColor = 0
        let newColor () =
            let color = nextColor
            nextColor <- nextColor + 1
            color

        for commit in commits do
            let hasIncoming = activeLaneIndexes.ContainsKey commit.Hash
            let currentLane =
                match activeLaneIndexes.TryGetValue commit.Hash with
                | true, lane -> lane
                | false, _ ->
                    let lane = activeLanes.FindIndex(fun hash -> System.String.IsNullOrEmpty hash)

                    let lane =
                        if lane >= 0 then lane
                        else activeLanes.Count

                    if lane = activeLanes.Count then
                        activeLanes.Add commit.Hash
                    else
                        activeLanes.[lane] <- commit.Hash

                    activeLaneIndexes.[commit.Hash] <- lane
                    lane

            let nextActiveLanes =
                System.Collections.Generic.List<string>(activeLanes)

            if currentLane < nextActiveLanes.Count then
                nextActiveLanes.[currentLane] <- emptyLane

            let nextLaneIndexes =
                System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal)

            for laneIndex in 0 .. nextActiveLanes.Count - 1 do
                let hash = nextActiveLanes.[laneIndex]
                if isOccupied hash then
                    nextLaneIndexes.[hash] <- laneIndex

            let tryPlaceParent (parentHash: string) =
                match nextLaneIndexes.TryGetValue parentHash with
                | true, lane -> lane
                | false, _ ->
                    let lane =
                        if currentLane < nextActiveLanes.Count && not (isOccupied nextActiveLanes.[currentLane]) then
                            currentLane
                        else
                            let emptyLaneIndex = nextActiveLanes.FindIndex(fun hash -> not (isOccupied hash))
                            if emptyLaneIndex >= 0 then emptyLaneIndex else nextActiveLanes.Count

                    if lane = nextActiveLanes.Count then
                        nextActiveLanes.Add parentHash
                    else
                        nextActiveLanes.[lane] <- parentHash

                    nextLaneIndexes.[parentHash] <- lane
                    lane

            let commitColor =
                match colors.TryGetValue commit.Hash with
                | true, color -> color
                | false, _ -> newColor ()

            let segments = System.Collections.Generic.List<LaneSegment>(activeLanes.Count + commit.Parents.Length)

            commit.Parents
            |> List.iteri (fun index parentHash ->
                let alreadyPlaced = nextLaneIndexes.ContainsKey parentHash
                let targetIdx = tryPlaceParent parentHash
                // The first parent continues this line; a merge's other parents start their own unless already drawn.
                if not alreadyPlaced && not (colors.ContainsKey parentHash) then
                    colors.[parentHash] <- if index = 0 then commitColor else newColor ()
                // An edge into an existing line takes the colour of the line it leaves (this commit's), like a merge arrow.
                let edgeColor = if index = 0 || not alreadyPlaced then colors.[parentHash] else commitColor
                segments.Add
                    {
                        Lane = currentLane
                        TargetLane = targetIdx
                        IsCommit = true
                        Color = edgeColor
                    })

            for laneIndex in 0 .. activeLanes.Count - 1 do
                if laneIndex <> currentLane then
                    let hash = activeLanes.[laneIndex]
                    if isOccupied hash then
                        let targetIdx = nextLaneIndexes.[hash]
                        segments.Add
                            {
                                Lane = laneIndex
                                TargetLane = targetIdx
                                IsCommit = false
                                Color = match colors.TryGetValue hash with | true, color -> color | false, _ -> laneIndex
                            }

            infos.Add
                {
                    Commit = commit
                    Lane = currentLane
                    Segments = List.ofSeq segments
                    HasIncoming = hasIncoming
                    Color = commitColor
                }

            colors.Remove commit.Hash |> ignore
            activeLanes <- nextActiveLanes
            activeLaneIndexes <- nextLaneIndexes

        List.ofSeq infos
