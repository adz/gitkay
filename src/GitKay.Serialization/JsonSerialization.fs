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

/// Every field is an option (a reference type): NativeAOT cannot create Reified's curried constructor
/// instantiations over value types, which made settings fail to load in AOT builds. Defaults apply on conversion.
type private AppSettingsWire = {
    ShowBranchRefs: bool option
    ShowStashes: bool option
    DiffContextLines: int option
    DiffPresentationModeKey: string option
    CommitRowFontFamily: string option
    CommitRowMonoFontFamily: string option
    CommitRowTextFontSize: double option
    CommitRowMetaFontSize: double option
    CommitRowBadgeFontSize: double option
    SearchDebounceSeconds: double option
    ThemeMode: string option
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
    let settings =
        schema<AppSettingsWire> {
            fieldAs "ShowBranchRefs" _.ShowBranchRefs
            fieldAs "ShowStashes" _.ShowStashes
            fieldAs "DiffContextLines" _.DiffContextLines
            fieldAs "DiffPresentationModeKey" _.DiffPresentationModeKey
            fieldAs "CommitRowFontFamily" _.CommitRowFontFamily
            fieldAs "CommitRowMonoFontFamily" _.CommitRowMonoFontFamily
            fieldAs "CommitRowTextFontSize" _.CommitRowTextFontSize
            fieldAs "CommitRowMetaFontSize" _.CommitRowMetaFontSize
            fieldAs "CommitRowBadgeFontSize" _.CommitRowBadgeFontSize
            fieldAs "SearchDebounceSeconds" _.SearchDebounceSeconds
            fieldAs "ThemeMode" _.ThemeMode
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
            wire.ShowBranchRefs |> Option.defaultValue Defaults.showBranchRefs,
            wire.ShowStashes |> Option.defaultValue Defaults.showStashes,
            wire.DiffContextLines |> Option.defaultValue Defaults.diffContextLines,
            wire.DiffPresentationModeKey |> Option.defaultValue Defaults.diffPresentationModeKey,
            wire.CommitRowFontFamily |> Option.defaultValue Defaults.commitRowFontFamily,
            wire.CommitRowMonoFontFamily |> Option.defaultValue Defaults.commitRowMonoFontFamily,
            wire.CommitRowTextFontSize |> Option.defaultValue Defaults.commitRowTextFontSize,
            wire.CommitRowMetaFontSize |> Option.defaultValue Defaults.commitRowMetaFontSize,
            wire.CommitRowBadgeFontSize |> Option.defaultValue Defaults.commitRowBadgeFontSize,
            wire.SearchDebounceSeconds |> Option.defaultValue Defaults.searchDebounceSeconds,
            wire.ThemeMode |> Option.defaultValue Defaults.themeMode)

    let settingsWire (document: AppSettingsDocument) = {
        ShowBranchRefs = Some document.ShowBranchRefs; ShowStashes = Some document.ShowStashes
        DiffContextLines = Some document.DiffContextLines; DiffPresentationModeKey = Option.ofObj document.DiffPresentationModeKey
        CommitRowFontFamily = Option.ofObj document.CommitRowFontFamily; CommitRowMonoFontFamily = Option.ofObj document.CommitRowMonoFontFamily
        CommitRowTextFontSize = Some document.CommitRowTextFontSize; CommitRowMetaFontSize = Some document.CommitRowMetaFontSize
        CommitRowBadgeFontSize = Some document.CommitRowBadgeFontSize; SearchDebounceSeconds = Some document.SearchDebounceSeconds
        ThemeMode = Option.ofObj document.ThemeMode
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

    /// Reads settings with JsonDocument rather than the Reified codec: the codec's ten-argument curried
    /// constructor needs a closure instantiation that NativeAOT does not generate, so settings failed to load in AOT builds.
    static member DeserializeSettings(json: string) =
        use document = System.Text.Json.JsonDocument.Parse(json)
        let root = document.RootElement

        let tryProperty (name: string) =
            match root.TryGetProperty name with
            | true, value -> Some value
            | _ -> None

        let bool name = tryProperty name |> Option.bind (fun v -> match v.ValueKind with System.Text.Json.JsonValueKind.True -> Some true | System.Text.Json.JsonValueKind.False -> Some false | _ -> None)
        let int name = tryProperty name |> Option.bind (fun v -> match v.TryGetInt32() with | true, value -> Some value | _ -> None)
        let double name = tryProperty name |> Option.bind (fun v -> match v.TryGetDouble() with | true, value -> Some value | _ -> None)
        let string name = tryProperty name |> Option.bind (fun v -> if v.ValueKind = System.Text.Json.JsonValueKind.String then Option.ofObj (v.GetString()) else None)

        Conversion.settingsDocument
            {
                ShowBranchRefs = bool "ShowBranchRefs"
                ShowStashes = bool "ShowStashes"
                DiffContextLines = int "DiffContextLines"
                DiffPresentationModeKey = string "DiffPresentationModeKey"
                CommitRowFontFamily = string "CommitRowFontFamily"
                CommitRowMonoFontFamily = string "CommitRowMonoFontFamily"
                CommitRowTextFontSize = double "CommitRowTextFontSize"
                CommitRowMetaFontSize = double "CommitRowMetaFontSize"
                CommitRowBadgeFontSize = double "CommitRowBadgeFontSize"
                SearchDebounceSeconds = double "SearchDebounceSeconds"
                ThemeMode = string "ThemeMode"
            }

    static member SerializeUiState(windowWidth: Nullable<double>, windowHeight: Nullable<double>, repoSelections: seq<KeyValuePair<string, string>>, layout: UiLayoutDocument) =
        Conversion.uiStateWire(windowWidth, windowHeight, repoSelections, layout) |> Json.serialize Codecs.uiState

    static member SerializeUiState(windowWidth: Nullable<double>, windowHeight: Nullable<double>, repoSelections: seq<KeyValuePair<string, string>>) =
        GitKayJson.SerializeUiState(windowWidth, windowHeight, repoSelections, null)

    static member DeserializeUiState(json: string) =
        json |> Json.deserialize Codecs.uiState |> Conversion.uiStateDocument
