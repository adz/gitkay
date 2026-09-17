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
      PaneFocusEffectKey: string
      PaneEffectColorKey: string
      PaneEffectIntensityKey: string
      HoverFocusesPane: bool
      PaneDimUnfocused: bool
      PaneFocusHighlight: bool
      PaneBorder: bool
      PaneBorderStyleKey: string
      PaneBorderColorKey: string
      PaneBorderThickness: float
      SplitterLinesHidden: bool }

type private RepoStateWire =
    { LastSelectedCommitHash: string option
      CommitDraft: string option }

type private UiStateWire =
    { WindowWidth: float option
      WindowHeight: float option
      RepoStates: Map<string, RepoStateWire>
      RecentSearches: string list
      ViewPreferences: Map<string, string>
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
            fieldAs "PaneFocusEffect" _.PaneFocusEffectKey { defaultValue (PaneFocusEffect.key d.PaneFocusEffect) }
            fieldAs "PaneEffectColor" _.PaneEffectColorKey { defaultValue (PaneEffectColor.key d.PaneEffectColor) }
            fieldAs "PaneEffectIntensity" _.PaneEffectIntensityKey { defaultValue (PaneEffectIntensity.key d.PaneEffectIntensity) }
            fieldAs "HoverFocusesPane" _.HoverFocusesPane { defaultValue d.HoverFocusesPane }
            fieldAs "PaneDimUnfocused" _.PaneDimUnfocused { defaultValue d.PaneDimUnfocused }
            fieldAs "PaneFocusHighlight" _.PaneFocusHighlight { defaultValue d.PaneFocusHighlight }
            fieldAs "PaneBorder" _.PaneBorder { defaultValue d.PaneBorder }
            fieldAs "PaneBorderStyle" _.PaneBorderStyleKey { defaultValue (PaneBorderStyle.key d.PaneBorderStyle) }
            fieldAs "PaneBorderColor" _.PaneBorderColorKey { defaultValue (PaneEffectColor.key d.PaneBorderColor) }
            fieldAs "PaneBorderThickness" _.PaneBorderThickness { defaultValue d.PaneBorderThickness }
            fieldAs "SplitterLinesHidden" _.SplitterLinesHidden { defaultValue d.SplitterLinesHidden }
            construct (fun showBranchRefs showStashes diffContextLines layout fontFamily monoFontFamily textSize metaSize badgeSize debounce theme paneGap paneEffect paneColor paneIntensity hoverFocuses dimUnfocused focusHighlight paneBorder borderStyle borderColor borderThickness splitterHidden ->
                { ShowBranchRefs = showBranchRefs; ShowStashes = showStashes; DiffContextLines = diffContextLines
                  DiffPresentationModeKey = layout; CommitRowFontFamily = fontFamily; CommitRowMonoFontFamily = monoFontFamily
                  CommitRowTextFontSize = textSize; CommitRowMetaFontSize = metaSize; CommitRowBadgeFontSize = badgeSize
                  SearchDebounceSeconds = debounce; ThemeMode = theme; PaneGap = paneGap; PaneFocusEffectKey = paneEffect
                  PaneEffectColorKey = paneColor; PaneEffectIntensityKey = paneIntensity; HoverFocusesPane = hoverFocuses
                  PaneDimUnfocused = dimUnfocused; PaneFocusHighlight = focusHighlight; PaneBorder = paneBorder
                  PaneBorderStyleKey = borderStyle; PaneBorderColorKey = borderColor; PaneBorderThickness = borderThickness
                  SplitterLinesHidden = splitterHidden })
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
            fieldAs "RecentSearches" _.RecentSearches { defaultValue [] }
            fieldAs "ViewPreferences" _.ViewPreferences { defaultValue Map.empty }
            fieldAs "HistoryPaneRatio" _.HistoryPaneRatio
            fieldAs "FileListWidth" _.FileListWidth
            fieldAs "GraphColumnWidth" _.GraphColumnWidth
            fieldAs "HashColumnWidth" _.HashColumnWidth
            fieldAs "AuthorColumnWidth" _.AuthorColumnWidth
            fieldAs "DateColumnWidth" _.DateColumnWidth
            construct (fun width height states searches preferences ratio fileList graph hash author date ->
                { WindowWidth = width; WindowHeight = height; RepoStates = states; RecentSearches = searches
                  ViewPreferences = preferences; HistoryPaneRatio = ratio
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
              PaneFocusEffectKey = PaneFocusEffect.key settings.PaneFocusEffect
              PaneEffectColorKey = PaneEffectColor.key settings.PaneEffectColor
              PaneEffectIntensityKey = PaneEffectIntensity.key settings.PaneEffectIntensity
              HoverFocusesPane = settings.HoverFocusesPane
              PaneDimUnfocused = settings.PaneDimUnfocused
              PaneFocusHighlight = settings.PaneFocusHighlight
              PaneBorder = settings.PaneBorder
              PaneBorderStyleKey = PaneBorderStyle.key settings.PaneBorderStyle
              PaneBorderColorKey = PaneEffectColor.key settings.PaneBorderColor
              PaneBorderThickness = settings.PaneBorderThickness
              SplitterLinesHidden = settings.SplitterLinesHidden }

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
                  PaneFocusEffect = PaneFocusEffect.tryParse wire.PaneFocusEffectKey |> Option.defaultValue Settings.defaults.PaneFocusEffect
                  PaneEffectColor = PaneEffectColor.tryParse wire.PaneEffectColorKey |> Option.defaultValue Settings.defaults.PaneEffectColor
                  PaneEffectIntensity = PaneEffectIntensity.tryParse wire.PaneEffectIntensityKey |> Option.defaultValue Settings.defaults.PaneEffectIntensity
                  HoverFocusesPane = wire.HoverFocusesPane
                  PaneDimUnfocused = wire.PaneDimUnfocused
                  PaneFocusHighlight = wire.PaneFocusHighlight
                  PaneBorder = wire.PaneBorder
                  PaneBorderStyle = PaneBorderStyle.tryParse wire.PaneBorderStyleKey |> Option.defaultValue Settings.defaults.PaneBorderStyle
                  PaneBorderColor = PaneEffectColor.tryParse wire.PaneBorderColorKey |> Option.defaultValue Settings.defaults.PaneBorderColor
                  PaneBorderThickness = wire.PaneBorderThickness
                  SplitterLinesHidden = wire.SplitterLinesHidden })

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
              RecentSearches = state.RecentSearches
              ViewPreferences = state.ViewPreferences
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
                  RecentSearches = wire.RecentSearches
                  ViewPreferences = wire.ViewPreferences
                  Layout =
                    { HistoryPaneRatio = wire.HistoryPaneRatio
                      FileListWidth = wire.FileListWidth
                      GraphColumnWidth = wire.GraphColumnWidth
                      HashColumnWidth = wire.HashColumnWidth
                      AuthorColumnWidth = wire.AuthorColumnWidth
                      DateColumnWidth = wire.DateColumnWidth } })
