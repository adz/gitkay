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
        let emptyLane = ""
        let isOccupied hash = not (System.String.IsNullOrEmpty hash)

        let mutable activeLanes = System.Collections.Generic.List<string>()
        let mutable activeLaneIndexes = System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal)
        let infos = System.Collections.Generic.List<CommitGraphInfo>()

        for commit in commits do
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

            let segments = System.Collections.Generic.List<LaneSegment>(activeLanes.Count + commit.Parents.Length)

            for parentHash in commit.Parents do
                let targetIdx = tryPlaceParent parentHash
                segments.Add
                    {
                        Lane = currentLane
                        TargetLane = targetIdx
                        IsCommit = true
                        Color = currentLane
                    }

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
                                Color = laneIndex
                            }

            infos.Add
                {
                    Commit = commit
                    Lane = currentLane
                    Segments = List.ofSeq segments
                }

            activeLanes <- nextActiveLanes
            activeLaneIndexes <- nextLaneIndexes

        List.ofSeq infos
