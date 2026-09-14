namespace GitKay.Serialization

open System
open System.Collections.Generic
open Reified
open Reified.SchemaDSL

type AppSettingsDocument
    (
        showBranchRefs: bool,
        showStashes: bool,
        diffContextLines: int,
        diffPresentationModeKey: string,
        commitRowFontFamily: string,
        commitRowMonoFontFamily: string,
        commitRowTextFontSize: double,
        commitRowMetaFontSize: double,
        commitRowBadgeFontSize: double,
        searchDebounceSeconds: double,
        themeMode: string
    ) =
    member _.ShowBranchRefs = showBranchRefs
    member _.ShowStashes = showStashes
    member _.DiffContextLines = diffContextLines
    member _.DiffPresentationModeKey = diffPresentationModeKey
    member _.CommitRowFontFamily = commitRowFontFamily
    member _.CommitRowMonoFontFamily = commitRowMonoFontFamily
    member _.CommitRowTextFontSize = commitRowTextFontSize
    member _.CommitRowMetaFontSize = commitRowMetaFontSize
    member _.CommitRowBadgeFontSize = commitRowBadgeFontSize
    member _.SearchDebounceSeconds = searchDebounceSeconds
    member _.ThemeMode = themeMode

/// Persisted splitter positions and history column widths; each value is absent until the user changes it.
[<AllowNullLiteral>]
type UiLayoutDocument
    (
        historyPaneRatio: Nullable<double>,
        fileListWidth: Nullable<double>,
        graphColumnWidth: Nullable<double>,
        hashColumnWidth: Nullable<double>,
        authorColumnWidth: Nullable<double>,
        dateColumnWidth: Nullable<double>
    ) =
    member _.HistoryPaneRatio = historyPaneRatio
    member _.FileListWidth = fileListWidth
    member _.GraphColumnWidth = graphColumnWidth
    member _.HashColumnWidth = hashColumnWidth
    member _.AuthorColumnWidth = authorColumnWidth
    member _.DateColumnWidth = dateColumnWidth

type AppUiStateDocument
    (
        windowWidth: Nullable<double>,
        windowHeight: Nullable<double>,
        repoSelections: IReadOnlyDictionary<string, string>,
        layout: UiLayoutDocument
    ) =
    member _.WindowWidth = windowWidth
    member _.WindowHeight = windowHeight
    member _.RepoSelections = repoSelections
    member _.Layout = layout

type private AppSettingsWire = {
    ShowBranchRefs: bool
    ShowStashes: bool
    DiffContextLines: int
    DiffPresentationModeKey: string
    CommitRowFontFamily: string
    CommitRowMonoFontFamily: string
    CommitRowTextFontSize: double
    CommitRowMetaFontSize: double
    CommitRowBadgeFontSize: double
    SearchDebounceSeconds: double
    ThemeMode: string
}

type private RepoStateWire = { LastSelectedCommitHash: string option }

type private AppUiStateWire = {
    WindowWidth: double option
    WindowHeight: double option
    RepoStates: Map<string, RepoStateWire>
    HistoryPaneRatio: double option
    FileListWidth: double option
    GraphColumnWidth: double option
    HashColumnWidth: double option
    AuthorColumnWidth: double option
    DateColumnWidth: double option
}

[<RequireQualifiedAccess>]
module private Defaults =
    let showBranchRefs = false
    let showStashes = false
    let diffContextLines = 3
    let diffPresentationModeKey = "diff"
    let commitRowFontFamily = "Helvetica,Arial,Liberation Sans,Noto Sans,sans-serif"
    let commitRowMonoFontFamily = "Courier,Courier New,Liberation Mono,Monospace"
    let commitRowTextFontSize = 13.0
    let commitRowMetaFontSize = 12.0
    let commitRowBadgeFontSize = 11.0
    let searchDebounceSeconds = 0.5
    let themeMode = "system"

