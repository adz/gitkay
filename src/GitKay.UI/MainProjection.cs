using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Microsoft.FSharp.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Elmish.Glue.Core;
using GitKay.Core;

namespace GitKay.UI;

public sealed class DiffPresentationModeProjection(GitKay.Core.DiffLayout layout) {
    public GitKay.Core.DiffLayout Layout { get; } = layout;
    public string Key => GitKay.Core.DiffLayoutModule.key(Layout);
    public string Label => GitKay.Core.DiffLayoutModule.label(Layout);
}

public sealed class ThemeModeProjection(GitKay.Core.ThemeMode mode) {
    public GitKay.Core.ThemeMode Mode { get; } = mode;
    public string Key => GitKay.Core.ThemeModeModule.key(Mode);
    public string Label => GitKay.Core.ThemeModeModule.label(Mode);
}

public sealed class PaneFocusEffectProjection(GitKay.Core.PaneFocusEffect effect) {
    public GitKay.Core.PaneFocusEffect Effect { get; } = effect;
    public string Label => GitKay.Core.PaneFocusEffectModule.label(Effect);
    public string Description => GitKay.Core.PaneFocusEffectModule.describe(Effect);
}

public sealed class PaneEffectColorProjection(GitKay.Core.PaneEffectColor color) {
    public GitKay.Core.PaneEffectColor Color { get; } = color;
    public string Label => GitKay.Core.PaneEffectColorModule.label(Color);
    /// <summary>A swatch for the settings list; the accent follows the theme, so it shows as the accent brush.</summary>
    public Avalonia.Media.IBrush Swatch =>
        GitKay.Core.PaneEffectColorModule.hex(Color) is { } hex && Avalonia.Media.Color.TryParse(hex.Value, out var parsed)
            ? new Avalonia.Media.SolidColorBrush(parsed)
            : Avalonia.Application.Current is { } app
              && app.Resources.TryGetResource("GitKayAccentBrush", app.ActualThemeVariant, out var accent)
              && accent is Avalonia.Media.IBrush accentBrush
                ? accentBrush
                : Avalonia.Media.Brushes.SteelBlue;
}

public sealed class ChromeBackgroundProjection(GitKay.Core.ChromeBackground background) {
    public GitKay.Core.ChromeBackground Background { get; } = background;
    public string Label { get; } = GitKay.Core.ChromeBackgroundModule.label(background);
    public string Description { get; } = GitKay.Core.ChromeBackgroundModule.describe(background);
}

public sealed class PaneBorderStyleProjection(GitKay.Core.PaneBorderStyle style) {
    public GitKay.Core.PaneBorderStyle Style { get; } = style;
    public string Label { get; } = GitKay.Core.PaneBorderStyleModule.label(style);
}

public sealed class PaneEffectIntensityProjection(GitKay.Core.PaneEffectIntensity intensity) {
    public GitKay.Core.PaneEffectIntensity Intensity { get; } = intensity;
    public string Label => GitKay.Core.PaneEffectIntensityModule.label(Intensity);
}

public sealed class DiffContextLineCountProjection {
    public DiffContextLineCountProjection(int count) {
        Count = count;
        Label = count == 1 ? "1 line" : $"{count} lines";
    }

    public int Count { get; }
    public string Label { get; }
}

public partial class MainProjection : ObservableObject, IProjection<GitKay.Core.App.Model, GitKay.Core.App.Msg> {
    private readonly long _createdAtTicks = Stopwatch.GetTimestamp();
    private bool _firstPaintLogged;
    private bool _suppressSelectionDispatch;
    private bool _suppressDiffSelectionSync;
    private bool _suppressSearchDispatch;
    private bool _suppressSearchSelectionDispatch;
    private bool _suppressShowBranchRefsDispatch;
    private bool _suppressShowStashesDispatch;
    private bool _suppressDiffContextDispatch;
    private bool _suppressIgnoreWhitespaceDispatch;
    private bool _suppressDiffPresentationDispatch;
    private object? _commitsSource;
    private object? _commitSearchResultsSource;
    private string? _selectedDiffHash;
    private object? _selectedDiffFilesSource;
    private DiffFileKey? _selectedDiffFileKey;
    private object? _selectedDiffFileSource;
    private object? _diffExpansionsSource;
    private object? _searchResultsSource;
    private string? _selectedSearchResultHash;
    /// <summary>What the applied commit search marks in the diff, and the search it was built from.</summary>
    private GitKay.Core.GitSearch.DiffMark _diffMark = GitKay.Core.GitSearch.noDiffMark;
    private string? _diffMarkKey;
    private CancellationTokenSource? _searchDebounceCancellation;

    public ObservableCollection<DiffPresentationModeProjection> DiffPresentationModes { get; } =
        new(GitKay.Core.DiffLayoutModule.all.Select(layout => new DiffPresentationModeProjection(layout)));

    public ObservableCollection<ThemeModeProjection> ThemeModes { get; } =
        new(GitKay.Core.ThemeModeModule.all.Select(mode => new ThemeModeProjection(mode)));

    public ObservableCollection<PaneFocusEffectProjection> PaneFocusEffects { get; } =
        new(GitKay.Core.PaneFocusEffectModule.all.Select(effect => new PaneFocusEffectProjection(effect)));

    public ObservableCollection<PaneEffectColorProjection> PaneEffectColors { get; } =
        new(GitKay.Core.PaneEffectColorModule.all.Select(color => new PaneEffectColorProjection(color)));

    public ObservableCollection<PaneEffectIntensityProjection> PaneHoverIntensities { get; } =
        new(GitKay.Core.PaneEffectIntensityModule.all.Select(intensity => new PaneEffectIntensityProjection(intensity)));

    /// <summary>Every installed font, for the picker; read from the system the first time settings are opened.</summary>
    public IReadOnlyList<InstalledFont> InstalledFonts => FontCatalog.All;

    /// <summary>Only the fixed-width families, for anything that lines up in columns.</summary>
    public IReadOnlyList<InstalledFont> InstalledMonoFonts => FontCatalog.Monospaced;

    public ObservableCollection<PaneBorderStyleProjection> PaneBorderStyles { get; } =
        new(GitKay.Core.PaneBorderStyleModule.all.Select(style => new PaneBorderStyleProjection(style)));

    /// <summary>The border's own colour list, the same choices the effect uses.</summary>
    public ObservableCollection<PaneEffectColorProjection> PaneBorderColors { get; } =
        new(GitKay.Core.PaneEffectColorModule.all.Select(color => new PaneEffectColorProjection(color)));

    public ObservableCollection<ChromeBackgroundProjection> ChromeBackgrounds { get; } =
        new(GitKay.Core.ChromeBackgroundModule.all.Select(background => new ChromeBackgroundProjection(background)));

    /// <summary>The chrome tint's colour list, the same choices the effect and border use.</summary>
    public ObservableCollection<PaneEffectColorProjection> ChromeColors { get; } =
        new(GitKay.Core.PaneEffectColorModule.all.Select(color => new PaneEffectColorProjection(color)));

    public ObservableCollection<DiffContextLineCountProjection> DiffContextLineCounts { get; } = new()
    {
        new DiffContextLineCountProjection(0),
        new DiffContextLineCountProjection(1),
        new DiffContextLineCountProjection(2),
        new DiffContextLineCountProjection(3),
        new DiffContextLineCountProjection(5),
        new DiffContextLineCountProjection(10),
        new DiffContextLineCountProjection(20),
    };

    public ObservableCollection<SearchScopeProjection> SearchScopes { get; } = new()
    {
        // Commit is instant; Path and Diff read each commit's diff.
        new SearchScopeProjection("commit", "Commit", "Headline, message, hash or branch — or author:, after:…"),
        new SearchScopeProjection("path", "Path", "File or folder, e.g. src/GitKay.UI"),
        new SearchScopeProjection("diff", "Diff", "Text added or removed by the commit"),
    };

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isInitialLoading = true;
    [ObservableProperty] private string _commitRowFontFamily = GitKay.Core.SettingsModule.defaults.CommitRowFontFamily;
    [ObservableProperty] private string _commitRowMonoFontFamily = GitKay.Core.SettingsModule.defaults.CommitRowMonoFontFamily;
    [ObservableProperty] private double _commitRowTextFontSize = GitKay.Core.SettingsModule.defaults.CommitRowTextFontSize;
    [ObservableProperty] private double _commitRowMetaFontSize = GitKay.Core.SettingsModule.defaults.CommitRowMetaFontSize;
    [ObservableProperty] private double _commitRowBadgeFontSize = GitKay.Core.SettingsModule.defaults.CommitRowBadgeFontSize;
    [ObservableProperty] private bool _showBranchRefs;
    [ObservableProperty] private bool _showStashes;
    [ObservableProperty] private int _diffContextLineCount = GitKay.Core.SettingsModule.defaults.DiffContextLines;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private double _searchDebounceSeconds = GitKay.Core.SettingsModule.defaults.SearchDebounceSeconds;
    [ObservableProperty] private bool _renderMarkdownByDefault = GitKay.Core.SettingsModule.defaults.RenderMarkdownByDefault;
    [ObservableProperty] private bool _renderedMarkdownChangesOnly;
    [ObservableProperty] private bool _loadRemoteMarkdownImages = GitKay.Core.SettingsModule.defaults.LoadRemoteMarkdownImages;
    [ObservableProperty] private string _commitFindQuery = "";
    [ObservableProperty] private SearchScopeProjection? _selectedSearchScope;
    [ObservableProperty] private bool _hasSearchResults;
    [ObservableProperty] private bool _isSearchPanelExpanded;
    [ObservableProperty] private DiffPresentationModeProjection? _selectedDiffPresentationMode;
    [ObservableProperty] private DiffContextLineCountProjection? _selectedDiffContextLineCount;
    [ObservableProperty] private ThemeModeProjection? _selectedThemeMode;
    [ObservableProperty] private PaneFocusEffectProjection? _selectedPaneFocusEffect;
    [ObservableProperty] private PaneEffectColorProjection? _selectedPaneEffectColor;
    [ObservableProperty] private PaneEffectIntensityProjection? _selectedPaneEffectIntensity;
    [ObservableProperty] private PaneBorderStyleProjection? _selectedPaneBorderStyle;
    [ObservableProperty] private PaneEffectColorProjection? _selectedPaneBorderColor;
    [ObservableProperty] private ChromeBackgroundProjection? _selectedChromeBackground;
    [ObservableProperty] private PaneEffectColorProjection? _selectedChromeColor;

    /// <summary>Space around each pane, in pixels.</summary>
    [ObservableProperty] private double _paneGap = GitKay.Core.SettingsModule.defaults.PaneGap;

    /// <summary>Whether each pane is outlined.</summary>
    [ObservableProperty] private bool _paneBorder = GitKay.Core.SettingsModule.defaults.PaneBorder;

    /// <summary>Whether the pane holding the keys is shown at all.</summary>
    [ObservableProperty] private bool _paneDimUnfocused = GitKay.Core.SettingsModule.defaults.PaneDimUnfocused;

    /// <summary>Whether the focused pane's edge is brightened.</summary>
    [ObservableProperty] private bool _paneFocusHighlight = GitKay.Core.SettingsModule.defaults.PaneFocusHighlight;

    /// <summary>Whether the hairline between panes is hidden, leaving the splitter to be felt rather than seen.</summary>
    [ObservableProperty] private bool _splitterLinesHidden = GitKay.Core.SettingsModule.defaults.SplitterLinesHidden;

    /// <summary>The outline's thickness, for a custom border.</summary>
    [ObservableProperty] private double _paneBorderThickness = GitKay.Core.SettingsModule.defaults.PaneBorderThickness;

    partial void OnPaneBorderChanged(bool value) => PaneChromeChanged?.Invoke();

    partial void OnPaneDimUnfocusedChanged(bool value) => PaneChromeChanged?.Invoke();

    partial void OnPaneFocusHighlightChanged(bool value) => PaneChromeChanged?.Invoke();

    partial void OnSplitterLinesHiddenChanged(bool value) => PaneChromeChanged?.Invoke();

    partial void OnHoverToFocusChanged(bool value) => PaneChromeChanged?.Invoke();

    partial void OnPaneBorderThicknessChanged(double value) {
        var clamped = Math.Clamp(Math.Round(value), 1, 4);
        if (Math.Abs(clamped - value) > 0.001) {
            PaneBorderThickness = clamped;
            return;
        }

        PaneChromeChanged?.Invoke();
    }

    partial void OnSelectedPaneBorderStyleChanged(PaneBorderStyleProjection? value) {
        OnPropertyChanged(nameof(PaneBorderStyle));
        OnPropertyChanged(nameof(IsCustomPaneBorder));
        PaneChromeChanged?.Invoke();
    }

