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
        searchDebounceSeconds: double
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

type AppUiStateDocument
    (
        windowWidth: Nullable<double>,
        windowHeight: Nullable<double>,
        repoSelections: IReadOnlyDictionary<string, string>
    ) =
    member _.WindowWidth = windowWidth
    member _.WindowHeight = windowHeight
    member _.RepoSelections = repoSelections

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
}

type private RepoStateWire = { LastSelectedCommitHash: string option }

type private AppUiStateWire = {
    WindowWidth: double option
    WindowHeight: double option
    RepoStates: Map<string, RepoStateWire>
}

[<RequireQualifiedAccess>]
module private Defaults =
    let settings = {
        ShowBranchRefs = false
        ShowStashes = false
        DiffContextLines = 3
        DiffPresentationModeKey = "diff"
        CommitRowFontFamily = "Helvetica,Arial,Liberation Sans,Noto Sans,sans-serif"
        CommitRowMonoFontFamily = "Courier,Courier New,Liberation Mono,Monospace"
        CommitRowTextFontSize = 13.0
        CommitRowMetaFontSize = 12.0
        CommitRowBadgeFontSize = 11.0
        SearchDebounceSeconds = 0.5
    }

[<RequireQualifiedAccess>]
module private Codecs =
    let settings =
        schema<AppSettingsWire> {
            fieldAs "ShowBranchRefs" _.ShowBranchRefs { defaultValue Defaults.settings.ShowBranchRefs }
            fieldAs "ShowStashes" _.ShowStashes { defaultValue Defaults.settings.ShowStashes }
            fieldAs "DiffContextLines" _.DiffContextLines { defaultValue Defaults.settings.DiffContextLines }
            fieldAs "DiffPresentationModeKey" _.DiffPresentationModeKey { defaultValue Defaults.settings.DiffPresentationModeKey }
            fieldAs "CommitRowFontFamily" _.CommitRowFontFamily { defaultValue Defaults.settings.CommitRowFontFamily }
            fieldAs "CommitRowMonoFontFamily" _.CommitRowMonoFontFamily { defaultValue Defaults.settings.CommitRowMonoFontFamily }
            fieldAs "CommitRowTextFontSize" _.CommitRowTextFontSize { defaultValue Defaults.settings.CommitRowTextFontSize }
            fieldAs "CommitRowMetaFontSize" _.CommitRowMetaFontSize { defaultValue Defaults.settings.CommitRowMetaFontSize }
            fieldAs "CommitRowBadgeFontSize" _.CommitRowBadgeFontSize { defaultValue Defaults.settings.CommitRowBadgeFontSize }
            fieldAs "SearchDebounceSeconds" _.SearchDebounceSeconds { defaultValue Defaults.settings.SearchDebounceSeconds }
            construct (fun showBranchRefs showStashes diffContextLines diffPresentationModeKey commitRowFontFamily commitRowMonoFontFamily commitRowTextFontSize commitRowMetaFontSize commitRowBadgeFontSize searchDebounceSeconds ->
                { ShowBranchRefs = showBranchRefs; ShowStashes = showStashes; DiffContextLines = diffContextLines
                  DiffPresentationModeKey = diffPresentationModeKey; CommitRowFontFamily = commitRowFontFamily
                  CommitRowMonoFontFamily = commitRowMonoFontFamily; CommitRowTextFontSize = commitRowTextFontSize
                  CommitRowMetaFontSize = commitRowMetaFontSize; CommitRowBadgeFontSize = commitRowBadgeFontSize
                  SearchDebounceSeconds = searchDebounceSeconds })
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
            construct (fun width height states -> { WindowWidth = width; WindowHeight = height; RepoStates = states })
        }
        |> Json.compile

[<RequireQualifiedAccess>]
module private Conversion =
    let settingsDocument (wire: AppSettingsWire) =
        AppSettingsDocument(wire.ShowBranchRefs, wire.ShowStashes, wire.DiffContextLines, wire.DiffPresentationModeKey,
            wire.CommitRowFontFamily, wire.CommitRowMonoFontFamily, wire.CommitRowTextFontSize,
            wire.CommitRowMetaFontSize, wire.CommitRowBadgeFontSize, wire.SearchDebounceSeconds)

    let settingsWire (document: AppSettingsDocument) = {
        ShowBranchRefs = document.ShowBranchRefs; ShowStashes = document.ShowStashes
        DiffContextLines = document.DiffContextLines; DiffPresentationModeKey = document.DiffPresentationModeKey
        CommitRowFontFamily = document.CommitRowFontFamily; CommitRowMonoFontFamily = document.CommitRowMonoFontFamily
        CommitRowTextFontSize = document.CommitRowTextFontSize; CommitRowMetaFontSize = document.CommitRowMetaFontSize
        CommitRowBadgeFontSize = document.CommitRowBadgeFontSize; SearchDebounceSeconds = document.SearchDebounceSeconds
    }

    let uiStateDocument (wire: AppUiStateWire) =
        let selections = Dictionary<string, string>(StringComparer.Ordinal)
        for KeyValue(key, state) in wire.RepoStates do
            match state.LastSelectedCommitHash with
            | Some hash -> selections[key] <- hash
            | None -> ()
        AppUiStateDocument(Option.toNullable wire.WindowWidth, Option.toNullable wire.WindowHeight, selections)

    let uiStateWire (windowWidth: Nullable<double>, windowHeight: Nullable<double>, repoSelections: seq<KeyValuePair<string, string>>) = {
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

    static member SerializeUiState(windowWidth: Nullable<double>, windowHeight: Nullable<double>, repoSelections: seq<KeyValuePair<string, string>>) =
        Conversion.uiStateWire(windowWidth, windowHeight, repoSelections) |> Json.serialize Codecs.uiState

    static member DeserializeUiState(json: string) =
        json |> Json.deserialize Codecs.uiState |> Conversion.uiStateDocument
