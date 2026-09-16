namespace GitKay.Core

open System
open Elmish
open Axial
open Axial.Elmish

/// <summary>
/// The commit window (git gui style): unstaged and staged files, staging whole files, hunks or lines, and committing.
/// The main window stays read-only; see dev-docs/commit-window-plan.md.
/// </summary>
module CommitWindow =

    let private operationJob = AxialLatestSlot(App.runtime)
    let private scanJob = AxialLatestSlot(App.runtime)

    /// <summary>The two file lists: working tree changes (with untracked files) and the index.</summary>
    type ListKind =
        | UnstagedList
        | StagedList

    type Model =
        { GitEnv: GitService.GitEnv
          Changes: GitService.WorkingTreeChanges option
          /// <summary>The checked-out branch (or a detached HEAD), shown like git gui's "Current Branch".</summary>
          Branch: string
          /// <summary>The list and current path of the file whose diff is shown.</summary>
          Selected: (ListKind * string) option
          Message: string
          Amend: bool
          /// <summary>The message loaded for amending, so turning amend off can put the draft back.</summary>
          AmendMessage: string option
          SignOff: bool
          /// <summary>What is running (a scan, staging, the commit), or None when idle.</summary>
          Busy: string option
          Status: string
          /// <summary>The last failure's full output (hooks, git apply), shown until the next operation.</summary>
          FailureOutput: string option
          /// <summary>Increments on each successful commit, so the main window knows to reread refs.</summary>
          Commits: int }

    type Msg =
        | Rescan
        | ChangesLoaded of Result<GitService.WorkingTreeChanges, GitError>
        | BranchLoaded of string
        | Select of ListKind * path: string
        | StagePaths of string list
        | UnstagePaths of string list
        | ApplyLines of GitService.PatchTarget * path: string * PatchBuilder.SelectedLine list
        | DiscardPaths of tracked: string list * untracked: string list
        | SetMessage of string
        | SetAmend of bool
        | AmendMessageLoaded of string
        | SetSignOff of bool
        | Commit
        | OperationSucceeded of description: string * committed: bool
        | OperationFailed of description: string * GitError

    /// <summary>A file's current path: the new path, or the old one for a deletion.</summary>
    let pathOf (file: Models.FileDiff) = FileChange.currentPath file.OldPath file.NewPath

    /// <summary>The files in a list, in path order; untracked files follow the unstaged ones.</summary>
    let filesIn (list: ListKind) (changes: GitService.WorkingTreeChanges) =
        match list with
        | UnstagedList -> changes.Unstaged @ changes.Untracked
        | StagedList -> changes.Staged

    let isUntracked (changes: GitService.WorkingTreeChanges) (path: string) =
        changes.Untracked |> List.exists (fun file -> pathOf file = path)

    /// <summary>The shown file's diff, if it is still in its list.</summary>
    let selectedFile (model: Model) =
        match model.Changes, model.Selected with
        | Some changes, Some(list, path) -> filesIn list changes |> List.tryFind (fun file -> pathOf file = path) |> Option.map (fun file -> list, file)
        | _ -> None

    let hasStaged (model: Model) =
        model.Changes |> Option.exists (fun changes -> not changes.Staged.IsEmpty)

    /// <summary>The first line, trimmed; commits need one.</summary>
    let subject (message: string) =
        message.Replace("\r\n", "\n").Split('\n') |> Array.tryHead |> Option.map _.Trim() |> Option.defaultValue ""

    /// <summary>Why committing isn't possible right now, or None when it is.</summary>
    let commitBlocker (model: Model) =
        if model.Busy.IsSome then Some "Wait for the current operation to finish"
        elif String.IsNullOrWhiteSpace(subject model.Message) then Some "Write a commit message"
        elif not model.Amend && not (hasStaged model) then Some "Stage changes to commit"
        else None

    /// <summary>
    /// The message as committed: trailing whitespace trimmed per line, and a blank line between the subject and a body
    /// that starts straight after it.
    /// </summary>
    let normalizeMessage (message: string) =
        let lines = message.Replace("\r\n", "\n").Split('\n') |> Array.map _.TrimEnd() |> List.ofArray
        let lines =
            match lines with
            | first :: second :: rest when second <> "" -> first :: "" :: second :: rest
            | _ -> lines
        (String.Join("\n", lines)).Trim('\n') + "\n"

    /// <summary>
    /// After a rescan the shown file stays when it's still in its list; otherwise the file now at its position, the
    /// last file of that list, or the first file of the other list.
    /// </summary>
    let reselect (previous: GitService.WorkingTreeChanges option) (selected: (ListKind * string) option) (changes: GitService.WorkingTreeChanges) =
        let first list = filesIn list changes |> List.tryHead |> Option.map (fun file -> list, pathOf file)
        match selected with
        | Some(list, path) when filesIn list changes |> List.exists (fun file -> pathOf file = path) -> selected
        | Some(list, path) ->
            let files = filesIn list changes
            let index =
                previous
                |> Option.bind (fun previous -> filesIn list previous |> List.tryFindIndex (fun file -> pathOf file = path))
                |> Option.defaultValue 0
            match List.tryItem (min index (files.Length - 1)) files with
            | Some file when not files.IsEmpty -> Some(list, pathOf file)
            | _ -> first (match list with UnstagedList -> StagedList | StagedList -> UnstagedList)
        | None -> first UnstagedList |> Option.orElse (first StagedList)

    let init (env: GitService.GitEnv) (draft: string) : Model * Cmd<Msg> =
        { GitEnv = env
          Changes = None
          Branch = ""
          Selected = None
          Message = draft
          Amend = false
          AmendMessage = None
          SignOff = false
          Busy = Some "Scanning"
          Status = "Scanning for changes…"
          FailureOutput = None
          Commits = 0 },
        Cmd.ofMsg Rescan

    let private scan (model: Model) =
        Cmd.batch
            [ Cmd.OfFlow.ofFlowLatest "commit window scan" scanJob model.GitEnv (GitService.fetchWorkingTreeChangesFor model.Amend 3) (Ok >> ChangesLoaded) (Error >> ChangesLoaded)
              Cmd.OfFlow.ofFlow "current branch" App.runtime model.GitEnv GitService.fetchCurrentBranch BranchLoaded (fun _ -> BranchLoaded "") ]

    let private operation (model: Model) (description: string) (committed: bool) (work: Flow<GitService.GitEnv, GitError, unit>) =
        { model with Busy = Some description; Status = description + "…"; FailureOutput = None },
        Cmd.OfFlow.ofFlowLatest description operationJob model.GitEnv work (fun () -> OperationSucceeded(description, committed)) (fun error -> OperationFailed(description, error))

    let private plural (count: int) (noun: string) = if count = 1 then "1 " + noun else $"{count} {noun}s"
    let private fileCount (count: int) = plural count "file"
    let private lineCount (count: int) = plural count "line"

    let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
        match msg with
        | Rescan -> { model with Busy = model.Busy |> Option.orElse (Some "Scanning") }, scan model
        | ChangesLoaded(Ok changes) ->
            let status =
                match changes.Unstaged.Length + changes.Untracked.Length, changes.Staged.Length with
                | 0, 0 -> "No changes"
                | unstaged, staged -> plural unstaged "unstaged file" + " · " + plural staged "staged file"
            { model with
                Changes = Some changes
                Selected = reselect model.Changes model.Selected changes
                Busy = (if model.Busy = Some "Scanning" then None else model.Busy)
                Status = (if model.FailureOutput.IsSome then model.Status else status) },
            Cmd.none
        | ChangesLoaded(Error error) ->
            { model with Busy = None; Status = "Scan failed: " + GitError.describe error }, Cmd.none
        | BranchLoaded branch -> { model with Branch = branch }, Cmd.none
        | Select(list, path) -> { model with Selected = Some(list, path) }, Cmd.none
        | StagePaths [] | UnstagePaths [] -> model, Cmd.none
        | StagePaths paths -> operation model $"Staging {fileCount paths.Length}" false (GitService.stageFiles paths)
        | UnstagePaths paths -> operation model $"Unstaging {fileCount paths.Length}" false (GitService.unstageFilesFor model.Amend paths)
        | ApplyLines(_, _, []) -> model, Cmd.none
        | ApplyLines(target, path, lines) ->
            let verb =
                match target with
                | GitService.StageInIndex -> "Staging"
                | GitService.UnstageFromIndex -> "Unstaging"
                | GitService.DiscardFromWorkingTree -> "Discarding"
            operation model $"{verb} {lineCount lines.Length} of {path}" false (GitService.applyLines target path lines)
        | DiscardPaths([], []) -> model, Cmd.none
        | DiscardPaths(tracked, untracked) ->
            operation model $"Discarding {fileCount (tracked.Length + untracked.Length)}" false (GitService.discardFiles tracked untracked)
        | SetMessage message -> { model with Message = message }, Cmd.none
        | SetAmend true when not model.Amend ->
            // Amending shows what the commit will contain, so the staged section is rescanned against its parent.
            let model = { model with Amend = true }
            model,
            Cmd.batch
                [ Cmd.OfFlow.ofFlow "last commit message" App.runtime model.GitEnv GitService.fetchLastCommitMessage AmendMessageLoaded (fun _ -> AmendMessageLoaded "")
                  scan model ]
        | SetAmend false when model.Amend ->
            // Put back an empty draft if the message is still the one loaded for amending.
            let message = if model.AmendMessage = Some model.Message then "" else model.Message
            let model = { model with Amend = false; AmendMessage = None; Message = message }
            model, scan model
        | SetAmend _ -> model, Cmd.none
        | AmendMessageLoaded loaded when model.Amend ->
            let loaded = loaded.TrimEnd() + "\n"
            if String.IsNullOrWhiteSpace model.Message then { model with Message = loaded; AmendMessage = Some loaded }, Cmd.none
            else model, Cmd.none
        | AmendMessageLoaded _ -> model, Cmd.none
        | SetSignOff signOff -> { model with SignOff = signOff }, Cmd.none
        | Commit ->
            match commitBlocker model with
            | Some reason -> { model with Status = reason }, Cmd.none
            | None ->
                let options: GitService.CommitOptions = { Amend = model.Amend; SignOff = model.SignOff }
                operation model (if model.Amend then "Amending the last commit" else "Committing") true (GitService.commit options (normalizeMessage model.Message))
        | OperationSucceeded(description, committed) ->
            let model = { model with Busy = None }
            if committed then
                { model with
                    Message = ""
                    Amend = false
                    AmendMessage = None
                    Status = $"{description}: done"
                    Commits = model.Commits + 1 },
                scan model
            else
                { model with Status = description + ": done" }, scan model
        | OperationFailed(description, error) ->
            { model with Busy = None; Status = $"{description} failed"; FailureOutput = Some(GitError.describe error) }, scan model

    let program (env: GitService.GitEnv) (draft: string) =
        Program.mkProgram (fun () -> init env draft) update (fun _ _ -> ())
