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
/// <summary>What the focused pane does in the space around panes; it is focused by pointing at it or clicking into it.</summary>
type PaneFocusEffect =
    | NoPaneEffect
    | PaneGlow
    | PaneShadow

module PaneFocusEffect =
    let all = [ NoPaneEffect; PaneGlow; PaneShadow ]

    /// <summary>The key used in settings files.</summary>
    let key effect =
        match effect with
        | NoPaneEffect -> "none"
        | PaneGlow -> "glow"
        | PaneShadow -> "shadow"

    let label effect =
        match effect with
        | NoPaneEffect -> "None"
        | PaneGlow -> "Glow"
        | PaneShadow -> "Shadow"

    let describe effect =
        match effect with
        | NoPaneEffect -> "The focused pane is left as it is"
        | PaneGlow -> "The focused pane glows into the space around it"
        | PaneShadow -> "The focused pane lifts, casting a soft shadow"

    let tryParse (text: string) =
        let normalized = if isNull text then "" else text.Trim().ToLowerInvariant()
        all |> List.tryFind (fun effect -> key effect = normalized)

/// <summary>How a pane's outline is drawn, when panes are bordered.</summary>
type PaneBorderStyle =
    /// <summary>The theme's own hairline, which barely separates the panes.</summary>
    | SubtleBorder
    /// <summary>The theme's border colour, as dialogs and inputs use it.</summary>
    | NormalBorder
    /// <summary>A colour and thickness of your own.</summary>
    | CustomBorder

module PaneBorderStyle =
    let all = [ SubtleBorder; NormalBorder; CustomBorder ]

    let key style =
        match style with
        | SubtleBorder -> "subtle"
        | NormalBorder -> "normal"
        | CustomBorder -> "custom"

    let label style =
        match style with
        | SubtleBorder -> "Subtle"
        | NormalBorder -> "Normal"
        | CustomBorder -> "Custom"

    let tryParse (text: string) =
        let normalized = if isNull text then "" else text.Trim().ToLowerInvariant()
        all |> List.tryFind (fun style -> key style = normalized)

/// <summary>
/// What the window's chrome — the toolbar, the column headers, the pane header bars — is filled with. Chrome that
/// shares the content's own background blends into it; a surface or a tint tells the reader where content stops.
/// </summary>
type ChromeBackground =
    /// <summary>The theme's surface tone, a step up from the content behind it.</summary>
    | SurfaceChrome
    /// <summary>No fill of its own: the chrome takes the window's background, as content does.</summary>
    | TransparentChrome
    /// <summary>A wash of a chosen colour over the window's background.</summary>
    | TintedChrome

module ChromeBackground =
    let all = [ SurfaceChrome; TransparentChrome; TintedChrome ]

    let key background =
        match background with
        | SurfaceChrome -> "surface"
        | TransparentChrome -> "transparent"
        | TintedChrome -> "tinted"

    let label background =
        match background with
        | SurfaceChrome -> "Surface"
        | TransparentChrome -> "Transparent"
        | TintedChrome -> "Colour"

    let describe background =
        match background with
        | SurfaceChrome -> "A step up from the content, as the file list is"
        | TransparentChrome -> "The same background as the content it sits above"
        | TintedChrome -> "A wash of the colour chosen below"

    let tryParse (text: string) =
        let normalized = if isNull text then "" else text.Trim().ToLowerInvariant()
        all |> List.tryFind (fun background -> key background = normalized)

/// <summary>The colour a pane's focus effect is drawn in.</summary>
type PaneEffectColor =
    /// <summary>The theme's own accent colour.</summary>
    | AccentEffectColor
    | BlueEffectColor
    | GreenEffectColor
    | PurpleEffectColor
    | OrangeEffectColor
    | PinkEffectColor
    | TealEffectColor
    | WhiteEffectColor
    | RedEffectColor
    | AmberEffectColor
    | LimeEffectColor
    | CyanEffectColor
    | IndigoEffectColor
    | MagentaEffectColor
    | SlateEffectColor
    | SandEffectColor

