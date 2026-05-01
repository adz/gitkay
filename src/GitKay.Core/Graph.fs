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
        let mutable activeLanes = System.Collections.Generic.List<string>()
        let mutable activeLaneIndexes = System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal)
        let infos = System.Collections.Generic.List<CommitGraphInfo>()

        for commit in commits do
            let currentLane =
                match activeLaneIndexes.TryGetValue commit.Hash with
                | true, lane -> lane
                | false, _ ->
                    let lane = activeLanes.Count
                    activeLanes.Add commit.Hash
                    activeLaneIndexes.[commit.Hash] <- lane
                    lane

            let occupiedHashes =
                System.Collections.Generic.HashSet<string>(activeLanes, System.StringComparer.Ordinal)

            let nextActiveLanes =
                System.Collections.Generic.List<string>(activeLanes.Count + commit.Parents.Length)

            // Keep neighboring lanes anchored while the current lane is replaced by any new
            // parents. This preserves continuity through merges/forks instead of compacting all
            // lanes toward the left on every step.
            for laneIndex in 0 .. currentLane - 1 do
                nextActiveLanes.Add activeLanes.[laneIndex]

            for parentHash in commit.Parents do
                if occupiedHashes.Add parentHash then
                    nextActiveLanes.Add parentHash

            for laneIndex in currentLane + 1 .. activeLanes.Count - 1 do
                nextActiveLanes.Add activeLanes.[laneIndex]

            let nextLaneIndexes =
                System.Collections.Generic.Dictionary<string, int>(
                    nextActiveLanes.Count,
                    System.StringComparer.Ordinal
                )

            for laneIndex in 0 .. nextActiveLanes.Count - 1 do
                let hash = nextActiveLanes.[laneIndex]
                nextLaneIndexes.[hash] <- laneIndex

            let segments = System.Collections.Generic.List<LaneSegment>(activeLanes.Count + commit.Parents.Length)

            for laneIndex in 0 .. activeLanes.Count - 1 do
                if laneIndex = currentLane then
                    for parentHash in commit.Parents do
                        let targetIdx = nextLaneIndexes.[parentHash]
                        segments.Add
                            {
                                Lane = laneIndex
                                TargetLane = targetIdx
                                IsCommit = true
                                Color = laneIndex
                            }
                else
                    let hash = activeLanes.[laneIndex]
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
