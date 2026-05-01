namespace GitKay.Core

module Models =

    type CommitRefKind =
        | Branch = 0
        | Remote = 1
        | Tag = 2
        | Stash = 3

    type CommitRef =
        {
            Name: string
            Kind: CommitRefKind
            IsCurrentHead: bool
        }

    type Commit =
        {
            Hash: string
            AuthorName: string
            AuthorEmail: string
            Timestamp: int64
            Parents: string list
            Subject: string
            Message: string
            Refs: CommitRef list
        }

    type BlameInfo =
        {
            Hash: string
            AuthorName: string
            AuthorEmail: string
            Timestamp: int64
        }

    type LineType =
        | Added
        | Removed
        | Context
        | Header

    type DiffLine =
        {
            Type: LineType
            Content: string
            OldLineNo: int option
            NewLineNo: int option
        }

    type DiffHunk =
        {
            Header: string
            Lines: DiffLine list
        }

    type FileDiff =
        {
            OldPath: string
            NewPath: string
            Hunks: DiffHunk list
        }