module PaneEffectColor =
    /// <summary>Every choice is a mid-tone or lighter: a black or near-black chrome reads as a hole, not a surface.</summary>
    let all =
        [ AccentEffectColor; BlueEffectColor; GreenEffectColor; PurpleEffectColor
          OrangeEffectColor; PinkEffectColor; TealEffectColor; WhiteEffectColor
          RedEffectColor; AmberEffectColor; LimeEffectColor; CyanEffectColor
          IndigoEffectColor; MagentaEffectColor; SlateEffectColor; SandEffectColor ]

    let key color =
        match color with
        | AccentEffectColor -> "accent"
        | BlueEffectColor -> "blue"
        | GreenEffectColor -> "green"
        | PurpleEffectColor -> "purple"
        | OrangeEffectColor -> "orange"
        | PinkEffectColor -> "pink"
        | TealEffectColor -> "teal"
        | WhiteEffectColor -> "white"
        | RedEffectColor -> "red"
        | AmberEffectColor -> "amber"
        | LimeEffectColor -> "lime"
        | CyanEffectColor -> "cyan"
        | IndigoEffectColor -> "indigo"
        | MagentaEffectColor -> "magenta"
        | SlateEffectColor -> "slate"
        | SandEffectColor -> "sand"

    let label color =
        match color with
        | AccentEffectColor -> "Theme accent"
        | BlueEffectColor -> "Blue"
        | GreenEffectColor -> "Green"
        | PurpleEffectColor -> "Purple"
        | OrangeEffectColor -> "Orange"
        | PinkEffectColor -> "Pink"
        | TealEffectColor -> "Teal"
        | WhiteEffectColor -> "White"
        | RedEffectColor -> "Red"
        | AmberEffectColor -> "Amber"
        | LimeEffectColor -> "Lime"
        | CyanEffectColor -> "Cyan"
        | IndigoEffectColor -> "Indigo"
        | MagentaEffectColor -> "Magenta"
        | SlateEffectColor -> "Slate"
        | SandEffectColor -> "Sand"

    /// <summary>The colour as #RRGGBB, or None for the theme's accent, which the window resolves.</summary>
    let hex color =
        match color with
        | AccentEffectColor -> None
        | BlueEffectColor -> Some "#58A6FF"
        | GreenEffectColor -> Some "#3FB950"
        | PurpleEffectColor -> Some "#BC8CFF"
        | OrangeEffectColor -> Some "#F0883E"
        | PinkEffectColor -> Some "#FF7EB6"
        | TealEffectColor -> Some "#39C5CF"
        | WhiteEffectColor -> Some "#FFFFFF"
        | RedEffectColor -> Some "#FF7B72"
        | AmberEffectColor -> Some "#E3B341"
        | LimeEffectColor -> Some "#A5D66F"
        | CyanEffectColor -> Some "#56D4DD"
        | IndigoEffectColor -> Some "#8B95FF"
        | MagentaEffectColor -> Some "#E879F9"
        | SlateEffectColor -> Some "#8B98A9"
        | SandEffectColor -> Some "#D9C2A0"

    let tryParse (text: string) =
        let normalized = if isNull text then "" else text.Trim().ToLowerInvariant()
        all |> List.tryFind (fun color -> key color = normalized)

/// <summary>How strongly a pane's hover effect is drawn.</summary>
type PaneEffectIntensity =
    | FullIntensity
    | HalfIntensity
    | QuarterIntensity
    | EighthIntensity

module PaneEffectIntensity =
    let all = [ FullIntensity; HalfIntensity; QuarterIntensity; EighthIntensity ]

    let key intensity =
        match intensity with
        | FullIntensity -> "full"
        | HalfIntensity -> "half"
        | QuarterIntensity -> "quarter"
        | EighthIntensity -> "eighth"

    /// <summary>Named for how much light they give off; the keys in the settings file stay full/half/quarter/eighth.</summary>
    let label intensity =
        match intensity with
        | FullIntensity -> "Supernova"
        | HalfIntensity -> "Bonfire"
        | QuarterIntensity -> "Candle"
        | EighthIntensity -> "Firefly"

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
      /// <summary>Open Markdown files in rendered mode unless a per-file preference overrides it.</summary>
      RenderMarkdownByDefault: bool
      /// <summary>Allow rendered Markdown to contact remote image hosts. Off protects reader privacy.</summary>
      LoadRemoteMarkdownImages: bool
      Theme: ThemeMode
      /// <summary>Space around each pane, in pixels; 0 keeps panes flush against each other.</summary>
      PaneGap: float
      /// <summary>Whether pointing at a pane focuses it, so keys go there without clicking.</summary>
      HoverFocusesPane: bool
      /// <summary>Whether the panes without the keys are washed over, so the focused one stands out by contrast.</summary>
      PaneDimUnfocused: bool
      /// <summary>Whether the focused pane's edge is brightened.</summary>
      PaneFocusHighlight: bool
      /// <summary>What else the focused pane does in the space around it.</summary>
      PaneFocusEffect: PaneFocusEffect
      /// <summary>The colour the highlight and effect are drawn in.</summary>
      PaneEffectColor: PaneEffectColor
      /// <summary>How strongly they are drawn.</summary>
      PaneEffectIntensity: PaneEffectIntensity
      /// <summary>Whether each pane is outlined, so the space around it reads as a frame.</summary>
      PaneBorder: bool
      /// <summary>How that outline is drawn.</summary>
      PaneBorderStyle: PaneBorderStyle
      /// <summary>The outline's colour, used by <see cref="T:GitKay.Core.CustomBorder"/>.</summary>
      PaneBorderColor: PaneEffectColor
      /// <summary>The outline's thickness in pixels, used by <see cref="T:GitKay.Core.CustomBorder"/>.</summary>
      PaneBorderThickness: float
      /// <summary>Whether the hairline between two panes is hidden, leaving the splitter to be felt rather than seen.</summary>
      SplitterLinesHidden: bool
      /// <summary>What the toolbar, column headers and pane header bars are filled with.</summary>
      ChromeBackground: ChromeBackground
      /// <summary>The tint's colour, used by <see cref="T:GitKay.Core.TintedChrome"/>.</summary>
      ChromeColor: PaneEffectColor }

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
          RenderMarkdownByDefault = true
          LoadRemoteMarkdownImages = false
          Theme = SystemTheme
          PaneGap = 3.0
          HoverFocusesPane = true
          PaneDimUnfocused = false
          PaneFocusHighlight = false
          PaneFocusEffect = PaneGlow
          PaneEffectColor = AccentEffectColor
          PaneEffectIntensity = EighthIntensity
          PaneBorder = false
          PaneBorderStyle = SubtleBorder
          PaneBorderColor = AccentEffectColor
          PaneBorderThickness = 1.0
          SplitterLinesHidden = false
          ChromeBackground = SurfaceChrome
          ChromeColor = AccentEffectColor }

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
            PaneGap = (if Double.IsFinite settings.PaneGap then Math.Clamp(settings.PaneGap, 0.0, 8.0) else defaults.PaneGap)
            PaneBorderThickness =
                if Double.IsFinite settings.PaneBorderThickness then Math.Clamp(settings.PaneBorderThickness, 1.0, 4.0)
                else defaults.PaneBorderThickness }

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
      LastSelectedCommits: Map<string, string>
      /// <summary>Full repository path to the commit message being written there, kept until it is committed.</summary>
      CommitDrafts: Map<string, string>
      /// <summary>Recent commit searches, newest first.</summary>
      RecentSearches: string list
      /// <summary>Small view toggles: file tree mode, details expanded, search mode and so on.</summary>
      ViewPreferences: Map<string, string> }

