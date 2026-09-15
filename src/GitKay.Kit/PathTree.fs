namespace GitKay.Kit

open System

/// <summary>A row of a tree built from slash-separated paths.</summary>
type PathTreeRow<'item> =
    /// <summary>A folder; a chain of folders holding only one folder shows as one ("dev-docs/releases").</summary>
    | FolderRow of name: string * path: string * depth: int * expanded: bool * items: 'item list
    /// <summary>A file, with its item when it has one.</summary>
    | FileRow of name: string * path: string * depth: int * item: 'item option

module PathTree =
    type private Node<'item> =
        { Folders: Collections.Generic.SortedDictionary<string, Node<'item>>
          Files: ResizeArray<struct (string * string * 'item option)>
          mutable HasItem: bool }

    let private node () =
        { Folders = Collections.Generic.SortedDictionary(StringComparer.OrdinalIgnoreCase)
          Files = ResizeArray()
          HasItem = false }

    /// <summary>
    /// Depth-first rows for <paramref name="entries"/>: folders first (by name, ignoring case), then files. A folder's
    /// contents follow only when <paramref name="isExpanded"/> says so, given its path and whether any entry beneath it
    /// has an item; its row lists every item beneath it either way.
    /// </summary>
    let rows (isExpanded: string -> bool -> bool) (entries: (string * 'item option) seq) : PathTreeRow<'item> list =
        let root = node ()
        for path, item in entries do
            let parts = path.Split '/'
            let mutable current = root
            if item.IsSome then current.HasItem <- true
            for folder in parts[.. parts.Length - 2] do
                match current.Folders.TryGetValue folder with
                | true, child -> current <- child
                | _ ->
                    let child = node ()
                    current.Folders[folder] <- child
                    current <- child
                if item.IsSome then current.HasItem <- true
            current.Files.Add(struct (parts[parts.Length - 1], path, item))

        let rec itemsUnder (current: Node<'item>) =
            [ for struct (_, _, item) in current.Files do
                  yield! Option.toList item
              for child in current.Folders.Values do
                  yield! itemsUnder child ]

        let rec emit (current: Node<'item>) (prefix: string) (depth: int) =
            [ for entry in current.Folders do
                  let mutable name = entry.Key
                  let mutable folder = entry.Value
                  while folder.Files.Count = 0 && folder.Folders.Count = 1 do
                      let only = Seq.head folder.Folders
                      name <- $"{name}/{only.Key}"
                      folder <- only.Value
                  let path = if prefix.Length = 0 then name else $"{prefix}/{name}"
                  let expanded = isExpanded path folder.HasItem
                  yield FolderRow(name, path, depth, expanded, itemsUnder folder)
                  if expanded then yield! emit folder path (depth + 1)
              for struct (name, path, item) in current.Files |> Seq.sortWith (fun (struct (a, _, _)) (struct (b, _, _)) -> StringComparer.OrdinalIgnoreCase.Compare(a, b)) do
                  yield FileRow(name, path, depth, item) ]

        emit root "" 0
