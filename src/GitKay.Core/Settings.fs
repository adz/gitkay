namespace GitKay.Core

open System

/// <summary>How a diff is laid out.</summary>
type DiffLayout =
    /// <summary>Changes inline, removed above added.</summary>
    | Unified
    /// <summary>Old on the left, new on the right.</summary>
    | SideBySide
    /// <summary>The file after the change.</summary>
    | NewFile
    /// <summary>The file before the change.</summary>
    | OldFile

module DiffLayout =
    let all = [ Unified; SideBySide; NewFile; OldFile ]

    /// <summary>The key used on the command line and in settings files.</summary>
    let key layout =
        match layout with
        | Unified -> "diff"
        | SideBySide -> "side-by-side"
        | NewFile -> "new"
        | OldFile -> "old"

    let label layout =
        match layout with
        | Unified -> "Diff"
        | SideBySide -> "Side-by-side"
        | NewFile -> "New"
        | OldFile -> "Old"

    let tryParse (text: string) =
        let normalized = if isNull text then "" else text.Trim().ToLowerInvariant()
        all |> List.tryFind (fun layout -> key layout = normalized)

    /// <summary>Whether rows show two content columns.</summary>
    let isSideBySide layout = layout = SideBySide

    /// <summary>
    /// The text a line shows in a content column: side 0 is the old (or only) column and side 1 the new one in
    /// side-by-side. <paramref name="content"/> is the unified line; old and new are empty on the side a line doesn't exist.
    /// </summary>
    let columnText layout (side: int) (content: string) (oldContent: string) (newContent: string) =
        match layout with
        | SideBySide -> if side = 0 then oldContent else newContent
        | NewFile -> newContent
        | OldFile -> oldContent
        | Unified -> content

type ThemeMode =
    | SystemTheme
    | LightTheme
    | DarkTheme

module ThemeMode =
    let all = [ SystemTheme; LightTheme; DarkTheme ]

    let key mode =
        match mode with
        | SystemTheme -> "system"
        | LightTheme -> "light"
        | DarkTheme -> "dark"

    let label mode =
        match mode with
        | SystemTheme -> "System"
        | LightTheme -> "Light"
        | DarkTheme -> "Dark"

    let tryParse (text: string) =
        let normalized = if isNull text then "" else text.Trim().ToLowerInvariant()
        all |> List.tryFind (fun mode -> key mode = normalized)

/// <summary>What the user chose in settings. Build through <see cref="M:GitKay.Core.SettingsModule.normalize"/> to keep values in range.</summary>
/// <summary>What the pane under the pointer does, in the space around panes.</summary>
type PaneHoverEffect =
    | NoHoverEffect
    | HoverGlow
    | HoverShadow
    | HoverHighlight

module PaneHoverEffect =
    let all = [ NoHoverEffect; HoverGlow; HoverShadow; HoverHighlight ]

    /// <summary>The key used in settings files.</summary>
    let key effect =
        match effect with
        | NoHoverEffect -> "none"
        | HoverGlow -> "glow"
        | HoverShadow -> "shadow"
        | HoverHighlight -> "highlight"

    let label effect =
        match effect with
        | NoHoverEffect -> "None"
        | HoverGlow -> "Glow"
        | HoverShadow -> "Shadow"
        | HoverHighlight -> "Highlight"

    let describe effect =
        match effect with
        | NoHoverEffect -> "Panes stay as they are"
        | HoverGlow -> "The pane under the pointer glows into the space around it"
        | HoverShadow -> "The pane under the pointer lifts, casting a soft shadow"
        | HoverHighlight -> "The pane under the pointer gets a brighter edge"

    let tryParse (text: string) =
        let normalized = if isNull text then "" else text.Trim().ToLowerInvariant()
        all |> List.tryFind (fun effect -> key effect = normalized)

/// <summary>The colour a pane's hover effect is drawn in.</summary>
type PaneHoverColor =
    /// <summary>The theme's own accent colour.</summary>
    | AccentHoverColor
    | BlueHoverColor
    | GreenHoverColor
    | PurpleHoverColor
    | OrangeHoverColor
    | PinkHoverColor
    | TealHoverColor
    | WhiteHoverColor

