namespace GitKay.Core

module Models =

    type Commit =
        {
            Hash: string
            AuthorName: string
            AuthorEmail: string
            Timestamp: int64
            Parents: string list
            Subject: string
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