module UiState =
    let empty =
        { Layout = UiLayout.empty
          WindowWidth = None
          WindowHeight = None
          LastSelectedCommits = Map.empty
          CommitDrafts = Map.empty
          RecentSearches = []
          ViewPreferences = Map.empty }

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
            |> Map.ofSeq
          CommitDrafts =
            state.CommitDrafts
            |> Map.toSeq
            // A draft keeps its own whitespace: only the repository path is normalized, and blank drafts are dropped.
            |> Seq.choose (fun (repository, draft) ->
                match repositoryKey repository with
                | Some key when not (String.IsNullOrWhiteSpace draft) -> Some(key, draft)
                | _ -> None)
            |> Map.ofSeq
          // Enough searches to be useful, not a log.
          RecentSearches =
            state.RecentSearches
            |> List.choose text
            |> List.distinct
            |> List.truncate 30
          ViewPreferences = state.ViewPreferences |> Map.filter (fun key _ -> not (String.IsNullOrWhiteSpace key)) }

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

    let viewPreference key state = state.ViewPreferences |> Map.tryFind key

    let withViewPreference key value state =
        { state with ViewPreferences = state.ViewPreferences |> Map.add key value }

    /// <summary>
    /// Forgets every view preference whose key starts with <paramref name="prefix"/>. These are written on every
    /// toggle and never expire, so a choice made once outlives any memory of making it: this is how they are cleared.
    /// </summary>
    let withoutViewPreferences (prefix: string) state =
        if String.IsNullOrEmpty prefix then state
        else { state with ViewPreferences = state.ViewPreferences |> Map.filter (fun key _ -> not (key.StartsWith(prefix, StringComparison.Ordinal))) }

    /// <summary>How many view preferences share a prefix, so a command can say what it is about to forget.</summary>
    let countViewPreferences (prefix: string) state =
        if String.IsNullOrEmpty prefix then 0
        else state.ViewPreferences |> Map.filter (fun key _ -> key.StartsWith(prefix, StringComparison.Ordinal)) |> Map.count

    let selectedCommit (repository: string) state =
        repositoryKey repository |> Option.bind (fun key -> state.LastSelectedCommits |> Map.tryFind key)

    /// <summary>Keeps the commit message being written for a repository; a blank draft removes it.</summary>
    let withCommitDraft (repository: string) (draft: string) state =
        match repositoryKey repository with
        | None -> state
        | Some key ->
            { state with
                CommitDrafts =
                    if String.IsNullOrWhiteSpace draft then state.CommitDrafts |> Map.remove key
                    else state.CommitDrafts |> Map.add key draft }

    let commitDraft (repository: string) state =
        repositoryKey repository |> Option.bind (fun key -> state.CommitDrafts |> Map.tryFind key)

    let withRecentSearches (searches: string seq) state = { state with RecentSearches = List.ofSeq searches }

    let withViewPreferences (preferences: (string * string) seq) state = { state with ViewPreferences = Map.ofSeq preferences }