    partial void OnSelectedPaneBorderColorChanged(PaneEffectColorProjection? value) {
        OnPropertyChanged(nameof(PaneBorderColor));
        PaneChromeChanged?.Invoke();
    }

    partial void OnSelectedChromeBackgroundChanged(ChromeBackgroundProjection? value) {
        OnPropertyChanged(nameof(ChromeBackground));
        OnPropertyChanged(nameof(IsTintedChrome));
        PaneChromeChanged?.Invoke();
    }

    partial void OnSelectedChromeColorChanged(PaneEffectColorProjection? value) {
        OnPropertyChanged(nameof(ChromeColor));
        PaneChromeChanged?.Invoke();
    }

    public GitKay.Core.ChromeBackground ChromeBackground => SelectedChromeBackground?.Background ?? GitKay.Core.SettingsModule.defaults.ChromeBackground;
    public GitKay.Core.PaneEffectColor ChromeColor => SelectedChromeColor?.Color ?? GitKay.Core.SettingsModule.defaults.ChromeColor;

    /// <summary>Whether the chrome's colour is the user's to choose.</summary>
    public bool IsTintedChrome => ChromeBackground.IsTintedChrome;

    public GitKay.Core.PaneBorderStyle PaneBorderStyle => SelectedPaneBorderStyle?.Style ?? GitKay.Core.SettingsModule.defaults.PaneBorderStyle;
    public GitKay.Core.PaneEffectColor PaneBorderColor => SelectedPaneBorderColor?.Color ?? GitKay.Core.SettingsModule.defaults.PaneBorderColor;

    /// <summary>Whether the border's colour and thickness are the user's to choose.</summary>
    public bool IsCustomPaneBorder => PaneBorderStyle.IsCustomBorder;

    /// <summary>The gap as a margin, for binding to each pane.</summary>
    public Avalonia.Thickness PaneGapThickness => new(PaneGap);
    public GitKay.Core.PaneFocusEffect PaneFocusEffect => SelectedPaneFocusEffect?.Effect ?? GitKay.Core.SettingsModule.defaults.PaneFocusEffect;
    public GitKay.Core.PaneEffectColor PaneEffectColor => SelectedPaneEffectColor?.Color ?? GitKay.Core.SettingsModule.defaults.PaneEffectColor;
    public GitKay.Core.PaneEffectIntensity PaneEffectIntensity => SelectedPaneEffectIntensity?.Intensity ?? GitKay.Core.SettingsModule.defaults.PaneEffectIntensity;

    partial void OnPaneGapChanged(double value) {
        var clamped = Math.Clamp(Math.Round(value), 0, 8);
        if (Math.Abs(clamped - value) > 0.001) {
            PaneGap = clamped;
            return;
        }
        OnPropertyChanged(nameof(PaneGapThickness));
        PaneChromeChanged?.Invoke();
    }

    partial void OnSelectedPaneFocusEffectChanged(PaneFocusEffectProjection? value) {
        OnPropertyChanged(nameof(PaneFocusEffect));
        PaneChromeChanged?.Invoke();
    }

    partial void OnSelectedPaneEffectColorChanged(PaneEffectColorProjection? value) {
        OnPropertyChanged(nameof(PaneEffectColor));
        PaneChromeChanged?.Invoke();
    }

    partial void OnSelectedPaneEffectIntensityChanged(PaneEffectIntensityProjection? value) {
        OnPropertyChanged(nameof(PaneEffectIntensity));
        PaneChromeChanged?.Invoke();
    }

    /// <summary>Raised when the pane gap or hover effect changes, so windows can restyle their panes.</summary>
    public event Action? PaneChromeChanged;
    [ObservableProperty] private string _selectedDiffPresentationModeLabel = "Diff";
    [ObservableProperty] private bool _isRevisionComparison;
    [ObservableProperty] private string _comparisonBaseRevision = "";
    [ObservableProperty] private string _comparisonTargetRevision = "";
    /// <summary>The configured per-repository base used by branch badge double-clicks.</summary>
    [ObservableProperty] private string _comparisonBase = "";
    [ObservableProperty] private bool _isCommitDetailsExpanded;
    [ObservableProperty] private bool _isDiffFileTreeMode;
    /// <summary>Commit search position, e.g. "3 of 27", "Searching…" or "No matches".</summary>
    [ObservableProperty] private string _commitSearchStatusText = "";
    [ObservableProperty] private string _searchPlaceholder = "Headline, message, hash or branch — or author:, after:…";
    [ObservableProperty] private bool _searchUseRegex;
    [ObservableProperty] private bool _commitFindUseRegex;
    [ObservableProperty] private bool _isAdvancedSearchExpanded;
    /// <summary>Hide non-matching commits instead of only showing matches in bold.</summary>
    [ObservableProperty] private bool _showOnlySearchMatches;
    /// <summary>A commit search has (or is producing) results; with show-only-matches, zero results means an empty list.</summary>
    [ObservableProperty] private bool _isCommitSearchActive;
    [ObservableProperty] private bool _isRecentSearchesOpen;
    [ObservableProperty] private bool _isShortcutHelpOpen;

    [RelayCommand]
    private void ToggleShortcutHelp() => IsShortcutHelpOpen = !IsShortcutHelpOpen;
    public ObservableCollection<string> RecentSearchMatches { get; } = new();
    private Microsoft.FSharp.Collections.FSharpList<string> _recentSearches = Microsoft.FSharp.Collections.FSharpList<string>.Empty;
    public bool IsCommitSearchMode => SelectedSearchScope?.Key is null or "commit";
    public bool IsPathSearchMode => SelectedSearchScope?.Key == "path";
    public bool IsDiffSearchMode => SelectedSearchScope?.Key == "diff";
    public bool HasSearchNavigation => !string.IsNullOrEmpty(CommitSearchStatusText);
    /// <summary>Find-in-diff position, e.g. "2 of 14" or "No matches".</summary>
    [ObservableProperty] private string _commitFindStatusText = "";
    /// <summary>The applied commit search's diff term, highlighted in the diff and borrowed by an empty find box.</summary>
    [ObservableProperty] private string _commitSearchDiffTerm = "";
    [ObservableProperty] private bool _commitSearchDiffTermUseRegex;
    [ObservableProperty] private string _commitSearchPathTerm = "";
    [ObservableProperty] private GitKay.Core.GitSearch.Highlight? _commitSearchHighlight;
    /// <summary>Parent and child commits of the selection, for links and p / c navigation.</summary>
    public ObservableCollection<CommitLinkProjection> SelectedCommitParents { get; } = new();
    public ObservableCollection<CommitLinkProjection> SelectedCommitChildren { get; } = new();
    private Dictionary<string, List<string>> _childrenByParent = new(StringComparer.Ordinal);
    private object? _childrenSource;
    [ObservableProperty] private string _commitFindPlaceholder = "Find in diff  (Ctrl+F or /)";
    private bool _revealSearchMatchInDiff;
    private string? _commitSearchHighlightKey;
    private bool _startupFilterApplied;
    private string? _lastRunSearchQuery;

    public event Action? RenderedMarkdownChanged;
    public event Action<GitKay.Core.App.WholeFileState>? WholeFileReady;
    public event Action<GitKay.Core.App.WholeFileState>? WholeFileChanged;
    private long _presentedWholeFileRequestId = -1;
    private GitKay.Core.GitService.WholeFilePayload? _presentedWholeFilePayload;
    public ObservableCollection<CommitProjection> Commits { get; } = new();
    public ObservableCollection<SearchResultProjection> SearchResults { get; } = new();
    public ObservableCollection<DiffFileProjection> SelectedDiffFiles { get; } = new();
    public AvaloniaList<IDiffRowProjection> SelectedDiffRows { get; } = new();
    /// <summary>Changed-files list rows: files in patch mode; folders and files in tree mode.</summary>
    public AvaloniaList<object> DiffFileListRows { get; } = new();
    private readonly HashSet<string> _collapsedDiffFolders = new(StringComparer.Ordinal);

    /// <summary>
    /// List selection follows the selected file only. Folder rows are never selected — clicking one toggles it
    /// (<see cref="ToggleDiffFolderCommand"/>) — so a collapsed folder can always be clicked open again.
    /// </summary>
    public object? SelectedDiffFileListRow {
        get => (object?)_selectedRepoFileRow ?? SelectedDiffFile;
        set {
            // Unchanged files have no diff to jump to; the list selects them and Enter or a double-click opens them.
            _selectedRepoFileRow = value as RepoFileRow;
            if (value is DiffFileProjection file) {
                SelectedDiffFile = file;
                FileJumpRequested?.Invoke(file);
            }

            OnPropertyChanged();
        }
    }

    [ObservableProperty] private CommitProjection? _selectedCommit;
    [ObservableProperty] private SearchResultProjection? _selectedSearchResult;
    [ObservableProperty] private DiffFileProjection? _selectedDiffFile;
    [ObservableProperty] private IDiffRowProjection? _selectedDiffRow;
    [ObservableProperty] private IReadOnlyDictionary<string, Bitmap> _renderedOldImages = new Dictionary<string, Bitmap>();
    [ObservableProperty] private IReadOnlyDictionary<string, Bitmap> _renderedNewImages = new Dictionary<string, Bitmap>();
    [ObservableProperty] private bool _renderedImagesLoading;
    private long _appliedRenderedRequestId = -1;
    private GitKay.Core.RenderedMarkdownContent? _appliedRenderedContent;

    public FontFamily CommitRowFont => FontStacks.Resolve(CommitRowFontFamily);
    public FontFamily CommitRowMonoFont => FontStacks.Resolve(CommitRowMonoFontFamily);

    public MainProjection() {
        SelectedDiffPresentationMode = DiffPresentationModes[0];
        SelectedDiffContextLineCount = DiffContextLineCounts.First(option => option.Count == DiffContextLineCount);
        SelectedThemeMode = ThemeModes[0];
        SelectedPaneFocusEffect = PaneFocusEffects.FirstOrDefault(effect => effect.Effect.Equals(GitKay.Core.SettingsModule.defaults.PaneFocusEffect)) ?? PaneFocusEffects[0];
        SelectedPaneEffectColor = PaneEffectColors[0];
        SelectedPaneEffectIntensity = PaneHoverIntensities.FirstOrDefault(intensity => intensity.Intensity.Equals(GitKay.Core.SettingsModule.defaults.PaneEffectIntensity)) ?? PaneHoverIntensities[0];
        SelectedPaneBorderStyle = PaneBorderStyles[0];
        SelectedPaneBorderColor = PaneBorderColors[0];
        SelectedChromeBackground = ChromeBackgrounds.FirstOrDefault(background => background.Background.Equals(GitKay.Core.SettingsModule.defaults.ChromeBackground)) ?? ChromeBackgrounds[0];
        SelectedChromeColor = ChromeColors[0];
    }

