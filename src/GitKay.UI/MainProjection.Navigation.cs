using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GitKay.UI;

public enum PaletteMode {
    Commands,
    Refs,
    Files,
}

/// <summary>One palette entry: a command, a ref/commit to jump to, or a changed file.</summary>
public sealed class PaletteItem {
    public PaletteItem(string title, string detail, string glyph, Action run, string? shortcut = null) {
        Title = title;
        Detail = detail;
        Glyph = glyph;
        Run = run;
        Shortcut = shortcut ?? "";
    }

    public string Title { get; }
    public string Detail { get; }
    public string Glyph { get; }
    public string Shortcut { get; }
    public bool HasShortcut => Shortcut.Length > 0;
    public Action Run { get; }
}

public partial class MainProjection {
    // ----- View preferences persisted across restarts. -----

    public IReadOnlyDictionary<string, string> CaptureViewPreferences() => new Dictionary<string, string> {
        ["diffFileTreeMode"] = IsDiffFileTreeMode.ToString(),
        ["diffFileAllMode"] = IsAllFilesMode.ToString(),
        ["diffFontSize"] = DiffFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["commitDetailsExpanded"] = IsCommitDetailsExpanded.ToString(),
        ["searchMode"] = SelectedSearchScope?.Key ?? "commit",
        ["searchRegex"] = SearchUseRegex.ToString(),
        ["findRegex"] = CommitFindUseRegex.ToString(),
        ["hoverToFocus"] = HoverToFocus.ToString(),
    };

    public void ApplyViewPreferences(IReadOnlyDictionary<string, string> preferences) {
        bool Flag(string key) => preferences.TryGetValue(key, out var value) && bool.TryParse(value, out var flag) && flag;
        IsDiffFileTreeMode = Flag("diffFileTreeMode");
        IsAllFilesMode = Flag("diffFileAllMode");
        if (preferences.TryGetValue("diffFontSize", out var fontSize)
            && double.TryParse(fontSize, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var size))
            DiffFontSize = size;
        IsCommitDetailsExpanded = Flag("commitDetailsExpanded");
        CommitFindUseRegex = Flag("findRegex");
        SearchUseRegex = Flag("searchRegex");
        if (preferences.TryGetValue("searchMode", out var mode)) SetSearchMode(mode);
        HoverToFocus = !preferences.TryGetValue("hoverToFocus", out var hover) || !bool.TryParse(hover, out var hoverFlag) || hoverFlag;
    }

    // ----- Back / forward through visited commits (Alt+Left / Alt+Right, Ctrl+O / Ctrl+I). -----

    /// <summary>Commits visited, for back and forward (Alt+Left / Alt+Right).</summary>
    private GitKay.Kit.Navigation<string> _navigation = GitKay.Kit.NavigationModule.empty<string>();
    /// <summary>An explicit jump to a file (file list, Ctrl+P): the diff puts its header at the top.</summary>
    public event Action<DiffFileProjection>? FileJumpRequested;

    /// <summary>The status bar only appears for errors, progress and notices — not for "Loaded N commits".</summary>
    public bool IsStatusVisible => IsSearchPromptOpen || !string.IsNullOrWhiteSpace(Status) && !Status.StartsWith("Loaded", StringComparison.Ordinal);