module PaneHoverColor =
    let all =
        [ AccentHoverColor; BlueHoverColor; GreenHoverColor; PurpleHoverColor
          OrangeHoverColor; PinkHoverColor; TealHoverColor; WhiteHoverColor ]

    let key color =
        match color with
        | AccentHoverColor -> "accent"
        | BlueHoverColor -> "blue"
        | GreenHoverColor -> "green"
        | PurpleHoverColor -> "purple"
        | OrangeHoverColor -> "orange"
        | PinkHoverColor -> "pink"
        | TealHoverColor -> "teal"
        | WhiteHoverColor -> "white"

    let label color =
        match color with
        | AccentHoverColor -> "Theme accent"
        | BlueHoverColor -> "Blue"
        | GreenHoverColor -> "Green"
        | PurpleHoverColor -> "Purple"
        | OrangeHoverColor -> "Orange"
        | PinkHoverColor -> "Pink"
        | TealHoverColor -> "Teal"
        | WhiteHoverColor -> "White"

    /// <summary>The colour as #RRGGBB, or None for the theme's accent, which the window resolves.</summary>
    let hex color =
        match color with
        | AccentHoverColor -> None
        | BlueHoverColor -> Some "#58A6FF"
        | GreenHoverColor -> Some "#3FB950"
        | PurpleHoverColor -> Some "#BC8CFF"
        | OrangeHoverColor -> Some "#F0883E"
        | PinkHoverColor -> Some "#FF7EB6"
        | TealHoverColor -> Some "#39C5CF"
        | WhiteHoverColor -> Some "#FFFFFF"

    let tryParse (text: string) =
        let normalized = if isNull text then "" else text.Trim().ToLowerInvariant()
        all |> List.tryFind (fun color -> key color = normalized)

/// <summary>How strongly a pane's hover effect is drawn.</summary>
type PaneHoverIntensity =
    | FullIntensity
    | HalfIntensity
    | QuarterIntensity
    | EighthIntensity

module PaneHoverIntensity =
    let all = [ FullIntensity; HalfIntensity; QuarterIntensity; EighthIntensity ]

    let key intensity =
        match intensity with
        | FullIntensity -> "full"
        | HalfIntensity -> "half"
        | QuarterIntensity -> "quarter"
        | EighthIntensity -> "eighth"

    let label intensity =
        match intensity with
        | FullIntensity -> "Full"
        | HalfIntensity -> "Half"
        | QuarterIntensity -> "Quarter"
        | EighthIntensity -> "Eighth"

    /// <summary>What the effect's colours are scaled by.</summary>
    let scale intensity =
        match intensity with
        | FullIntensity -> 1.0
        | HalfIntensity -> 0.5
        | QuarterIntensity -> 0.25
        | EighthIntensity -> 0.125

    let tryParse (text: string) =
        let normalized = if isNull text then "" else text.Trim().ToLowerInvariant()
        all |> List.tryFind (fun intensity -> key intensity = normalized)

type Settings =
    { ShowBranchRefs: bool
      ShowStashes: bool
      DiffContextLines: int
      DiffLayout: DiffLayout
      CommitRowFontFamily: string
      CommitRowMonoFontFamily: string
      CommitRowTextFontSize: float
      CommitRowMetaFontSize: float
      CommitRowBadgeFontSize: float
      SearchDebounceSeconds: float
      Theme: ThemeMode
      /// <summary>Space around each pane, in pixels; 0 keeps panes flush against each other.</summary>
      PaneGap: float
      /// <summary>What the pane under the pointer does in that space.</summary>
      PaneHoverEffect: PaneHoverEffect
      /// <summary>The colour that effect is drawn in.</summary>
      PaneHoverColor: PaneHoverColor
      /// <summary>How strongly it is drawn.</summary>
      PaneHoverIntensity: PaneHoverIntensity }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Settings =
    let defaults =
        { ShowBranchRefs = false
          ShowStashes = false
          DiffContextLines = 3
          DiffLayout = Unified
          CommitRowFontFamily = "Helvetica,Arial,Liberation Sans,Noto Sans,sans-serif"
          CommitRowMonoFontFamily = "Courier,Courier New,Liberation Mono,Monospace"
          CommitRowTextFontSize = 13.0
          CommitRowMetaFontSize = 12.0
          CommitRowBadgeFontSize = 11.0
          SearchDebounceSeconds = 0.5
          Theme = SystemTheme
          PaneGap = 3.0
          PaneHoverEffect = HoverGlow
          PaneHoverColor = AccentHoverColor
          PaneHoverIntensity = HalfIntensity }

    let private fontFamily fallback (value: string) = if String.IsNullOrWhiteSpace value then fallback else value

    let private fontSize fallback (value: float) = if Double.IsFinite value && value > 0.0 then value else fallback

    let private nonNegative (value: float) = if Double.IsFinite value && value > 0.0 then value else 0.0

    /// <summary>Replaces blank font names, non-positive font sizes and negative counts with defaults or zero.</summary>
    let normalize (settings: Settings) =
        { settings with
            DiffContextLines = max 0 settings.DiffContextLines
            CommitRowFontFamily = fontFamily defaults.CommitRowFontFamily settings.CommitRowFontFamily
            CommitRowMonoFontFamily = fontFamily defaults.CommitRowMonoFontFamily settings.CommitRowMonoFontFamily
            CommitRowTextFontSize = fontSize defaults.CommitRowTextFontSize settings.CommitRowTextFontSize
            CommitRowMetaFontSize = fontSize defaults.CommitRowMetaFontSize settings.CommitRowMetaFontSize
            CommitRowBadgeFontSize = fontSize defaults.CommitRowBadgeFontSize settings.CommitRowBadgeFontSize
            SearchDebounceSeconds = nonNegative settings.SearchDebounceSeconds
            PaneGap = (if Double.IsFinite settings.PaneGap then Math.Clamp(settings.PaneGap, 0.0, 8.0) else defaults.PaneGap) }