    public void ApplySettings(GitKay.Core.Settings settings) {
        var normalized = GitKay.Core.SettingsModule.normalize(settings);

        _suppressShowBranchRefsDispatch = true;
        _suppressShowStashesDispatch = true;
        _suppressDiffContextDispatch = true;
        _suppressDiffPresentationDispatch = true;

        try {
            ShowBranchRefs = normalized.ShowBranchRefs;
            ShowStashes = normalized.ShowStashes;
            CommitRowFontFamily = normalized.CommitRowFontFamily;
            CommitRowMonoFontFamily = normalized.CommitRowMonoFontFamily;
            CommitRowTextFontSize = normalized.CommitRowTextFontSize;
            CommitRowMetaFontSize = normalized.CommitRowMetaFontSize;
            CommitRowBadgeFontSize = normalized.CommitRowBadgeFontSize;
            SearchDebounceSeconds = normalized.SearchDebounceSeconds;
            RenderMarkdownByDefault = normalized.RenderMarkdownByDefault;
            LoadRemoteMarkdownImages = normalized.LoadRemoteMarkdownImages;
            DiffContextLineCount = normalized.DiffContextLines;
            SelectedDiffContextLineCount =
                DiffContextLineCounts.FirstOrDefault(option => option.Count == normalized.DiffContextLines)
                ?? DiffContextLineCounts.First();
            SelectedDiffPresentationMode = PresentationModeFor(normalized.DiffLayout);
            SelectedThemeMode = ThemeModes.FirstOrDefault(mode => mode.Mode.Equals(normalized.Theme)) ?? ThemeModes.First();
            PaneGap = normalized.PaneGap;
            PaneBorder = normalized.PaneBorder;
            SelectedPaneFocusEffect = PaneFocusEffects.FirstOrDefault(effect => effect.Effect.Equals(normalized.PaneFocusEffect)) ?? PaneFocusEffects.First();
            SelectedPaneEffectColor = PaneEffectColors.FirstOrDefault(color => color.Color.Equals(normalized.PaneEffectColor)) ?? PaneEffectColors.First();
            SelectedPaneEffectIntensity = PaneHoverIntensities.FirstOrDefault(intensity => intensity.Intensity.Equals(normalized.PaneEffectIntensity)) ?? PaneHoverIntensities.First();
            HoverToFocus = normalized.HoverFocusesPane;
            PaneDimUnfocused = normalized.PaneDimUnfocused;
            PaneFocusHighlight = normalized.PaneFocusHighlight;
            PaneBorderThickness = normalized.PaneBorderThickness;
            SplitterLinesHidden = normalized.SplitterLinesHidden;
            SelectedPaneBorderStyle = PaneBorderStyles.FirstOrDefault(style => style.Style.Equals(normalized.PaneBorderStyle)) ?? PaneBorderStyles.First();
            SelectedPaneBorderColor = PaneBorderColors.FirstOrDefault(color => color.Color.Equals(normalized.PaneBorderColor)) ?? PaneBorderColors.First();
            SelectedChromeBackground = ChromeBackgrounds.FirstOrDefault(background => background.Background.Equals(normalized.ChromeBackground)) ?? ChromeBackgrounds.First();
            SelectedChromeColor = ChromeColors.FirstOrDefault(color => color.Color.Equals(normalized.ChromeColor)) ?? ChromeColors.First();
        }
        finally {
            _suppressDiffPresentationDispatch = false;
            _suppressDiffContextDispatch = false;
            _suppressShowStashesDispatch = false;
            _suppressShowBranchRefsDispatch = false;
        }
    }

    public GitKay.Core.Settings CaptureSettings() => GitKay.Core.SettingsModule.normalize(new GitKay.Core.Settings(
        ShowBranchRefs,
        ShowStashes,
        DiffContextLineCount,
        DiffLayout,
        CommitRowFontFamily,
        CommitRowMonoFontFamily,
        CommitRowTextFontSize,
        CommitRowMetaFontSize,
        CommitRowBadgeFontSize,
        SearchDebounceSeconds,
        RenderMarkdownByDefault,
        LoadRemoteMarkdownImages,
        SelectedThemeMode?.Mode ?? GitKay.Core.SettingsModule.defaults.Theme,
        PaneGap,
        HoverToFocus,
        PaneDimUnfocused,
        PaneFocusHighlight,
        PaneFocusEffect,
        PaneEffectColor,
        PaneEffectIntensity,
        PaneBorder,
        PaneBorderStyle,
        PaneBorderColor,
        PaneBorderThickness,
        SplitterLinesHidden,
        ChromeBackground,
        ChromeColor));

    /// <summary>The diff layout chosen in the view menu.</summary>
    public GitKay.Core.DiffLayout DiffLayout => SelectedDiffPresentationMode?.Layout ?? GitKay.Core.SettingsModule.defaults.DiffLayout;

    private DiffPresentationModeProjection PresentationModeFor(GitKay.Core.DiffLayout layout) =>
        DiffPresentationModes.FirstOrDefault(mode => mode.Layout.Equals(layout)) ?? DiffPresentationModes.First();

    private static void LogTiming(string message) {
        var line = $"[timing] {message}";
        Trace.WriteLine(line);
    }

    partial void OnRenderedMarkdownChangesOnlyChanged(bool value) {
        foreach (var file in SelectedDiffFiles) file.RenderedChangesOnly = value;
        RefreshDiffRows();
    }

    partial void OnCommitRowFontFamilyChanged(string value) {
        OnPropertyChanged(nameof(CommitRowFont));
    }

    partial void OnCommitRowMonoFontFamilyChanged(string value) {
        OnPropertyChanged(nameof(CommitRowMonoFont));
    }

    public void Update(GitKay.Core.App.Model model) {
        var startedAtTicks = Stopwatch.GetTimestamp();
        Status = model.Status;
        IsInitialLoading = model.Commits.IsEmpty && !model.Status.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
        UpdateHistoryTargets(model.StartupTargets);
        if (ShowBranchRefs != model.ShowBranchRefs) {
            _suppressShowBranchRefsDispatch = true;
            try {
                ShowBranchRefs = model.ShowBranchRefs;
            }
            finally {
                _suppressShowBranchRefsDispatch = false;
            }
        }

        if (ShowStashes != model.ShowStashes) {
            _suppressShowStashesDispatch = true;
            try {
                ShowStashes = model.ShowStashes;
            }
            finally {
                _suppressShowStashesDispatch = false;
            }
        }

        if (model.StartupShowOnlyMatches && !_startupFilterApplied) {
            _startupFilterApplied = true;
            ShowOnlySearchMatches = true;
        }

        if (SearchUseRegex != model.SearchUseRegex) {
            _suppressSearchDispatch = true;
            try { SearchUseRegex = model.SearchUseRegex; } finally { _suppressSearchDispatch = false; }
        }

        IsRevisionComparison = model.RevisionComparison != null;
        ComparisonBaseRevision = model.RevisionComparison?.Value.BaseRevision ?? "";
        ComparisonTargetRevision = model.RevisionComparison?.Value.TargetRevision ?? "";
        UpdateDiffContextState(model);
        UpdateIgnoreWhitespaceState(model);
        UpdateDiffPresentationState(model);
        UpdateSearchState(model);
        UpdateDiffState(model);
        UpdateRenderedMarkdown(model);
        UpdateWholeFile(model);
        UpdateCommits(model);
        UpdateSelectedCommit(model);
        UpdateCommitRelations(model);
        UpdateCommitSearchStatus(model);
        CanUndoDiscard = model.LastDiscard != null;

        var elapsed = Stopwatch.GetElapsedTime(startedAtTicks);
        LogTiming($"ui projection elapsed={elapsed.TotalMilliseconds:F1}ms commits={model.Commits.Length} searchResults={SearchResults.Count} diffFiles={SelectedDiffFiles.Count} diffRows={SelectedDiffRows.Count}");
    }

