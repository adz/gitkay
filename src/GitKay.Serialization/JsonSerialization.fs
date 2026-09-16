namespace GitKay.Serialization

open GitKay.Core
open Reified
open Reified.SchemaDSL

// The on-disk JSON: property names and value spellings are the file format, so they stay fixed while the Core types
// change. Settings missing from a file (an older version wrote it, or never did) take their defaults.

type private SettingsWire =
    { ShowBranchRefs: bool
      ShowStashes: bool
      DiffContextLines: int
      DiffPresentationModeKey: string
      CommitRowFontFamily: string
      CommitRowMonoFontFamily: string
      CommitRowTextFontSize: float
      CommitRowMetaFontSize: float
      CommitRowBadgeFontSize: float
      SearchDebounceSeconds: float
      ThemeMode: string
      PaneGap: float
      PaneHoverEffectKey: string
      PaneHoverColorKey: string
      PaneHoverIntensityKey: string
      PaneBorder: bool }

type private RepoStateWire =
    { LastSelectedCommitHash: string option
      CommitDraft: string option }

type private UiStateWire =
    { WindowWidth: float option
      WindowHeight: float option
      RepoStates: Map<string, RepoStateWire>
      HistoryPaneRatio: float option
      FileListWidth: float option
      GraphColumnWidth: float option
      HashColumnWidth: float option
      AuthorColumnWidth: float option
      DateColumnWidth: float option }

[<RequireQualifiedAccess>]
module private Codecs =
    let private d = Settings.defaults

    let settings =
        schema<SettingsWire> {
            fieldAs "ShowBranchRefs" _.ShowBranchRefs { defaultValue d.ShowBranchRefs }
            fieldAs "ShowStashes" _.ShowStashes { defaultValue d.ShowStashes }
            fieldAs "DiffContextLines" _.DiffContextLines { defaultValue d.DiffContextLines }
            fieldAs "DiffPresentationModeKey" _.DiffPresentationModeKey { defaultValue (DiffLayout.key d.DiffLayout) }
            fieldAs "CommitRowFontFamily" _.CommitRowFontFamily { defaultValue d.CommitRowFontFamily }
            fieldAs "CommitRowMonoFontFamily" _.CommitRowMonoFontFamily { defaultValue d.CommitRowMonoFontFamily }
            fieldAs "CommitRowTextFontSize" _.CommitRowTextFontSize { defaultValue d.CommitRowTextFontSize }
            fieldAs "CommitRowMetaFontSize" _.CommitRowMetaFontSize { defaultValue d.CommitRowMetaFontSize }
            fieldAs "CommitRowBadgeFontSize" _.CommitRowBadgeFontSize { defaultValue d.CommitRowBadgeFontSize }
            fieldAs "SearchDebounceSeconds" _.SearchDebounceSeconds { defaultValue d.SearchDebounceSeconds }
            fieldAs "ThemeMode" _.ThemeMode { defaultValue (ThemeMode.key d.Theme) }
            fieldAs "PaneGap" _.PaneGap { defaultValue d.PaneGap }
            fieldAs "PaneHoverEffect" _.PaneHoverEffectKey { defaultValue (PaneHoverEffect.key d.PaneHoverEffect) }
            fieldAs "PaneHoverColor" _.PaneHoverColorKey { defaultValue (PaneHoverColor.key d.PaneHoverColor) }
            fieldAs "PaneHoverIntensity" _.PaneHoverIntensityKey { defaultValue (PaneHoverIntensity.key d.PaneHoverIntensity) }
            fieldAs "PaneBorder" _.PaneBorder { defaultValue d.PaneBorder }
            construct (fun showBranchRefs showStashes diffContextLines layout fontFamily monoFontFamily textSize metaSize badgeSize debounce theme paneGap paneHover paneColor paneIntensity paneBorder ->
                { ShowBranchRefs = showBranchRefs; ShowStashes = showStashes; DiffContextLines = diffContextLines
                  DiffPresentationModeKey = layout; CommitRowFontFamily = fontFamily; CommitRowMonoFontFamily = monoFontFamily
                  CommitRowTextFontSize = textSize; CommitRowMetaFontSize = metaSize; CommitRowBadgeFontSize = badgeSize
                  SearchDebounceSeconds = debounce; ThemeMode = theme; PaneGap = paneGap; PaneHoverEffectKey = paneHover
                  PaneHoverColorKey = paneColor; PaneHoverIntensityKey = paneIntensity; PaneBorder = paneBorder })
        }
        |> Json.compile

    let private repoState =
        schema<RepoStateWire> {
            fieldAs "LastSelectedCommitHash" _.LastSelectedCommitHash
            fieldAs "CommitDraft" _.CommitDraft
            construct (fun hash draft -> { LastSelectedCommitHash = hash; CommitDraft = draft })
        }

    let uiState =
        schema<UiStateWire> {
            fieldAs "WindowWidth" _.WindowWidth
            fieldAs "WindowHeight" _.WindowHeight
            fieldAs "RepoStates" _.RepoStates { withSchema (Schema.mapWith repoState |> Schema.withDefault Map.empty) }
            fieldAs "HistoryPaneRatio" _.HistoryPaneRatio
            fieldAs "FileListWidth" _.FileListWidth
            fieldAs "GraphColumnWidth" _.GraphColumnWidth
            fieldAs "HashColumnWidth" _.HashColumnWidth
            fieldAs "AuthorColumnWidth" _.AuthorColumnWidth
            fieldAs "DateColumnWidth" _.DateColumnWidth
            construct (fun width height states ratio fileList graph hash author date ->
                { WindowWidth = width; WindowHeight = height; RepoStates = states; HistoryPaneRatio = ratio
                  FileListWidth = fileList; GraphColumnWidth = graph; HashColumnWidth = hash; AuthorColumnWidth = author
                  DateColumnWidth = date })
        }
        |> Json.compile