[<RequireQualifiedAccess>]
module private Codecs =
    // A field missing from the file (an older version wrote it, or never did) takes its default.
    let settings =
        schema<AppSettingsWire> {
            fieldAs "ShowBranchRefs" _.ShowBranchRefs { defaultValue Defaults.showBranchRefs }
            fieldAs "ShowStashes" _.ShowStashes { defaultValue Defaults.showStashes }
            fieldAs "DiffContextLines" _.DiffContextLines { defaultValue Defaults.diffContextLines }
            fieldAs "DiffPresentationModeKey" _.DiffPresentationModeKey { defaultValue Defaults.diffPresentationModeKey }
            fieldAs "CommitRowFontFamily" _.CommitRowFontFamily { defaultValue Defaults.commitRowFontFamily }
            fieldAs "CommitRowMonoFontFamily" _.CommitRowMonoFontFamily { defaultValue Defaults.commitRowMonoFontFamily }
            fieldAs "CommitRowTextFontSize" _.CommitRowTextFontSize { defaultValue Defaults.commitRowTextFontSize }
            fieldAs "CommitRowMetaFontSize" _.CommitRowMetaFontSize { defaultValue Defaults.commitRowMetaFontSize }
            fieldAs "CommitRowBadgeFontSize" _.CommitRowBadgeFontSize { defaultValue Defaults.commitRowBadgeFontSize }
            fieldAs "SearchDebounceSeconds" _.SearchDebounceSeconds { defaultValue Defaults.searchDebounceSeconds }
            fieldAs "ThemeMode" _.ThemeMode { defaultValue Defaults.themeMode }
            construct (fun showBranchRefs showStashes diffContextLines diffPresentationModeKey commitRowFontFamily commitRowMonoFontFamily commitRowTextFontSize commitRowMetaFontSize commitRowBadgeFontSize searchDebounceSeconds themeMode ->
                { ShowBranchRefs = showBranchRefs; ShowStashes = showStashes; DiffContextLines = diffContextLines
                  DiffPresentationModeKey = diffPresentationModeKey; CommitRowFontFamily = commitRowFontFamily
                  CommitRowMonoFontFamily = commitRowMonoFontFamily; CommitRowTextFontSize = commitRowTextFontSize
                  CommitRowMetaFontSize = commitRowMetaFontSize; CommitRowBadgeFontSize = commitRowBadgeFontSize
                  SearchDebounceSeconds = searchDebounceSeconds; ThemeMode = themeMode })
        }
        |> Json.compile

    let private repoState =
        schema<RepoStateWire> {
            fieldAs "LastSelectedCommitHash" _.LastSelectedCommitHash
            construct (fun hash -> { LastSelectedCommitHash = hash })
        }

    let uiState =
        schema<AppUiStateWire> {
            fieldAs "WindowWidth" _.WindowWidth
            fieldAs "WindowHeight" _.WindowHeight
            fieldAs "RepoStates" _.RepoStates {
                withSchema (Schema.mapWith repoState |> Schema.withDefault Map.empty)
            }
            fieldAs "HistoryPaneRatio" _.HistoryPaneRatio
            fieldAs "FileListWidth" _.FileListWidth
            fieldAs "GraphColumnWidth" _.GraphColumnWidth
            fieldAs "HashColumnWidth" _.HashColumnWidth
            fieldAs "AuthorColumnWidth" _.AuthorColumnWidth
            fieldAs "DateColumnWidth" _.DateColumnWidth
            construct (fun width height states ratio fileList graph hash author date ->
                { WindowWidth = width; WindowHeight = height; RepoStates = states
                  HistoryPaneRatio = ratio; FileListWidth = fileList
                  GraphColumnWidth = graph; HashColumnWidth = hash; AuthorColumnWidth = author; DateColumnWidth = date })
        }
        |> Json.compile