    public void LoadRemoteMarkdownImage(string source) =>
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewLoadRemoteMarkdownImage(source));

    public void ToggleRenderedMarkdown(DiffFileProjection file) {
        var enable = !file.IsRenderedMarkdown;
        var requestId = Stopwatch.GetTimestamp();
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetRenderedMarkdown(
            new GitKay.Core.GitService.DiffFileKey(file.Key.OldPath, file.Key.NewPath), file.Key.Section, enable, LoadRemoteMarkdownImages, requestId));
    }

    private void UpdateRenderedMarkdown(GitKay.Core.App.Model model) {
        if (model.RenderedMarkdown == null) {
            var rendered = SelectedDiffFiles.FirstOrDefault(file => file.IsRenderedMarkdown);
            var hadState = _appliedRenderedRequestId >= 0 || rendered != null || RenderedImagesLoading;
            _appliedRenderedRequestId = -1;
            _appliedRenderedContent = null;
            RenderedImagesLoading = false;
            rendered?.ClearRendered();
            if (!hadState) return;
            foreach (var bitmap in RenderedOldImages.Values) bitmap.Dispose();
            foreach (var bitmap in RenderedNewImages.Values) bitmap.Dispose();
            RenderedOldImages = new Dictionary<string, Bitmap>();
            RenderedNewImages = new Dictionary<string, Bitmap>();
            RefreshDiffRows();
            RenderedMarkdownChanged?.Invoke();
            return;
        }
        var state = model.RenderedMarkdown.Value;
        RenderedImagesLoading = state.Content == null;
        if (state.Content == null || ReferenceEquals(state.Content.Value, _appliedRenderedContent)) return;
        var file = SelectedDiffFiles.FirstOrDefault(candidate => candidate.Key.OldPath == state.Key.OldPath
            && candidate.Key.NewPath == state.Key.NewPath && candidate.Key.Section == state.Section);
        if (file == null) return;
        file.ApplyRenderedContent(state.Content.Value);
        file.RenderedChangesOnly = RenderedMarkdownChangesOnly;
        var oldImages = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        var newImages = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        foreach (var image in state.Content.Value.Images) {
            if (image.Bytes == null) continue;
            try {
                var bitmap = new Bitmap(new System.IO.MemoryStream(image.Bytes.Value));
                (image.Side.IsOld ? oldImages : newImages)[image.Source] = bitmap;
            }
            catch { }
        }
        foreach (var bitmap in RenderedOldImages.Values) bitmap.Dispose();
        foreach (var bitmap in RenderedNewImages.Values) bitmap.Dispose();
        RenderedOldImages = oldImages;
        RenderedNewImages = newImages;
        _appliedRenderedRequestId = state.RequestId;
        _appliedRenderedContent = state.Content.Value;
        RefreshDiffRows();
        RenderedMarkdownChanged?.Invoke();
    }

    private void UpdateWholeFile(GitKay.Core.App.Model model) {
        if (model.WholeFile == null || model.WholeFile.Value.Payload == null) return;
        var state = model.WholeFile.Value;
        if (ReferenceEquals(state.Payload.Value, _presentedWholeFilePayload)) return;
        _presentedWholeFilePayload = state.Payload.Value;
        if (state.RequestId == _presentedWholeFileRequestId) WholeFileChanged?.Invoke(state);
        else {
            _presentedWholeFileRequestId = state.RequestId;
            WholeFileReady?.Invoke(state);
        }
    }

    public void RequestWholeFile(FileTarget target, bool preview = false) {
        var requestId = Stopwatch.GetTimestamp();
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewOpenWholeFile(
            new GitKay.Core.GitService.DiffFileKey(target.OldPath, target.NewPath), target.Changed?.Key.Section ?? "",
            preview, LoadRemoteMarkdownImages, requestId));
    }

    public void DismissWholeFile(long requestId) => _dispatch?.Invoke(GitKay.Core.App.Msg.NewDismissWholeFile(requestId));

    private void UpdateSearchState(GitKay.Core.App.Model model) {
        var searchResultsSource = model.SearchResults != null ? (object?)model.SearchResults.Value : null;

        if (!string.Equals(SearchQuery, model.SearchQuery, StringComparison.Ordinal)) {
            _suppressSearchDispatch = true;
            try {
                SearchQuery = model.SearchQuery;
            }
            finally {
                _suppressSearchDispatch = false;
            }
        }

        var selectedScope = SearchScopes.FirstOrDefault(scope => scope.Key == model.SearchScopeKey) ?? SearchScopes.FirstOrDefault();
        if (!ReferenceEquals(SelectedSearchScope, selectedScope)) {
            _suppressSearchDispatch = true;
            try {
                SelectedSearchScope = selectedScope;
            }
            finally {
                _suppressSearchDispatch = false;
            }
        }

        var shouldAutoOpenSearchPanel =
            !string.IsNullOrWhiteSpace(model.SearchQuery)
            || model.SearchResults != null;

        if (shouldAutoOpenSearchPanel) {
            IsSearchPanelExpanded = true;
        }

        var searchResultsChanged = !ReferenceEquals(_searchResultsSource, searchResultsSource);

        if (model.SearchResults == null) {
            if (SearchResults.Count > 0 || _searchResultsSource != null) {
                SyncSelectedSearchResult(null);
                SearchResults.Clear();
            }

            _searchResultsSource = null;
            _selectedSearchResultHash = null;
            HasSearchResults = false;
            return;
        }

        if (searchResultsChanged) {
            var previousSelectedSearchHash = SelectedSearchResult?.FullHash ?? _selectedSearchResultHash;
            SyncSelectedSearchResult(null);
            SearchResults.Clear();

            foreach (var result in model.SearchResults.Value) {
                var projection = new SearchResultProjection();
                projection.Update(result);
                SearchResults.Add(projection);
            }

            _searchResultsSource = searchResultsSource;
            var selectedSearchResult = ResolveSelectedSearchResult(previousSelectedSearchHash);
            SyncSelectedSearchResult(selectedSearchResult);
            _selectedSearchResultHash = selectedSearchResult?.FullHash;
            HasSearchResults = SearchResults.Count > 0;
        }
    }

    /// <summary>Stands in for a commit hash while the uncommitted changes are shown; never passed to git.</summary>
    private const string WorkingTreeDiffId = "\u0001working-tree";
    private readonly HashSet<string> _collapsedDiffSections = new(StringComparer.Ordinal);

    public bool IsWorkingTreeDiffShown => _selectedDiffHash == WorkingTreeDiffId;

    /// <summary>The diffs the uncommitted view is showing, for mapping rows back onto each file's own diff.</summary>
    private GitKay.Core.GitService.WorkingTreeChanges? _workingTreeChanges;

    /// <summary>A discard that can still be put back.</summary>
    [ObservableProperty] private bool _canUndoDiscard;

    // ----- Staging from the history window's uncommitted view. -----

    private static GitKay.Core.WorkingTree.Section? SectionOf(DiffFileProjection file) =>
        GitKay.Core.WorkingTree.tryParseSection(file.Key.Section) is { } section ? section.Value : null;

    /// <summary>Where each changed line of a file sits in that file's own diff, in order.</summary>
    private List<(int Hunk, int Line)> ChangePositions(DiffFileProjection file) {
        var positions = new List<(int, int)>();
        if (_workingTreeChanges is not { } changes || SectionOf(file) is not { } section) return positions;
        var diffs = GitKay.Core.GitService.workingTreeSections(changes).FirstOrDefault(entry => entry.Item1.Equals(section))?.Item2;
        var diff = diffs?.FirstOrDefault(candidate => candidate.OldPath == file.Key.OldPath && candidate.NewPath == file.Key.NewPath);
        if (diff == null) return positions;
        for (var hunk = 0; hunk < diff.Hunks.Length; hunk++) {
            var lines = diff.Hunks[hunk].Lines;
            for (var line = 0; line < lines.Length; line++)
                if (!lines[line].Type.IsContext) positions.Add((hunk, line));
        }
        return positions;
    }

    /// <summary>
    /// The chosen rows as lines of the file's own diff, so context expansion doesn't shift them. Without a selection
    /// (<paramref name="wholeHunk"/>) the cursor's whole hunk is taken, as in the commit window.
    /// </summary>
    public IReadOnlyList<GitKay.Core.PatchBuilder.SelectedLine> LinesOf(DiffFileProjection file, IEnumerable<IDiffRowProjection> rows, bool wholeHunk = false) {
        var positions = ChangePositions(file);
        var changed = SelectedDiffRows.OfType<DiffLineProjection>().Where(line => line.IsAdded || line.IsRemoved).ToList();
        var chosen = rows.OfType<DiffLineProjection>().Where(line => line.IsAdded || line.IsRemoved).ToHashSet();

        if (wholeHunk) {
            // The row at the cursor may be a hunk header or context line: take the next change at or after it.
            var start = rows.FirstOrDefault() is { } row ? SelectedDiffRows.IndexOf(row) : -1;
            var focus = chosen.Count > 0
                ? changed.FindIndex(line => chosen.Contains(line))
                : changed.FindIndex(line => SelectedDiffRows.IndexOf(line) >= start);
            if (focus < 0 || focus >= positions.Count) return [];
            var hunk = positions[focus].Hunk;
            chosen = changed.Where((_, index) => index < positions.Count && positions[index].Hunk == hunk).ToHashSet();
        }

        var lines = new List<GitKay.Core.PatchBuilder.SelectedLine>();
        for (var i = 0; i < changed.Count && i < positions.Count; i++) {
            if (!chosen.Contains(changed[i])) continue;
            lines.Add(new GitKay.Core.PatchBuilder.SelectedLine(positions[i].Hunk, positions[i].Line,
                changed[i].IsAdded ? GitKay.Core.Models.LineType.Added : GitKay.Core.Models.LineType.Removed, changed[i].Content));
        }
        return lines;
    }

    /// <summary>Whether this file can move to the index (unstaged and untracked files) or out of it (staged ones).</summary>
    public bool IsStagedFile(DiffFileProjection file) => file.Key.Section == "Staged";
    public bool IsUntrackedFile(DiffFileProjection file) => file.Key.Section == "Untracked";

    public void StageWorkingTreeFile(DiffFileProjection file) =>
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewStageWorkingTree(ListModule.OfSeq(PathsOf(file))));

    public void UnstageWorkingTreeFile(DiffFileProjection file) =>
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewUnstageWorkingTree(ListModule.OfSeq(PathsOf(file))));

    public void DiscardWorkingTreeFile(DiffFileProjection file) {
        var path = ListModule.OfSeq([DiffFileTree.PathOf(file)]);
        var empty = Microsoft.FSharp.Collections.FSharpList<string>.Empty;
        _dispatch?.Invoke(IsUntrackedFile(file)
            ? GitKay.Core.App.Msg.NewDiscardWorkingTree(empty, path)
            : GitKay.Core.App.Msg.NewDiscardWorkingTree(path, empty));
    }

    /// <summary>Stages, unstages or discards the chosen lines of an uncommitted file.</summary>
    public void ApplyWorkingTreeLines(GitKay.Core.GitService.PatchTarget target, DiffFileProjection file, IEnumerable<IDiffRowProjection> rows, bool wholeHunk = false) {
        var lines = LinesOf(file, rows, wholeHunk);
        if (lines.Count == 0) {
            Status = "Put the cursor in a hunk or select changed lines";
            return;
        }
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewApplyWorkingTreeLines(target, DiffFileTree.PathOf(file), ListModule.OfSeq(lines)));
    }

    public void UndoDiscard() => _dispatch?.Invoke(GitKay.Core.App.Msg.UndoWorkingTreeDiscard);

    // A rename moves with both of its paths, so the index doesn't keep half of it.
    private static IEnumerable<string> PathsOf(DiffFileProjection file) =>
        file.Key.OldPath != file.Key.NewPath && file.Key.OldPath != "/dev/null" && file.Key.NewPath != "/dev/null"
            ? [file.Key.OldPath, file.Key.NewPath]
            : [DiffFileTree.PathOf(file)];

    /// <summary>Shows the uncommitted changes as Staged, Unstaged and Untracked files, keeping collapsed files and the selected file across refreshes.</summary>
    private void UpdateWorkingTreeDiffState(GitKay.Core.App.Model model) {
        var changes = model.WorkingTreeChanges?.Value;
        _workingTreeChanges = changes;
        var wasShown = IsWorkingTreeDiffShown;
        if (wasShown && ReferenceEquals(_selectedDiffFilesSource, changes)) return;

        if (!wasShown) {
            SaveCommitViewState();
            _collapsedDiffSections.Clear();
            _workingTreeExpansions.Clear();
            _workingTreeContents.Clear();
        }
        var previousKey = wasShown ? SelectedDiffFile?.Key : null;
        var collapsed = wasShown ? SelectedDiffFiles.Where(file => file.IsCollapsed).Select(file => file.Key).ToHashSet() : [];
        var existing = SelectedDiffFiles.ToDictionary(file => file.Key);

        SyncSelectedDiffSelection(null);
        SelectedDiffRows.Clear();
        SelectedDiffFiles.Clear();
        _selectedDiffHash = WorkingTreeDiffId;
        _selectedDiffFilesSource = changes;
        _selectedDiffFileSource = changes;
        _diffExpansionsSource = model.DiffExpansions;

        if (changes != null) {
            var markers = changes.Entries.ToDictionary(entry => entry.Path, GitKay.Core.WorkingTree.marker, StringComparer.Ordinal);
            foreach (var (section, files) in GitKay.Core.GitService.workingTreeSections(changes)) {
                var sectionName = GitKay.Core.WorkingTree.sectionName(section);
                foreach (var diff in files) {
                    var key = new DiffFileKey(diff.OldPath, diff.NewPath, sectionName);
                    var summary = new GitKay.Core.GitService.DiffFileSummary(diff.OldPath, diff.NewPath, GitKay.Core.FileChange.displayPath(diff.OldPath, diff.NewPath));
                    if (!existing.TryGetValue(key, out var file)) file = new DiffFileProjection(summary, sectionName);
                    else file.UpdateSummary(summary);
                    // A refresh keeps revealed context for files whose change is the same.
                    if (!_workingTreeContents.TryGetValue(key, out var previous) || !previous.Equals(diff)) _workingTreeExpansions.Remove(key);
                    _workingTreeContents[key] = diff;
                    file.ApplyContent(diff, _workingTreeExpansions.GetValueOrDefault(key));
                    file.IsCollapsed = collapsed.Contains(key);
                    file.Marker = markers.GetValueOrDefault(DiffFileTree.PathOf(file), "");
                    SelectedDiffFiles.Add(file);
                }
            }
        }

        RebuildDiffFileListRows();
        RenderSelectedDiffRows();
        var selected = SelectedDiffFiles.FirstOrDefault(file => file.Key == previousKey) ?? SelectedDiffFiles.FirstOrDefault();
        SyncSelectedDiffSelection(selected);
        _selectedDiffFileKey = selected?.Key;
        ApplyDiffMark();
        OnPropertyChanged(nameof(IsWorkingTreeDiffShown));
    }

    private void UpdateDiffState(GitKay.Core.App.Model model) {
        if (model.IsWorkingTreeSelected) {
            UpdateWorkingTreeDiffState(model);
            return;
        }
        var leavingWorkingTree = IsWorkingTreeDiffShown;
        var selectedDiffHash =
            model.SelectedDiffHash != null
            && model.SelectedDiffFiles != null
            && (model.RevisionComparison != null || (model.SelectedCommitHash != null && model.SelectedCommitHash.Value == model.SelectedDiffHash.Value))
                ? model.SelectedDiffHash.Value
                : null;

        var selectedDiffFilesSource =
            selectedDiffHash != null && model.SelectedDiffFiles != null
                ? (object?)model.SelectedDiffFiles.Value
                : null;

        var selectedDiffContentSource =
            selectedDiffHash != null && model.SelectedDiff != null
                ? (object?)model.SelectedDiff.Value
                : null;

        // Mark in the diff only what the commit search looked for in diffs (its diff: or path: terms).
        var diffMarkKey = $"{model.SearchScopeKey}\u0001{model.SearchUseRegex}\u0001{model.SearchQuery}";
        var searchStateChanged = !string.Equals(_diffMarkKey, diffMarkKey, StringComparison.Ordinal);
        if (searchStateChanged) {
            _diffMark = GitKay.Core.GitSearch.diffMark(GitKay.Core.GitSearch.parseMode(model.SearchScopeKey), model.SearchUseRegex, model.SearchQuery);
            _diffMarkKey = diffMarkKey;
        }

        var diffCollectionChanged =
            !string.Equals(_selectedDiffHash, selectedDiffHash, StringComparison.Ordinal)
            || !ReferenceEquals(_selectedDiffFilesSource, selectedDiffFilesSource);

        if (leavingWorkingTree) OnPropertyChanged(nameof(IsWorkingTreeDiffShown));
        if (selectedDiffHash == null) {
            if (!leavingWorkingTree) SaveCommitViewState();
            if (SelectedDiffFiles.Count > 0 || SelectedDiffRows.Count > 0 || _selectedDiffHash != null) {
                SyncSelectedDiffSelection(null);
                SelectedDiffFiles.Clear();
                SelectedDiffRows.Clear();
                DiffFileListRows.Clear();
            }

            _selectedDiffHash = null;
            _selectedDiffFilesSource = null;
            _selectedDiffFileKey = null;
            _selectedDiffFileSource = null;
            return;
        }

        if (diffCollectionChanged) {
            if (!leavingWorkingTree) SaveCommitViewState();
            var previousSelectedDiffFileKey = SelectedDiffFile?.Key;

            SyncSelectedDiffSelection(null);
            SelectedDiffRows.Clear();

            if (model.SelectedDiffFiles != null) {
                SyncSelectedDiffFiles(model.SelectedDiffFiles.Value, true);
            }

            var restored = RestoreCommitViewState(selectedDiffHash);
            if (restored?.SelectedFile is { } restoredFile && SelectedDiffFiles.Any(file => file.Key == restoredFile)) {
                previousSelectedDiffFileKey = restoredFile;
                if (model.SelectedDiffFileKey == null || !MatchesKey(restoredFile, model.SelectedDiffFileKey.Value))
                    _dispatch?.Invoke(GitKay.Core.App.Msg.NewSelectDiffFile(selectedDiffHash, restoredFile.OldPath, restoredFile.NewPath));
            }

            _selectedDiffHash = selectedDiffHash;
            _selectedDiffFilesSource = selectedDiffFilesSource;

            if (model.SelectedDiff != null) {
                SyncSelectedDiffFileContents(model.SelectedDiff.Value, model.DiffExpansions);
            }

            _selectedDiffFileSource = selectedDiffContentSource;
            _diffExpansionsSource = model.DiffExpansions;

            var selectedDiffFile = ResolveSelectedDiffFile(model, previousSelectedDiffFileKey);

            SyncSelectedDiffSelection(selectedDiffFile);
            RenderSelectedDiffRows();
            _selectedDiffFileKey = selectedDiffFile?.Key;
        }

        if (model.SelectedDiffFiles != null) {
            var selectedDiffFile = ResolveSelectedDiffFile(model, _selectedDiffFileKey);
            var selectedDiffFileKey = selectedDiffFile?.Key;
            // A context expansion temporarily publishes no content while its replacement
            // loads. Keep the current rows visible until the new diff can replace them.
            var diffContentChanged = selectedDiffContentSource != null
                                     && !ReferenceEquals(_selectedDiffFileSource, selectedDiffContentSource);
            var selectionChanged = !Nullable.Equals(_selectedDiffFileKey, selectedDiffFileKey);

            if (diffContentChanged) {
                SyncSelectedDiffFileContents(model.SelectedDiff!.Value, model.DiffExpansions);
                RenderSelectedDiffRows();
                _selectedDiffFileSource = selectedDiffContentSource;
                _diffExpansionsSource = model.DiffExpansions;
            }
            else if (!ReferenceEquals(_diffExpansionsSource, model.DiffExpansions)) {
                // Expansion is file-scoped: only files whose expansion state changed re-project.
                _diffExpansionsSource = model.DiffExpansions;
                if (SyncSelectedDiffExpansions(model.DiffExpansions)) {
                    RenderSelectedDiffRows();
                    diffContentChanged = true;
                }
            }

            if (selectionChanged || diffContentChanged || searchStateChanged) {
                SyncSelectedDiffSelection(selectedDiffFile);
                _selectedDiffFileKey = selectedDiffFileKey;
            }

            if (searchStateChanged || diffContentChanged || diffCollectionChanged) ApplyDiffMark();
        }

        if (_revealSearchMatchInDiff && model.SelectedDiff != null && SelectedDiffRows.Any(row => row is DiffLineProjection)) {
            _revealSearchMatchInDiff = false;
            SelectedDiffRow = null;
            StepDiffFind(+1);
        }
    }

    /// <summary>Whether whitespace-only changes are left out of the diff, as git's -w does.</summary>
    [ObservableProperty] private bool _ignoreWhitespace;

    partial void OnIgnoreWhitespaceChanged(bool value) {
        if (_suppressIgnoreWhitespaceDispatch) return;
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetIgnoreWhitespace(value));
        Status = value ? "Ignoring whitespace changes" : "Showing whitespace changes";
    }

    private void UpdateIgnoreWhitespaceState(GitKay.Core.App.Model model) {
        if (IgnoreWhitespace == model.IgnoreWhitespace) return;
        _suppressIgnoreWhitespaceDispatch = true;
        try { IgnoreWhitespace = model.IgnoreWhitespace; }
        finally { _suppressIgnoreWhitespaceDispatch = false; }
    }

    private void UpdateDiffContextState(GitKay.Core.App.Model model) {
        if (DiffContextLineCount != model.DiffContextLines) {
            DiffContextLineCount = model.DiffContextLines;
        }

        var selectedContextLineCount =
            DiffContextLineCounts.FirstOrDefault(option => option.Count == model.DiffContextLines)
            ?? DiffContextLineCounts.FirstOrDefault();

        if (!ReferenceEquals(SelectedDiffContextLineCount, selectedContextLineCount)) {
            _suppressDiffContextDispatch = true;
            try {
                SelectedDiffContextLineCount = selectedContextLineCount;
            }
            finally {
                _suppressDiffContextDispatch = false;
            }
        }
    }

    private void UpdateDiffPresentationState(GitKay.Core.App.Model model) {
        var selectedPresentationMode = PresentationModeFor(model.DiffLayout);

        if (!ReferenceEquals(SelectedDiffPresentationMode, selectedPresentationMode)) {
            _suppressDiffPresentationDispatch = true;
            try {
                SelectedDiffPresentationMode = selectedPresentationMode;
            }
            finally {
                _suppressDiffPresentationDispatch = false;
            }
        }
    }

    private void ApplyDiffMark() {
        foreach (var file in SelectedDiffFiles) file.ApplyDiffMark(_diffMark);
    }

    private void UpdateCommits(GitKay.Core.App.Model model) {
        var searchResultsSource = model.SearchResults != null ? (object?)model.SearchResults.Value : null;
        var commitsChanged = !ReferenceEquals(_commitsSource, model.Commits);
        var searchResultsChanged = !ReferenceEquals(_commitSearchResultsSource, searchResultsSource);

        var workingTreeChanged = !ReferenceEquals(_workingTreeSource, model.WorkingTree);
        if (commitsChanged && Commits.Count > 0 && Commits[0].IsWorkingTree) Commits.RemoveAt(0);

        if (commitsChanged) {
            Commits.SyncWith(
                model.Commits,
                m => m.Commit.Hash,
                vm => vm.FullHash,
                _ => new CommitProjection(),
                _dispatch!);

            _commitsSource = model.Commits;
            ApplyRefVisibility();
            RefreshSuggestions();
        }

        if (commitsChanged || workingTreeChanged) SyncWorkingTreeRow(model);

        // While a new search runs, keep the previous matches instead of flashing every commit back.
        var searchPending = model.SearchStartedAtTicks != null && model.SearchResults == null;
        if ((commitsChanged || searchResultsChanged) && !searchPending) {
            ApplyCommitSearchMatches(model.SearchResults?.Value);
            _commitSearchResultsSource = searchResultsSource;
        }

        IsCommitSearchActive = !string.IsNullOrWhiteSpace(model.SearchQuery) && (model.SearchResults != null || searchPending);
    }

    private object? _workingTreeSource;
    private readonly CommitProjection _workingTreeRow = new() { IsWorkingTree = true };

    /// <summary>Keeps the uncommitted changes row first in the list while the working tree differs from HEAD.</summary>
    private void SyncWorkingTreeRow(GitKay.Core.App.Model model) {
        _workingTreeSource = model.WorkingTree;
        HasUncommittedChanges = !model.WorkingTree.IsEmpty;
        var shown = Commits.Count > 0 && Commits[0].IsWorkingTree;
        // Only shown above history that starts at HEAD: a filtered or other-branch history has nowhere to attach it.
        var head = Commits.Skip(shown ? 1 : 0).FirstOrDefault();
        var wanted = !model.WorkingTree.IsEmpty && (head == null || head.LocalBranches.Any(branch => branch.IsCurrentHead) || model.IsWorkingTreeSelected);
        if (wanted) {
            _workingTreeRow.UpdateWorkingTree(model.WorkingTree, head?.Lane ?? 0);
            if (!shown) Commits.Insert(0, _workingTreeRow);
        }
        else if (shown) {
            Commits.RemoveAt(0);
        }
    }

    public CommitProjection WorkingTreeRow => _workingTreeRow;

    private void ApplyRefVisibility() {
        foreach (var commit in Commits) {
            commit.ShowBranchRefs = ShowBranchRefs;
            commit.ShowStashes = ShowStashes;
        }
    }

    // ----- Per-commit view state: returning to a commit restores its selected file and collapsed files/folders. -----

    private sealed record CommitViewState(DiffFileKey? SelectedFile, HashSet<DiffFileKey> CollapsedFiles, HashSet<string> CollapsedFolders);
    private readonly Dictionary<string, CommitViewState> _commitViewStates = new(StringComparer.Ordinal);

    private void SaveCommitViewState() {
        if (_selectedDiffHash == null || SelectedDiffFiles.Count == 0) return;
        _commitViewStates[_selectedDiffHash] = new CommitViewState(
            SelectedDiffFile?.Key,
            SelectedDiffFiles.Where(file => file.IsCollapsed).Select(file => file.Key).ToHashSet(),
            new HashSet<string>(_collapsedDiffFolders, StringComparer.Ordinal));
        if (_commitViewStates.Count > 500) _commitViewStates.Remove(_commitViewStates.Keys.First());
    }

    private CommitViewState? RestoreCommitViewState(string hash) {
        _commitViewStates.TryGetValue(hash, out var state);
        foreach (var file in SelectedDiffFiles) {
            // File projections are reused across commits by path, so collapse state is always reset here.
            file.IsCollapsed = state?.CollapsedFiles.Contains(file.Key) == true;
        }

        _collapsedDiffFolders.Clear();
        if (state != null) _collapsedDiffFolders.UnionWith(state.CollapsedFolders);
        RebuildDiffFileListRows();
        return state;
    }

    private void UpdateCommitRelations(GitKay.Core.App.Model model) {
        if (!ReferenceEquals(_childrenSource, model.Commits)) {
            _childrenSource = model.Commits;
            _childrenByParent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var info in model.Commits) {
                foreach (var parent in info.Commit.Parents) {
                    if (!_childrenByParent.TryGetValue(parent, out var childHashes))
                        _childrenByParent[parent] = childHashes = new List<string>();
                    childHashes.Add(info.Commit.Hash);
                }
            }
        }

        var selected = SelectedCommit is { IsWorkingTree: true } ? null : SelectedCommit;
        RecordVisitedCommit(selected?.FullHash);
        var parents = selected == null
            ? new List<string>()
            : model.Commits.FirstOrDefault(info => info.Commit.Hash == selected.FullHash)?.Commit.Parents.ToList() ?? new List<string>();
        var children = selected != null && _childrenByParent.TryGetValue(selected.FullHash, out var found) ? found : new List<string>();
        SyncLinks(SelectedCommitParents, parents);
        SyncLinks(SelectedCommitChildren, children);
    }

    private void SyncLinks(ObservableCollection<CommitLinkProjection> target, IReadOnlyList<string> hashes) {
        if (target.Select(link => link.FullHash).SequenceEqual(hashes, StringComparer.Ordinal)) return;
        target.Clear();
        foreach (var hash in hashes) {
            var commit = Commits.FirstOrDefault(candidate => candidate.FullHash == hash);
            target.Add(new CommitLinkProjection(hash, commit?.Subject ?? "(not in loaded history)"));
        }
    }

    [RelayCommand]
    private void GoToCommit(string? hash) {
        var commit = hash == null ? null : Commits.FirstOrDefault(candidate => candidate.FullHash == hash);
        if (commit != null) SelectedCommit = commit;
    }

    /// <summary>p: first parent (Shift+P: second parent of a merge); c: first child.</summary>
    public void GoToParent(int index) {
        if (index < SelectedCommitParents.Count) GoToCommit(SelectedCommitParents[index].FullHash);
    }

    public void GoToChild() {
        if (SelectedCommitChildren.Count > 0) GoToCommit(SelectedCommitChildren[0].FullHash);
    }

    private void UpdateSelectedCommit(GitKay.Core.App.Model model) {
        CommitProjection? selectedCommit = null;

        if (model.IsWorkingTreeSelected && Commits.Count > 0 && Commits[0].IsWorkingTree) {
            selectedCommit = Commits[0];
        }
        else if (model.SelectedCommitHash != null) {
            var hash = model.SelectedCommitHash.Value;
            foreach (var commit in Commits) {
                if (commit.FullHash == hash) {
                    selectedCommit = commit;
                    break;
                }
            }
        }

        if (selectedCommit != null) {
            _suppressSelectionDispatch = true;
            try {
                SelectedCommit = selectedCommit;
            }
            finally {
                _suppressSelectionDispatch = false;
            }
        }
        else if (SelectedCommit != null) {
            _suppressSelectionDispatch = true;
            try {
                SelectedCommit = null;
            }
            finally {
                _suppressSelectionDispatch = false;
            }
        }
    }

    private SearchResultProjection? ResolveSelectedSearchResult(string? previousSelectedSearchHash) {
        if (previousSelectedSearchHash != null) {
            var selectedSearchResult = SearchResults.FirstOrDefault(result => result.FullHash == previousSelectedSearchHash);
            if (selectedSearchResult != null) {
                return selectedSearchResult;
            }
        }

        return null;
    }

    private Action<GitKay.Core.App.Msg>? _dispatch;

    public void SetDispatch(Action<GitKay.Core.App.Msg> dispatch) {
        _dispatch = dispatch;
    }

    public void OpenRevisionComparison(string baseRevision, string targetRevision) {
        if (string.IsNullOrWhiteSpace(baseRevision) || string.IsNullOrWhiteSpace(targetRevision)) return;
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewOpenRevisionComparison(baseRevision.Trim(), targetRevision.Trim(), Stopwatch.GetTimestamp()));
    }

    public void CloseRevisionComparison() => _dispatch?.Invoke(GitKay.Core.App.Msg.CloseRevisionComparison);

    /// <summary>Whether anything is staged, unstaged or untracked, so committing is possible.</summary>
    [ObservableProperty] private bool _hasUncommittedChanges;

    /// <summary>Rereads git status: the working tree changed on disk, or the window regained focus.</summary>
    public void RefreshWorkingTree() => _dispatch?.Invoke(GitKay.Core.App.Msg.RefreshWorkingTree);

    public void LogFirstPaint() {
        if (_firstPaintLogged) {
            return;
        }

        _firstPaintLogged = true;
        var firstPaintElapsed = Stopwatch.GetElapsedTime(_createdAtTicks);
        LogTiming($"first paint elapsed={firstPaintElapsed.TotalMilliseconds:F1}ms");
    }

    partial void OnSelectedCommitChanged(CommitProjection? value) {
        if (_suppressSelectionDispatch) {
            return;
        }

        if (value is { IsWorkingTree: true }) {
            _dispatch?.Invoke(GitKay.Core.App.Msg.NewSelectWorkingTree(Stopwatch.GetTimestamp()));
        }
        else if (value != null) {
            var startedAtTicks = Stopwatch.GetTimestamp();
            LogTiming($"commit click hash={value.FullHash}");
            _dispatch?.Invoke(GitKay.Core.App.Msg.NewSelectCommit(value.FullHash, startedAtTicks));
        }
    }

    /// <summary>Why part of the search is being ignored (an unparseable date), or empty.</summary>
    [ObservableProperty] private string _searchValidationMessage = "";
    [ObservableProperty] private bool _isAfterDateInvalid;
    [ObservableProperty] private bool _isBeforeDateInvalid;
    public bool HasSearchValidationMessage => SearchValidationMessage.Length > 0;
    partial void OnSearchValidationMessageChanged(string value) => OnPropertyChanged(nameof(HasSearchValidationMessage));

    private void ValidateSearchQuery(string query) {
        var problems = GitKay.Core.GitSearch.invalidDates(DateTimeOffset.Now, CurrentSearchMode, query).ToList();
        IsAfterDateInvalid = problems.Any(problem => problem.Field.IsAfter);
        IsBeforeDateInvalid = problems.Any(problem => problem.Field.IsBefore);
        SearchValidationMessage = string.Join("  ·  ", problems.Select(GitKay.Core.GitSearch.describeDateProblem));
    }

    partial void OnSearchQueryChanged(string value) {
        ValidateSearchQuery(value);
        RaiseAdvancedFieldsChanged();
        if (_suppressSearchDispatch) {
            return;
        }

        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetSearchQuery(value));
        ScheduleSearchDebounce();
    }

    partial void OnShowStashesChanged(bool value) {
        ApplyRefVisibility();
        if (_suppressShowStashesDispatch) {
            return;
        }

        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetShowStashes(value));
    }

    partial void OnShowBranchRefsChanged(bool value) {
        ApplyRefVisibility();
        if (_suppressShowBranchRefsDispatch) {
            return;
        }

        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetShowBranchRefs(value));
    }

    partial void OnSelectedDiffContextLineCountChanged(DiffContextLineCountProjection? value) {
        if (_suppressDiffContextDispatch || value == null) {
            return;
        }

        DiffContextLineCount = value.Count;
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetDiffContextLines(value.Count));
    }

    partial void OnSelectedSearchScopeChanged(SearchScopeProjection? value) {
        SearchPlaceholder = value?.Placeholder ?? SearchPlaceholder;
        OnPropertyChanged(nameof(IsCommitSearchMode));
        OnPropertyChanged(nameof(IsPathSearchMode));
        OnPropertyChanged(nameof(IsDiffSearchMode));
        if (_suppressSearchDispatch || value == null) {
            return;
        }

        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetSearchScope(value.Key));
        ScheduleSearchDebounce();
    }

    partial void OnSelectedSearchResultChanged(SearchResultProjection? value) {
        if (_suppressSearchSelectionDispatch) {
            return;
        }

        if (value == null) {
            return;
        }

        _selectedSearchResultHash = value.FullHash;
        var startedAtTicks = Stopwatch.GetTimestamp();
        LogTiming($"search result click hash={value.FullHash} summary={value.MatchSummary}");
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSelectCommit(value.FullHash, startedAtTicks));
    }

    partial void OnSelectedThemeModeChanged(ThemeModeProjection? value) {
        if (Avalonia.Application.Current is { } application) {
            application.RequestedThemeVariant =
                value?.Mode is { IsLightTheme: true } ? Avalonia.Styling.ThemeVariant.Light
                : value?.Mode is { IsDarkTheme: true } ? Avalonia.Styling.ThemeVariant.Dark
                : Avalonia.Styling.ThemeVariant.Default;
        }
    }

    partial void OnSelectedDiffPresentationModeChanged(DiffPresentationModeProjection? value) {
        SelectedDiffPresentationModeLabel = value?.Label ?? "Diff";
        OnPropertyChanged(nameof(IsUnifiedDiffMode));
        OnPropertyChanged(nameof(IsSideBySideDiffMode));
        OnPropertyChanged(nameof(IsNewDiffMode));
        OnPropertyChanged(nameof(IsOldDiffMode));
        OnPropertyChanged(nameof(DiffLayout));
        RenderSelectedDiffRows();

        if (_suppressDiffPresentationDispatch || value == null) {
            return;
        }

        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetDiffLayout(value.Layout));
    }

    partial void OnSelectedDiffFileChanged(DiffFileProjection? value) {
        OnPropertyChanged(nameof(SelectedDiffFileListRow));
        if (_suppressDiffSelectionSync) {
            return;
        }

        SyncSelectedDiffSelection(value);

        if (value == null || SelectedCommit is null or { IsWorkingTree: true }) {
            return;
        }

        LogTiming($"file click hash={SelectedCommit.FullHash} path={value.DisplayPath}");
        if (_selectedDiffHash != null)
            _dispatch?.Invoke(GitKay.Core.App.Msg.NewSelectDiffFile(_selectedDiffHash, value.Key.OldPath, value.Key.NewPath));
    }

    partial void OnSelectedDiffRowChanged(IDiffRowProjection? value) {
        if (_suppressDiffSelectionSync) {
            return;
        }

        if (value is DiffFileHeaderProjection fileHeader) {
            SyncSelectedDiffSelection(fileHeader.File);
        }
    }

    /// <summary>Set by ? and #: Enter and n step backwards through diff matches, N forwards, as in vim.</summary>
    public bool DiffFindBackward { get; set; }

    [RelayCommand]
    private void FindInCommit() => StepDiffFind(DiffFindBackward ? -1 : +1);

    [RelayCommand]
    private void FindPreviousInCommit() => StepDiffFind(DiffFindBackward ? +1 : -1);

    private bool _steppingFromSelection;

    /// <summary>vim's * and #: finds the whole word in the diff, starting from the focused row rather than the top.</summary>
    public void FindWordInDiff(string word, bool forward) {
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
        var pattern = System.Text.RegularExpressions.Regex.Escape(word);
        if (IsWord(word[0])) pattern = @"\b" + pattern;
        if (IsWord(word[^1])) pattern += @"\b";
        _steppingFromSelection = true;
        try {
            CommitFindUseRegex = true;
            CommitFindQuery = pattern;
        }
        finally {
            _steppingFromSelection = false;
        }
        DiffFindBackward = !forward;
        StepDiffFind(forward ? +1 : -1);
    }

    [RelayCommand]
    private void ClearCommitFind() => CommitFindQuery = "";

    partial void OnCommitSearchStatusTextChanged(string value) => OnPropertyChanged(nameof(HasSearchNavigation));

    partial void OnCommitFindUseRegexChanged(bool value) {
        if (_steppingFromSelection) return;
        SelectedDiffRow = null;
        StepDiffFind(+1);
    }

    partial void OnCommitFindQueryChanged(string value) {
        UpdateCommitFindPlaceholder();
        if (_steppingFromSelection) return;
        // Incremental, like a browser: typing jumps to the first match in the selected commit's diff.
        SelectedDiffRow = null;
        StepDiffFind(+1);
    }

    /// <summary>
    /// The / and ? prompt: sets the find text to a regex without resetting the selection, then moves from
    /// <paramref name="origin"/> to the nearest matching row in the search direction, including the origin itself.
    /// </summary>
    public void SearchDiffFrom(string regex, bool forward, IDiffRowProjection? origin) {
        _steppingFromSelection = true;
        try {
            CommitFindUseRegex = true;
            CommitFindQuery = regex;
        }
        finally {
            _steppingFromSelection = false;
        }
        DiffFindBackward = !forward;
        SelectedDiffRow = origin;
        StepDiffFind(forward ? +1 : -1, includeCurrent: true);
    }

    /// <summary>Puts back the find text and regex setting a cancelled / search replaced, without moving the selection.</summary>
    public void RestoreDiffFind(string query, bool useRegex, bool backward) {
        _steppingFromSelection = true;
        try {
            CommitFindUseRegex = useRegex;
            CommitFindQuery = query;
        }
        finally {
            _steppingFromSelection = false;
        }
        DiffFindBackward = backward;
    }

    /// <summary>Moves the diff selection to the next or previous row containing the find text, wrapping.</summary>
    private void StepDiffFind(int direction, bool includeCurrent = false) {
        if (GitKay.Core.DiffFind.choose(CommitFindQuery, CommitFindUseRegex, CommitSearchDiffTerm, CommitSearchDiffTermUseRegex) is not { } found) {
            CommitFindStatusText = "";
            return;
        }

        var find = found.Value;
        var rows = SelectedDiffRows;
        var includesHeaders = GitKay.Core.DiffFind.includesHeaders(find);
        var matches = GitKay.Kit.Cycle.positionsWhere(rows.Count, index =>
            rows[index] is DiffLineProjection || includesHeaders ? RowMatchesFindQuery(rows[index], find.Query.IsMatch) : false);
        var focus = SelectedDiffRow == null ? -1 : rows.IndexOf(SelectedDiffRow);
        var next = GitKay.Kit.Cycle.step(matches, focus, direction > 0 ? GitKay.Kit.Direction.Forward : GitKay.Kit.Direction.Backward, includeCurrent);
        if (next >= 0) SelectedDiffRow = rows[matches[next]];
        CommitFindStatusText = GitKay.Core.DiffFind.status(find, next, matches.Length);
    }

    partial void OnCommitSearchDiffTermChanged(string value) => UpdateCommitFindPlaceholder();

    private void UpdateCommitFindPlaceholder() {
        var term = CommitSearchDiffTerm.Trim();
        CommitFindPlaceholder = term.Length > 0
            ? $"Enter steps through “{term}”"
            : "Find in diff  (Ctrl+F)";
        if (string.IsNullOrWhiteSpace(CommitFindQuery) && term.Length == 0) CommitFindStatusText = "";
    }

    private static string FirstTermOf(GitKay.Core.App.Model model, GitKay.Core.GitSearch.Field field) =>
        GitKay.Core.GitSearch.firstTermText(GitKay.Core.GitSearch.parseMode(model.SearchScopeKey), model.SearchQuery, field) is { } text ? text.Value : "";

    private GitKay.Core.GitSearch.Mode CurrentSearchMode => GitKay.Core.GitSearch.parseMode(SelectedSearchScope?.Key ?? "commit");

    [RelayCommand]
    private void SetSearchMode(string key) {
        var scope = SearchScopes.FirstOrDefault(candidate => candidate.Key == key);
        if (scope != null) SelectedSearchScope = scope;
    }

    partial void OnSearchUseRegexChanged(bool value) {
        if (_suppressSearchDispatch) return;
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetSearchRegex(value));
        ScheduleSearchDebounce();
    }

    // ----- Advanced search: one input per field, kept in sync with prefixed terms in the query text. -----

    private string GetFieldText(GitKay.Core.GitSearch.Field field) =>
        GitKay.Core.GitSearch.fieldText(CurrentSearchMode, SearchQuery, field);

    private void SetFieldText(GitKay.Core.GitSearch.Field field, string? value) =>
        SearchQuery = GitKay.Core.GitSearch.withFieldText(CurrentSearchMode, SearchQuery, field, value ?? "");

    public string AdvancedMessage { get => GetFieldText(GitKay.Core.GitSearch.Field.Message); set => SetFieldText(GitKay.Core.GitSearch.Field.Message, value); }
    public string AdvancedAuthor { get => GetFieldText(GitKay.Core.GitSearch.Field.Author); set => SetFieldText(GitKay.Core.GitSearch.Field.Author, value); }
    public string AdvancedHash { get => GetFieldText(GitKay.Core.GitSearch.Field.Hash); set => SetFieldText(GitKay.Core.GitSearch.Field.Hash, value); }
    public string AdvancedRef { get => GetFieldText(GitKay.Core.GitSearch.Field.Ref); set => SetFieldText(GitKay.Core.GitSearch.Field.Ref, value); }
    public string AdvancedPath { get => GetFieldText(GitKay.Core.GitSearch.Field.ChangedPath); set => SetFieldText(GitKay.Core.GitSearch.Field.ChangedPath, value); }
    public string AdvancedDiff { get => GetFieldText(GitKay.Core.GitSearch.Field.ChangedLine); set => SetFieldText(GitKay.Core.GitSearch.Field.ChangedLine, value); }
    public string AdvancedAfter { get => GetFieldText(GitKay.Core.GitSearch.Field.After); set => SetFieldText(GitKay.Core.GitSearch.Field.After, value); }
    public string AdvancedBefore { get => GetFieldText(GitKay.Core.GitSearch.Field.Before); set => SetFieldText(GitKay.Core.GitSearch.Field.Before, value); }

    // ----- What the advanced search fields suggest, read from the loaded history. -----

    /// <summary>Branch, remote and tag names on loaded commits.</summary>
    public IEnumerable<string> RefSuggestions =>
        Commits.SelectMany(commit => commit.RefNames.Select(reference => reference.Name)).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal);

    /// <summary>Author names and email addresses on loaded commits.</summary>
    public IEnumerable<string> AuthorSuggestions =>
        Commits.SelectMany(commit => new[] { commit.Author, commit.AuthorEmail })
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(text => text, StringComparer.OrdinalIgnoreCase);

    /// <summary>Short hashes of loaded commits.</summary>
    public IEnumerable<string> HashSuggestions => Commits.Select(commit => commit.Hash);

    /// <summary>Paths in the selected commit, and every path in its tree once "All files" has read it.</summary>
    public IEnumerable<string> PathSuggestions =>
        SelectedDiffFiles.Select(DiffFileTree.PathOf).Concat(UnchangedFilePaths()).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal);

    /// <summary>Refreshes the suggestion lists; the fields read them when they open.</summary>
    private void RefreshSuggestions() {
        foreach (var name in new[] { nameof(RefSuggestions), nameof(AuthorSuggestions), nameof(HashSuggestions), nameof(PathSuggestions) })
            OnPropertyChanged(name);
    }

    public bool HasAuthorFilter => !string.IsNullOrEmpty(AdvancedAuthor);
    // Funnels mark explicit field filters only; a plain search isn't a column filter.
    public bool HasCommitFilter => !string.IsNullOrEmpty(AdvancedMessage) || !string.IsNullOrEmpty(AdvancedRef);
    public bool HasHashFilter => !string.IsNullOrEmpty(AdvancedHash);
    public bool HasDateFilter => !string.IsNullOrEmpty(AdvancedAfter) || !string.IsNullOrEmpty(AdvancedBefore);

    private void RaiseAdvancedFieldsChanged() {
        foreach (var name in new[] { nameof(AdvancedMessage), nameof(AdvancedAuthor), nameof(AdvancedHash), nameof(AdvancedRef), nameof(AdvancedPath),
                     nameof(AdvancedDiff), nameof(AdvancedAfter), nameof(AdvancedBefore), nameof(HasAuthorFilter), nameof(HasCommitFilter), nameof(HasHashFilter), nameof(HasDateFilter) }) {
            OnPropertyChanged(name);
        }
    }

    [RelayCommand]
    private void ClearAdvancedSearch() => SearchQuery = "";

    // ----- Column filters: right-click actions add a field term and switch to showing only matches. -----

    /// <summary>Adds or replaces a field filter (e.g. "author", "Jane") and runs the search showing only matches.</summary>
    public void ApplyColumnFilter(string field, string value) {
        SetFieldText(GitKay.Core.GitSearch.fieldNamed(field), value);
        ShowOnlySearchMatches = true;
        Search();
    }

    [RelayCommand]
    private void ClearColumnFilter(string field) {
        switch (field) {
            case "author": AdvancedAuthor = ""; break;
            case "hash": AdvancedHash = ""; break;
            case "date": AdvancedAfter = ""; AdvancedBefore = ""; break;
            default:
                AdvancedMessage = "";
                AdvancedRef = "";
                SetFieldText(GitKay.Core.GitSearch.Field.CommitInfo, null);
                break;
        }

        if (string.IsNullOrWhiteSpace(SearchQuery)) ClearSearch(); else Search();
    }

    // ----- Recent searches, shown under the search box while typing (like JetBrains). -----

    public void LoadRecentSearches(IEnumerable<string> searches) => _recentSearches = GitKay.Core.GitSearch.recentSearches(searches);

    public IReadOnlyList<string> RecentSearches => _recentSearches.ToList();

    private void RememberSearch(string query) {
        _recentSearches = GitKay.Core.GitSearch.rememberSearch(query, _recentSearches);
        OnPropertyChanged(nameof(RecentSearches));
    }

    /// <summary>Refreshes the recent-search suggestions for the current text; opens them when any match.</summary>
    public void UpdateRecentSearchMatches(bool open) {
        RecentSearchMatches.Clear();
        foreach (var recent in GitKay.Core.GitSearch.suggestSearches(SearchQuery, _recentSearches)) RecentSearchMatches.Add(recent);
        IsRecentSearchesOpen = open && RecentSearchMatches.Count > 0;
    }

    public void ApplyRecentSearch(string query) {
        IsRecentSearchesOpen = false;
        SearchQuery = query;
        Search();
    }

    private bool _jumpToMatchWhenResultsArrive;
    private bool _modelSearchPending;
    private string _modelSearchQuery = "";

    /// <summary>
    /// Enter in the commit search box always means "find next". When results for this text aren't in yet
    /// (still searching, or the text just changed), the first match is selected as soon as they arrive.
    /// </summary>
    [RelayCommand]
    private void SearchOrNextCommit() {
        var query = SearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query)) {
            return;
        }

        var resultsAreCurrent = string.Equals(_lastRunSearchQuery, query, StringComparison.Ordinal);
        if (resultsAreCurrent && !_modelSearchPending) {
            StepCommitMatch(+1);
            return;
        }

        _jumpToMatchWhenResultsArrive = true;
        if (!(resultsAreCurrent && _modelSearchPending && string.Equals(_modelSearchQuery.Trim(), query, StringComparison.Ordinal))) {
            Search();
        }
    }

    [RelayCommand]
    private void FindNextCommit() => StepCommitMatch(+1);

    [RelayCommand]
    private void FindPreviousCommit() => StepCommitMatch(-1);

    /// <summary>Selects the next or previous matching commit in history order, wrapping.</summary>
    private int[] CommitMatchPositions() => GitKay.Kit.Cycle.positionsWhere(Commits.Count, index => Commits[index].HasSearchMatch);

    private void StepCommitMatch(int direction) {
        var positions = CommitMatchPositions();
        var focus = SelectedCommit == null ? -1 : Commits.IndexOf(SelectedCommit);
        var next = GitKay.Kit.Cycle.step(positions, focus, direction > 0 ? GitKay.Kit.Direction.Forward : GitKay.Kit.Direction.Backward, false);
        if (next < 0) return;
        // Land on the change: once this commit's diff loads, select its first match.
        _revealSearchMatchInDiff = !string.IsNullOrWhiteSpace(CommitFindQuery) || !string.IsNullOrWhiteSpace(CommitSearchDiffTerm);
        SelectedCommit = Commits[positions[next]];
    }

    private void UpdateCommitSearchStatus(GitKay.Core.App.Model model) {
        // Only an applied search (with results) highlights the diff, so half-typed text doesn't flicker there.
        if (model.SearchResults != null || string.IsNullOrWhiteSpace(model.SearchQuery)) {
            var term = FirstTermOf(model, GitKay.Core.GitSearch.Field.ChangedLine);
            CommitSearchDiffTerm = term;
            CommitSearchPathTerm = FirstTermOf(model, GitKay.Core.GitSearch.Field.ChangedPath);
            var highlightKey = $"{model.SearchScopeKey}\u0001{model.SearchUseRegex}\u0001{model.SearchQuery}";
            if (!string.Equals(_commitSearchHighlightKey, highlightKey, StringComparison.Ordinal)) {
                _commitSearchHighlightKey = highlightKey;
                CommitSearchHighlight = string.IsNullOrWhiteSpace(model.SearchQuery)
                    ? null
                    : GitKay.Core.GitSearch.highlight(GitKay.Core.GitSearch.parseMode(model.SearchScopeKey), model.SearchUseRegex, model.SearchQuery);
            }
            CommitSearchDiffTermUseRegex = model.SearchUseRegex;
        }

        _modelSearchPending = model.SearchStartedAtTicks != null;
        _modelSearchQuery = model.SearchQuery;
        if (_jumpToMatchWhenResultsArrive && !_modelSearchPending && model.SearchResults != null) {
            _jumpToMatchWhenResultsArrive = false;
            // Selecting dispatches SelectCommit; the status position updates on the next model update.
            StepCommitMatch(+1);
        }

        if (string.IsNullOrWhiteSpace(model.SearchQuery)) {
            IsSearchRunning = false;
            CommitSearchStatusText = "";
            return;
        }

        var wasSearchRunning = IsSearchRunning;
        IsSearchRunning = model.SearchStartedAtTicks != null;
        // A finished search leaves libgit2's freed allocations resident; hand them back to the OS.
        if (wasSearchRunning && !IsSearchRunning) System.Threading.Tasks.Task.Run(NativeMemory.TrimNow);
        GitKay.Core.GitSearch.Progress progress;
        if (model.SearchStartedAtTicks != null) {
            progress = GitKay.Core.GitSearch.Progress.NewSearching(model.SearchResults?.Value.Length ?? 0, model.SearchProgress);
        }
        else if (model.SearchResults == null) {
            progress = GitKay.Core.GitSearch.Progress.NotSearching;
        }
        else {
            var positions = CommitMatchPositions();
            var selected = SelectedCommit is { HasSearchMatch: true } ? Array.BinarySearch(positions, Commits.IndexOf(SelectedCommit)) : -1;
            progress = GitKay.Core.GitSearch.Progress.NewSearched(model.SearchResults.Value.Length, Math.Max(-1, selected));
        }

        CommitSearchStatusText = GitKay.Core.GitSearch.progressText(progress);
    }

    public void RereadRefs() => _dispatch?.Invoke(GitKay.Core.App.Msg.RereadRefs);

    [RelayCommand]
    private void SetDiffPresentationMode(string key) {
        if (GitKay.Core.DiffLayoutModule.tryParse(key) is { } layout) SelectedDiffPresentationMode = PresentationModeFor(layout.Value);
    }

    [RelayCommand]
    private void ToggleCommitDetails() => IsCommitDetailsExpanded = !IsCommitDetailsExpanded;

    public bool IsUnifiedDiffMode => DiffLayout.IsUnified;
    public bool IsSideBySideDiffMode => DiffLayout.IsSideBySide;
    public bool IsNewDiffMode => DiffLayout.IsNewFile;
    public bool IsOldDiffMode => DiffLayout.IsOldFile;

    [RelayCommand]
    private void Search() {
        CancelSearchDebounce();
        var query = SearchQuery;
        var scopeKey = SelectedSearchScope?.Key ?? "commit";
        _lastRunSearchQuery = query.Trim();
        RememberSearch(query);
        var startedAtTicks = Stopwatch.GetTimestamp();
        LogTiming($"search click query={query} scope={scopeKey}");
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewRunSearch(query, scopeKey, startedAtTicks));
    }

    /// <summary>A commit search is running; Esc cancels it.</summary>
    [ObservableProperty] private bool _isSearchRunning;

    /// <summary>Stops the running search and keeps its query.</summary>
    public void CancelSearch() {
        CancelSearchDebounce();
        _dispatch?.Invoke(GitKay.Core.App.Msg.CancelSearch);
    }

    [RelayCommand]
    private void ClearSearch() {
        CancelSearchDebounce();
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetSearchQuery(""));
        _lastRunSearchQuery = null;
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewRunSearch("", SelectedSearchScope?.Key ?? "commit", Stopwatch.GetTimestamp()));
    }

    partial void OnIsSearchPanelExpandedChanged(bool value) {
        // No-op for now
    }

    private void ScheduleSearchDebounce() {
        if (string.IsNullOrWhiteSpace(SearchQuery)) {
            CancelSearchDebounce();
            return;
        }

        CancelSearchDebounce();

        var cancellation = new CancellationTokenSource();
        _searchDebounceCancellation = cancellation;

        _ = DebounceSearchAsync(cancellation, TimeSpan.FromSeconds(Math.Max(0d, SearchDebounceSeconds)));
    }

    private async Task DebounceSearchAsync(CancellationTokenSource cancellation, TimeSpan delay) {
        try {
            await Task.Delay(delay, cancellation.Token);

            if (cancellation.IsCancellationRequested || !ReferenceEquals(_searchDebounceCancellation, cancellation)) {
                return;
            }

            var query = SearchQuery;
            if (string.IsNullOrWhiteSpace(query)) {
                return;
            }

            var scopeKey = SelectedSearchScope?.Key ?? "commit";
            _lastRunSearchQuery = query.Trim();
            var startedAtTicks = Stopwatch.GetTimestamp();
            _dispatch?.Invoke(GitKay.Core.App.Msg.NewRunSearch(query, scopeKey, startedAtTicks));
        }
        catch (OperationCanceledException) {
        }
        finally {
            if (ReferenceEquals(_searchDebounceCancellation, cancellation)) {
                _searchDebounceCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelSearchDebounce() {
        if (_searchDebounceCancellation == null) {
            return;
        }

        _searchDebounceCancellation.Cancel();
        _searchDebounceCancellation = null;
    }

    private void SyncSelectedDiffFiles(IReadOnlyList<GitKay.Core.GitService.DiffFileSummary> summaries, bool clearContent) {
        var existingByKey = SelectedDiffFiles.ToDictionary(file => file.Key);

        SelectedDiffFiles.Clear();
        foreach (var summary in summaries) {
            var key = new DiffFileKey(summary.OldPath, summary.NewPath);

            if (!existingByKey.TryGetValue(key, out var fileProjection)) {
                fileProjection = new DiffFileProjection(summary);
            }
            else {
                fileProjection.UpdateSummary(summary);
                if (clearContent) {
                    fileProjection.ClearContent();
                }
            }

            SelectedDiffFiles.Add(fileProjection);
        }

        _collapsedDiffFolders.Clear();
        _toggledAllFilesFolders.Clear();
        RebuildDiffFileListRows();
    }

    private static bool MatchesKey(DiffFileKey uiKey, GitKay.Core.GitService.DiffFileKey coreKey) =>
        uiKey.OldPath == coreKey.OldPath && uiKey.NewPath == coreKey.NewPath;

    private DiffFileProjection? ResolveSelectedDiffFile(GitKay.Core.App.Model model, DiffFileKey? previousSelectedDiffFileKey) {
        if (model.SelectedDiffFileKey != null) {
            var key = model.SelectedDiffFileKey.Value;
            var selectedDiffFile = SelectedDiffFiles.FirstOrDefault(file => MatchesKey(file.Key, key));
            if (selectedDiffFile != null) {
                return selectedDiffFile;
            }
        }

        if (previousSelectedDiffFileKey != null) {
            var key = previousSelectedDiffFileKey.Value;
            var selectedDiffFile = SelectedDiffFiles.FirstOrDefault(file => file.Key == key);
            if (selectedDiffFile != null) {
                return selectedDiffFile;
            }
        }

        return SelectedDiffFiles.FirstOrDefault();
    }

    private static GitKay.Core.App.FileExpansion? FindExpansion(
        Microsoft.FSharp.Collections.FSharpMap<GitKay.Core.GitService.DiffFileKey, GitKay.Core.App.FileExpansion>? expansions,
        DiffFileKey key) {
        if (expansions == null) return null;
        var found = expansions.TryFind(new GitKay.Core.GitService.DiffFileKey(key.OldPath, key.NewPath));
        return found == null ? null : found.Value;
    }

    private bool SyncSelectedDiffExpansions(
        Microsoft.FSharp.Collections.FSharpMap<GitKay.Core.GitService.DiffFileKey, GitKay.Core.App.FileExpansion> expansions) {
        var changed = false;
        foreach (var fileProjection in SelectedDiffFiles) {
            if (fileProjection.IsLoaded) {
                changed |= fileProjection.ApplyExpansion(FindExpansion(expansions, fileProjection.Key));
            }
        }

        return changed;
    }

    private void SyncSelectedDiffFileContents(
        IReadOnlyList<GitKay.Core.Models.FileDiff> files,
        Microsoft.FSharp.Collections.FSharpMap<GitKay.Core.GitService.DiffFileKey, GitKay.Core.App.FileExpansion>? expansions = null) {
        var filesByKey = files.ToDictionary(
            file => new DiffFileKey(file.OldPath, file.NewPath),
            file => file);

        foreach (var fileProjection in SelectedDiffFiles) {
            if (filesByKey.TryGetValue(fileProjection.Key, out var loadedFile)) {
                fileProjection.ApplyContent(loadedFile, FindExpansion(expansions, fileProjection.Key));
            }
            else {
                fileProjection.ClearContent();
            }
        }
    }

    private void ApplyCommitSearchMatches(IEnumerable<GitKay.Core.GitSearch.Result>? results) {
        var resultsByHash = results?.ToDictionary(result => result.Commit.Hash);

        foreach (var commit in Commits) {
            if (resultsByHash != null && resultsByHash.TryGetValue(commit.FullHash, out var result)) {
                commit.ApplySearchMatch(result);
            }
            else {
                commit.ApplySearchMatch(null);
            }
        }
    }

    private void SyncSelectedSearchResult(SearchResultProjection? selectedSearchResult) {
        _suppressSearchSelectionDispatch = true;
        try {
            SelectedSearchResult = selectedSearchResult;
        }
        finally {
            _suppressSearchSelectionDispatch = false;
        }
    }

    private void RenderSelectedDiffRows() {
        var rows = new List<IDiffRowProjection>();
        var mode = DiffLayout;
        // Patch mode follows Git's diff order. Tree mode follows the same alphabetical folder/file order as the
        // file pane; use an uncollapsed tree so collapsing navigation folders never hides files from the diff.
        var orderedFiles = IsDiffFileTreeMode && !IsAllFilesMode
            ? DiffFileTree.BuildRows(SelectedDiffFiles, true, new HashSet<string>()).OfType<DiffFileProjection>()
            : SelectedDiffFiles;
        foreach (var section in orderedFiles.GroupBy(file => file.Key.Section)) {
            var sectionCollapsed = false;
            if (section.Key.Length > 0) {
                var name = section.Key;
                sectionCollapsed = _collapsedDiffSections.Contains(name);
                rows.Add(new DiffSectionHeaderProjection(name, section.Count(), sectionCollapsed, () => ToggleDiffSection(name)));
            }
            if (sectionCollapsed) continue;
            foreach (var file in section)
                DiffRowBuilder.AppendFile(rows, file, mode);
        }

        SelectedDiffRows.Clear();
        SelectedDiffRows.AddRange(rows);
    }

    // ----- Context expansion for uncommitted files: presentation state kept here, keyed by section and path. -----

    private static readonly GitKay.Core.DiffExpansion.LineRange AllLines = new(1, int.MaxValue);
    private readonly Dictionary<DiffFileKey, GitKay.Core.App.FileExpansion> _workingTreeExpansions = new();
    private readonly Dictionary<DiffFileKey, GitKay.Core.Models.FileDiff> _workingTreeContents = new();

    private void RevealWorkingTreeContext(DiffFileProjection file, GitKay.Core.DiffExpansion.LineRange range) {
        var key = file.Key;
        var current = _workingTreeExpansions.GetValueOrDefault(key)
                      ?? new GitKay.Core.App.FileExpansion(null, Microsoft.FSharp.Collections.FSharpList<GitKay.Core.DiffExpansion.LineRange>.Empty, null);
        var revealed = GitKay.Core.DiffExpansion.addRange(range, current.Revealed);
        var section = GitKay.Core.WorkingTree.tryParseSection(key.Section);
        var needsLoad = current.FullContext == null && current.PendingRequestId == null && RepositoryPath != null && section != null;
        var requestId = Stopwatch.GetTimestamp();
        var next = new GitKay.Core.App.FileExpansion(current.FullContext, revealed,
            needsLoad ? Microsoft.FSharp.Core.FSharpOption<long>.Some(requestId) : current.PendingRequestId);
        SetWorkingTreeExpansion(file, next);
        if (needsLoad) _ = LoadWorkingTreeContextAsync(RepositoryPath!, file, section!.Value, requestId);
    }

    private async Task LoadWorkingTreeContextAsync(string repo, DiffFileProjection file, GitKay.Core.WorkingTree.Section section, long requestId) {
        string? error;
        GitKay.Core.Models.FileDiff? loaded = null;
        try {
            var result = await Task.Run(() => GitKay.Core.GitService.loadWorkingTreeFile(repo, section, file.Key.OldPath, file.Key.NewPath));
            error = result.IsError ? GitKay.Core.GitErrorModule.describe(result.ErrorValue) : null;
            if (result.IsOk) loaded = result.ResultValue;
        }
        catch (Exception ex) {
            error = ex.Message;
        }
        if (!IsWorkingTreeDiffShown || !_workingTreeExpansions.TryGetValue(file.Key, out var current) || current.PendingRequestId?.Value != requestId) return;
        if (loaded != null) {
            SetWorkingTreeExpansion(file, new GitKay.Core.App.FileExpansion(loaded, current.Revealed, null));
        }
        else {
            Status = $"Context Error: {error}";
            _workingTreeExpansions.Remove(file.Key);
            if (file.ApplyExpansion(null)) RenderSelectedDiffRows();
        }
    }

    private void CollapseWorkingTreeContext(DiffFileProjection file) {
        if (!_workingTreeExpansions.TryGetValue(file.Key, out var current)) return;
        SetWorkingTreeExpansion(file, new GitKay.Core.App.FileExpansion(current.FullContext,
            Microsoft.FSharp.Collections.FSharpList<GitKay.Core.DiffExpansion.LineRange>.Empty, current.PendingRequestId));
    }

    private void SetWorkingTreeExpansion(DiffFileProjection file, GitKay.Core.App.FileExpansion expansion) {
        _workingTreeExpansions[file.Key] = expansion;
        if (file.ApplyExpansion(expansion)) RenderSelectedDiffRows();
    }

    /// <summary>Collapses or expands an uncommitted changes section in the diff pane.</summary>
    public void ToggleDiffSection(string section) {
        if (!_collapsedDiffSections.Remove(section)) _collapsedDiffSections.Add(section);
        RenderSelectedDiffRows();
    }

    [RelayCommand]
    private void ToggleDiffFileCollapsed(DiffFileProjection file) {
        file.IsCollapsed = !file.IsCollapsed;
        RenderSelectedDiffRows();
    }

    [RelayCommand]
    private void ToggleDiffFileContext(DiffFileProjection file) {
        if (IsWorkingTreeDiffShown) {
            if (file.HasHiddenContext) RevealWorkingTreeContext(file, AllLines);
            else if (file.HasRevealedContext) CollapseWorkingTreeContext(file);
            return;
        }
        if (_selectedDiffHash == null) {
            return;
        }

        var key = new GitKay.Core.GitService.DiffFileKey(file.Key.OldPath, file.Key.NewPath);
        if (file.HasHiddenContext) {
            _dispatch?.Invoke(GitKay.Core.App.Msg.NewExpandDiffFile(_selectedDiffHash, key, Stopwatch.GetTimestamp()));
        }
        else if (file.HasRevealedContext) {
            _dispatch?.Invoke(GitKay.Core.App.Msg.NewCollapseDiffFileContext(_selectedDiffHash, key));
        }
    }

    [RelayCommand]
    private void ExpandDiffGap(DiffGapExpansionRequest request) {
        if (IsWorkingTreeDiffShown) {
            if (request.File != null && GitKay.Core.DiffExpansion.revealRange(request.Direction, request.Gap) is { } range)
                RevealWorkingTreeContext(request.File, range.Value);
            return;
        }
        if (_selectedDiffHash == null) {
            return;
        }

        _dispatch?.Invoke(GitKay.Core.App.Msg.NewExpandDiffGap(
            _selectedDiffHash,
            request.Gap,
            request.Direction,
            Stopwatch.GetTimestamp()));
    }

    private void SyncSelectedDiffSelection(DiffFileProjection? selectedDiffFile) {
        _suppressDiffSelectionSync = true;
        try {
            SelectedDiffFile = selectedDiffFile;
            SelectedDiffRow = selectedDiffFile?.Header;
        }
        finally {
            _suppressDiffSelectionSync = false;
        }
    }

    private static bool RowMatchesFindQuery(IDiffRowProjection row, Func<string, bool> matches) =>
        row switch {
            DiffFileHeaderProjection fileHeader => matches(fileHeader.DisplayPath),
            DiffHunkHeaderProjection hunkHeader => matches(hunkHeader.Header),
            DiffLineProjection line => matches(line.Content),
            _ => false,
        };
}