/// <summary>Splitter positions and history column widths the user dragged; None keeps the layout's default.</summary>
type UiLayout =
    { /// <summary>Commit list height as a fraction of the commit list plus the diff area.</summary>
      HistoryPaneRatio: float option
      FileListWidth: float option
      GraphColumnWidth: float option
      HashColumnWidth: float option
      AuthorColumnWidth: float option
      DateColumnWidth: float option }

module UiLayout =
    let empty =
        { HistoryPaneRatio = None
          FileListWidth = None
          GraphColumnWidth = None
          HashColumnWidth = None
          AuthorColumnWidth = None
          DateColumnWidth = None }

    let private width value =
        value |> Option.filter (fun width -> Double.IsFinite width && width > 0.0) |> Option.map (min 10000.0)

    /// <summary>Clamps the pane ratio to 10–90% and drops widths that aren't positive.</summary>
    let normalize (layout: UiLayout) =
        { HistoryPaneRatio = layout.HistoryPaneRatio |> Option.filter Double.IsFinite |> Option.map (fun ratio -> Math.Clamp(ratio, 0.1, 0.9))
          FileListWidth = width layout.FileListWidth
          GraphColumnWidth = width layout.GraphColumnWidth
          HashColumnWidth = width layout.HashColumnWidth
          AuthorColumnWidth = width layout.AuthorColumnWidth
          DateColumnWidth = width layout.DateColumnWidth }

/// <summary>Window size, layout, and the commit last selected in each repository.</summary>
type UiState =
    { Layout: UiLayout
      WindowWidth: float option
      WindowHeight: float option
      /// <summary>Full repository path to the hash of the commit last selected there.</summary>
      LastSelectedCommits: Map<string, string> }

module UiState =
    let empty =
        { Layout = UiLayout.empty
          WindowWidth = None
          WindowHeight = None
          LastSelectedCommits = Map.empty }

    let private dimension (value: float option) = value |> Option.filter (fun size -> Double.IsFinite size && size > 0.0)

    let private text (value: string) = if String.IsNullOrWhiteSpace value then None else Some(value.Trim())

    /// <summary>The same repository reached by different spellings of its path shares one entry.</summary>
    let repositoryKey (path: string) =
        text path |> Option.map (fun trimmed -> IO.Path.TrimEndingDirectorySeparator(IO.Path.GetFullPath trimmed))

    let normalize (state: UiState) =
        { Layout = UiLayout.normalize state.Layout
          WindowWidth = dimension state.WindowWidth
          WindowHeight = dimension state.WindowHeight
          LastSelectedCommits =
            state.LastSelectedCommits
            |> Map.toSeq
            |> Seq.choose (fun (repository, hash) ->
                match repositoryKey repository, text hash with
                | Some key, Some hash -> Some(key, hash)
                | _ -> None)
            |> Map.ofSeq }

    let withLayout layout state = { state with Layout = UiLayout.normalize layout }

    /// <summary>Keeps the current size for a dimension that isn't a positive number.</summary>
    let withWindowSize (width: float option) (height: float option) state =
        { state with
            WindowWidth = dimension width |> Option.orElse state.WindowWidth
            WindowHeight = dimension height |> Option.orElse state.WindowHeight }

    let withSelectedCommit (repository: string) (hash: string) state =
        match repositoryKey repository, text hash with
        | Some key, Some hash -> { state with LastSelectedCommits = state.LastSelectedCommits |> Map.add key hash }
        | _ -> state

    let selectedCommit (repository: string) state =
        repositoryKey repository |> Option.bind (fun key -> state.LastSelectedCommits |> Map.tryFind key)