    /// <summary>The / or ? search prompt is showing in the status bar.</summary>
    public bool IsSearchPromptOpen {
        get => _isSearchPromptOpen;
        set {
            if (_isSearchPromptOpen == value) return;
            _isSearchPromptOpen = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsStatusVisible));
            OnPropertyChanged(nameof(IsStatusError));
        }
    }

    private bool _isSearchPromptOpen;

    /// <summary>The status reports a failure, so the footer should draw attention to it.</summary>
    public bool IsStatusError => !IsSearchPromptOpen && IsErrorStatus(Status);

    internal static bool IsErrorStatus(string status) =>
        status.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
        || status.StartsWith("Could not", StringComparison.OrdinalIgnoreCase)
        || status.StartsWith("Not a Git repository", StringComparison.OrdinalIgnoreCase)
        || status.Contains(" failed", StringComparison.OrdinalIgnoreCase)
        || status.StartsWith("No commit found", StringComparison.OrdinalIgnoreCase);

    partial void OnStatusChanged(string value) {
        OnPropertyChanged(nameof(IsStatusVisible));
        OnPropertyChanged(nameof(IsStatusError));
        if (IsErrorStatus(value)) ErrorStatusRaised?.Invoke();
    }

    /// <summary>A new error status arrived; the window pulses the footer.</summary>
    public event Action? ErrorStatusRaised;

    /// <summary>Pointing at a pane makes it the target for keyboard shortcuts (on by default).</summary>
    [ObservableProperty] private bool _hoverToFocus = true;

    /// <summary>Ctrl held: show key badges on everything with a Ctrl shortcut.</summary>
    [ObservableProperty] private bool _isCtrlHintsVisible;
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoForward;

    private void RecordVisitedCommit(string? hash) {
        if (hash != null) SetNavigation(GitKay.Kit.NavigationModule.visit(hash, _navigation));
    }

    [RelayCommand]
    private void GoBack() => MoveThroughHistory(GitKay.Kit.NavigationModule.back(IsLoadedCommit, _navigation));

    [RelayCommand]
    private void GoForward() => MoveThroughHistory(GitKay.Kit.NavigationModule.forward(IsLoadedCommit, _navigation));

    private Microsoft.FSharp.Core.FSharpFunc<string, bool> IsLoadedCommit =>
        Microsoft.FSharp.Core.FuncConvert.FromFunc<string, bool>(hash => Commits.Any(commit => commit.FullHash == hash));

    private void MoveThroughHistory(Microsoft.FSharp.Core.FSharpOption<Tuple<string, GitKay.Kit.Navigation<string>>>? step) {
        if (step is not { } moved) return;
        // The navigation already points at the commit, so selecting it records no new visit.
        SetNavigation(moved.Value.Item2);
        SelectedCommit = Commits.First(commit => commit.FullHash == moved.Value.Item1);
    }

    private void SetNavigation(GitKay.Kit.Navigation<string> navigation) {
        _navigation = navigation;
        CanGoBack = GitKay.Kit.NavigationModule.canGoBack(navigation);
        CanGoForward = GitKay.Kit.NavigationModule.canGoForward(navigation);
    }

    // ----- Palette: commands (Ctrl+Shift+P or :), refs and commits (Ctrl+G), changed files (Ctrl+P). -----

    [ObservableProperty] private bool _isPaletteOpen;
    [ObservableProperty] private string _paletteQuery = "";
    [ObservableProperty] private PaletteMode _paletteMode;
    [ObservableProperty] private int _paletteSelectedIndex;
    [ObservableProperty] private string _palettePlaceholder = "";
    public ObservableCollection<PaletteItem> PaletteItems { get; } = new();
    private List<PaletteItem> _paletteSource = new();

    /// <summary>Window actions the projection can't perform itself (settings dialog, clipboard, focus).</summary>
    public event Action<string>? WindowCommandRequested;

    [RelayCommand]
    public void OpenPalette(PaletteMode mode) {
        PaletteMode = mode;
        _paletteSource = BuildPaletteItems(mode);
        PalettePlaceholder = mode switch {
            PaletteMode.Refs => "Go to branch, tag or commit (hash or subject)…",
            PaletteMode.Files => "Go to changed file…",
            _ => "Type a command…   (@ for refs, # for files)",
        };
        PaletteQuery = "";
        FilterPalette();
        IsPaletteOpen = true;
    }

    public void ClosePalette() => IsPaletteOpen = false;

    partial void OnPaletteQueryChanged(string value) {
        // Prefixes switch mode without leaving the box, like VS Code.
        var mode = value.StartsWith('>') ? PaletteMode.Commands
            : value.StartsWith('@') ? PaletteMode.Refs
            : value.StartsWith('#') ? PaletteMode.Files
            : (PaletteMode?)null;
        if (mode is { } switched && switched != PaletteMode) {
            PaletteMode = switched;
            _paletteSource = BuildPaletteItems(switched);
        }

        FilterPalette();
    }

    private void FilterPalette() {
        var query = PaletteQuery.TrimStart('>', '@', '#').Trim();
        var candidates = Microsoft.FSharp.Collections.ListModule.OfSeq(_paletteSource.Select(item => (item.Title, item.Detail)));
        PaletteItems.Clear();
        foreach (var index in GitKay.Kit.Fuzzy.rank(60, query, candidates)) PaletteItems.Add(_paletteSource[index]);

        // Refilling the list clears its selection; re-announce even when the index is unchanged.
        PaletteSelectedIndex = -1;
        PaletteSelectedIndex = PaletteItems.Count > 0 ? 0 : -1;
    }

    public void MovePaletteSelection(int delta) {
        if (PaletteItems.Count == 0) return;
        PaletteSelectedIndex = Math.Clamp(PaletteSelectedIndex + delta, 0, PaletteItems.Count - 1);
    }

    public void RunPaletteItem(PaletteItem? item = null) {
        item ??= PaletteSelectedIndex >= 0 && PaletteSelectedIndex < PaletteItems.Count ? PaletteItems[PaletteSelectedIndex] : null;
        if (item == null) return;
        IsPaletteOpen = false;
        item.Run();
    }

    private List<PaletteItem> BuildPaletteItems(PaletteMode mode) => mode switch {
        PaletteMode.Refs => BuildRefItems(),
        PaletteMode.Files => SelectedDiffFiles
            .Select(file => new PaletteItem(file.Key.NewPath == "/dev/null" ? file.Key.OldPath : file.Key.NewPath,
                $"{file.ChangeKind} · +{file.AddedLines} −{file.RemovedLines}", "▤", () => { SelectedDiffFile = file; FileJumpRequested?.Invoke(file); }))
            .Concat(UnchangedFilePaths().Select(path =>
                new PaletteItem(path, "unchanged · open whole file", "·", () => ShowWholeFile(new FileTarget(path, path, path, null)))))
            .ToList(),
        _ => BuildCommandItems(),
    };

    private List<PaletteItem> BuildRefItems() {
        var items = new List<PaletteItem>();
        foreach (var commit in Commits) {
            foreach (var reference in commit.RefBadges) {
                var target = commit;
                items.Add(new PaletteItem(reference.Text, $"{reference.Kind.ToString().ToLowerInvariant()} · {commit.Hash} {commit.Subject}", "⎇", () => SelectedCommit = target));
            }
        }

        foreach (var commit in Commits.Where(commit => !commit.IsWorkingTree)) {
            var target = commit;
            items.Add(new PaletteItem($"{commit.Hash}  {commit.Subject}", $"{commit.Author} · {commit.Date}", "●", () => SelectedCommit = target));
        }

        return items;
    }

    private List<PaletteItem> BuildCommandItems() {
        PaletteItem Command(string title, string detail, Action run, string? shortcut = null) => new(title, detail, "›", run, shortcut);
        var items = new List<PaletteItem>
        {
            Command("Go to branch, tag or commit…", "Jump anywhere in history", () => OpenPalette(PaletteMode.Refs), "Ctrl+G"),
            Command("Go to changed file…", "Jump to a file in this commit's diff", () => OpenPalette(PaletteMode.Files), "Ctrl+P"),
            Command("Go back", "Previously viewed commit", GoBack, "Alt+←"),
            Command("Go forward", "Next viewed commit", GoForward, "Alt+→"),
            Command("Go to parent commit", "First parent", () => GoToParent(0), "p"),
            Command("Go to second parent", "Merge commits", () => GoToParent(1), "Shift+P"),
            Command("Go to child commit", "First child", GoToChild, "c"),
            Command("Next matching commit", "Commit search", () => FindNextCommitCommand.Execute(null), "n"),
            Command("Previous matching commit", "Commit search", () => FindPreviousCommitCommand.Execute(null), "N"),
            Command("Search commits…", "Focus the commit search", () => WindowCommandRequested?.Invoke("focus-search"), "Ctrl+F"),
            Command("Find in diff…", "Focus the diff search", () => WindowCommandRequested?.Invoke("focus-find"), "Ctrl+F"),
            Command("Search mode: Commit", "Headline, message, hash and branch names", () => SetSearchMode("commit")),
            Command("Search mode: Path", "Files the commit changed", () => SetSearchMode("path")),
            Command("Search mode: Diff", "Lines the commit added or removed", () => SetSearchMode("diff")),
            Command(SearchUseRegex ? "Turn off regex search" : "Turn on regex search", "Commit search", () => SearchUseRegex = !SearchUseRegex),
            Command(ShowOnlySearchMatches ? "Show all commits" : "Show only matching commits", "Commit list filter", () => ShowOnlySearchMatches = !ShowOnlySearchMatches),
            Command(IsAdvancedSearchExpanded ? "Hide advanced search" : "Show advanced search", "One input per field", () => IsAdvancedSearchExpanded = !IsAdvancedSearchExpanded),
            Command("Clear commit search", "", ClearSearch),
            Command("View: unified diff", "Changes inline", () => SetDiffPresentationMode("diff")),
            Command("View: side-by-side", "Old left, new right", () => SetDiffPresentationMode("side-by-side")),
            Command("View: new file", "After the change", () => SetDiffPresentationMode("new")),
            Command("View: old file", "Before the change", () => SetDiffPresentationMode("old")),
            Command("Files: patch list", "Changed files as full paths", () => SetDiffFileListMode("patch")),
            Command("Files: tree", "Changed files grouped by folder", () => SetDiffFileListMode("tree")),
            Command("Files: all files", "Every file in the commit, as a tree", () => SetDiffFileListMode("all")),
            Command("Zoom diff in", "Larger diff text", () => ZoomDiff(1), "Ctrl+="),
            Command("Zoom diff out", "Smaller diff text", () => ZoomDiff(-1), "Ctrl+-"),
            Command("Reset diff zoom", "", () => ZoomDiff(0), "Ctrl+0"),
            Command("Open repository…", "Open another local Git repository", () => WindowCommandRequested?.Invoke("open-repository")),
            Command("Fetch all remotes", "git fetch --all --prune", () => WindowCommandRequested?.Invoke("fetch-all")),
            Command("Collapse all files", "Show only file headers", () => SetAllFilesCollapsed(true)),
            Command("Expand all files", "", () => SetAllFilesCollapsed(false)),
            Command(IsCommitDetailsExpanded ? "Hide commit details" : "Show commit details", "Full message, parents and children", ToggleCommitDetails),
            Command(ShowBranchRefs ? "Hide branch markers" : "Show branch markers", "Commit list", () => ShowBranchRefs = !ShowBranchRefs),
            Command(ShowStashes ? "Hide stashes" : "Show stashes", "Commit list", () => ShowStashes = !ShowStashes),
            Command(IsAllBranches ? "History: current branch only" : "History: all branches", IsAllBranches ? "What HEAD reaches, like gitk" : "Every branch, tag and remote, like --all", () => IsAllBranches = !IsAllBranches),
            Command("Copy commit hash", SelectedCommit?.FullHash ?? "", () => WindowCommandRequested?.Invoke("copy-hash"), "y"),
            Command("Copy commit subject", SelectedCommit?.Subject ?? "", () => WindowCommandRequested?.Invoke("copy-subject"), "Y"),
            Command("Commit…", "Stage changes and commit, like git gui", () => WindowCommandRequested?.Invoke("commit-window"), "Ctrl+Shift+C"),
            Command("Reread refs", "Reload history from the repository", RereadRefs, "F5"),
            Command("Keyboard shortcuts", "", () => IsShortcutHelpOpen = true, "F1"),
            Command("Settings…", "", () => WindowCommandRequested?.Invoke("settings")),
            Command("Show diagnostics", "Live Axial fibers, flows, failures, messages and log", () => WindowCommandRequested?.Invoke("diagnostics"), "Ctrl+Shift+D"),
        };

        if (HasHistoryPathFilter)
            items.Add(Command("Clear file filter", HistoryPathFilter, ClearHistoryPathFilter));

        foreach (var count in DiffContextLineCounts) {
            var option = count;
            items.Add(Command($"Context lines: {option.Label}", "Unchanged lines around each change", () => SelectedDiffContextLineCount = option));
        }

        return items;
    }

    private void SetAllFilesCollapsed(bool collapsed) {
        foreach (var file in SelectedDiffFiles) {
            file.IsCollapsed = collapsed;
        }

        RenderSelectedDiffRows();
    }
}