[<RequireQualifiedAccess>]
module private Conversion =
    let settingsDocument (wire: AppSettingsWire) =
        AppSettingsDocument(
            wire.ShowBranchRefs, wire.ShowStashes, wire.DiffContextLines, wire.DiffPresentationModeKey,
            wire.CommitRowFontFamily, wire.CommitRowMonoFontFamily, wire.CommitRowTextFontSize,
            wire.CommitRowMetaFontSize, wire.CommitRowBadgeFontSize, wire.SearchDebounceSeconds, wire.ThemeMode)

    let private textOr fallback (value: string) = if isNull value then fallback else value

    let settingsWire (document: AppSettingsDocument) = {
        ShowBranchRefs = document.ShowBranchRefs; ShowStashes = document.ShowStashes
        DiffContextLines = document.DiffContextLines
        DiffPresentationModeKey = document.DiffPresentationModeKey |> textOr Defaults.diffPresentationModeKey
        CommitRowFontFamily = document.CommitRowFontFamily |> textOr Defaults.commitRowFontFamily
        CommitRowMonoFontFamily = document.CommitRowMonoFontFamily |> textOr Defaults.commitRowMonoFontFamily
        CommitRowTextFontSize = document.CommitRowTextFontSize; CommitRowMetaFontSize = document.CommitRowMetaFontSize
        CommitRowBadgeFontSize = document.CommitRowBadgeFontSize; SearchDebounceSeconds = document.SearchDebounceSeconds
        ThemeMode = document.ThemeMode |> textOr Defaults.themeMode
    }

    let uiStateDocument (wire: AppUiStateWire) =
        let selections = Dictionary<string, string>(StringComparer.Ordinal)
        for KeyValue(key, state) in wire.RepoStates do
            match state.LastSelectedCommitHash with
            | Some hash -> selections[key] <- hash
            | None -> ()
        let layout =
            UiLayoutDocument(Option.toNullable wire.HistoryPaneRatio, Option.toNullable wire.FileListWidth,
                Option.toNullable wire.GraphColumnWidth, Option.toNullable wire.HashColumnWidth,
                Option.toNullable wire.AuthorColumnWidth, Option.toNullable wire.DateColumnWidth)
        AppUiStateDocument(Option.toNullable wire.WindowWidth, Option.toNullable wire.WindowHeight, selections, layout)

    let uiStateWire (windowWidth: Nullable<double>, windowHeight: Nullable<double>, repoSelections: seq<KeyValuePair<string, string>>, layout: UiLayoutDocument) = {
        HistoryPaneRatio = if isNull layout then None else Option.ofNullable layout.HistoryPaneRatio
        FileListWidth = if isNull layout then None else Option.ofNullable layout.FileListWidth
        GraphColumnWidth = if isNull layout then None else Option.ofNullable layout.GraphColumnWidth
        HashColumnWidth = if isNull layout then None else Option.ofNullable layout.HashColumnWidth
        AuthorColumnWidth = if isNull layout then None else Option.ofNullable layout.AuthorColumnWidth
        DateColumnWidth = if isNull layout then None else Option.ofNullable layout.DateColumnWidth
        WindowWidth = Option.ofNullable windowWidth
        WindowHeight = Option.ofNullable windowHeight
        RepoStates =
            repoSelections
            |> Seq.choose (fun entry ->
                if String.IsNullOrWhiteSpace(entry.Key) || isNull entry.Value then None
                else Some(entry.Key, { LastSelectedCommitHash = Some entry.Value }))
            |> Map.ofSeq
    }

[<AbstractClass; Sealed>]
type GitKayJson =
    static member SerializeSettings(document: AppSettingsDocument) =
        document |> Conversion.settingsWire |> Json.serialize Codecs.settings

    static member DeserializeSettings(json: string) =
        json |> Json.deserialize Codecs.settings |> Conversion.settingsDocument

    static member SerializeUiState(windowWidth: Nullable<double>, windowHeight: Nullable<double>, repoSelections: seq<KeyValuePair<string, string>>, layout: UiLayoutDocument) =
        Conversion.uiStateWire(windowWidth, windowHeight, repoSelections, layout) |> Json.serialize Codecs.uiState

    static member SerializeUiState(windowWidth: Nullable<double>, windowHeight: Nullable<double>, repoSelections: seq<KeyValuePair<string, string>>) =
        GitKayJson.SerializeUiState(windowWidth, windowHeight, repoSelections, null)

    static member DeserializeUiState(json: string) =
        json |> Json.deserialize Codecs.uiState |> Conversion.uiStateDocument