/// <summary>Settings as the settings file stores them.</summary>
module SettingsJson =
    let encode (settings: Settings) =
        let settings = Settings.normalize settings
        Json.serialize
            Codecs.settings
            { ShowBranchRefs = settings.ShowBranchRefs
              ShowStashes = settings.ShowStashes
              DiffContextLines = settings.DiffContextLines
              DiffPresentationModeKey = DiffLayout.key settings.DiffLayout
              CommitRowFontFamily = settings.CommitRowFontFamily
              CommitRowMonoFontFamily = settings.CommitRowMonoFontFamily
              CommitRowTextFontSize = settings.CommitRowTextFontSize
              CommitRowMetaFontSize = settings.CommitRowMetaFontSize
              CommitRowBadgeFontSize = settings.CommitRowBadgeFontSize
              SearchDebounceSeconds = settings.SearchDebounceSeconds
              ThemeMode = ThemeMode.key settings.Theme
              PaneGap = settings.PaneGap
              PaneHoverEffectKey = PaneHoverEffect.key settings.PaneHoverEffect
              PaneHoverColorKey = PaneHoverColor.key settings.PaneHoverColor
              PaneHoverIntensityKey = PaneHoverIntensity.key settings.PaneHoverIntensity
              PaneBorder = settings.PaneBorder }

    /// <summary>Normalized settings, or why the text isn't a settings file. Unknown layout or theme names take the defaults.</summary>
    let decode (json: string) : Result<Settings, string> =
        Json.tryDeserialize Codecs.settings json
        |> Result.map (fun wire ->
            Settings.normalize
                { ShowBranchRefs = wire.ShowBranchRefs
                  ShowStashes = wire.ShowStashes
                  DiffContextLines = wire.DiffContextLines
                  DiffLayout = DiffLayout.tryParse wire.DiffPresentationModeKey |> Option.defaultValue Settings.defaults.DiffLayout
                  CommitRowFontFamily = wire.CommitRowFontFamily
                  CommitRowMonoFontFamily = wire.CommitRowMonoFontFamily
                  CommitRowTextFontSize = wire.CommitRowTextFontSize
                  CommitRowMetaFontSize = wire.CommitRowMetaFontSize
                  CommitRowBadgeFontSize = wire.CommitRowBadgeFontSize
                  SearchDebounceSeconds = wire.SearchDebounceSeconds
                  Theme = ThemeMode.tryParse wire.ThemeMode |> Option.defaultValue Settings.defaults.Theme
                  PaneGap = wire.PaneGap
                  PaneHoverEffect = PaneHoverEffect.tryParse wire.PaneHoverEffectKey |> Option.defaultValue Settings.defaults.PaneHoverEffect
                  PaneHoverColor = PaneHoverColor.tryParse wire.PaneHoverColorKey |> Option.defaultValue Settings.defaults.PaneHoverColor
                  PaneHoverIntensity = PaneHoverIntensity.tryParse wire.PaneHoverIntensityKey |> Option.defaultValue Settings.defaults.PaneHoverIntensity
                  PaneBorder = wire.PaneBorder })

/// <summary>Window size, layout and per-repository selections as the UI state file stores them.</summary>
module UiStateJson =
    let encode (state: UiState) =
        let state = UiState.normalize state
        let layout = state.Layout
        Json.serialize
            Codecs.uiState
            { WindowWidth = state.WindowWidth
              WindowHeight = state.WindowHeight
              RepoStates =
                // One entry per repository, holding whichever of the two it has.
                Map.toSeq state.LastSelectedCommits
                |> Seq.map fst
                |> Seq.append (Map.toSeq state.CommitDrafts |> Seq.map fst)
                |> Seq.distinct
                |> Seq.map (fun repository ->
                    repository,
                    { LastSelectedCommitHash = state.LastSelectedCommits |> Map.tryFind repository
                      CommitDraft = state.CommitDrafts |> Map.tryFind repository })
                |> Map.ofSeq
              HistoryPaneRatio = layout.HistoryPaneRatio
              FileListWidth = layout.FileListWidth
              GraphColumnWidth = layout.GraphColumnWidth
              HashColumnWidth = layout.HashColumnWidth
              AuthorColumnWidth = layout.AuthorColumnWidth
              DateColumnWidth = layout.DateColumnWidth }

    let decode (json: string) : Result<UiState, string> =
        Json.tryDeserialize Codecs.uiState json
        |> Result.map (fun wire ->
            UiState.normalize
                { WindowWidth = wire.WindowWidth
                  WindowHeight = wire.WindowHeight
                  LastSelectedCommits = wire.RepoStates |> Map.toSeq |> Seq.choose (fun (repository, state) -> state.LastSelectedCommitHash |> Option.map (fun hash -> repository, hash)) |> Map.ofSeq
                  CommitDrafts = wire.RepoStates |> Map.toSeq |> Seq.choose (fun (repository, state) -> state.CommitDraft |> Option.map (fun draft -> repository, draft)) |> Map.ofSeq
                  Layout =
                    { HistoryPaneRatio = wire.HistoryPaneRatio
                      FileListWidth = wire.FileListWidth
                      GraphColumnWidth = wire.GraphColumnWidth
                      HashColumnWidth = wire.HashColumnWidth
                      AuthorColumnWidth = wire.AuthorColumnWidth
                      DateColumnWidth = wire.DateColumnWidth } })
