namespace GitKay.Core

open System
open Reified
open Reified.Result

/// Startup argument parsing and normalization. This module is pure: it performs no Git or host effects.
module GitStartup =
    type StartupTarget =
        | All
        | Branch of string
        | Sha of string
        | Tag of string
        | Revision of string
        /// ^X, or the left side of A..B: hide commits reachable from X.
        | Exclude of string
        /// A...B: hide commits reachable from the merge base of A and B.
        | ExcludeMergeBase of string * string
        /// -- <paths>: only commits touching these paths.
        | Path of string

    type StartupOptions =
        {
            StartupTargets: StartupTarget list
            ShowBranchRefs: bool
            ShowStashes: bool
            DiffContextLines: int
            DiffLayout: DiffLayout
            SearchQuery: string
            SearchScopeKey: string
            SelectedCommitHash: string option
            /// Filters from --author/--grep/-G etc. are regex when -G was given.
            SearchUseRegex: bool
            /// Filters given on the command line limit the list to matching commits, like git log.
            ShowOnlyMatches: bool
            LogFile: string option
            HelpRequested: bool
            VersionRequested: bool
        }

    /// <summary>
    /// The command-line arguments for the settings the command line also accepts, so saved settings start GitKay the
    /// way the same flags would. Settings at their defaults add nothing.
    /// </summary>
    let settingsArguments (settings: Settings) : string list =
        let settings = Settings.normalize settings
        [ if settings.ShowBranchRefs then "--show-branch-refs"
          if settings.ShowStashes then "--show-stashes"
          if settings.DiffContextLines <> Settings.defaults.DiffContextLines then $"--diff-context={settings.DiffContextLines}"
          if settings.DiffLayout <> Settings.defaults.DiffLayout then $"--diff-presentation={DiffLayout.key settings.DiffLayout}" ]

    let defaultStartupOptions =
        {
            StartupTargets = []
            ShowBranchRefs = false
            ShowStashes = false
            DiffContextLines = 3
            DiffLayout = Settings.defaults.DiffLayout
            SearchQuery = ""
            SearchScopeKey = "commit"
            SelectedCommitHash = None
            SearchUseRegex = false
            ShowOnlyMatches = false
            LogFile = None
            HelpRequested = false
            VersionRequested = false
        }

    /// Search mode keys (commit, path, diff); older scope names are accepted and mapped to the nearest mode.
    let private tryParseSearchScopeKey (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "commit" | "path" | "diff" | "fields" | "all" | "message" | "author" | "hash" | "ref" | "text" as key ->
            Some(GitSearch.modeKey (GitSearch.parseMode key))
        | _ -> None

    /// <summary>Why the command line couldn't be read.</summary>
    type StartupError =
        | UnrecognizedArgument of argument: string
        | MissingValue of option: string * label: string
        | InvalidValue of label: string * value: string

    let describeError error =
        match error with
        | UnrecognizedArgument argument -> "Unrecognized startup argument: " + argument
        | MissingValue(option, label) -> $"Missing {label} after {option}."
        | InvalidValue(label, value) -> $"Invalid {label}: {value}"

    /// Expands one revision argument: ^X, A..B and A...B ranges (an empty side means HEAD), or a plain revision.
    let revisionTargets (arg: string) : StartupTarget list =
        let side (value: string) = if String.IsNullOrWhiteSpace value then "HEAD" else value
        if arg.StartsWith "^" && arg.Length > 1 then
            [ Exclude(arg.Substring 1) ]
        elif arg.Contains "..." then
            let index = arg.IndexOf "..."
            let left, right = side (arg.Substring(0, index)), side (arg.Substring(index + 3))
            [ Revision left; Revision right; ExcludeMergeBase(left, right) ]
        elif arg.Contains ".." then
            let index = arg.IndexOf ".."
            [ Revision(side (arg.Substring(index + 2))); Exclude(side (arg.Substring(0, index))) ]
        else
            [ Revision arg ]

    let private quoteTerm (value: string) =
        if value |> Seq.exists Char.IsWhiteSpace then "\"" + value.Replace("\"", "") + "\"" else value

    let getHelpText () =
        "Usage: gitkay [options] [<revision>...] [-- <path>...]\n\n" +
        "Options:\n" +
        "  --help, -h               Show this help message\n" +
        "  --version, -v            Show version information\n" +
        "  --all                    Show commits from all branches and tags\n" +
        "  --branch <name>          Show commits from the specified branch\n" +
        "  --sha <hash>             Show commits from the specified commit hash\n" +
        "  --tag <name>             Show commits from the specified tag\n" +
        "  --select <revision>      Select a commit on startup: hash, short hash, branch, tag or HEAD~n\n" +
        "                           (also --select-commit, as in gitk)\n" +
        "  --search <query>         Filter commits by the specified search query\n" +
        "  --search-scope <scope>   Set search mode (commit, path, diff)\n" +
        "  --show-branch-refs       Show branch and tag markers in the history list\n" +
        "  --hide-branch-refs       Hide branch and tag markers in the history list\n" +
        "  --show-stashes           Show stashes in the history list\n" +
        "  --hide-stashes           Hide stashes in the history list\n" +
        "  --diff-context <n>       Number of context lines to show in diffs\n" +
        "  --diff-presentation <m>  Diff presentation mode (diff, side-by-side, new, old)\n" +
        "  --log <file>             Write logs to the specified file\n\n" +
        "Filters (shown in the search bar, list limited to matches):\n" +
        "  --author <pattern>       author:<pattern>\n" +
        "  --grep <pattern>         message:<pattern>\n" +
        "  --since, --after <date>  after:<date>  (2024-05-01, \"2 weeks ago\", 3.days.ago)\n" +
        "  --until, --before <date> before:<date>\n" +
        "  -S <text>                diff:<text>   commits adding or removing the text\n" +
        "  -G <regex>               diff:<regex>  with regex search on\n\n" +
        "Arguments:\n" +
        "  <sha>                    Open with that commit selected (full or short hash)\n" +
        "  <revision>...            History tips: branches, tags, hashes; several allowed\n" +
        "  A..B  A...B  ^X          Ranges: in B not A; in either not both; exclude X\n" +
        "  -- <path>...             Only commits touching these paths\n"

    /// Maps a git log filter option onto the equivalent search prefix.
    let private filterTerm (option: string) (value: string) =
        let prefix =
            match option with
            | "--author" -> "author:"
            | "--grep" -> "message:"
            | "--since" | "--after" -> "after:"
            | _ -> "before:"
        prefix + quoteTerm value

    /// <summary>What has been read so far; lists are newest first until the end.</summary>
    type private Reading =
        { Options: StartupOptions
          HasAll: bool
          Targets: StartupTarget list
          Positionals: string list
          Paths: string list
          Filters: string list }

    /// <summary>A reader for one option: None when the arguments don't start with it.</summary>
    type private OptionReader = string list -> Reading -> Result<Reading * string list, StartupError> option

    let private flag (names: string list) (apply: Reading -> Reading) : OptionReader =
        fun args reading ->
            match args with
            | arg :: rest when List.contains arg names -> Some(Ok(apply reading, rest))
            | _ -> None

    /// <summary>
    /// An option taking a value as <c>--name value</c> or <c>--name=value</c>. A separate value can't start with "--".
    /// <paramref name="optional"/> says when a missing value is ignored rather than an error.
    /// </summary>
    let private valued (name: string) (label: string) (allowEmpty: bool) (optional: Reading -> bool) (apply: string -> Reading -> Result<Reading, StartupError>) : OptionReader =
        fun args reading ->
            let missing rest = if optional reading then Ok(reading, rest) else Error(MissingValue(name, label))
            match args with
            | arg :: rest when arg = name ->
                match rest with
                | value :: after when not (value.StartsWith "--") -> Some(apply value reading |> Result.map (fun next -> next, after))
                | _ -> Some(missing rest)
            | arg :: rest when arg.StartsWith(name + "=") ->
                let value = arg.Substring(name.Length + 1)
                if allowEmpty || not (String.IsNullOrWhiteSpace value) then Some(apply value reading |> Result.map (fun next -> next, rest))
                else Some(missing rest)
            | _ -> None

    let private always _ = false

    let private withOptions (update: StartupOptions -> StartupOptions) (reading: Reading) = { reading with Options = update reading.Options }

    let private setOption (update: StartupOptions -> StartupOptions) : string -> Reading -> Result<Reading, StartupError> =
        fun _ reading -> Ok(withOptions update reading)

    let private parsedOption (label: string) (parse: string -> 'value option) (update: 'value -> StartupOptions -> StartupOptions) =
        fun (value: string) reading ->
            parse value
            |> Result.fromOption
            |> Result.orError (InvalidValue(label, value))
            |> Result.map (fun parsed -> withOptions (update parsed) reading)

    /// <summary>--branch, --sha and --tag name history tips, ignored (value and all) once --all is given.</summary>
    let private tip (name: string) (label: string) (target: string -> StartupTarget) =
        valued name label false (fun reading -> reading.HasAll) (fun value reading ->
            Ok(if reading.HasAll then reading else { reading with Targets = target value :: reading.Targets }))

    let private filter (option: string) =
        valued option "value" false always (fun value reading -> Ok { reading with Filters = filterTerm option value :: reading.Filters })

    /// <summary>-S and -G take the next argument whatever it looks like, or a value joined on.</summary>
    let private pickaxe (option: string) : OptionReader =
        fun args reading ->
            let add value reading =
                { reading with
                    Filters = ("diff:" + quoteTerm value) :: reading.Filters
                    Options = if option = "-G" then { reading.Options with SearchUseRegex = true } else reading.Options }
            match args with
            | arg :: value :: rest when arg = option -> Some(Ok(add value reading, rest))
            | arg :: rest when arg.StartsWith option && arg.Length > 2 -> Some(Ok(add (arg.Substring 2) reading, rest))
            | _ -> None

    let private readers: OptionReader list =
        [ flag [ "--help"; "-h" ] (withOptions (fun o -> { o with HelpRequested = true }))
          flag [ "--version"; "-v" ] (withOptions (fun o -> { o with VersionRequested = true }))
          flag [ "--all" ] (fun reading -> { reading with HasAll = true })
          flag [ "--show-branch-refs" ] (withOptions (fun o -> { o with ShowBranchRefs = true }))
          flag [ "--hide-branch-refs" ] (withOptions (fun o -> { o with ShowBranchRefs = false }))
          flag [ "--show-stashes" ] (withOptions (fun o -> { o with ShowStashes = true }))
          flag [ "--hide-stashes" ] (withOptions (fun o -> { o with ShowStashes = false }))
          valued "--diff-context" "diff context line count" false always
              (fun value reading ->
                  Parse.int value
                  |> Result.orError (InvalidValue("diff context line count", value))
                  |> Result.map (fun lines -> withOptions (fun o -> { o with DiffContextLines = max 0 lines }) reading))
          valued "--diff-presentation" "diff presentation mode" false always
              (parsedOption "diff presentation mode" DiffLayout.tryParse (fun layout o -> { o with DiffLayout = layout }))
          valued "--search" "search query" true always (fun value -> setOption (fun o -> { o with SearchQuery = value }) value)
          valued "--search-scope" "search scope" false always
              (parsedOption "search scope" tryParseSearchScopeKey (fun key o -> { o with SearchScopeKey = key }))
          valued "--select" "revision" false always (fun value -> setOption (fun o -> { o with SelectedCommitHash = Some value }) value)
          valued "--select-commit" "revision" false always (fun value -> setOption (fun o -> { o with SelectedCommitHash = Some value }) value)
          valued "--log" "log file" false always (fun value -> setOption (fun o -> { o with LogFile = Some value }) value)
          tip "--branch" "branch name" Branch
          tip "--sha" "commit hash" Sha
          tip "--tag" "tag name" Tag
          filter "--author"
          filter "--grep"
          filter "--since"
          filter "--after"
          filter "--until"
          filter "--before"
          pickaxe "-S"
          pickaxe "-G" ]

    let rec private read (reading: Reading) (args: string list) : Result<Reading, StartupError> =
        match args with
        | [] -> Ok reading
        | "--" :: paths -> Ok { reading with Paths = paths }
        | _ ->
            match readers |> List.tryPick (fun reader -> reader args reading) with
            | Some(Ok(next, rest)) -> read next rest
            | Some(Error error) -> Error error
            | None ->
                match args with
                | arg :: rest when not (arg.StartsWith "-") || (arg.StartsWith "^" && arg.Length > 1) ->
                    read { reading with Positionals = arg :: reading.Positionals } rest
                | arg :: _ -> Error(UnrecognizedArgument arg)
                | [] -> Ok reading

    /// <summary>Options with what was read turned into history targets and the search query.</summary>
    let private finish (reading: Reading) =
        // A single bare hash selects that commit in the normal history; anything else names history tips.
        let isHash (value: string) = value.Length >= 4 && value.Length <= 40 && value |> Seq.forall Uri.IsHexDigit
        let explicitTargets = List.rev reading.Targets
        let positionals = List.rev reading.Positionals
        let selected, targets =
            match positionals with
            | [ single ] when isHash single && explicitTargets.IsEmpty ->
                reading.Options.SelectedCommitHash |> Option.orElse (Some single), explicitTargets
            | many -> reading.Options.SelectedCommitHash, explicitTargets @ List.collect revisionTargets many
        let pathTargets = reading.Paths |> List.map Path
        let exclusions = targets |> List.filter (function Exclude _ | ExcludeMergeBase _ -> true | _ -> false)
        let filters = List.rev reading.Filters
        let query = reading.Options.SearchQuery
        { reading.Options with
            StartupTargets = (if reading.HasAll then All :: exclusions else targets) @ pathTargets
            SelectedCommitHash = selected
            SearchQuery = ([ if not (String.IsNullOrWhiteSpace query) then query.Trim() ] @ filters) |> String.concat " "
            ShowOnlyMatches = not filters.IsEmpty }

    /// <summary>Reads GitKay's command line: gitk- and git-log-style options, revisions, ranges and -- paths.</summary>
    let parseStartupOptions (args: string array) : Result<StartupOptions, StartupError> =
        let empty = { Options = defaultStartupOptions; HasAll = false; Targets = []; Positionals = []; Paths = []; Filters = [] }
        read empty (List.ofArray args) |> Result.map finish

    let parseStartupTargets args =
        parseStartupOptions args |> Result.map _.StartupTargets

    let parseSearchScope value = GitSearch.parseMode value
