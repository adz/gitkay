using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GitKay.UI;

/// <summary>A right-click on a file header or code line; handlers add file actions to <see cref="Menu"/>.</summary>
public sealed class DiffFileMenuEventArgs(DiffFileProjection file, int? lineNumber, ContextMenu menu) : EventArgs {
    public DiffFileProjection File { get; } = file;
    /// <summary>The new-side (or old-side, for removed lines) line number that was clicked.</summary>
    public int? LineNumber { get; } = lineNumber;
    public ContextMenu Menu { get; } = menu;
}

/// <summary>Index-addressable diff viewport. It realizes no child controls and draws only visible rows.</summary>
public sealed class DiffSurfaceControl : Control, GitKay.Core.Vim.IVimHost, IOverviewSource {
    private List<OverviewMark> _overviewMarks = new();
    private bool _overviewDirty = true;

    public IReadOnlyList<OverviewMark> OverviewMarks {
        get {
            if (_overviewDirty) RebuildOverview();
            return _overviewMarks;
        }
    }

    public ScrollViewer? OverviewScrollViewer => _scrollViewer;
    public event EventHandler? OverviewChanged;

    private void InvalidateOverview() {
        _overviewDirty = true;
        OverviewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Added/removed runs and search/find matches, as fractions of the document height.</summary>
    private void RebuildOverview() {
        _overviewDirty = false;
        _overviewMarks = new List<OverviewMark>();
        var total = _tops.Length > 0 ? _tops[^1] : 0;
        if (total <= 0) return;

        var search = SearchHighlightQuery?.Trim();
        var find = FindQuery?.Trim();
        var searchQuery = _queries.Get(SearchHighlightUseRegex, search ?? "");
        var findQuery = _queries.Get(FindUseRegex, find ?? "");
        var runStart = -1;
        OverviewMarkKind runKind = OverviewMarkKind.Added;

        void CloseRun(int end) {
            if (runStart < 0) return;
            _overviewMarks.Add(new OverviewMark(_tops[runStart] / total, (_tops[end] - _tops[runStart]) / total, runKind));
            runStart = -1;
        }

        for (var i = 0; i < _rows.Length; i++) {
            if (_rows[i] is RenderedMarkdownRowProjection rendered) {
                var renderedKind = rendered.IsAdded ? OverviewMarkKind.Added : rendered.IsRemoved ? OverviewMarkKind.Removed : rendered.IsChanged ? OverviewMarkKind.Modified : (OverviewMarkKind?)null;
                if (renderedKind != runKind || renderedKind == null) CloseRun(i);
                if (renderedKind is { } renderedChanged && runStart < 0) { runStart = i; runKind = renderedChanged; }
                var renderedTop = _tops[i] / total;
                var renderedHeight = (_tops[i + 1] - _tops[i]) / total;
                if (searchQuery.IsMatch(rendered.Text)) _overviewMarks.Add(new OverviewMark(renderedTop, renderedHeight, OverviewMarkKind.SearchMatch));
                if (findQuery.IsMatch(rendered.Text)) _overviewMarks.Add(new OverviewMark(renderedTop, renderedHeight, OverviewMarkKind.FindMatch));
                continue;
            }
            if (_rows[i] is not DiffLineProjection line) {
                CloseRun(i);
                continue;
            }

            OverviewMarkKind? kind = line.IsAdded ? OverviewMarkKind.Added : line.IsRemoved ? OverviewMarkKind.Removed : null;
            if (kind != runKind || kind == null) CloseRun(i);
            if (kind is { } changed && runStart < 0) { runStart = i; runKind = changed; }

            var top = _tops[i] / total;
            var height = (_tops[i + 1] - _tops[i]) / total;
            if (searchQuery.IsMatch(line.Content)) _overviewMarks.Add(new OverviewMark(top, height, OverviewMarkKind.SearchMatch));
            if (findQuery.IsMatch(line.Content)) _overviewMarks.Add(new OverviewMark(top, height, OverviewMarkKind.FindMatch));
        }

        CloseRun(_rows.Length);
    }

    /// <summary>Row height for code lines; scales with <see cref="CodeFontSize"/> (20 at the default 12).</summary>
    private double LineHeight => Math.Round(CodeFontSize * 20 / 12);
    /// <summary>Scales a gutter measurement laid out for the default 12px code font.</summary>
    private double Z(double value) => value * CodeFontSize / 12;
    private double CodeTextTop => Math.Floor((LineHeight - CodeFontSize * 4 / 3) / 2);
    public const double DefaultCodeFontSize = 12;
    private const double HunkHeight = 28;
    private const double GapHeight = 40;
    private const double FileHeight = 50;
    private const double SectionHeight = 34;
    private const double FileCardTop = 12;
    private const double FileChevronWidth = 32;
    /// <summary>Breathing room down each side of a file card, so its title doesn't sit hard against the pane edge.</summary>
    private const double FileCardInset = 6;
    private const int HeaderChevronAction = 100;
    private const int HeaderContextAction = 101;
    private const int HeaderPreviewAction = 102;
    private const int HeaderChangesOnlyAction = 103;
    private const int MaxHighlightedLineLength = 240;
    private const int MaxLayoutCacheEntries = 2048;

    /// <summary>Counters for the scrolling benchmark; they cost an increment and say where a stutter came from.</summary>
    internal static int DiagRebuilds, DiagPrefetches, DiagCacheClears, DiagLayoutsBuilt, DiagHighlights, DiagRenders;
    internal static double DiagRenderMs, DiagRenderMaxMs, DiagPrefetchMs;
    private static readonly Typeface CodeTypeface = new(FontStacks.Mono);
    private static readonly Typeface ProseTypeface = new(FontStacks.Ui);
    private static readonly Typeface EmphasisTypeface = new(FontStacks.Ui, FontStyle.Italic, FontWeight.Normal);
    private static readonly Typeface StrongTypeface = new(FontStacks.Ui, FontStyle.Normal, FontWeight.Bold);
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromRgb(51, 51, 51)).ToImmutable();
    private static readonly IBrush FileBrush = new SolidColorBrush(Color.FromRgb(157, 167, 179)).ToImmutable();
    private static readonly IBrush HunkBrush = new SolidColorBrush(Color.FromRgb(136, 136, 136)).ToImmutable();

    public static readonly StyledProperty<IEnumerable<IDiffRowProjection>?> ItemsSourceProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, IEnumerable<IDiffRowProjection>?>(nameof(ItemsSource));
    public static readonly StyledProperty<IDiffRowProjection?> SelectedItemProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, IDiffRowProjection?>(nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> CodeFontSizeProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, double>(nameof(CodeFontSize), DefaultCodeFontSize);
    public static readonly StyledProperty<GitKay.Core.DiffLayout> DiffLayoutProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, GitKay.Core.DiffLayout>(nameof(DiffLayout), GitKay.Core.DiffLayout.Unified);
    public static readonly StyledProperty<string?> FindQueryProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, string?>(nameof(FindQuery));
    public static readonly StyledProperty<bool> FindUseRegexProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, bool>(nameof(FindUseRegex));
    public static readonly StyledProperty<string?> SearchHighlightQueryProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, string?>(nameof(SearchHighlightQuery));
    public static readonly StyledProperty<string?> SearchPathQueryProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, string?>(nameof(SearchPathQuery));
    public static readonly StyledProperty<bool> SearchHighlightUseRegexProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, bool>(nameof(SearchHighlightUseRegex));
    public static readonly StyledProperty<ICommand?> ToggleFileCommandProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, ICommand?>(nameof(ToggleFileCommand));
    public static readonly StyledProperty<ICommand?> ToggleFileContextCommandProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, ICommand?>(nameof(ToggleFileContextCommand));
    public static readonly StyledProperty<IReadOnlyDictionary<string, Bitmap>?> RenderedImagesProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, IReadOnlyDictionary<string, Bitmap>?>(nameof(RenderedImages));
    public static readonly StyledProperty<IReadOnlyDictionary<string, Bitmap>?> OldRenderedImagesProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, IReadOnlyDictionary<string, Bitmap>?>(nameof(OldRenderedImages));
    public static readonly StyledProperty<bool> RenderedImagesLoadingProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, bool>(nameof(RenderedImagesLoading));
    public static readonly StyledProperty<ICommand?> ExpandGapCommandProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, ICommand?>(nameof(ExpandGapCommand));

    private IDiffRowProjection[] _rows = Array.Empty<IDiffRowProjection>();
    private double[] _tops = [0];
    private double _measurementWidth = 1000;
    private INotifyCollectionChanged? _collection;
    private ScrollViewer? _scrollViewer;
    private ViewportAnchor? _pendingAnchor;
    private GapActionHit _hoveredGapAction = GapActionHit.None;

    /// <summary>Whether Ctrl is down: every gap expander then reads, and acts, as "reveal the whole gap".</summary>
    private bool _expandAllHeld;
    private GapActionHit _pressedGapAction = GapActionHit.None;
    private ExpansionAnchor? _expansionAnchor;
    private HashSet<(int?, int?)> _knownLineKeys = new();
    private readonly HashSet<IDiffRowProjection> _growingRows = new(ReferenceEqualityComparer.Instance);
    private DispatcherTimer? _growTimer;
    private long _growStartedAt;
    private double _growProgress = 1;
    private const double GrowDurationMs = 100;
    private static readonly TimeSpan ExpansionAnchorLifetime = TimeSpan.FromSeconds(10);
    private const double GapGutterWidth = 56;
    private bool _programmaticScroll;
    private readonly Dictionary<string, FormattedText> _plainLayouts = new(StringComparer.Ordinal);
    private readonly List<RenderedLinkHit> _renderedLinks = new();
    private readonly List<RenderedTextHit> _renderedTextHits = new();

    public event EventHandler<DiffFileMenuEventArgs>? FileContextRequested;
    public event EventHandler<DiffFileProjection>? PreviewRequested;

    /// <summary>The rendered file's "changes only" icon was clicked.</summary>
    public event EventHandler<DiffFileProjection>? ChangesOnlyRequested;
    public event EventHandler<string>? RenderedLinkRequested;
    /// <summary>Raised after text is copied, with the number of lines copied (0 for part of a line).</summary>
    public event EventHandler<int>? TextCopied;
    /// <summary>Lets the host put its own actions at the top of a line's right-click menu.</summary>
    public event Action<ContextMenu>? LineMenuOpening;

    public IEnumerable<IDiffRowProjection>? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public IDiffRowProjection? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public GitKay.Core.DiffLayout DiffLayout { get => GetValue(DiffLayoutProperty); set => SetValue(DiffLayoutProperty, value); }
    /// <summary>Font size of diff code and line numbers only; file headers and hunk labels keep their size.</summary>
    public double CodeFontSize { get => GetValue(CodeFontSizeProperty); set => SetValue(CodeFontSizeProperty, value); }
    /// <summary>Find-in-diff text; every occurrence in visible lines is highlighted.</summary>
    public string? FindQuery { get => GetValue(FindQueryProperty); set => SetValue(FindQueryProperty, value); }
    public bool FindUseRegex { get => GetValue(FindUseRegexProperty); set => SetValue(FindUseRegexProperty, value); }
    /// <summary>The commit search's diff term; highlighted in a separate colour beneath find-in-diff matches.</summary>
    public string? SearchHighlightQuery { get => GetValue(SearchHighlightQueryProperty); set => SetValue(SearchHighlightQueryProperty, value); }
    /// <summary>The commit search's path term; underlined in file headers.</summary>
    public string? SearchPathQuery { get => GetValue(SearchPathQueryProperty); set => SetValue(SearchPathQueryProperty, value); }
    public bool SearchHighlightUseRegex { get => GetValue(SearchHighlightUseRegexProperty); set => SetValue(SearchHighlightUseRegexProperty, value); }
    public ICommand? ToggleFileCommand { get => GetValue(ToggleFileCommandProperty); set => SetValue(ToggleFileCommandProperty, value); }
    public ICommand? ToggleFileContextCommand { get => GetValue(ToggleFileContextCommandProperty); set => SetValue(ToggleFileContextCommandProperty, value); }
    /// Images for the current/new side. Kept as RenderedImages for whole-file binding compatibility.
    public IReadOnlyDictionary<string, Bitmap>? RenderedImages { get => GetValue(RenderedImagesProperty); set => SetValue(RenderedImagesProperty, value); }
    public IReadOnlyDictionary<string, Bitmap>? OldRenderedImages { get => GetValue(OldRenderedImagesProperty); set => SetValue(OldRenderedImagesProperty, value); }
    public bool RenderedImagesLoading { get => GetValue(RenderedImagesLoadingProperty); set => SetValue(RenderedImagesLoadingProperty, value); }
    public ICommand? ExpandGapCommand { get => GetValue(ExpandGapCommandProperty); set => SetValue(ExpandGapCommandProperty, value); }

    static DiffSurfaceControl() {
        ItemsSourceProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.RebuildRows());
        SelectedItemProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.InvalidateVisual());
        DiffLayoutProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.InvalidateVisual());
        CodeFontSizeProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.OnCodeFontSizeChanged());
        FindQueryProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => { control.InvalidateVisual(); control.InvalidateOverview(); });
        FindUseRegexProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.InvalidateVisual());
        SearchHighlightQueryProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => { control.InvalidateVisual(); control.InvalidateOverview(); });
        SearchPathQueryProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.InvalidateVisual());
        SearchHighlightUseRegexProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.InvalidateVisual());
        RenderedImagesProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.RebuildRows());
        OldRenderedImagesProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.RebuildRows());
        RenderedImagesLoadingProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.InvalidateVisual());
    }

    public DiffSurfaceControl() {
        Focusable = true;
        ActualThemeVariantChanged += (_, _) => {
            _plainLayouts.Clear();
            InvalidateVisual();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _scrollViewer = this.FindAncestorOfType<ScrollViewer>();
        if (_scrollViewer != null) {
            _scrollViewer.ScrollChanged += OnScrollChanged;
            _scrollViewer.AddHandler(InputElement.ScrollGestureEvent, OnScrollGesture,
                Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble);
            _scrollViewer.AddHandler(InputElement.ScrollGestureEndedEvent, OnScrollGestureEnded,
                Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble);
        }
        OverviewChanged?.Invoke(this, EventArgs.Empty);
        RebuildRows();
    }

    private bool _gestureScrolling;

    private void OnScrollGesture(object? sender, ScrollGestureEventArgs e) {
        _gestureScrolling = true;
        _userScrolls++;
        _pendingAnchor = null;
        _anchorTarget = null;
        _expansionAnchor = null;
        _pendingScrollOffset = null;
    }

    private void OnScrollGestureEnded(object? sender, ScrollGestureEndedEventArgs e) => _gestureScrolling = false;

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        if (_scrollViewer != null) {
            _scrollViewer.ScrollChanged -= OnScrollChanged;
            _scrollViewer.RemoveHandler(InputElement.ScrollGestureEvent, OnScrollGesture);
            _scrollViewer.RemoveHandler(InputElement.ScrollGestureEndedEvent, OnScrollGestureEnded);
        }
        DetachCollection();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnCodeFontSizeChanged() {
        // Keep the row at the top of the viewport in place while rows change height.
        var anchorIndex = _scrollViewer == null || _rows.Length == 0 ? -1 : FindRow(_scrollViewer.Offset.Y);
        var within = anchorIndex < 0 ? 0 : (_scrollViewer!.Offset.Y - _tops[anchorIndex]) / Math.Max(1, _tops[anchorIndex + 1] - _tops[anchorIndex]);
        ComputeTops();
        InvalidateMeasure();
        InvalidateVisual();
        InvalidateOverview();
        if (anchorIndex >= 0) {
            var top = _tops[anchorIndex] + within * (_tops[anchorIndex + 1] - _tops[anchorIndex]);
            Dispatcher.UIThread.Post(() => SetOffsetWithoutScrolling(Math.Max(0, top)), DispatcherPriority.Loaded);
        }
    }

    private void RebuildRows() {
        DiagRebuilds++;
        if (_expansionAnchor != null && System.Diagnostics.Stopwatch.GetElapsedTime(_expansionAnchor.StartedAt) > ExpansionAnchorLifetime)
            _expansionAnchor = null;
        if (_expansionAnchor == null)
            CaptureViewportAnchor();
        DetachCollection();
        var previousRows = _rows;
        // Remember where the selection was, so it can be kept in place if its row disappears (e.g. an expanded gap).
        if (SelectedItem != null && Array.IndexOf(previousRows, SelectedItem) is var previousIndex and >= 0)
            _selectionIndexToRestore = previousIndex;
        _rows = ItemsSource?.ToArray() ?? Array.Empty<IDiffRowProjection>();
        RestoreSelectionPosition();
        var sourceChanged = !ReferenceEquals(ItemsSource, _lastItemsSource);
        // A different diff (or reshaped rows) invalidates row-based selection positions.
        if (_textSelection != null && (previousRows.Length != _rows.Length || sourceChanged)) {
            _textSelection = null;
            _visualAnchorRow = -1;
        }
        if (sourceChanged) _horizontalOffset = 0;
        _lastItemsSource = ItemsSource;
        _hoveredGapAction = GapActionHit.None;
        _pressedGapAction = GapActionHit.None;
        TrackInsertedRows();
        ComputeTops();
        if (ItemsSource is INotifyCollectionChanged collection) {
            _collection = collection;
            _collection.CollectionChanged += OnCollectionChanged;
        }
        InvalidateMeasure();
        InvalidateVisual();
        InvalidateOverview();
        ApplyPendingScrollOffset();
        if (_expansionAnchor != null)
            RestoreExpansionAnchor();
        else
            RestoreViewportAnchor();
    }

    private double? _pendingScrollOffset;

    /// <summary>Scroll to an offset once rows with content arrive (restoring a revisited commit's position).</summary>
    public void RestoreScrollOffsetWhenReady(double offset) => _pendingScrollOffset = offset;

    public double CurrentScrollOffset => _scrollViewer?.Offset.Y ?? 0;

    /// A semantic line anchor used while source rows are replaced by rendered blocks (or vice versa).
    /// <para>The line number is only meaningful inside its own file: every file in a multi-file diff
    /// restarts at line 1, so the owning file travels with the anchor.</para>
    public readonly record struct MarkdownViewAnchor(string File, int Line, double ViewportY, string? SelectedFile, int? SelectedLine);

    private static int LineOfRow(IDiffRowProjection row) => row switch {
        DiffLineProjection source => source.NewLineNo ?? source.OldLineNo ?? 1,
        RenderedMarkdownRowProjection rendered => rendered.Located.FirstLine,
        _ => 1,
    };

    private static bool RowContainsLine(IDiffRowProjection row, int line) => row switch {
        DiffLineProjection source => (source.NewLineNo ?? source.OldLineNo) == line,
        RenderedMarkdownRowProjection rendered => line >= rendered.Located.FirstLine && line <= rendered.Located.LastLine,
        _ => false,
    };

    /// <summary>The path of the file whose header most recently preceded this row.</summary>
    private string FileOfRow(int index) {
        for (var i = Math.Min(index, _rows.Length - 1); i >= 0; i--)
            if (_rows[i] is DiffFileHeaderProjection header) return header.File.ContentPath;
        return "";
    }

    public MarkdownViewAnchor? CaptureMarkdownViewAnchor() {
        if (_scrollViewer == null || _rows.Length == 0) return null;
        var index = FindRow(_scrollViewer.Offset.Y);
        while (index < _rows.Length && _rows[index] is not (DiffLineProjection or RenderedMarkdownRowProjection)) index++;
        if (index >= _rows.Length) return null;
        string? selectedFile = null;
        int? selectedLine = null;
        if (SelectedItem is DiffLineProjection or RenderedMarkdownRowProjection
            && Array.IndexOf(_rows, SelectedItem) is var selectedIndex and >= 0) {
            selectedFile = FileOfRow(selectedIndex);
            selectedLine = LineOfRow((IDiffRowProjection)SelectedItem);
        }
        return new MarkdownViewAnchor(FileOfRow(index), LineOfRow(_rows[index]),
            _tops[index] - _scrollViewer.Offset.Y, selectedFile, selectedLine);
    }

    /// <summary>Finds a line inside one file, so an identical line number in an earlier file can't steal the anchor.</summary>
    private int FindRowInFile(string file, int line) {
        var current = "";
        for (var i = 0; i < _rows.Length; i++) {
            if (_rows[i] is DiffFileHeaderProjection header) { current = header.File.ContentPath; continue; }
            if (current == file && RowContainsLine(_rows[i], line)) return i;
        }
        return -1;
    }

    public void RestoreMarkdownViewAnchor(MarkdownViewAnchor? anchor) {
        if (anchor is not { } value || _scrollViewer == null) return;
        var index = FindRowInFile(value.File, value.Line);
        if (index >= 0) SetOffsetWithoutScrolling(Math.Max(0, _tops[index] - value.ViewportY));
        if (value.SelectedLine is { } selectedLine && value.SelectedFile is { } selectedFile
            && FindRowInFile(selectedFile, selectedLine) is var selectedIndex and >= 0)
            SelectedItem = _rows[selectedIndex];
    }

    private void ApplyPendingScrollOffset() {
        if (_pendingScrollOffset is not { } offset || _scrollViewer == null || !_rows.Any(row => row is DiffLineProjection)) return;
        _pendingScrollOffset = null;
        // Background priority runs after the selection's scroll-into-view, so the restored position wins.
        Dispatcher.UIThread.Post(() => SetOffsetWithoutScrolling(offset), DispatcherPriority.Background);
    }

    private int _selectionIndexToRestore = -1;

    private void RestoreSelectionPosition() {
        // Lists are replaced by clearing then refilling; wait for the refill.
        if (_rows.Length == 0) return;
        if (SelectedItem != null && Array.IndexOf(_rows, SelectedItem) >= 0) {
            _selectionIndexToRestore = -1;
            return;
        }

        if (_selectionIndexToRestore < 0) return;
        var index = Math.Clamp(_selectionIndexToRestore, 0, _rows.Length - 1);
        _selectionIndexToRestore = -1;
        if (SelectedItem != null) SelectedItem = _rows[index];
    }

    private void ComputeTops() {
        if (_tops.Length != _rows.Length + 1) _tops = new double[_rows.Length + 1];
        _tops[0] = 0;
        for (var i = 0; i < _rows.Length; i++) _tops[i + 1] = _tops[i] + RowHeight(_rows[i]);
    }

    /// <summary>Marks source rows that did not exist before an expansion so they grow in; other rows stay put.</summary>
    private void TrackInsertedRows() {
        var lineKeys = new HashSet<(int?, int?)>();
        foreach (var row in _rows)
            if (row is DiffLineProjection line) lineKeys.Add((line.OldLineNo, line.NewLineNo));

        // A replacement publishes an empty list first; only remember sets that contain source rows.
        if (lineKeys.Count == 0) return;

        if (_expansionAnchor != null && _knownLineKeys.Count > 0) {
            _growingRows.Clear();
            foreach (var row in _rows)
                if (row is DiffLineProjection line && !_knownLineKeys.Contains((line.OldLineNo, line.NewLineNo)))
                    _growingRows.Add(row);
            if (_growingRows.Count > 0) StartGrowAnimation();
        }
        else if (_expansionAnchor == null) {
            StopGrowAnimation();
        }

        _knownLineKeys = lineKeys;
    }

    private void StartGrowAnimation() {
        _growStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _growProgress = 0;
        _growTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => OnGrowTick());
        _growTimer.Start();
    }

    private void StopGrowAnimation() {
        _growTimer?.Stop();
        _growProgress = 1;
        _growingRows.Clear();
    }

    private void OnGrowTick() {
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_growStartedAt).TotalMilliseconds;
        _growProgress = Math.Clamp(elapsed / GrowDurationMs, 0, 1);
        var finished = _growProgress >= 1;
        if (finished) StopGrowAnimation();
        ComputeTops();
        InvalidateMeasure();
        InvalidateVisual();
        if (_expansionAnchor != null) {
            RestoreExpansionAnchor();
            if (finished && !_rows.Any(row => row is DiffGapProjection gap && gap.Gap.Equals(_expansionAnchor?.Gap)))
                _expansionAnchor = null;
        }
    }

    /// <summary>Keeps the boundary line next to an expanded gap at its viewport Y until the expansion is realized.</summary>
    private void RestoreExpansionAnchor() {
        if (_expansionAnchor is not { } expansion || _scrollViewer == null) return;
        var anchor = expansion.Anchor;
        var index = FindAnchorRow(anchor);
        if (index < 0) {
            // Rows are briefly empty while the list is replaced; a different diff drops the anchor.
            if (_rows.Any(row => row is DiffLineProjection)) _expansionAnchor = null;
            return;
        }

        var realized = !_rows.Any(row => row is DiffGapProjection gap && gap.Gap.Equals(expansion.Gap));
        if (realized && _growingRows.Count == 0) _expansionAnchor = null;

        var row = _rows[index];
        var scrolls = _userScrolls;
        Dispatcher.UIThread.Post(() => {
            if (_scrollViewer == null || _userScrolls != scrolls) return;
            var current = Array.IndexOf(_rows, row);
            if (current < 0) return;
            SetOffsetWithoutScrolling(Math.Max(0, _tops[current] - anchor.ViewportOffset));
        }, DispatcherPriority.Loaded);
    }

    private int FindAnchorRow(ViewportAnchor anchor) {
        // Row instances survive re-rendering, so match the exact row first; line numbers and text repeat across files.
        if (anchor.Row != null && Array.IndexOf(_rows, anchor.Row) is var exact and >= 0) return exact;
        if (anchor.Row is DiffFileHeaderProjection) return -1;
        return Array.FindIndex(_rows, row => row is DiffLineProjection line
            && line.OldLineNo == anchor.OldLineNo
            && line.NewLineNo == anchor.NewLineNo
            && line.Content == anchor.Content);
    }

    /// <summary>Toggles a file's rows while its header stays at the same viewport position.</summary>
    /// <summary>How far into each collapsed file the view had scrolled, so expanding it returns there.</summary>
    private readonly Dictionary<DiffFileProjection, double> _collapsedScroll = new();

    private void ToggleFileAnchored(DiffFileHeaderProjection header, ICommand? command) {
        if (command?.CanExecute(header.File) != true) return;
        var index = Array.IndexOf(_rows, header);
        if (_scrollViewer != null && index >= 0) {
            _expansionAnchor = null;
            // A header scrolled above the viewport is drawn as the sticky header: keep its card exactly where that
            // was drawn (flush with the top, or pushed up by the next file), not where its row would be.
            var rowTop = _stickyIndex == index ? _stickyRowTop : _tops[index];
            _pendingAnchor = new ViewportAnchor(null, null, "", rowTop - _scrollViewer.Offset.Y, header);

            if (!header.File.IsCollapsed) {
                // Collapsing: remember how far into the file the view had reached.
                var into = _scrollViewer.Offset.Y - _tops[index];
                if (into > 0) _collapsedScroll[header.File] = into;
                else _collapsedScroll.Remove(header.File);
            }
            else if (_collapsedScroll.TryGetValue(header.File, out var into)) {
                // Expanding it again: go back to where its content was.
                _collapsedScroll.Remove(header.File);
                _expandRestore = (header, into);
            }
        }
        command.Execute(header.File);
    }

    /// <summary>A file being expanded, and how far into it to scroll once its rows are back.</summary>
    private (DiffFileHeaderProjection Header, double Into)? _expandRestore;

    /// <summary>Scrolls back into a re-expanded file, no further than its own content.</summary>
    private void ApplyExpandRestore() {
        if (_expandRestore is not { } restore || _scrollViewer == null) return;
        _expandRestore = null;
        var index = Array.IndexOf(_rows, restore.Header);
        if (index < 0) return;
        var next = index + 1;
        while (next < _rows.Length && _rows[next] is not DiffFileHeaderProjection) next++;
        var fileHeight = (next < _rows.Length ? _tops[next] : _tops[^1]) - _tops[index];
        var into = Math.Min(restore.Into, Math.Max(0, fileHeight - _scrollViewer.Viewport.Height));
        SetOffsetWithoutScrolling(Math.Max(0, _tops[index] + into));
    }

    private void BeginExpansion(int gapIndex, DiffGapProjection gap, GitKay.Core.DiffExpansion.ExpandDirection direction) {
        if (_scrollViewer == null) {
            _expansionAnchor = null;
            return;
        }

        // Upward expansion grows above the following content; downward and complete expansion
        // grow below the preceding content, leaving the scroll offset unchanged.
        var below = NearestLine(gapIndex, +1);
        var above = NearestLine(gapIndex, -1);
        var anchorIndex = direction.IsUp ? (below >= 0 ? below : above) : (above >= 0 ? above : below);
        if (anchorIndex < 0 || _rows[anchorIndex] is not DiffLineProjection line) {
            _expansionAnchor = null;
            return;
        }

        _pendingAnchor = null;
        _expansionAnchor = new ExpansionAnchor(
            gap.Gap,
            new ViewportAnchor(line.OldLineNo, line.NewLineNo, line.Content, _tops[anchorIndex] - _scrollViewer.Offset.Y, line),
            System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private int NearestLine(int index, int step) {
        for (var i = index + step; i >= 0 && i < _rows.Length; i += step) {
            if (_rows[i] is DiffLineProjection) return i;
            if (_rows[i] is DiffFileHeaderProjection) return -1;
        }
        return -1;
    }

    private void CaptureViewportAnchor() {
        if (_pendingAnchor != null || _scrollViewer == null || _rows.Length == 0) return;
        var first = FindRow(_scrollViewer.Offset.Y);
        for (var index = first; index < _rows.Length; index++) {
            if (_rows[index] is not DiffLineProjection line) continue;
            _pendingAnchor = new ViewportAnchor(line.OldLineNo, line.NewLineNo, line.Content, _tops[index] - _scrollViewer.Offset.Y, line);
            return;
        }
    }

    private void RestoreViewportAnchor() {
        if (_pendingAnchor is not { } anchor || _scrollViewer == null || _rows.Length == 0) return;
        var index = FindAnchorRow(anchor);
        if (index < 0) {
            // Context reloads temporarily leave only file headers. Keep the anchor until
            // source rows arrive; a genuinely different diff clears it after that.
            if (_rows.Any(row => row is DiffLineProjection))
                _pendingAnchor = null;
            return;
        }
        _pendingAnchor = null;
        var row = _rows[index];
        var target = Math.Max(0, _tops[index] - anchor.ViewportOffset);
        // Apply it now so the next frame is already in the right place: posting it paints one frame at the old
        // offset first, which reads as a flash when a pinned file header is collapsed.
        SetOffsetWithoutScrolling(target);
        if (_expandRestore != null) {
            ApplyExpandRestore();
            return;
        }
        // The scroll viewer clamps to the extent it knows about, which grows or shrinks with these rows; re-apply
        // once this layout pass has measured them.
        _anchorTarget = (row, anchor.ViewportOffset);
        var scrolls = _userScrolls;
        Dispatcher.UIThread.Post(() => { if (_userScrolls == scrolls) ApplyAnchorTarget(); else _anchorTarget = null; }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Counts scrolls the user made. Anchors are captured before rows change and re-applied a layout pass later; if a
    /// scroll happened in between, re-applying would drag the view back under the hand, which reads as scrolling
    /// sticking until a fast flick outruns it.
    /// </summary>
    private int _userScrolls;

    /// <summary>The row the view is anchored to while the scroll viewer catches up with the new extent.</summary>
    private (IDiffRowProjection Row, double ViewportOffset)? _anchorTarget;

    private void ApplyAnchorTarget() {
        if (_anchorTarget is not { } target || _scrollViewer == null) {
            _anchorTarget = null;
            return;
        }
        _anchorTarget = null;
        var current = Array.IndexOf(_rows, target.Row);
        if (current >= 0) SetOffsetWithoutScrolling(Math.Max(0, _tops[current] - target.ViewportOffset));
    }

    private void DetachCollection() {
        if (_collection != null) _collection.CollectionChanged -= OnCollectionChanged;
        _collection = null;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildRows();

    protected override Size MeasureOverride(Size availableSize) {
        var width = double.IsInfinity(availableSize.Width) ? 1000 : availableSize.Width;
        if (Math.Abs(width - _measurementWidth) > .5) {
            _measurementWidth = width;
            ComputeTops();
            InvalidateOverview();
        }
        return new Size(width, _tops[^1]);
    }

    protected override Size ArrangeOverride(Size finalSize) {
        var size = base.ArrangeOverride(finalSize);
        // The extent is known by now, so an anchor that the earlier attempt clamped lands correctly, before painting.
        if (_expandRestore != null) ApplyExpandRestore();
        else if (_anchorTarget != null) ApplyAnchorTarget();
        return size;
    }

    /// <summary>Anchoring adjusts the offset without treating it as a new user scroll.</summary>
    private void SetOffsetWithoutScrolling(double y) {
        if (_scrollViewer == null || _gestureScrolling) return;
        _programmaticScroll = true;
        try { _scrollViewer.Offset = _scrollViewer.Offset.WithY(y); }
        finally { _programmaticScroll = false; }
    }

    /// <summary>
    /// Scrolling repaints, and nothing else. It used to cancel every in-flight highlight, allocate a cancellation
    /// token and a task per scroll event, and start a 40ms timer that prefetched five viewports of lines once the
    /// hand paused — which is exactly when a slow scroll pauses. Lines are coloured as they are drawn instead.
    /// </summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) {
        if (!_programmaticScroll) _userScrolls++;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context) {
        var diagStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try { RenderCore(context); }
        finally {
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(diagStart).TotalMilliseconds;
            DiagRenders++;
            DiagRenderMs += elapsed;
            if (elapsed > DiagRenderMaxMs) DiagRenderMaxMs = elapsed;
        }
    }

    private void RenderCore(DrawingContext context) {
        _renderedLinks.Clear();
        _renderedTextHits.Clear();
        var offset = _scrollViewer?.Offset.Y ?? 0;
        var viewport = _scrollViewer?.Viewport.Height ?? Bounds.Height;
        // Hit testing follows what was drawn: paint a transparent backdrop so blank areas (context lines,
        // space past the end of code) still receive clicks.
        context.FillRectangle(Brushes.Transparent, new Rect(0, offset, Bounds.Width, viewport));
        var first = FindRow(offset);
        var last = Math.Min(_rows.Length, FindRow(offset + viewport) + 2);
        for (var i = first; i < last; i++) {
            var height = _tops[i + 1] - _tops[i];
            if (height <= 0) continue;
            if (_growingRows.Count > 0 && _growingRows.Contains(_rows[i])) {
                using (context.PushClip(new Rect(0, _tops[i], Bounds.Width, height)))
                    DrawRow(context, _rows[i], _tops[i], i);
            }
            else {
                DrawRow(context, _rows[i], _tops[i], i);
            }
        }

        DrawStickyHeader(context, offset, first);
    }

    private static readonly IBrush StickyWindowFallback = new SolidColorBrush(Color.FromRgb(0x0D, 0x11, 0x17)).ToImmutable();
    private static readonly IBrush StickySurfaceFallback = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x22)).ToImmutable();
    private int _stickyIndex = -1;
    private double _stickyRowTop;

    /// <summary>
    /// Pins the current file's header to the top of the viewport while its rows scroll by; the next file's header
    /// pushes it up. Hit testing maps clicks in that area to the pinned header.
    /// </summary>
    private void DrawStickyHeader(DrawingContext context, double offset, int first) {
        _stickyIndex = -1;
        if (_rows.Length == 0) return;

        var header = -1;
        for (var i = Math.Min(first, _rows.Length - 1); i >= 0; i--) {
            if (_rows[i] is DiffFileHeaderProjection) { header = i; break; }
            if (_rows[i] is DiffSectionHeaderProjection) break;
        }

        if (header < 0 || _tops[header] + FileCardTop >= offset) return;

        var next = header + 1;
        while (next < _rows.Length && _rows[next] is not (DiffFileHeaderProjection or DiffSectionHeaderProjection)) next++;
        var cardHeight = FileHeight - FileCardTop;
        var rowTop = offset - FileCardTop;
        if (next < _rows.Length)
            rowTop = Math.Min(rowTop, _tops[next] + FileCardTop - cardHeight - FileCardTop);

        _stickyIndex = header;
        _stickyRowTop = rowTop;
        // Opaque: rows scroll underneath the pinned header.
        var backdrop = new Rect(0, rowTop + FileCardTop - 1, Bounds.Width, cardHeight + 2);
        context.FillRectangle(ThemeBrush("GitKayWindowBrush", StickyWindowFallback), backdrop);
        // The surface band is inset to the card's own width, so a pinned header looks like the one it replaced.
        context.FillRectangle(ThemeBrush("GitKaySurfaceBrush", StickySurfaceFallback),
            backdrop.Deflate(new Thickness(FileCardInset + 0.5, 1, FileCardInset + 0.5, 1)));
        DrawFileHeader(context, (DiffFileHeaderProjection)_rows[header], rowTop, header);
    }

    /// <summary>The row under a point, accounting for the pinned header drawn over the rows.</summary>
    private int RowAt(Point position, out double rowTop) {
        if (_stickyIndex >= 0 && position.Y >= _stickyRowTop + FileCardTop && position.Y < _stickyRowTop + FileHeight) {
            rowTop = _stickyRowTop;
            return _stickyIndex;
        }

        var index = FindRow(position.Y);
        rowTop = (uint)index < (uint)_rows.Length ? _tops[index] : 0;
        return index;
    }

    private void DrawRow(DrawingContext context, IDiffRowProjection row, double y, int index) {
        _drawingRowIndex = index;
        if (ReferenceEquals(row, SelectedItem) && row is not DiffFileHeaderProjection) context.FillRectangle(ThemeBrush("GitKaySelectionBrush", SelectionBrush), new Rect(0, y, Bounds.Width, RowHeight(row)));
        switch (row) {
            case DiffFileHeaderProjection file:
                DrawFileHeader(context, file, y, index);
                break;
            case DiffHunkHeaderProjection hunk:
                context.FillRectangle(ThemeBrush("GitKayHunkBrush", Brushes.Transparent), new Rect(0, y, Bounds.Width, HunkHeight - 1));
                DrawPlain(context, hunk.Header, 12, y + 7, 11, ThemeBrush("GitKaySecondaryTextBrush", HunkBrush));
                break;
            case DiffGapProjection gap:
                DrawGap(context, gap, y, index);
                if (ReferenceEquals(row, SelectedItem))
                    context.DrawRectangle(null, new Pen(ThemeBrush("GitKayAccentBrush", SearchMatchFallback), 1), new Rect(0.5, y + 0.5, Bounds.Width - 1, GapHeight - 1), 3, 3);
                break;
            case DiffLineProjection line:
                DrawLine(context, line, y);
                break;
            case RenderedMarkdownRowProjection rendered:
                DrawRenderedMarkdown(context, rendered, y);
                break;
            case ImagePreviewRowProjection image:
                DrawImagePreview(context, image, y);
                break;
            case RenderedMarkdownGapProjection gap:
                context.FillRectangle(ThemeBrush("GitKayRaisedBrush", CodeBlockFallback), new Rect(12, y + 5, Math.Max(1, Bounds.Width - 24), GapHeight - 10));
                DrawPlain(context, gap.Label, 24, y + 13, 11, ThemeBrush("GitKayMutedTextBrush", HunkBrush));
                break;
            case DiffSectionHeaderProjection section:
                DrawSectionHeader(context, section, y);
                break;
        }
    }

    /// <summary>An uncommitted changes section: chevron, name and file count on a rule, above its file cards.</summary>
    private void DrawSectionHeader(DrawingContext context, DiffSectionHeaderProjection section, double y) {
        var secondary = ThemeBrush("GitKaySecondaryTextBrush", HunkBrush);
        var centerY = y + 20;
        var chevronPen = new Pen(secondary, 1.5, lineCap: PenLineCap.Round);
        var cx = FileChevronWidth / 2 + 1;
        if (section.IsCollapsed) {
            context.DrawLine(chevronPen, new Point(cx - 2, centerY - 4), new Point(cx + 2, centerY));
            context.DrawLine(chevronPen, new Point(cx + 2, centerY), new Point(cx - 2, centerY + 4));
        }
        else {
            context.DrawLine(chevronPen, new Point(cx - 4, centerY - 2), new Point(cx, centerY + 2));
            context.DrawLine(chevronPen, new Point(cx, centerY + 2), new Point(cx + 4, centerY - 2));
        }
        var labelX = FileChevronWidth + 8;
        var label = Layout(section.Name, 13, ThemeBrush("GitKayTextBrush", FileBrush), false);
        context.DrawText(label, new Point(labelX, centerY - label.Height / 2));
        var count = Layout(section.FileCount == 1 ? "1 file" : $"{section.FileCount} files", 11, secondary, false);
        context.DrawText(count, new Point(labelX + label.Width + 10, centerY - count.Height / 2));
        var ruleX = labelX + label.Width + 10 + count.Width + 10;
        context.FillRectangle(ThemeBrush("GitKayBorderBrush", secondary), new Rect(ruleX, centerY, Math.Max(0, Bounds.Width - ruleX - 8), 1));
    }

    private Rect FileCardRect(double y) =>
        new(FileCardInset + 0.5, y + FileCardTop + 0.5, Math.Max(0, Bounds.Width - 2 * FileCardInset - 1), FileHeight - FileCardTop - 1);

    private Rect FileChevronRect(double y) =>
        new(FileCardInset + 4, y + FileCardTop + 5, FileChevronWidth - 6, FileHeight - FileCardTop - 10);

    /// <summary>Where a file header's title starts, inside the card.</summary>
    private static double FileLabelX => FileCardInset + FileChevronWidth + 8;

    /// <summary>
    /// The preview control is a labelled pill, not a bare icon. A rendered Markdown file is obviously rendered, but
    /// reformatted JSON and XML are still text: without a label nothing on screen says which of the two is showing.
    /// </summary>
    private static string PreviewLabel(DiffFileProjection file) =>
        file.IsRenderedMarkdown || file.IsFormattedPreview || file.IsImagePreview ? "Preview" : "Source";

    // The pill's parts, laid out left to right: padding, eye, gap, label, padding.
    private const double PillPadding = 7;
    private const double PillIconWidth = 13;
    private const double PillGap = 6;

    private double PreviewPillWidth(DiffFileProjection file) =>
        PillPadding + PillIconWidth + PillGap + Layout(PreviewLabel(file), 10, FileBrush, false).Width + PillPadding;

    private Rect FilePreviewRect(DiffFileHeaderProjection file, double y) {
        var pathWidth = Layout(file.DisplayPath, 12, FileBrush, false).Width;
        return new Rect(FileLabelX + pathWidth + 8, y + FileCardTop + 6, PreviewPillWidth(file.File), FileHeight - FileCardTop - 12);
    }

    /// <summary>Only while the file is rendered: it says what to do with the parts of the document that did not change.</summary>
    private static bool HasChangesOnlyToggle(DiffFileProjection file) => file.IsRenderedMarkdown;

    private Rect FileChangesOnlyRect(DiffFileHeaderProjection file, double y) {
        var rect = FilePreviewRect(file, y);
        return new Rect(rect.Right + (IsPreviewable(file.File) ? 6 : 0), rect.Y, 26, rect.Height);
    }

    /// <summary>How far apart the header's action icons sit.</summary>
    private const double ActionStep = 30;

    private Rect FileContextRect(DiffFileHeaderProjection file, double y) {
        var after = HasChangesOnlyToggle(file.File) ? FileChangesOnlyRect(file, y).Right : FilePreviewRect(file, y).Right;
        var rect = FilePreviewRect(file, y);
        return new Rect(after + (IsPreviewable(file.File) || HasChangesOnlyToggle(file.File) ? 6 : 0), rect.Y, 26, rect.Height);
    }

    /// <summary>Anything the diff pane can show as something other than its source text.</summary>
    private static bool IsPreviewable(DiffFileProjection file) =>
        !GitKay.Core.Markdown.previewKind(file.ContentPath).IsSourceOnly;

    private static bool HasContextToggle(DiffFileProjection file) =>
        file.IsLoaded && (file.HasHiddenContext || file.HasRevealedContext);

    /// <summary>GitHub-style file card header: chevron, path, expand-all context toggle, and change stats.</summary>
    private void DrawFileHeader(DrawingContext context, DiffFileHeaderProjection header, double y, int index) {
        var file = header.File;
        var card = FileCardRect(y);
        // Soft file headers: a faint card edge and a secondary-coloured path, so code stays the focus.
        var selected = ReferenceEquals(header, SelectedItem);
        // No outline: a faint band marks the file; selection tints it.
        using (context.PushOpacity(selected ? 1 : 0.7)) {
            var fill = selected
                ? ThemeBrush("GitKaySelectionBrush", SelectionBrush)
                : ThemeBrush("GitKaySurfaceBrush", StickySurfaceFallback);
            context.DrawRectangle(fill, null, card, 6, 6);
        }

        var secondary = ThemeBrush("GitKayMutedTextBrush", HunkBrush);
        var text = ThemeBrush("GitKaySecondaryTextBrush", FileBrush);
        var hover = ThemeBrush("GitKayHoverBrush", SelectionBrush);
        var centerY = card.Y + card.Height / 2;

        var chevron = FileChevronRect(y);
        if (IsHeaderPartActive(index, HeaderChevronAction, out var pressed))
            context.DrawRectangle(pressed ? ThemeBrush("GitKaySelectionBrush", SelectionBrush) : hover, null, chevron, 4, 4);
        var chevronPen = new Pen(secondary, 1.5, lineCap: PenLineCap.Round);
        var cx = chevron.X + chevron.Width / 2;
        if (file.IsCollapsed) {
            context.DrawLine(chevronPen, new Point(cx - 2, centerY - 4), new Point(cx + 2, centerY));
            context.DrawLine(chevronPen, new Point(cx + 2, centerY), new Point(cx - 2, centerY + 4));
        }
        else {
            context.DrawLine(chevronPen, new Point(cx - 4, centerY - 2), new Point(cx, centerY + 2));
            context.DrawLine(chevronPen, new Point(cx, centerY + 2), new Point(cx + 4, centerY - 2));
        }

        var path = Layout(file.DisplayPath, 12, text, false);
        context.DrawText(path, new Point(FileLabelX, centerY - path.Height / 2));
        var pathUnderline = ThemeBrush("GitKayAccentBrush", SearchMatchFallback);
        ForEachMatch(file.DisplayPath, SearchPathQuery, SearchHighlightUseRegex, (start, length) =>
            DrawDottedUnderline(context, file.DisplayPath, start, length, FileLabelX, centerY + path.Height / 2 - 1, pathUnderline, 12));

        if (IsPreviewable(file)) {
            var preview = FilePreviewRect(header, y);
            var showingPreview = PreviewLabel(file) == "Preview";
            IsHeaderPartActive(index, HeaderPreviewAction, out var previewPressed);
            // Filled while previewing, outlined while showing source: the state is the pill, not a tint on a glyph.
            var accent = ThemeBrush("GitKayAccentBrush", FileBrush);
            var fill = previewPressed ? ThemeBrush("GitKaySelectionBrush", SelectionBrush)
                : showingPreview ? accent
                : ThemeBrush("GitKayRaisedBrush", CodeBlockFallback);
            context.DrawRectangle(fill, new Pen(showingPreview ? accent : ThemeBrush("GitKayBorderBrush", secondary), 1), preview, 9, 9);
            var label = showingPreview ? ThemeBrush("GitKayWindowBrush", StickyWindowFallback) : secondary;
            DrawPreviewIcon(context, new Rect(preview.X + PillPadding, preview.Y, PillIconWidth, preview.Height), label, PillIconWidth / 2);
            var pillText = Layout(PreviewLabel(file), 10, label, false);
            context.DrawText(pillText, new Point(preview.X + PillPadding + PillIconWidth + PillGap,
                preview.Y + (preview.Height - pillText.Height) / 2));
        }

        if (HasChangesOnlyToggle(file)) {
            var changesOnly = FileChangesOnlyRect(header, y);
            if (IsHeaderPartActive(index, HeaderChangesOnlyAction, out var changesOnlyPressed))
                context.DrawRectangle(changesOnlyPressed ? ThemeBrush("GitKaySelectionBrush", SelectionBrush) : hover, null, changesOnly, 4, 4);
            DrawChangesOnlyIcon(context, changesOnly, file.RenderedChangesOnly,
                file.RenderedChangesOnly ? ThemeBrush("GitKayAccentBrush", FileBrush) : secondary);
        }

        if (HasContextToggle(file)) {
            var toggle = FileContextRect(header, y);
            if (IsHeaderPartActive(index, HeaderContextAction, out var togglePressed))
                context.DrawRectangle(togglePressed ? ThemeBrush("GitKaySelectionBrush", SelectionBrush) : hover, null, toggle, 4, 4);
            DrawContextToggleIcon(context, toggle, file.HasHiddenContext, secondary);
            if (file.IsContextLoading)
                DrawPlain(context, "Loading…", toggle.Right + 6, centerY - 7, 11, ThemeBrush("GitKayMutedTextBrush", HunkBrush));
        }

        // Size blocks stay grey until the header is hovered or selected; the +N −M numbers carry the detail.
        using (context.PushOpacity(selected || _hoveredRowIndex == index ? 1 : 0.35))
            DrawDiffStat(context, file, card.Right - 12, centerY, blocksOnly: true);
        DrawDiffStat(context, file, card.Right - 12, centerY, blocksOnly: false);
    }

    private void DrawRenderedMarkdown(DrawingContext context, RenderedMarkdownRowProjection row, double y) {
        var accent = row.IsAdded ? ThemeBrush("GitKayAddedAccentBrush", Brushes.Green)
            : row.IsRemoved ? ThemeBrush("GitKayRemovedAccentBrush", Brushes.Red)
            : ThemeBrush("GitKayAccentBrush", FileBrush);
        if (row.IsChanged) {
            using (context.PushOpacity(.08)) context.FillRectangle(accent, new Rect(0, y, Bounds.Width, RowHeight(row)));
            context.FillRectangle(accent, new Rect(8, y + 4, 3, Math.Max(1, RowHeight(row) - 8)));
            if (row.IsMoved) DrawPlain(context, row.NewLocated != null ? "↳" : "↱", 13, y + 7, 11, accent);
        }
        if (row.Located.Block is GitKay.Core.MarkdownBlock.CodeBlock codeBlock) {
            if (DiffLayout.IsSideBySide) {
                var middle = Bounds.Width / 2;
                context.FillRectangle(ThemeBrush("GitKayBorderBrush", HunkBrush), new Rect(middle, y, 1, RowHeight(row)));
                if (row.OldLocated?.Block is GitKay.Core.MarkdownBlock.CodeBlock oldCode)
                    DrawRenderedCodeBlock(context, oldCode, y, row, 18, Math.Max(40, middle - 30), true, false);
                if (row.NewLocated?.Block is GitKay.Core.MarkdownBlock.CodeBlock newCode)
                    DrawRenderedCodeBlock(context, newCode, y, row, middle + 12, Math.Max(40, middle - 26), false, false);
            }
            else DrawRenderedCodeBlock(context, codeBlock, y, row, 18, Math.Max(40, Bounds.Width - 32), false, true);
            return;
        }
        if (row.Located.Block is GitKay.Core.MarkdownBlock.Table table) {
            if (DiffLayout.IsSideBySide) {
                var middle = Bounds.Width / 2;
                context.FillRectangle(ThemeBrush("GitKayBorderBrush", HunkBrush), new Rect(middle, y, 1, RowHeight(row)));
                if (row.OldLocated?.Block is GitKay.Core.MarkdownBlock.Table oldTable)
                    DrawRenderedTable(context, oldTable, y, 22, Math.Max(80, middle - 34));
                if (row.NewLocated?.Block is GitKay.Core.MarkdownBlock.Table newTable)
                    DrawRenderedTable(context, newTable, y, middle + 14, Math.Max(80, middle - 28));
            }
            else DrawRenderedTable(context, table, y, 22, Math.Max(80, Bounds.Width - 44));
            return;
        }
        if (TryImageInline(row, out var oldImageInline, out var newImageInline)) {
            if (DiffLayout.IsSideBySide || row.Kind == GitKay.Core.MarkdownChangeKind.Modified && oldImageInline != null && newImageInline != null) {
                var middle = Bounds.Width / 2;
                context.FillRectangle(ThemeBrush("GitKayBorderBrush", HunkBrush), new Rect(middle, y, 1, RowHeight(row)));
                DrawRenderedImage(context, oldImageInline, OldRenderedImages, 22, y + 8, Math.Max(80, middle - 44));
                DrawRenderedImage(context, newImageInline, RenderedImages, middle + 14, y + 8, Math.Max(80, middle - 30));
            }
            else DrawRenderedImage(context, newImageInline ?? oldImageInline, newImageInline != null ? RenderedImages : OldRenderedImages, 22, y + 8, Math.Max(80, _measurementWidth - 44));
            return;
        }
        var renderedSize = row.Located.Block is GitKay.Core.MarkdownBlock.Heading heading ? Math.Max(CodeFontSize + 1, CodeFontSize + 8 - heading.level) : CodeFontSize + 1;
        var renderedText = row.Located.Block switch {
            GitKay.Core.MarkdownBlock.ListItem item => (item.marker is GitKay.Core.MarkdownListMarker.Ordered ordered ? $"{ordered.Item}. " : "• ") + row.Text,
            GitKay.Core.MarkdownBlock.Quote _ => "▍ " + row.Text,
            _ => row.Text,
        };
        if (DiffLayout.IsSideBySide) {
            var middle = Bounds.Width / 2;
            context.FillRectangle(ThemeBrush("GitKayBorderBrush", HunkBrush), new Rect(middle, y, 1, RowHeight(row)));
            if (row.Kind == GitKay.Core.MarkdownChangeKind.Modified && row.Words.Count > 0) {
                DrawRenderedWords(context, row.Words.Where(span => span.Kind != GitKay.Core.MarkdownWordSpanKind.Inserted).ToArray(), 0, 22, y + 8, Math.Max(40, middle - 38));
                DrawRenderedWords(context, row.Words.Where(span => span.Kind != GitKay.Core.MarkdownWordSpanKind.Deleted).ToArray(), 1, middle + 14, y + 8, Math.Max(40, middle - 30));
            }
            else {
                DrawRenderedSpans(context, row.OldSpans, 0, 22, y + 8, Math.Max(40, middle - 38), row.IsRemoved ? accent : ThemeBrush("GitKayTextBrush", FileBrush), renderedSize);
                DrawRenderedSpans(context, row.NewSpans, 1, middle + 14, y + 8, Math.Max(40, middle - 30), row.IsAdded ? accent : ThemeBrush("GitKayTextBrush", FileBrush), renderedSize);
            }
        }
        else if (row.Kind == GitKay.Core.MarkdownChangeKind.Modified && row.Words.Count > 0) DrawRenderedWords(context, row.Words, 0, 22, y + 8, Math.Max(40, Bounds.Width - 38));
        else if (row.NewSpans.Count > 0) DrawRenderedSpans(context, row.NewSpans, 0, 22, y + 8, Math.Max(40, Bounds.Width - 38), ThemeBrush("GitKayTextBrush", FileBrush), renderedSize);
        else DrawRenderedText(context, renderedText, 22, y + 8, Math.Max(40, Bounds.Width - 38), ThemeBrush("GitKayTextBrush", FileBrush), renderedSize);
    }

    private void DrawRenderedCodeBlock(DrawingContext context, GitKay.Core.MarkdownBlock.CodeBlock block, double y, RenderedMarkdownRowProjection row, double x, double width, bool oldSide, bool unified) {
        var bounds = new Rect(x, y + 5, width, RowHeight(row) - 10);
        context.DrawRectangle(ThemeBrush("GitKayRaisedBrush", CodeBlockFallback), new Pen(ThemeBrush("GitKayBorderBrush", HunkBrush), .7), bounds, 5, 5);
        using var clip = context.PushClip(bounds);
        var lineY = y + 9;
        if (row.CodeLines.Count > 0) {
            foreach (var line in row.CodeLines) {
                void DrawVersion(Microsoft.FSharp.Core.FSharpOption<string>? text, bool removed, bool added) {
                    if (text == null) { if (!unified) lineY += LineHeight; return; }
                    var tint = removed ? ThemeBrush("GitKayRemovedStrongBrush", Brushes.Transparent) : added ? ThemeBrush("GitKayAddedStrongBrush", Brushes.Transparent) : Brushes.Transparent;
                    if (tint != Brushes.Transparent) context.FillRectangle(tint, new Rect(x, lineY - 2, width, LineHeight));
                    if (line.Change == GitKay.Core.MarkdownChangeKind.Modified && line.Previous != null && line.Current != null) {
                        var span = GitKay.Core.DiffText.changedSpan(line.Previous.Value, line.Current.Value);
                        if (span.IsSome) DrawChangedSpan(context, text.Value, span.Value.Item1, removed ? span.Value.Item2 : span.Value.Item3, x + 10, lineY - 2, tint);
                    }
                    DrawCode(context, text.Value, x + 10, lineY, ThemeBrush("GitKayTextBrush", FileBrush), SyntaxFlavour.Code);
                    lineY += LineHeight;
                }
                if (unified) {
                    if (line.Change == GitKay.Core.MarkdownChangeKind.Modified) {
                        DrawVersion(line.Previous, true, false);
                        DrawVersion(line.Current, false, true);
                    }
                    else DrawVersion(line.Current ?? line.Previous, line.Change == GitKay.Core.MarkdownChangeKind.Removed, line.Change == GitKay.Core.MarkdownChangeKind.Added);
                }
                else DrawVersion(oldSide ? line.Previous : line.Current,
                    line.Change == GitKay.Core.MarkdownChangeKind.Removed || oldSide && line.Change == GitKay.Core.MarkdownChangeKind.Modified,
                    line.Change == GitKay.Core.MarkdownChangeKind.Added || !oldSide && line.Change == GitKay.Core.MarkdownChangeKind.Modified);
            }
        }
        else foreach (var line in block.text.Replace("\r\n", "\n").Split('\n')) {
            DrawCode(context, line, x + 10, lineY, ThemeBrush("GitKayTextBrush", FileBrush), SyntaxFlavour.Code);
            lineY += LineHeight;
        }
    }

    private void DrawRenderedTable(DrawingContext context, GitKay.Core.MarkdownBlock.Table table, double y, double left, double width) {
        var rows = new List<IReadOnlyList<Microsoft.FSharp.Collections.FSharpList<GitKay.Core.MarkdownInline>>>();
        var hasHeader = !table.header.IsEmpty;
        if (hasHeader) rows.Add(table.header);
        rows.AddRange(table.rows);
        var columns = Math.Max(1, rows.Select(row => row.Count).DefaultIfEmpty(1).Max());
        var cellWidth = width / columns;
        var top = y + 7;
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++) {
            var layouts = new FormattedText?[columns];
            var rowHeight = LineHeight;
            for (var column = 0; column < columns; column++) {
                if (column >= rows[rowIndex].Count) continue;
                var text = string.Concat(rows[rowIndex][column].Select(GitKay.Core.Markdown.inlineText));
                layouts[column] = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    rowIndex == 0 && hasHeader ? StrongTypeface : ProseTypeface, CodeFontSize + 1, ThemeBrush("GitKayTextBrush", FileBrush)) { MaxTextWidth = Math.Max(10, cellWidth - 10) };
                rowHeight = Math.Max(rowHeight, layouts[column]!.Height + 4);
            }
            for (var column = 0; column < columns; column++) {
                var rect = new Rect(left + column * cellWidth, top, cellWidth, rowHeight);
                context.DrawRectangle(null, new Pen(ThemeBrush("GitKayBorderBrush", HunkBrush), .7), rect);
                if (layouts[column] is not { } layout) continue;
                var alignment = column < table.alignments.Length ? table.alignments[column] : GitKay.Core.MarkdownAlignment.Default;
                var textX = alignment.IsRight ? rect.Right - 5 - layout.Width
                    : alignment.IsCenter ? rect.X + (rect.Width - layout.Width) / 2 : rect.X + 5;
                context.DrawText(layout, new Point(Math.Max(rect.X + 5, textX), rect.Y + 2));
            }
            top += rowHeight;
        }
    }

    private static Typeface RenderedTypeface(GitKay.Core.MarkdownSpanStyle style) => style switch {
        GitKay.Core.MarkdownSpanStyle.Emphasis => EmphasisTypeface,
        GitKay.Core.MarkdownSpanStyle.Strong => StrongTypeface,
        GitKay.Core.MarkdownSpanStyle.Code or GitKay.Core.MarkdownSpanStyle.Html => CodeTypeface,
        _ => ProseTypeface,
    };

    private void DrawRenderedSpans(DrawingContext context, IReadOnlyList<GitKay.Core.RenderedMarkdownSpan> spans,
        int side, double x, double y, double width, IBrush defaultBrush, double size) {
        var currentX = x;
        var currentY = y;
        var lineHeight = Math.Round(size * 1.45);
        var proseBaseline = new FormattedText("Ag", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, ProseTypeface, size, defaultBrush).Baseline;
        var logicalOffset = 0;
        foreach (var span in spans) {
            var typeface = RenderedTypeface(span.Style);
            var brush = span.Style == GitKay.Core.MarkdownSpanStyle.Link ? ThemeBrush("GitKayAccentBrush", defaultBrush) : defaultBrush;
            // Keep whitespace attached to its preceding word. This preserves authored spacing while allowing
            // wrapping at word boundaries across differently styled spans.
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(span.Text, @"[^\s]+[ \t]*|\r?\n|[ \t]+")) {
                var value = match.Value;
                if (value.Contains('\n')) { logicalOffset += value.Length; currentX = x; currentY += lineHeight; continue; }
                var layout = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);
                var spanWidth = layout.WidthIncludingTrailingWhitespace;
                if (currentX > x && currentX + spanWidth > x + width) { currentX = x; currentY += lineHeight; }
                var drawY = currentY + proseBaseline - layout.Baseline;
                var textHit = new RenderedTextHit(_drawingRowIndex, side, new Rect(currentX, drawY, Math.Max(1, spanWidth), layout.Height), logicalOffset, value, typeface, size);
                DrawRenderedSegmentSelection(context, textHit);
                _renderedTextHits.Add(textHit);
                context.DrawText(layout, new Point(currentX, drawY));
                if (span.Style is GitKay.Core.MarkdownSpanStyle.Strikethrough)
                    context.DrawLine(new Pen(brush, 1), new Point(currentX, drawY + layout.Height / 2), new Point(currentX + spanWidth, drawY + layout.Height / 2));
                if (span.Style is GitKay.Core.MarkdownSpanStyle.Link) {
                    context.DrawLine(new Pen(brush, 1), new Point(currentX, drawY + layout.Height), new Point(currentX + spanWidth, drawY + layout.Height));
                    if (span.Target is { } target) _renderedLinks.Add(new RenderedLinkHit(new Rect(currentX, drawY, spanWidth, layout.Height + 2), target.Value));
                }
                currentX += spanWidth;
                logicalOffset += value.Length;
            }
        }
    }

    private void DrawRenderedSegmentSelection(DrawingContext context, RenderedTextHit hit) {
        if (_textSelection == null || hit.Side != _textSelection.Side || OrderedSelection() is not { } ordered) return;
        var (start, end) = ordered;
        if (hit.Row < start.Row || hit.Row > end.Row) return;
        var selectionStart = hit.Row == start.Row ? start.Char : 0;
        var selectionEnd = hit.Row == end.Row ? end.Char : int.MaxValue;
        var from = Math.Clamp(selectionStart - hit.Start, 0, hit.Text.Length);
        var to = Math.Clamp(selectionEnd - hit.Start, 0, hit.Text.Length);
        if (to <= from) return;
        double width(string value) => new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, hit.Typeface, hit.Size, Brushes.Transparent).WidthIncludingTrailingWhitespace;
        var left = hit.Bounds.X + width(hit.Text[..from]);
        var selectedWidth = width(hit.Text[from..to]);
        context.FillRectangle(ThemeBrush("GitKayTextSelectionBrush", TextSelectionFallback), new Rect(left, hit.Bounds.Y, Math.Max(1, selectedWidth), hit.Bounds.Height));
    }

    private void DrawRenderedText(DrawingContext context, string text, double x, double y, double width, IBrush brush, double? size = null) {
        if (string.IsNullOrEmpty(text)) return;
        var layout = Layout(text, size ?? CodeFontSize + 1, brush, false);
        layout.MaxTextWidth = width;
        context.DrawText(layout, new Point(x, y));
    }

    private void DrawRenderedWords(DrawingContext context, IReadOnlyList<GitKay.Core.MarkdownWordSpan> words, int side, double x, double y, double width) {
        var currentX = x;
        var currentY = y;
        var lineHeight = Math.Round((CodeFontSize + 1) * 1.45);
        var proseBaseline = new FormattedText("Ag", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, ProseTypeface, CodeFontSize + 1, ThemeBrush("GitKayTextBrush", FileBrush)).Baseline;
        string? previous = null;
        var logicalOffset = 0;
        foreach (var span in words) {
            var deleted = span.Kind == GitKay.Core.MarkdownWordSpanKind.Deleted;
            var inserted = span.Kind == GitKay.Core.MarkdownWordSpanKind.Inserted;
            var brush = deleted ? ThemeBrush("GitKayRemovedAccentBrush", Brushes.Red)
                : inserted ? ThemeBrush("GitKayAddedAccentBrush", Brushes.Green)
                : span.Style == GitKay.Core.MarkdownSpanStyle.Link ? ThemeBrush("GitKayAccentBrush", FileBrush)
                : ThemeBrush("GitKayTextBrush", FileBrush);
            var needsSpace = previous != null && char.IsLetterOrDigit(previous[^1]) && span.Text.Length > 0 && char.IsLetterOrDigit(span.Text[0]);
            var value = (needsSpace ? " " : "") + span.Text;
            var layout = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                RenderedTypeface(span.Style), CodeFontSize + 1, brush);
            var itemWidth = layout.WidthIncludingTrailingWhitespace;
            if (currentX > x && currentX + itemWidth > x + width) { currentX = x; currentY += lineHeight; }
            var drawY = currentY + proseBaseline - layout.Baseline;
            var textHit = new RenderedTextHit(_drawingRowIndex, side, new Rect(currentX, drawY, Math.Max(1, itemWidth), layout.Height), logicalOffset, value, RenderedTypeface(span.Style), CodeFontSize + 1);
            DrawRenderedSegmentSelection(context, textHit);
            _renderedTextHits.Add(textHit);
            if (inserted || deleted) using (context.PushOpacity(.16)) context.FillRectangle(brush, new Rect(currentX, drawY, itemWidth, layout.Height));
            context.DrawText(layout, new Point(currentX, drawY));
            if (deleted || span.Style == GitKay.Core.MarkdownSpanStyle.Strikethrough)
                context.DrawLine(new Pen(brush, 1), new Point(currentX, drawY + layout.Height / 2), new Point(currentX + itemWidth, drawY + layout.Height / 2));
            if (span.Style == GitKay.Core.MarkdownSpanStyle.Link && span.Target is { } target)
                _renderedLinks.Add(new RenderedLinkHit(new Rect(currentX, drawY, itemWidth, layout.Height + 2), target.Value));
            currentX += itemWidth;
            logicalOffset += value.Length;
            previous = span.Text;
        }
    }

    private bool IsHeaderPartActive(int index, int action, out bool pressed) {
        pressed = _pressedGapAction.Row == index && _pressedGapAction.Action == action;
        return pressed || (_hoveredGapAction.Row == index && _hoveredGapAction.Action == action);
    }

    /// <summary>Three stacked lines; filtering to changes leaves the middle one as a gap, like a collapsed section.</summary>
    private static void DrawChangesOnlyIcon(DrawingContext context, Rect bounds, bool changesOnly, IBrush brush) {
        var x = bounds.X + bounds.Width / 2 - 5;
        var y = bounds.Y + bounds.Height / 2 - 4;
        context.FillRectangle(brush, new Rect(x, y, 10, 1.5));
        if (changesOnly) {
            var pen = new Pen(brush, 1, dashStyle: new DashStyle(new double[] { 2, 2 }, 0));
            context.DrawLine(pen, new Point(x, y + 4.25), new Point(x + 10, y + 4.25));
        }
        else context.FillRectangle(brush, new Rect(x, y + 3.5, 10, 1.5));
        context.FillRectangle(brush, new Rect(x, y + 7, 10, 1.5));
    }

    /// <summary>An eye, drawn to a half-width so it can sit in a pill beside a label as well as alone.</summary>
    private static void DrawPreviewIcon(DrawingContext context, Rect bounds, IBrush brush, double half = 8) {
        var pen = new Pen(brush, 1.2, lineCap: PenLineCap.Round);
        var center = bounds.Center;
        var lid = half * 0.75;
        var geometry = StreamGeometry.Parse(
            $"M {center.X - half},{center.Y} C {center.X - half / 2},{center.Y - lid} {center.X + half / 2},{center.Y - lid} {center.X + half},{center.Y} "
            + $"C {center.X + half / 2},{center.Y + lid} {center.X - half / 2},{center.Y + lid} {center.X - half},{center.Y} Z");
        context.DrawGeometry(null, pen, geometry);
        context.DrawEllipse(brush, null, center, half * 0.28, half * 0.28);
    }

    /// <summary>Arrows pointing away from (expand) or toward (collapse) a dotted centre line.</summary>
    private static void DrawContextToggleIcon(DrawingContext context, Rect bounds, bool expand, IBrush brush) {
        var pen = new Pen(brush, 1.4, lineCap: PenLineCap.Round);
        var cx = bounds.X + bounds.Width / 2;
        var cy = bounds.Y + bounds.Height / 2;
        for (var x = cx - 6; x <= cx + 6; x += 3)
            context.FillRectangle(brush, new Rect(x - 0.6, cy - 0.6, 1.3, 1.3));

        void Arrow(double tipY, double tailY) {
            var head = tipY < tailY ? 3 : -3;
            context.DrawLine(pen, new Point(cx, tailY), new Point(cx, tipY));
            context.DrawLine(pen, new Point(cx - 3, tipY + head), new Point(cx, tipY));
            context.DrawLine(pen, new Point(cx + 3, tipY + head), new Point(cx, tipY));
        }

        if (expand) {
            Arrow(cy - 8, cy - 3);
            Arrow(cy + 8, cy + 3);
        }
        else {
            Arrow(cy - 3, cy - 8);
            Arrow(cy + 3, cy + 8);
        }
    }

    /// <summary>"+N −M" followed by five blocks sized to the change ratio, right-aligned at <paramref name="right"/>.</summary>
    private int _hoveredRowIndex = -1;

    private void DrawDiffStat(DrawingContext context, DiffFileProjection file, double right, double centerY, bool blocksOnly) {
        if (!file.IsLoaded) return;
        const double block = 8, spacing = 2;
        var added = ThemeBrush("GitKayAddedAccentBrush", Brushes.Green);
        var removed = ThemeBrush("GitKayRemovedAccentBrush", Brushes.Red);
        var neutral = ThemeBrush("GitKayDiffStatNeutralBrush", HunkBrush);

        var (green, red) = DiffStatBar.Blocks(file.AddedLines, file.RemovedLines);

        var x = right - (5 * block + 4 * spacing);
        if (blocksOnly) {
            for (var i = 0; i < 5; i++) {
                var blockBrush = i < green ? added : i < green + red ? removed : neutral;
                context.DrawRectangle(blockBrush, null, new Rect(x + i * (block + spacing), centerY - block / 2, block, block), 2, 2);
            }
            return;
        }

        x -= 8;
        if (file.RemovedLines > 0) {
            var removedText = Layout($"−{file.RemovedLines}", 12, removed, false);
            x -= removedText.Width;
            context.DrawText(removedText, new Point(x, centerY - removedText.Height / 2));
            x -= 6;
        }
        if (file.AddedLines > 0 || file.RemovedLines == 0) {
            var addedText = Layout($"+{file.AddedLines}", 12, added, false);
            x -= addedText.Width;
            context.DrawText(addedText, new Point(x, centerY - addedText.Height / 2));
        }
    }

    private void DrawGap(DrawingContext context, DiffGapProjection gap, double y, int index) {
        var accent = ThemeBrush("GitKayAccentBrush", FileBrush);
        var muted = ThemeBrush("GitKayMutedTextBrush", HunkBrush);
        context.FillRectangle(ThemeBrush("GitKayHunkBrush", Brushes.Transparent), new Rect(0, y, Bounds.Width, GapHeight));
        context.FillRectangle(ThemeBrush("GitKayGutterBrush", Brushes.Transparent), new Rect(0, y, GapGutterWidth, GapHeight));

        var cells = GapCells(gap, y);
        for (var i = 0; i < cells.Count; i++) {
            var cell = cells[i];
            if (_pressedGapAction.Row == index && _pressedGapAction.Action == i)
                context.FillRectangle(ThemeBrush("GitKayPressedBrush", ThemeBrush("GitKaySelectionBrush", SelectionBrush)), cell.Bounds);
            else if (_hoveredGapAction.Row == index && _hoveredGapAction.Action == i)
                context.FillRectangle(ThemeBrush("GitKayHoverBrush", SelectionBrush), cell.Bounds);
            DrawExpandIcon(context, cell, accent);
        }

        var label = gap.HeaderText ?? gap.Label;
        var labelLayout = Layout(label, 11, muted, false);
        var labelY = y + (GapHeight - labelLayout.Height) / 2;
        context.DrawText(labelLayout, new Point(GapGutterWidth + 12, labelY));
        if (gap.IsLoading)
            DrawPlain(context, "Loading…", GapGutterWidth + 24 + labelLayout.Width, labelY, 11, muted);
    }

    /// <summary>GitHub-style expander glyph: an arrow pointing away from a dotted boundary line.</summary>
    private void DrawExpandIcon(DrawingContext context, GapCell cell, IBrush brush) {
        var centerX = cell.Bounds.X + cell.Bounds.Width / 2;
        var centerY = cell.Bounds.Y + cell.Bounds.Height / 2;
        var pen = new Pen(brush, 1.5, lineCap: PenLineCap.Round);

        void Dots(double dotY) {
            for (var x = centerX - 6; x <= centerX + 6; x += 3)
                context.FillRectangle(brush, new Rect(x - 0.5, dotY, 1.5, 1.5));
        }

        void Arrow(double tipY, double tailY) {
            var head = tipY < tailY ? 3.5 : -3.5;
            context.DrawLine(pen, new Point(centerX, tailY), new Point(centerX, tipY));
            context.DrawLine(pen, new Point(centerX - 3.5, tipY + head), new Point(centerX, tipY));
            context.DrawLine(pen, new Point(centerX + 3.5, tipY + head), new Point(centerX, tipY));
        }

        if (_expandAllHeld) {
            // Both ways at once, from a solid line: a click reveals the gap entirely.
            context.DrawLine(new Pen(brush, 1.5), new Point(centerX - 6, centerY), new Point(centerX + 6, centerY));
            Arrow(centerY - 7, centerY - 2);
            Arrow(centerY + 7, centerY + 2);
        }
        else if (cell.Direction.IsDown) {
            Dots(centerY - 6);
            Arrow(centerY + 5, centerY - 2);
        }
        else if (cell.Direction.IsUp) {
            Arrow(centerY - 5, centerY + 2);
            Dots(centerY + 5);
        }
        else {
            Arrow(centerY - 7, centerY - 1);
            Arrow(centerY + 7, centerY + 1);
        }
    }

    /// <summary>Incremental expanders stack in the gutter; a gap of ten lines or fewer offers one "all" expander.</summary>
    private static List<GapCell> GapCells(DiffGapProjection gap, double y) {
        var directions = gap.Directions.Where(direction => !direction.IsAll).ToList();
        if (directions.Count == 0) directions = gap.Directions.ToList();
        var cells = new List<GapCell>(directions.Count);
        var height = GapHeight / Math.Max(1, directions.Count);
        for (var i = 0; i < directions.Count; i++)
            cells.Add(new GapCell(directions[i], new Rect(0, y + i * height, GapGutterWidth, height)));
        return cells;
    }

    private GapActionHit GapActionAt(Point position) {
        var index = RowAt(position, out var headerTop);
        if ((uint)index < (uint)_rows.Length && _rows[index] is DiffFileHeaderProjection header) {
            if (FileChevronRect(headerTop).Contains(position)) return new GapActionHit(index, HeaderChevronAction);
            if (IsPreviewable(header.File) && FilePreviewRect(header, headerTop).Contains(position))
                return new GapActionHit(index, HeaderPreviewAction);
            if (HasChangesOnlyToggle(header.File) && FileChangesOnlyRect(header, headerTop).Contains(position))
                return new GapActionHit(index, HeaderChangesOnlyAction);
            if (HasContextToggle(header.File) && FileContextRect(header, headerTop).Contains(position))
                return new GapActionHit(index, HeaderContextAction);
            return GapActionHit.None;
        }
        if ((uint)index >= (uint)_rows.Length || _rows[index] is not DiffGapProjection gap) return GapActionHit.None;
        var cells = GapCells(gap, _tops[index]);
        for (var i = 0; i < cells.Count; i++)
            if (cells[i].Bounds.Contains(position))
                return new GapActionHit(index, i);
        return GapActionHit.None;
    }

    private void ShowGapMenu(int index, DiffGapProjection gap) {
        var menu = new ContextMenu();
        void Add(string header, GitKay.Core.DiffExpansion.ExpandDirection direction) {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => RequestExpansion(index, gap, direction);
            menu.Items.Add(item);
        }

        Add(gap.HiddenLineCount is { } count ? $"Load all {count} lines" : "Load all", GitKay.Core.DiffExpansion.ExpandDirection.All);
        foreach (var direction in gap.Directions.Where(direction => !direction.IsAll))
            Add(gap.ActionLabel(direction), direction);
        menu.Open(this);
    }

    private void RequestExpansion(int index, DiffGapProjection gap, GitKay.Core.DiffExpansion.ExpandDirection direction) {
        // Rows may have been rebuilt while a menu was open; act only on the same gap.
        if ((uint)index >= (uint)_rows.Length || !ReferenceEquals(_rows[index], gap)) return;
        var request = new DiffGapExpansionRequest(gap.Gap, direction, gap.File);
        if (ExpandGapCommand?.CanExecute(request) != true) return;
        BeginExpansion(index, gap, direction);
        ExpandGapCommand.Execute(request);
    }

    private void DrawLine(DrawingContext context, DiffLineProjection line, double y) {
        var rowBackground = line.IsAdded
            ? ThemeBrush("GitKayAddedBrush", line.RowBackground)
            : line.IsRemoved
                ? ThemeBrush("GitKayRemovedBrush", line.RowBackground)
                : Brushes.Transparent;
        if (rowBackground != Brushes.Transparent)
            context.FillRectangle(rowBackground, new Rect(0, y, Bounds.Width, LineHeight - 1));

        if (DiffLayout.IsSideBySide) DrawSideBySideLine(context, line, y);
        else if (DiffLayout.IsNewFile) DrawSingleSideLine(context, line.NewLineNoText, line.NewContent, y, line);
        else if (DiffLayout.IsOldFile) DrawSingleSideLine(context, line.OldLineNoText, line.OldContent, y, line);
        else DrawUnifiedLine(context, line, y);
    }

    private void DrawUnifiedLine(DrawingContext context, DiffLineProjection line, double y) {
        var gutter = line.IsAdded ? ThemeBrush("GitKayAddedGutterBrush", Brushes.Transparent)
            : line.IsRemoved ? ThemeBrush("GitKayRemovedGutterBrush", Brushes.Transparent)
            : ThemeBrush("GitKayGutterBrush", Brushes.Transparent);
        context.FillRectangle(gutter, new Rect(0, y, Z(50), LineHeight - 1));
        var lineNumber = line.IsRemoved ? line.OldLineNoText : line.NewLineNoText;
        DrawLineNumber(context, lineNumber, Z(2), y, ThemeBrush("GitKayLineNumberBrush", LineNumberFallback));
        // The +/- marker only appears on changed lines, in a narrow column.
        if (line.IsAdded || line.IsRemoved)
            DrawPlain(context, line.Prefix, Z(41), y + CodeTextTop, CodeFontSize, line.IsAdded ? ThemeBrush("GitKayAddedAccentBrush", line.PrefixForeground) : ThemeBrush("GitKayRemovedAccentBrush", line.PrefixForeground));
        using (context.PushClip(new Rect(Z(54), y, Math.Max(0, Bounds.Width - Z(54)), LineHeight))) {
            DrawFindMatches(context, line.Content, Z(54) - _horizontalOffset, y);
            DrawCode(context, line.Content, Z(54) - _horizontalOffset, y + CodeTextTop, ThemeBrush("GitKayTextBrush", line.Foreground), line.Flavour);
        }
    }

    private void DrawSingleSideLine(
        DrawingContext context,
        string lineNumber,
        string content,
        double y,
        DiffLineProjection line) {
        var gutter = line.IsAdded ? ThemeBrush("GitKayAddedGutterBrush", Brushes.Transparent)
            : line.IsRemoved ? ThemeBrush("GitKayRemovedGutterBrush", Brushes.Transparent)
            : ThemeBrush("GitKayGutterBrush", Brushes.Transparent);
        context.FillRectangle(gutter, new Rect(0, y, Z(48), LineHeight - 1));
        DrawLineNumber(context, lineNumber, Z(8), y, ThemeBrush("GitKayLineNumberBrush", LineNumberFallback));
        using (context.PushClip(new Rect(Z(52), y, Math.Max(0, Bounds.Width - Z(52)), LineHeight))) {
            DrawFindMatches(context, content, Z(52) - _horizontalOffset, y);
            DrawCode(context, content, Z(52) - _horizontalOffset, y + CodeTextTop, ThemeBrush("GitKayTextBrush", line.Foreground), line.Flavour);
        }
    }

    private void DrawSideBySideLine(DrawingContext context, DiffLineProjection line, double y) {
        var middle = Bounds.Width / 2;
        var gutter = ThemeBrush("GitKayGutterBrush", Brushes.Transparent);
        var border = ThemeBrush("GitKayBorderBrush", HunkBrush);
        var isPairedChange = !string.IsNullOrEmpty(line.OldContent)
                             && !string.IsNullOrEmpty(line.NewContent)
                             && !string.Equals(line.OldContent, line.NewContent, StringComparison.Ordinal);
        if (!line.IsAdded)
            context.FillRectangle(line.IsRemoved || isPairedChange ? ThemeBrush("GitKayRemovedBrush", line.OldCellBackground) : Brushes.Transparent, new Rect(0, y, middle, LineHeight - 1));
        if (!line.IsRemoved)
            context.FillRectangle(line.IsAdded || isPairedChange ? ThemeBrush("GitKayAddedBrush", line.NewCellBackground) : Brushes.Transparent, new Rect(middle + 1, y, Math.Max(0, Bounds.Width - middle - 1), LineHeight - 1));
        context.FillRectangle(line.IsRemoved || isPairedChange ? ThemeBrush("GitKayRemovedGutterBrush", gutter) : line.IsAdded ? Brushes.Transparent : gutter, new Rect(0, y, Z(48), LineHeight - 1));
        context.FillRectangle(line.IsAdded || isPairedChange ? ThemeBrush("GitKayAddedGutterBrush", gutter) : line.IsRemoved ? Brushes.Transparent : gutter, new Rect(middle + 1, y, Z(48), LineHeight - 1));
        context.FillRectangle(border, new Rect(middle, y, 1, LineHeight));

        DrawIntralineHighlights(context, line.OldContent, line.NewContent, Z(56), middle + Z(57), middle, y);

        DrawLineNumber(context, line.OldLineNoText, Z(8), y, ThemeBrush("GitKayLineNumberBrush", LineNumberFallback));
        using (context.PushClip(OldColumnClip(middle, y))) {
            DrawFindMatches(context, line.OldContent, Z(56) - _horizontalOffset, y);
            DrawCode(context, line.OldContent, Z(56) - _horizontalOffset, y + CodeTextTop, ThemeBrush("GitKayTextBrush", line.Foreground), line.Flavour);
        }

        DrawLineNumber(context, line.NewLineNoText, middle + Z(9), y, ThemeBrush("GitKayLineNumberBrush", LineNumberFallback));
        using (context.PushClip(NewColumnClip(middle, y))) {
            DrawFindMatches(context, line.NewContent, middle + Z(57) - _horizontalOffset, y);
            DrawCode(context, line.NewContent, middle + Z(57) - _horizontalOffset, y + CodeTextTop, ThemeBrush("GitKayTextBrush", line.Foreground), line.Flavour);
        }
    }

    private Rect OldColumnClip(double middle, double y) => new(Z(56), y, Math.Max(0, middle - Z(64)), LineHeight);

    private Rect NewColumnClip(double middle, double y) => new(middle + Z(57), y, Math.Max(0, Bounds.Width - middle - Z(57)), LineHeight);

    private void DrawIntralineHighlights(DrawingContext context, string oldText, string newText, double oldX, double newX, double middle, double y) {
        if (oldText.Length > MaxHighlightedLineLength || newText.Length > MaxHighlightedLineLength) return;
        if (GitKay.Core.DiffText.changedSpan(oldText, newText) is not { IsSome: true } span) return;
        var (start, oldLength, newLength) = span.Value;

        using (context.PushClip(OldColumnClip(middle, y)))
            DrawChangedSpan(context, oldText, start, oldLength, oldX - _horizontalOffset, y,
                ThemeBrush("GitKayRemovedStrongBrush", ThemeBrush("GitKayRemovedBrush", Brushes.Transparent)));
        using (context.PushClip(NewColumnClip(middle, y)))
            DrawChangedSpan(context, newText, start, newLength, newX - _horizontalOffset, y,
                ThemeBrush("GitKayAddedStrongBrush", ThemeBrush("GitKayAddedBrush", Brushes.Transparent)));
    }

    private static readonly IBrush FindMatchFallback = new SolidColorBrush(Color.FromArgb(110, 187, 128, 9)).ToImmutable();

    private static readonly IBrush SearchMatchFallback = new SolidColorBrush(Color.FromRgb(88, 166, 255)).ToImmutable();

    /// <summary>
    /// Marks the commit search's diff term with a dotted underline, and find-in-diff text with a solid highlight.
    /// </summary>
    private void DrawFindMatches(DrawingContext context, string text, double x, double y) {
        DrawTextSelection(context, text, x, y);
        DrawCaret(context, text, x, y);
        if (string.IsNullOrEmpty(text)) return;
        var underline = ThemeBrush("GitKayAccentBrush", SearchMatchFallback);
        ForEachMatch(text, SearchHighlightQuery, SearchHighlightUseRegex, (start, length) => DrawDottedUnderline(context, text, start, length, x, y + LineHeight - 3, underline, CodeFontSize));
        var highlight = ThemeBrush("GitKayFindMatchBrush", FindMatchFallback);
        ForEachMatch(text, FindQuery, FindUseRegex, (start, length) => DrawChangedSpan(context, text, start, length, x, y, highlight));
    }

    private readonly GitKay.Kit.TextQueryCache _queries = new();

    private void ForEachMatch(string text, string? rawQuery, bool useRegex, Action<int, int> onMatch) {
        foreach (var (start, length) in _queries.Get(useRegex, rawQuery?.Trim() ?? "").Spans(text))
            onMatch(start, length);
    }

    private void DrawDottedUnderline(DrawingContext context, string text, int start, int length, double x, double baseline, IBrush brush, double size) {
        var left = x + (start == 0 ? 0 : Layout(text[..start], size, Brushes.Transparent, false).Width);
        var width = Layout(text.Substring(start, length), size, Brushes.Transparent, false).Width;
        for (var dot = left; dot < left + width; dot += 3)
            context.FillRectangle(brush, new Rect(dot, baseline, 1.5, 1.5));
    }

    private void DrawChangedSpan(DrawingContext context, string text, int start, int length, double x, double y, IBrush brush) {
        if (length <= 0) return;
        var prefixWidth = start == 0 ? 0 : Layout(text[..start], CodeFontSize, Brushes.Transparent, false).Width;
        var changedWidth = Layout(text.Substring(start, length), CodeFontSize, Brushes.Transparent, false).Width;
        context.FillRectangle(brush, new Rect(x + prefixWidth, y, changedWidth, LineHeight - 1));
    }

    private static readonly IBrush CodeBlockFallback = new SolidColorBrush(Color.FromArgb(18, 110, 118, 129)).ToImmutable();
    private static readonly IBrush LineNumberFallback = new SolidColorBrush(Color.FromRgb(110, 118, 129)).ToImmutable();

    private void DrawLineNumber(DrawingContext context, string text, double x, double y, IBrush foreground) {
        var layout = Layout(text, CodeFontSize, foreground, false);
        context.DrawText(layout, new Point(x + Z(34) - layout.Width, y + CodeTextTop));
    }

    private void DrawCode(DrawingContext context, string text, double x, double y, IBrush foreground, SyntaxFlavour flavour = SyntaxFlavour.Code) {
        if (string.IsNullOrEmpty(text)) return;

        using var shaped = TextShaper.Current.ShapeText(text.AsMemory(),
            new TextShaperOptions(CodeTypeface.GlyphTypeface, CodeFontSize, culture: CultureInfo.CurrentCulture));
        var allGlyphs = new GlyphInfo[shaped.Length];
        for (var i = 0; i < shaped.Length; i++) allGlyphs[i] = shaped[i];
        var baseline = new Point(x, y + CodeFontSize);

        if (text.Length > MaxHighlightedLineLength) {
            context.DrawGlyphRun(foreground,
                new GlyphRun(shaped.GlyphTypeface, CodeFontSize, text.AsMemory(), allGlyphs, baseline, shaped.BidiLevel));
            return;
        }

        DiagHighlights++;
        var characterOffset = 0;
        var runX = x;
        foreach (var token in SyntaxHighlighting.Tokenize(text, flavour)) {
            var end = characterOffset + token.Text.Length;
            var tokenGlyphs = allGlyphs
                .Where(glyph => glyph.GlyphCluster >= characterOffset && glyph.GlyphCluster < end)
                .Select(glyph => new GlyphInfo(glyph.GlyphIndex, glyph.GlyphCluster - characterOffset, glyph.GlyphAdvance, glyph.GlyphOffset))
                .ToArray();
            if (tokenGlyphs.Length > 0) {
                var run = new GlyphRun(shaped.GlyphTypeface, CodeFontSize, token.Text.AsMemory(), tokenGlyphs,
                    new Point(runX, baseline.Y), shaped.BidiLevel);
                context.DrawGlyphRun(TokenBrush(token.Kind, foreground), run);
                runX += tokenGlyphs.Sum(glyph => glyph.GlyphAdvance);
            }
            characterOffset = end;
        }
        DiagLayoutsBuilt++;
    }


    private FormattedText Layout(string text, double size, IBrush brush, bool coloured) {
        var key = $"{size}:{text}";
        var cache = _plainLayouts;
        if (!cache.TryGetValue(key, out var layout)) {
            DiagLayoutsBuilt++;
            layout = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, CodeTypeface, size, brush);
            if (cache.Count >= MaxLayoutCacheEntries) {
                DiagCacheClears++;
                cache.Clear();
            }
            cache[key] = layout;
        }
        else {
            // The cache is keyed by size and text only, so the same words drawn in two colours share one layout.
            // Whoever asked for it last decides its colour, or a label takes the colour of the measurement that
            // happened to build it first.
            layout.SetForegroundBrush(brush);
        }

        return layout;
    }

    private void DrawPlain(DrawingContext context, string text, double x, double y, double size, IBrush brush) =>
        context.DrawText(Layout(text, size, brush, false), new Point(x, y));

    /// <summary>
    /// Dark keeps the established drawn colours (the fallbacks); other variants resolve the palette for the
    /// actual theme. Without the variant, lookups never reach the theme dictionaries.
    /// </summary>
    /// <summary>
    /// Resolved theme brushes. Looking one up walks the tree to the application's resources, and a frame asks for
    /// dozens per row; at a screenful of rows that search was most of the time spent drawing.
    /// </summary>
    private readonly Dictionary<string, IBrush?> _themeBrushes = new(StringComparer.Ordinal);
    private Avalonia.Styling.ThemeVariant? _themeBrushesVariant;

    private IBrush ThemeBrush(string key, IBrush fallback) {
        if (ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark) return fallback;
        if (!Equals(_themeBrushesVariant, ActualThemeVariant)) {
            _themeBrushes.Clear();
            _themeBrushesVariant = ActualThemeVariant;
        }

        if (!_themeBrushes.TryGetValue(key, out var cached)) {
            cached = this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : null;
            _themeBrushes[key] = cached;
        }

        return cached ?? fallback;
    }

    private IBrush TokenBrush(HighlightKind kind, IBrush fallback) => kind switch {
        HighlightKind.Keyword => ThemeBrush("GitKaySyntaxKeywordBrush", SyntaxHighlighting.GetKeywordBrush()),
        HighlightKind.String => ThemeBrush("GitKaySyntaxStringBrush", SyntaxHighlighting.GetStringBrush()),
        HighlightKind.Number => ThemeBrush("GitKaySyntaxNumberBrush", SyntaxHighlighting.GetNumberBrush()),
        HighlightKind.Comment => ThemeBrush("GitKaySyntaxCommentBrush", SyntaxHighlighting.GetCommentBrush()),
        HighlightKind.TypeName => ThemeBrush("GitKaySyntaxTypeBrush", SyntaxHighlighting.GetTypeBrush()),
        _ => fallback
    };

    private int FindRow(double y) {
        var index = Array.BinarySearch(_tops, y);
        if (index < 0) index = ~index - 1;
        return Math.Clamp(index, 0, Math.Max(0, _rows.Length - 1));
    }

    internal double RowHeightAt(int index) => RowHeight(_rows[index]);

    private double RowHeight(IDiffRowProjection row) {
        var height = row switch {
            DiffFileHeaderProjection => FileHeight,
            DiffHunkHeaderProjection => HunkHeight,
            DiffGapProjection => GapHeight,
            DiffSectionHeaderProjection => SectionHeight,
            RenderedMarkdownRowProjection rendered => RenderedHeight(rendered),
            RenderedMarkdownGapProjection => GapHeight,
            ImagePreviewRowProjection image => ImageRowHeight(image),
            _ => LineHeight
        };
        return _growProgress < 1 && _growingRows.Contains(row) ? height * EaseOut(_growProgress) : height;
    }

    /// <summary>Room for the picture at its natural size where it fits, plus the caption under it.</summary>
    private const double ImageCaptionHeight = 34;
    private const double ImagePadding = 16;

    private Size ImageDrawSize(ImagePreviewRowProjection image) {
        var pixels = image.Image.PixelSize;
        var scale = GitKay.Core.Presentation.ImageView.scale(image.Zoom, ContentWidth - 2 * ImagePadding, pixels.Width);
        var drawn = GitKay.Core.Presentation.ImageView.drawnSize(scale, pixels.Width, pixels.Height);
        return new Size(drawn.Item1, drawn.Item2);
    }

    /// <summary>
    /// The width rows are laid out against. Bounds are not set during the first measure, and a picture sized from
    /// a zero width would measure one pixel tall and only correct itself on a later pass.
    /// </summary>
    private double ContentWidth => _measurementWidth > 0 ? _measurementWidth : Bounds.Width;

    private double FitScale(ImagePreviewRowProjection image) =>
        GitKay.Core.Presentation.ImageView.fitScale(ContentWidth - 2 * ImagePadding, image.Image.PixelSize.Width);

    /// <summary>
    /// Zooms one image about the middle of the viewport, keeping its top where it is so the page does not jump
    /// under the pointer as the row grows or shrinks.
    /// </summary>
    private void ZoomImage(ImagePreviewRowProjection image, GitKay.Core.Presentation.ZoomChange change) {
        var index = Array.IndexOf(_rows, image);
        if (index < 0) return;
        var current = image.Zoom > 0 ? image.Zoom : FitScale(image);
        var next = GitKay.Core.Presentation.ImageView.step(change, current);
        if (Math.Abs(next - image.Zoom) < 0.0001) return;

        if (_scrollViewer != null)
            _pendingAnchor = new ViewportAnchor(null, null, "", _tops[index] - _scrollViewer.Offset.Y, image);
        image.Zoom = next;
        if (next == 0 || ImageDrawSize(image).Width <= ContentWidth) SetHorizontalOffset(0);
        // Row heights are only recomputed when the pane's width changes; this changes one row's height on its own.
        ComputeTops();
        InvalidateOverview();
        InvalidateMeasure();
        InvalidateVisual();
        RestoreViewportAnchor();
    }

    /// <summary>
    /// Dragging a picture moves the picture, the way every image viewer works — not a text selection, which is
    /// what a drag means everywhere else in this pane. The row under the press decides which it is.
    /// </summary>
    private ImagePreviewRowProjection? _panningImage;
    private Point _panFrom;
    private double _panFromHorizontal;
    private double _panFromVertical;

    private void BeginImagePan(ImagePreviewRowProjection image, Point position, IPointer pointer) {
        _panningImage = image;
        _panFrom = position;
        _panFromHorizontal = _horizontalOffset;
        _panFromVertical = _scrollViewer?.Offset.Y ?? 0;
        pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    private void UpdateImagePan(Point position) {
        // The picture follows the pointer: dragging left moves the view right.
        SetHorizontalOffset(_panFromHorizontal - (position.X - _panFrom.X));
        if (_scrollViewer != null)
            SetOffsetWithoutScrolling(Math.Max(0, _panFromVertical - (position.Y - _panFrom.Y)));
    }

    private ImagePreviewRowProjection? ImageRowAt(Point position) {
        var index = RowAt(position, out _);
        return (uint)index < (uint)_rows.Length ? _rows[index] as ImagePreviewRowProjection : null;
    }

    private double ImageRowHeight(ImagePreviewRowProjection image) =>
        ImageDrawSize(image).Height + ImageCaptionHeight + 2 * ImagePadding;

    private void DrawImagePreview(DrawingContext context, ImagePreviewRowProjection image, double y) {
        var size = ImageDrawSize(image);
        var x = Math.Max(ImagePadding, (Bounds.Width - size.Width) / 2) - _horizontalOffset;
        var top = y + ImagePadding;
        if (size.Width > 0 && size.Height > 0) {
            // A chequer behind it, so a transparent PNG does not read as whatever the theme is behind it.
            var backdrop = new Rect(x, top, size.Width, size.Height);
            context.FillRectangle(ThemeBrush("GitKayRaisedBrush", CodeBlockFallback), backdrop);
            context.DrawImage(image.Image, new Rect(image.Image.Size), backdrop);
            context.DrawRectangle(null, new Pen(ThemeBrush("GitKayBorderBrush", HunkBrush), 1), backdrop);
        }

        var caption = image.IsOldSide ? image.Metadata + "  ·  as it was before deletion" : image.Metadata;
        var layout = Layout(caption, 11, ThemeBrush("GitKayMutedTextBrush", HunkBrush), false);
        // The caption stays put while the picture pans under it: it describes the file, not the part on screen.
        context.DrawText(layout, new Point(Math.Max(ImagePadding, (Bounds.Width - layout.Width) / 2), top + size.Height + 10));
    }

    private static GitKay.Core.MarkdownInline.Image? ImageInline(GitKay.Core.LocatedMarkdownBlock? located) {
        if (located?.Block is not GitKay.Core.MarkdownBlock.Paragraph paragraph
            || paragraph.Item is not { IsEmpty: false, Tail.IsEmpty: true }
            || paragraph.Item.Head is not GitKay.Core.MarkdownInline.Image image) return null;
        return image;
    }

    private static bool TryImageInline(RenderedMarkdownRowProjection row, out GitKay.Core.MarkdownInline.Image? oldImage, out GitKay.Core.MarkdownInline.Image? newImage) {
        oldImage = ImageInline(row.OldLocated);
        newImage = ImageInline(row.NewLocated);
        return oldImage != null || newImage != null;
    }

    private void DrawRenderedImage(DrawingContext context, GitKay.Core.MarkdownInline.Image? markdownImage,
        IReadOnlyDictionary<string, Bitmap>? images, double x, double y, double maxWidth) {
        if (markdownImage == null) return;
        if (markdownImage.source.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) {
            var svgPlaceholder = new Rect(x, y, maxWidth, 54);
            context.DrawRectangle(ThemeBrush("GitKayRaisedBrush", CodeBlockFallback), new Pen(ThemeBrush("GitKayBorderBrush", HunkBrush), .7), svgPlaceholder, 4, 4);
            DrawPlain(context, "SVG image", x + 10, y + 8, 12, ThemeBrush("GitKaySecondaryTextBrush", FileBrush));
            DrawPlain(context, string.IsNullOrWhiteSpace(markdownImage.alt) ? markdownImage.source : markdownImage.alt, x + 10, y + 28, 11, ThemeBrush("GitKayMutedTextBrush", HunkBrush));
            return;
        }
        if (images?.TryGetValue(markdownImage.source, out var image) == true && image != null) {
            var scale = Math.Min(1, maxWidth / Math.Max(1, image.Size.Width));
            context.DrawImage(image, new Rect(image.Size), new Rect(x, y, image.Size.Width * scale, image.Size.Height * scale));
            if (!string.IsNullOrWhiteSpace(markdownImage.title))
                DrawPlain(context, markdownImage.title, x, y + image.Size.Height * scale + 4, 11, ThemeBrush("GitKayMutedTextBrush", HunkBrush));
            return;
        }
        var remote = Uri.TryCreate(markdownImage.source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        var label = remote ? $"Load image from {uri!.Host}" : RenderedImagesLoading ? "Loading image…" : "Missing image";
        var detail = string.IsNullOrWhiteSpace(markdownImage.alt) ? markdownImage.source : markdownImage.alt;
        var placeholder = new Rect(x, y, maxWidth, 54);
        context.DrawRectangle(ThemeBrush("GitKayRaisedBrush", CodeBlockFallback), new Pen(ThemeBrush("GitKayBorderBrush", HunkBrush), .7), placeholder, 4, 4);
        if (remote) _renderedLinks.Add(new RenderedLinkHit(placeholder, "gitkay-load-image:" + markdownImage.source));
        DrawPlain(context, label, x + 10, y + 8, 12, remote ? ThemeBrush("GitKayAccentBrush", FileBrush) : ThemeBrush("GitKaySecondaryTextBrush", FileBrush));
        DrawPlain(context, detail, x + 10, y + 28, 11, ThemeBrush("GitKayMutedTextBrush", HunkBrush));
    }

    private double RenderedHeight(RenderedMarkdownRowProjection row) {
        if (row.Located.Block is GitKay.Core.MarkdownBlock.CodeBlock code) {
            var codeLineCount = row.CodeLines.Count > 0
                ? DiffLayout.IsSideBySide ? row.CodeLines.Count : row.CodeLines.Sum(line => line.Change == GitKay.Core.MarkdownChangeKind.Modified ? 2 : 1)
                : code.text.Replace("\r\n", "\n").Split('\n').Length;
            return 18 + Math.Max(1, codeLineCount) * LineHeight;
        }
        if (row.Located.Block is GitKay.Core.MarkdownBlock.Table table) {
            var tableWidth = Math.Max(80, (DiffLayout.IsSideBySide ? _measurementWidth / 2 : _measurementWidth) - 44);
            var height = RenderedTableHeight(table, tableWidth);
            if (row.OldLocated?.Block is GitKay.Core.MarkdownBlock.Table oldTable) height = Math.Max(height, RenderedTableHeight(oldTable, tableWidth));
            if (row.NewLocated?.Block is GitKay.Core.MarkdownBlock.Table newTable) height = Math.Max(height, RenderedTableHeight(newTable, tableWidth));
            return 14 + height;
        }
        if (TryImageInline(row, out var oldImageInline, out var newImageInline)) {
            var splitImages = DiffLayout.IsSideBySide || row.Kind == GitKay.Core.MarkdownChangeKind.Modified && oldImageInline != null && newImageInline != null;
            var widthForImage = Math.Max(80, (splitImages ? _measurementWidth / 2 : _measurementWidth) - 44);
            double height(GitKay.Core.MarkdownInline.Image? inline, IReadOnlyDictionary<string, Bitmap>? images) {
                if (inline == null) return 0;
                if (images?.TryGetValue(inline.source, out var bitmap) != true || bitmap == null) return 54;
                return bitmap.Size.Height * Math.Min(1, widthForImage / Math.Max(1, bitmap.Size.Width)) + (string.IsNullOrWhiteSpace(inline.title) ? 0 : 20);
            }
            return 16 + Math.Max(height(oldImageInline, OldRenderedImages), height(newImageInline, RenderedImages));
        }
        var width = Math.Max(80, (DiffLayout.IsSideBySide ? _measurementWidth / 2 : _measurementWidth) - 38);
        var size = row.Located.Block is GitKay.Core.MarkdownBlock.Heading heading ? Math.Max(CodeFontSize + 1, CodeFontSize + 8 - heading.level) : CodeFontSize + 1;
        var lines = !DiffLayout.IsSideBySide && row.Kind == GitKay.Core.MarkdownChangeKind.Modified && row.Words.Count > 0
            ? RenderedWordLineCount(row.Words, width, size)
            : DiffLayout.IsSideBySide
                ? row.Kind == GitKay.Core.MarkdownChangeKind.Modified && row.Words.Count > 0
                    ? Math.Max(RenderedWordLineCount(row.Words.Where(span => span.Kind != GitKay.Core.MarkdownWordSpanKind.Inserted).ToArray(), width, size),
                        RenderedWordLineCount(row.Words.Where(span => span.Kind != GitKay.Core.MarkdownWordSpanKind.Deleted).ToArray(), width, size))
                    : Math.Max(RenderedSpanLineCount(row.OldSpans, width, size), RenderedSpanLineCount(row.NewSpans, width, size))
                : RenderedSpanLineCount(row.NewSpans.Count > 0 ? row.NewSpans : row.OldSpans, width, size);
        return 16 + Math.Max(1, lines) * Math.Round(size * 1.45);
    }

    private double RenderedTableHeight(GitKay.Core.MarkdownBlock.Table table, double width) {
        var rows = new List<IReadOnlyList<Microsoft.FSharp.Collections.FSharpList<GitKay.Core.MarkdownInline>>>();
        if (!table.header.IsEmpty) rows.Add(table.header);
        rows.AddRange(table.rows);
        var columns = Math.Max(1, rows.Select(row => row.Count).DefaultIfEmpty(1).Max());
        var cellWidth = width / columns;
        var total = 0.0;
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++) {
            var rowHeight = LineHeight;
            foreach (var cell in rows[rowIndex]) {
                var text = string.Concat(cell.Select(GitKay.Core.Markdown.inlineText));
                var layout = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    rowIndex == 0 ? StrongTypeface : ProseTypeface, CodeFontSize + 1, Brushes.Transparent) { MaxTextWidth = Math.Max(10, cellWidth - 10) };
                rowHeight = Math.Max(rowHeight, layout.Height + 4);
            }
            total += rowHeight;
        }
        return total;
    }

    private static int RenderedWordLineCount(IReadOnlyList<GitKay.Core.MarkdownWordSpan> words, double width, double size) {
        var lines = 1;
        var x = 0.0;
        foreach (var word in words) {
            var layout = new FormattedText(word.Text + " ", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, ProseTypeface, size, Brushes.Transparent);
            var itemWidth = layout.WidthIncludingTrailingWhitespace;
            if (x > 0 && x + itemWidth > width) { lines++; x = 0; }
            x += itemWidth;
        }
        return lines;
    }

    private static int RenderedSpanLineCount(IReadOnlyList<GitKay.Core.RenderedMarkdownSpan> spans, double width, double size) {
        var lines = 1;
        var x = 0.0;
        foreach (var span in spans) {
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(span.Text, @"[^\s]+[ \t]*|\r?\n|[ \t]+")) {
                var value = match.Value;
                if (value.Contains('\n')) { lines++; x = 0; continue; }
                var layout = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    RenderedTypeface(span.Style), size, Brushes.Transparent);
                var itemWidth = layout.WidthIncludingTrailingWhitespace;
                if (x > 0 && x + itemWidth > width) { lines++; x = 0; }
                x += itemWidth;
            }
        }
        return lines;
    }

    private static double EaseOut(double t) => 1 - (1 - t) * (1 - t);

    private readonly record struct ViewportAnchor(int? OldLineNo, int? NewLineNo, string Content, double ViewportOffset, IDiffRowProjection? Row = null);
    private sealed record ExpansionAnchor(GitKay.Core.DiffExpansion.DiffGap Gap, ViewportAnchor Anchor, long StartedAt);
    private readonly record struct GapCell(GitKay.Core.DiffExpansion.ExpandDirection Direction, Rect Bounds);
    private readonly record struct RenderedLinkHit(Rect Bounds, string Target);
    private readonly record struct RenderedTextHit(int Row, int Side, Rect Bounds, int Start, string Text, Typeface Typeface, double Size);
    private readonly record struct GapActionHit(int Row, int Action) {
        public static readonly GapActionHit None = new(-1, -1);
        public bool IsNone => Row < 0;
    }

    /// <summary>Ctrl changes what the expanders mean, so the glyphs are redrawn as it goes down and comes back up.</summary>
    private void TrackExpandAll(KeyModifiers modifiers) {
        var held = modifiers.HasFlag(KeyModifiers.Control);
        if (held == _expandAllHeld) return;
        _expandAllHeld = held;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        TrackExpandAll(e.KeyModifiers);
        if (_panningImage != null) {
            UpdateImagePan(e.GetPosition(this));
            return;
        }

        if (_selectingText) {
            UpdateTextSelection(e.GetPosition(this));
            return;
        }

        var hoveredRow = RowAt(e.GetPosition(this), out _);
        if (hoveredRow != _hoveredRowIndex) {
            var headerChanged = (uint)hoveredRow < (uint)_rows.Length && _rows[hoveredRow] is DiffFileHeaderProjection
                                || (uint)_hoveredRowIndex < (uint)_rows.Length && _rows[_hoveredRowIndex] is DiffFileHeaderProjection;
            _hoveredRowIndex = hoveredRow;
            if (headerChanged) InvalidateVisual();
        }

        var point = e.GetPosition(this);
        var hit = GapActionAt(point);
        var renderedLink = _renderedLinks.FirstOrDefault(candidate => candidate.Bounds.Contains(point));
        var overLink = !string.IsNullOrEmpty(renderedLink.Target);
        ToolTip.SetTip(this, overLink ? (renderedLink.Target.StartsWith("gitkay-load-image:", StringComparison.Ordinal) ? renderedLink.Target[18..] : renderedLink.Target) : null);
        if (hit == _hoveredGapAction && overLink == _hoveredRenderedLink) return;
        _hoveredGapAction = hit;
        _hoveredRenderedLink = overLink;
        Cursor = !hit.IsNone || overLink ? new Cursor(StandardCursorType.Hand)
            : ImageRowAt(point) != null ? new Cursor(StandardCursorType.SizeAll)
            : Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e) {
        base.OnPointerExited(e);
        if (_hoveredGapAction.IsNone && _pressedGapAction.IsNone && !_hoveredRenderedLink) return;
        _hoveredGapAction = GapActionHit.None;
        _pressedGapAction = GapActionHit.None;
        _hoveredRenderedLink = false;
        ToolTip.SetTip(this, null);
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        Focus();
        var position = e.GetPosition(this);
        var rowIndex = FindRow(position.Y);
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) {
            if ((uint)rowIndex < (uint)_rows.Length && _rows[rowIndex] is DiffGapProjection menuGap)
                ShowGapMenu(rowIndex, menuGap);
            else if ((uint)rowIndex < (uint)_rows.Length && _rows[rowIndex] is DiffLineProjection menuLine) {
                // Right-clicking a line focuses it unless it's inside a text selection the menu may act on.
                if (!HasTextSelection) SelectedItem = menuLine;
                ShowLineMenu(rowIndex, menuLine);
            }
            else if (RowAt(position, out _) is var headerIndex && (uint)headerIndex < (uint)_rows.Length && _rows[headerIndex] is DiffFileHeaderProjection menuHeader) {
                SelectedItem = menuHeader;
                var menu = new ContextMenu();
                FileContextRequested?.Invoke(this, new DiffFileMenuEventArgs(menuHeader.File, null, menu));
                if (menu.Items.Count > 0) menu.Open(this);
            }
            e.Handled = true;
            return;
        }

        var hit = GapActionAt(position);
        if (!hit.IsNone) {
            _pressedGapAction = hit;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
        else {
            var index = RowAt(position, out _);
            if ((uint)index < (uint)_rows.Length && _rows[index] is DiffSectionHeaderProjection section)
                section.Toggle();
            else if ((uint)index < (uint)_rows.Length && _rows[index] is not (DiffHunkHeaderProjection or DiffGapProjection))
                SelectedItem = _rows[index];
            if ((uint)index < (uint)_rows.Length && _rows[index] is DiffFileHeaderProjection clickedHeader && e.ClickCount == 2)
                ToggleFileAnchored(clickedHeader, ToggleFileCommand);
            if ((uint)index < (uint)_rows.Length && _rows[index] is DiffLineProjection or RenderedMarkdownRowProjection)
                BeginTextSelection(index, position, e.ClickCount, e.KeyModifiers.HasFlag(KeyModifiers.Shift), e.Pointer);
            else if ((uint)index < (uint)_rows.Length && _rows[index] is ImagePreviewRowProjection image
                     && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginImagePan(image, position, e.Pointer);
        }
        e.Handled = true;
    }

    // ----- Text selection: drag across code, double-click word, triple-click line, Shift+click extends. -----

    private readonly record struct TextPosition(int Row, int Char);

    private static GitKay.Core.DiffNavigation.TextPosition ToCore(TextPosition position) => new(position.Row, position.Char);
    private static TextPosition FromCore(GitKay.Core.DiffNavigation.TextPosition position) => new(position.Row, position.Column);

    /// <summary>The rows as navigation sees them; rebuilt only when the rows change.</summary>
    private GitKay.Core.DiffNavigation.Row[] NavigationRows {
        get {
            if (_navigationRowsFor == _rows) return _navigationRows;
            _navigationRows = _rows.Select(row => row switch {
                DiffFileHeaderProjection => new GitKay.Core.DiffNavigation.Row(GitKay.Core.DiffNavigation.RowKind.FileHeader, -1, -1),
                DiffHunkHeaderProjection => new GitKay.Core.DiffNavigation.Row(GitKay.Core.DiffNavigation.RowKind.HunkHeader, -1, -1),
                DiffGapProjection => new GitKay.Core.DiffNavigation.Row(GitKay.Core.DiffNavigation.RowKind.CollapsedGap, -1, -1),
                DiffLineProjection line => new GitKay.Core.DiffNavigation.Row(GitKay.Core.DiffNavigation.RowKind.Line, line.OldLineNo ?? -1, line.NewLineNo ?? -1),
                RenderedMarkdownRowProjection rendered => new GitKay.Core.DiffNavigation.Row(GitKay.Core.DiffNavigation.RowKind.Line, rendered.Located.FirstLine, rendered.Located.FirstLine),
                _ => new GitKay.Core.DiffNavigation.Row(GitKay.Core.DiffNavigation.RowKind.HunkHeader, -1, -1),
            }).ToArray();
            _navigationRowsFor = _rows;
            return _navigationRows;
        }
    }

    private GitKay.Core.DiffNavigation.Row[] _navigationRows = [];
    private IDiffRowProjection[]? _navigationRowsFor;

    private static string RenderedWordsText(IEnumerable<GitKay.Core.MarkdownWordSpan> words) {
        var output = new System.Text.StringBuilder();
        string? previous = null;
        foreach (var span in words) {
            if (previous != null && char.IsLetterOrDigit(previous[^1]) && span.Text.Length > 0 && char.IsLetterOrDigit(span.Text[0])) output.Append(' ');
            output.Append(span.Text);
            previous = span.Text;
        }
        return output.ToString();
    }

    private string RenderedRowText(RenderedMarkdownRowProjection rendered, int side) {
        if (rendered.Kind == GitKay.Core.MarkdownChangeKind.Modified && rendered.Words.Count > 0) {
            var words = DiffLayout.IsSideBySide
                ? side == 0 ? rendered.Words.Where(span => span.Kind != GitKay.Core.MarkdownWordSpanKind.Inserted)
                    : rendered.Words.Where(span => span.Kind != GitKay.Core.MarkdownWordSpanKind.Deleted)
                : rendered.Words;
            return RenderedWordsText(words);
        }
        var spans = DiffLayout.IsSideBySide
            ? side == 0 ? rendered.OldSpans : rendered.NewSpans
            : rendered.NewSpans.Count > 0 ? rendered.NewSpans : rendered.OldSpans;
        return string.Concat(spans.Select(span => span.Text));
    }

    /// <summary>A row's text on a selection side, or null for rows that aren't lines.</summary>
    private string? RowText(int row, int side) => _rows[row] switch {
        DiffLineProjection line => TextFor(line, side),
        RenderedMarkdownRowProjection rendered => RenderedRowText(rendered, side),
        _ => null,
    };

    private Microsoft.FSharp.Core.FSharpFunc<int, string> TextAt(int side) =>
        Microsoft.FSharp.Core.FuncConvert.FromFunc<int, string>(row => RowText(row, side)!);

    private Microsoft.FSharp.Core.FSharpFunc<int, int> LengthAt(int side) =>
        Microsoft.FSharp.Core.FuncConvert.FromFunc<int, int>(row => RowText(row, side)?.Length ?? 0);
    private sealed record TextSelection(TextPosition Anchor, TextPosition Active, int Side);

    private TextSelection? _textSelection;
    private bool _selectingText;
    private bool _hoveredRenderedLink;
    private int _drawingRowIndex = -1;
    private object? _lastItemsSource;

    /// <summary>Selects code between two row/character positions (used by tooling and tests).</summary>
    internal void SelectText(int startRow, int startChar, int endRow, int endChar, int side = 0) {
        _textSelection = new TextSelection(new TextPosition(startRow, startChar), new TextPosition(endRow, endChar), side);
        InvalidateVisual();
    }

    internal int RowIndexAt(double documentY) => FindRow(documentY);
    internal (int Row, int Character) TextPositionAtForTest(Point point, int side = 0) {
        var position = PositionAt(point, side);
        return (position.Row, position.Char);
    }

    internal string RowKindAt(double documentY) {
        var index = FindRow(documentY);
        return (uint)index < (uint)_rows.Length ? _rows[index] switch { DiffLineProjection => "line", DiffFileHeaderProjection => "header", DiffGapProjection => "gap", _ => "hunk" } : "none";
    }

    internal string HitDebug(Point documentPoint) => $"row={FindRow(documentPoint.Y)} sticky={_stickyIndex} gap={GapActionAt(documentPoint)}";

    internal int FirstLineRowIndex(int skip = 0) =>
        Enumerable.Range(0, _rows.Length).Where(i => _rows[i] is DiffLineProjection).Skip(skip).DefaultIfEmpty(-1).First();

    public bool HasTextSelection => _visualAnchorRow >= 0 || _textSelection is { } selection && selection.Anchor != selection.Active;

    /// <summary>The rows a text or visual selection spans, or the focused row when nothing is selected.</summary>
    public IReadOnlyList<IDiffRowProjection> SelectedRows {
        get {
            if (_textSelection is { } selection && (selection.Anchor != selection.Active || _visualAnchorRow >= 0)) {
                var first = Math.Max(0, Math.Min(selection.Anchor.Row, selection.Active.Row));
                var last = Math.Min(_rows.Length - 1, Math.Max(selection.Anchor.Row, selection.Active.Row));
                return first > last ? [] : _rows[first..(last + 1)];
            }
            return SelectedItem is { } item ? [item] : [];
        }
    }

    private static bool SameLineAt(IDiffRowProjection[] rows, int index) =>
        (uint)index < (uint)rows.Length && rows[index] is DiffLineProjection;

    /// <summary>Which content column a point is in: 0 for the only / old side, 1 for the new side in side-by-side.</summary>
    private int SideAt(double x) => DiffLayout.IsSideBySide && x >= Bounds.Width / 2 ? 1 : 0;

    /// <summary>Where a content column's text starts, before horizontal scrolling.</summary>
    private double ColumnOrigin(int side) =>
        DiffLayout.IsSideBySide ? (side == 0 ? Z(56) : Bounds.Width / 2 + Z(57))
        : DiffLayout.IsUnified ? Z(54)
        : Z(52);

    private double ContentOrigin(int side) => ColumnOrigin(side) - _horizontalOffset;

    private double ColumnWidth(int side) => DiffLayout.IsSideBySide
        ? side == 0 ? Bounds.Width / 2 - Z(64) : Bounds.Width / 2 - Z(57)
        : Bounds.Width - ColumnOrigin(0);

    // ----- Horizontal scrolling: code columns share one offset; gutters and line numbers stay put. -----

    private double _horizontalOffset;

    private const double HorizontalWheelStep = 48;

    private void SetHorizontalOffset(double offset) {
        var clamped = Math.Clamp(offset, 0, MaxHorizontalOffset());
        if (Math.Abs(clamped - _horizontalOffset) < 0.1) return;
        _horizontalOffset = clamped;
        InvalidateVisual();
    }

    /// <summary>How far the widest line near the viewport extends past its column.</summary>
    private double MaxHorizontalOffset() {
        if (_rows.Length == 0) return 0;
        var top = _scrollViewer?.Offset.Y ?? 0;
        var height = _scrollViewer?.Viewport.Height ?? Bounds.Height;
        var start = Math.Max(0, FindRow(top));
        var end = Math.Min(_rows.Length, FindRow(top + height) + 1);
        var widest = 0.0;
        for (var index = start; index < end; index++) {
            if (_rows[index] is ImagePreviewRowProjection image) {
                widest = Math.Max(widest, ImageDrawSize(image).Width + 2 * ImagePadding - ContentWidth);
                continue;
            }
            if (_rows[index] is not DiffLineProjection line) continue;
            if (DiffLayout.IsSideBySide) {
                widest = Math.Max(widest, TextWidth(line.OldContent) - ColumnWidth(0));
                widest = Math.Max(widest, TextWidth(line.NewContent) - ColumnWidth(1));
            }
            else {
                widest = Math.Max(widest, TextWidth(TextFor(line, 0)) - ColumnWidth(0));
            }
        }
        // A little slack past the end, so the last character isn't flush against the edge.
        return widest <= 0 ? 0 : widest + Z(24);
    }

    internal double HorizontalOffset => _horizontalOffset;

    // Very long lines (minified files) are estimated from one monospace cell rather than laid out.
    private double TextWidth(string text) =>
        text.Length == 0 ? 0
        : text.Length > MaxHighlightedLineLength ? text.Length * Layout("0", CodeFontSize, Brushes.Transparent, false).Width
        : Layout(text, CodeFontSize, Brushes.Transparent, false).Width;

    /// <summary>Scrolls horizontally just enough to show the caret.</summary>
    private void EnsureCaretVisible() {
        if (SelectedItem is RenderedMarkdownRowProjection) { InvalidateVisual(); return; }
        if (SelectedItem is not DiffLineProjection line) return;
        var side = CaretSide(line);
        var text = TextFor(line, side);
        var column = Math.Min(_caretChar, text.Length);
        var x = column == 0 ? 0 : Layout(text[..column], CodeFontSize, Brushes.Transparent, false).Width;
        var width = ColumnWidth(side);
        var margin = Math.Min(Z(32), width / 4);
        if (x - _horizontalOffset < margin) _horizontalOffset = Math.Max(0, x - margin);
        else if (x - _horizontalOffset > width - margin) _horizontalOffset = x - width + margin;
        InvalidateVisual();
    }

    private string TextFor(DiffLineProjection line, int side) =>
        GitKay.Core.DiffLayoutModule.columnText(DiffLayout, side, line.Content, line.OldContent, line.NewContent);

    private int CharIndexAt(string text, double originX, double x) {
        var target = x - originX;
        if (target <= 0 || text.Length == 0) return 0;
        int low = 0, high = text.Length;
        while (low < high) {
            var mid = (low + high + 1) / 2;
            if (Layout(text[..mid], CodeFontSize, Brushes.Transparent, false).Width <= target) low = mid; else high = mid - 1;
        }

        if (low < text.Length) {
            var before = Layout(text[..low], CodeFontSize, Brushes.Transparent, false).Width;
            var after = Layout(text[..(low + 1)], CodeFontSize, Brushes.Transparent, false).Width;
            if (target - before > after - target) low++;
        }

        return low;
    }

    private int RenderedCharIndexAt(RenderedTextHit hit, double x) {
        if (x <= hit.Bounds.X) return hit.Start;
        if (x >= hit.Bounds.Right) return hit.Start + hit.Text.Length;
        var target = x - hit.Bounds.X;
        var low = 0;
        var high = hit.Text.Length;
        double width(int length) => length == 0 ? 0 : new FormattedText(hit.Text[..length], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, hit.Typeface, hit.Size, Brushes.Transparent).WidthIncludingTrailingWhitespace;
        while (low < high) {
            var mid = (low + high + 1) / 2;
            if (width(mid) <= target) low = mid; else high = mid - 1;
        }
        if (low < hit.Text.Length && target - width(low) > width(low + 1) - target) low++;
        return hit.Start + low;
    }

    private TextPosition PositionAt(Point point, int side) {
        var index = Math.Clamp(FindRow(point.Y), 0, Math.Max(0, _rows.Length - 1));
        if (_rows[index] is RenderedMarkdownRowProjection) {
            var hits = _renderedTextHits.Where(hit => hit.Row == index && hit.Side == side).ToArray();
            if (hits.Length == 0) return new TextPosition(index, 0);
            var visualLine = hits.GroupBy(hit => hit.Bounds.Y)
                .OrderBy(group => Math.Abs(point.Y - (group.Key + group.Max(hit => hit.Bounds.Height) / 2)))
                .First().OrderBy(hit => hit.Bounds.X).ToArray();
            var hit = visualLine.FirstOrDefault(candidate => point.X <= candidate.Bounds.Right);
            if (hit.Text == null) hit = visualLine[^1];
            return new TextPosition(index, RenderedCharIndexAt(hit, point.X));
        }
        // Header, hunk and gap rows aren't selectable text: snap to the nearest textual row in the drag direction.
        if (_rows[index] is not DiffLineProjection) {
            var anchorRow = _textSelection?.Anchor.Row ?? index;
            var step = index >= anchorRow ? -1 : 1;
            while (index >= 0 && index < _rows.Length && _rows[index] is not (DiffLineProjection or RenderedMarkdownRowProjection)) index += step;
            index = Math.Clamp(index, 0, _rows.Length - 1);
            var snapped = RowText(index, side);
            return new TextPosition(index, step < 0 ? snapped?.Length ?? 0 : 0);
        }

        var line = (DiffLineProjection)_rows[index];
        return new TextPosition(index, CharIndexAt(TextFor(line, side), ContentOrigin(side), point.X));
    }

    private void BeginTextSelection(int index, Point point, int clickCount, bool extend, IPointer pointer) {
        _visualAnchorRow = -1;
        var side = extend && _textSelection != null ? _textSelection.Side : SideAt(point.X);
        var position = PositionAt(point, side);
        _caretSide = side;
        _caretChar = position.Char;
        var text = RowText(index, side) ?? "";

        if (clickCount >= 3) {
            _textSelection = new TextSelection(new TextPosition(index, 0), new TextPosition(index, text.Length), side);
        }
        else if (clickCount == 2) {
            static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
            var start = Math.Min(position.Char, text.Length);
            var end = start;
            while (start > 0 && IsWord(text[start - 1])) start--;
            while (end < text.Length && IsWord(text[end])) end++;
            _textSelection = new TextSelection(new TextPosition(index, start), new TextPosition(index, end), side);
        }
        else if (extend && _textSelection != null) {
            _textSelection = _textSelection with { Active = position };
        }
        else {
            _textSelection = new TextSelection(position, position, side);
            _selectingText = true;
            pointer.Capture(this);
        }

        InvalidateVisual();
    }

    private void UpdateTextSelection(Point point) {
        if (_textSelection == null) return;
        _textSelection = _textSelection with { Active = PositionAt(point, _textSelection.Side) };

        // Scroll while dragging past the top or bottom edge.
        if (_scrollViewer != null) {
            var top = _scrollViewer.Offset.Y;
            var bottom = top + _scrollViewer.Viewport.Height;
            if (point.Y < top) _scrollViewer.Offset = _scrollViewer.Offset.WithY(Math.Max(0, point.Y));
            else if (point.Y > bottom) _scrollViewer.Offset = _scrollViewer.Offset.WithY(point.Y - _scrollViewer.Viewport.Height);
        }

        InvalidateVisual();
    }

    private (TextPosition Start, TextPosition End)? OrderedSelection() {
        if (_textSelection is not { } selection || selection.Anchor == selection.Active) return null;
        var (first, last) = GitKay.Core.DiffNavigation.ordered(ToCore(selection.Anchor), ToCore(selection.Active));
        return (FromCore(first), FromCore(last));
    }

    private static readonly IBrush TextSelectionFallback = new SolidColorBrush(Color.FromArgb(110, 56, 139, 253)).ToImmutable();

    private void DrawTextSelection(DrawingContext context, string text, double x, double y) {
        if (OrderedSelection() is not var (start, end) || _textSelection == null) return;
        var row = _drawingRowIndex;
        if (row < start.Row || row > end.Row) return;
        var side = DiffLayout.IsSideBySide && x + _horizontalOffset >= Bounds.Width / 2 ? 1 : 0;
        if (side != _textSelection.Side) return;

        var from = row == start.Row ? Math.Min(start.Char, text.Length) : 0;
        var to = row == end.Row ? Math.Min(end.Char, text.Length) : text.Length;
        var left = x + (from == 0 ? 0 : Layout(text[..from], CodeFontSize, Brushes.Transparent, false).Width);
        var right = x + (to == 0 ? 0 : Layout(text[..to], CodeFontSize, Brushes.Transparent, false).Width);
        // Lines continuing past this row show a little of the line break, like an editor.
        if (row < end.Row) right += 6;
        if (right > left)
            context.FillRectangle(ThemeBrush("GitKayTextSelectionBrush", TextSelectionFallback), new Rect(left, y, right - left, LineHeight - 1));
    }

    /// <summary>The selected code (no line numbers or +/- markers); the focused line when nothing is selected.</summary>
    public string? GetCopyText() {
        if (OrderedSelection() is var (start, end) && _textSelection != null)
            return GitKay.Core.DiffNavigation.selectedText(ToCore(start), ToCore(end), TextAt(_textSelection.Side));

        return SelectedItem switch {
            DiffLineProjection selected => LineText(selected),
            RenderedMarkdownRowProjection rendered => rendered.Source,
            _ => null,
        };
    }

    private string LineText(DiffLineProjection line) =>
        DiffLayout.IsSideBySide && !string.IsNullOrEmpty(line.NewContent) ? line.NewContent : TextFor(line, 0);

    public async void CopySelection() {
        if (GetCopyText() is not { } text || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        // Like vim, a yank ends visual mode.
        if (_visualAnchorRow >= 0) {
            _visualAnchorRow = -1;
            _textSelection = null;
            InvalidateVisual();
        }
        await clipboard.SetTextAsync(text);
        TextCopied?.Invoke(this, text.Split('\n').Length);
    }

    // ----- Caret and vim visual mode -----
    // The caret sits on the focused line: h / l move it, 0 / $ jump to the line ends, w / b move by word.
    // In side-by-side it belongs to one column; moving past a column's edge crosses to the other column.
    // v selects characters from the caret, V selects whole lines; j / k / h / l extend; y yanks; Esc cancels.

    private int _visualAnchorRow = -1;
    private int _visualAnchorChar;
    private bool _visualLinewise;
    private int _caretChar;
    private int _caretSide = -1;

    public bool IsVisualMode => _visualAnchorRow >= 0;

    private int SelectedLineIndex => SelectedItem is DiffLineProjection or RenderedMarkdownRowProjection && Array.IndexOf(_rows, SelectedItem) is var index and >= 0 ? index : -1;

    /// <summary>The caret's column: side-by-side starts on the new side, and an empty side (an added or removed line) yields to the other.</summary>
    private int CaretSide(DiffLineProjection line) {
        if (!DiffLayout.IsSideBySide) return 0;
        var side = _caretSide < 0 ? 1 : _caretSide;
        if (TextFor(line, side).Length == 0 && TextFor(line, 1 - side).Length > 0) side = 1 - side;
        return side;
    }

    private void DrawCaret(DrawingContext context, string text, double x, double y) {
        if (!IsKeyboardFocusWithin || _drawingRowIndex != SelectedLineIndex || SelectedItem is not DiffLineProjection line) return;
        var side = DiffLayout.IsSideBySide && x + _horizontalOffset >= Bounds.Width / 2 ? 1 : 0;
        if (side != CaretSide(line)) return;
        var column = Math.Min(_caretChar, text.Length);
        var left = x + (column == 0 ? 0 : Layout(text[..column], CodeFontSize, Brushes.Transparent, false).Width);
        context.FillRectangle(ThemeBrush("GitKayAccentBrush", SearchMatchFallback), new Rect(Math.Round(left) - 0.5, y + 1, 2, LineHeight - 3));
    }

    protected override void OnGotFocus(FocusChangedEventArgs e) {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e) {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    private void ToggleVisualMode(bool linewise) {
        if (_visualAnchorRow >= 0) {
            var sameKind = _visualLinewise == linewise;
            _visualAnchorRow = -1;
            _textSelection = null;
            InvalidateVisual();
            // Like vim, v in V mode (or V in v mode) switches kind instead of leaving.
            if (sameKind) return;
        }

        var index = SelectedItem == null ? -1 : Array.IndexOf(_rows, SelectedItem);
        if (index < 0 && _scrollViewer != null) index = FindRow(_scrollViewer.Offset.Y);
        while (index >= 0 && index < _rows.Length && _rows[index] is not (DiffLineProjection or RenderedMarkdownRowProjection)) index++;
        if (index < 0 || index >= _rows.Length) return;
        SelectedItem = _rows[index];
        _caretSide = _rows[index] is DiffLineProjection line ? CaretSide(line) : 0;
        _caretChar = Math.Min(_caretChar, RowText(index, _caretSide)?.Length ?? 0);
        _visualAnchorRow = index;
        _visualAnchorChar = _caretChar;
        _visualLinewise = linewise;
        UpdateVisualSelection(index);
    }

    private void UpdateVisualSelection(int activeRow) {
        if (_visualAnchorRow < 0 || _visualAnchorRow >= _rows.Length || _rows[_visualAnchorRow] is not (DiffLineProjection or RenderedMarkdownRowProjection)) return;
        var side = _rows[_visualAnchorRow] is DiffLineProjection anchorLine ? CaretSide(anchorLine) : _caretSide < 0 ? 0 : _caretSide;
        if (_visualLinewise) {
            if (GitKay.Core.DiffNavigation.linewise(NavigationRows, _visualAnchorRow, activeRow, LengthAt(side)) is not { IsSome: true } lines) return;
            var (first, last) = lines.Value;
            _textSelection = new TextSelection(FromCore(first), FromCore(last), side);
        }
        else {
            var anchor = new GitKay.Core.DiffNavigation.TextPosition(_visualAnchorRow, _visualAnchorChar);
            var active = new GitKay.Core.DiffNavigation.TextPosition(activeRow, _caretChar);
            var (first, last) = GitKay.Core.DiffNavigation.characterwise(anchor, active, LengthAt(side));
            _textSelection = new TextSelection(FromCore(first), FromCore(last), side);
        }
        InvalidateVisual();
    }

    /// <summary>Moves the caret within the focused line; returns false when there is no line to move on.</summary>
    /// <summary>After find-in-diff moves to a row, puts the caret on the first match in it.</summary>
    public void PlaceCaretOnFindMatch() {
        var row = SelectedLineIndex;
        if (row < 0 || _rows[row] is not DiffLineProjection line) return;
        var (query, regex) = string.IsNullOrWhiteSpace(FindQuery) ? (SearchHighlightQuery, SearchHighlightUseRegex) : (FindQuery, FindUseRegex);
        var preferred = CaretSide(line);
        foreach (var side in DiffLayout.IsSideBySide ? new[] { preferred, 1 - preferred } : new[] { 0 }) {
            var first = -1;
            ForEachMatch(TextFor(line, side), query, regex, (start, _) => { if (first < 0) first = start; });
            if (first < 0) continue;
            _caretSide = side;
            PlaceCaret(row, side, first);
            return;
        }
    }

    /// <summary>vim's zt / zz / zb: scrolls so the focused row sits at the top, centre or bottom of the viewport.</summary>
    public void ScrollSelectionTo(GitKay.Core.Vim.VimScroll position) {
        var index = SelectedItem == null ? -1 : Array.IndexOf(_rows, SelectedItem);
        if (_scrollViewer == null || index < 0) return;
        var viewport = _scrollViewer.Viewport.Height;
        var top = _tops[index];
        var height = _tops[index + 1] - top;
        var offset = position switch {
            GitKay.Core.Vim.VimScroll.Top => top,
            GitKay.Core.Vim.VimScroll.Bottom => top + height - viewport,
            _ => top - (viewport - height) / 2,
        };
        _scrollViewer.Offset = _scrollViewer.Offset.WithY(Math.Clamp(offset, 0, Math.Max(0, _tops[^1] - viewport)));
    }

    /// <summary>vim's {count}G: focuses the row with that line number in the focused file (the first file when none).</summary>
    public void GoToLine(int number) {
        var focus = SelectedItem == null ? 0 : Math.Max(0, Array.IndexOf(_rows, SelectedItem));
        var target = GitKay.Core.DiffNavigation.goToLine(NavigationRows, focus, DiffLayout, number);
        if (target < 0) return;
        var best = _rows[target];
        SelectedItem = best;
        ScrollIntoView(best);
        if (_visualAnchorRow >= 0) UpdateVisualSelection(Array.IndexOf(_rows, best));
    }

    private bool PlaceCaret(int row, int side, int column) {
        if (_caretSide < 0) _caretSide = side;
        _caretChar = column;
        if (_visualAnchorRow >= 0) UpdateVisualSelection(row);
        EnsureCaretVisible();
        return true;
    }

    // ----- vim keys: GitKay.Core.Vim interprets them; this view reports its state and carries out the actions. -----

    private readonly GitKay.Core.Vim.VimSession _ownVim = new();

    /// <summary>The window's shared session; when set, the window dispatches this view's keys to it.</summary>
    public GitKay.Core.Vim.VimSession? SharedVim { get; set; }

    /// <summary>App-level actions keys can trigger (finding, commit relations, copying commit references).</summary>
    public IVimCommands? VimCommands { get; set; }

    /// <summary>The span the last yank copied.</summary>
    internal (int Row, int Side, int From, int To)? LastYank { get; private set; }

    GitKay.Core.Vim.VimPane GitKay.Core.Vim.IVimHost.Pane => GitKay.Core.Vim.VimPane.Diff;

    private int EffectiveSide(int row) => _rows[row] is DiffLineProjection line ? CaretSide(line) : _caretSide < 0 ? 0 : _caretSide;

    string GitKay.Core.Vim.IVimHost.LineText =>
        SelectedLineIndex is var row && row >= 0 ? RowText(row, EffectiveSide(row))! : null!;

    int GitKay.Core.Vim.IVimHost.Caret =>
        SelectedLineIndex is var row && row >= 0 ? Math.Min(_caretChar, RowText(row, EffectiveSide(row))?.Length ?? 0) : 0;

    string GitKay.Core.Vim.IVimHost.OtherSideText {
        get {
            if (!DiffLayout.IsSideBySide || SelectedItem is not DiffLineProjection line) return null!;
            var other = TextFor(line, 1 - CaretSide(line));
            return other.Length == 0 ? null! : other;
        }
    }

    int GitKay.Core.Vim.IVimHost.Side => SelectedItem is DiffLineProjection line ? CaretSide(line) : 0;

    bool GitKay.Core.Vim.IVimHost.HasSelection => HasTextSelection;

    int GitKay.Core.Vim.IVimHost.HalfPageRows => Math.Max(1, ViewportRowCount / 2);

    void GitKay.Core.Vim.IVimHost.SetCaret(int column) {
        if (SelectedLineIndex is var row && row >= 0) PlaceCaret(row, _rows[row] is DiffLineProjection line ? CaretSide(line) : 0, column);
    }

    void GitKay.Core.Vim.IVimHost.SwitchSide(int column) {
        if (SelectedLineIndex is not (var row and >= 0) || _rows[row] is not DiffLineProjection line) return;
        _caretSide = 1 - CaretSide(line);
        PlaceCaret(row, _caretSide, column);
    }

    void GitKay.Core.Vim.IVimHost.MoveRows(int delta) => MoveSelection(delta);

    void GitKay.Core.Vim.IVimHost.MoveToEdge(bool last) => MoveSelection(last ? int.MaxValue / 2 : int.MinValue / 2);

    void GitKay.Core.Vim.IVimHost.GoToPosition(int position) => GoToLine(position);

    void GitKay.Core.Vim.IVimHost.MoveToHunk(int direction) => MoveToHunk(direction);

    void GitKay.Core.Vim.IVimHost.ScrollFocus(GitKay.Core.Vim.VimScroll position) => ScrollSelectionTo(position);

    int GitKay.Core.Vim.IVimHost.PageRows => Math.Max(1, ViewportRowCount - 2);

    void GitKay.Core.Vim.IVimHost.FocusScreenRow(GitKay.Core.Vim.VimScreenRow row, int offset) {
        if (_scrollViewer == null || _rows.Length == 0) return;
        var (first, last) = FullyVisibleRows();
        var index = row switch {
            GitKay.Core.Vim.VimScreenRow.Top => Math.Min(last, first + offset),
            GitKay.Core.Vim.VimScreenRow.Bottom => Math.Max(first, last - offset),
            _ => FindRow(_scrollViewer.Offset.Y + _scrollViewer.Viewport.Height / 2),
        };
        FocusRow(index);
    }

    void GitKay.Core.Vim.IVimHost.ScrollRows(int delta) {
        if (_scrollViewer == null || _rows.Length == 0) return;
        var top = Math.Clamp(FindRow(_scrollViewer.Offset.Y) + delta, 0, _rows.Length - 1);
        var maximum = Math.Max(0, _tops[^1] - _scrollViewer.Viewport.Height);
        _scrollViewer.Offset = _scrollViewer.Offset.WithY(Math.Min(_tops[top], maximum));
        // Like vim, the focus stays put until scrolling would take it off screen.
        var (first, last) = FullyVisibleRows(_scrollViewer.Offset.Y);
        if (SelectedItem != null && Array.IndexOf(_rows, SelectedItem) is var focused && focused >= 0 && (focused < first || focused > last))
            FocusRow(focused < first ? first : last);
    }

    /// <summary>The first and last rows wholly inside the viewport (or the partly visible ones when none fit).</summary>
    private (int First, int Last) FullyVisibleRows(double? offset = null) {
        var top = offset ?? _scrollViewer!.Offset.Y;
        var bottom = top + _scrollViewer!.Viewport.Height;
        var first = FindRow(top);
        if (_tops[first] < top - 0.5 && first + 1 < _rows.Length) first++;
        var last = FindRow(Math.Max(top, bottom - 1));
        if (_tops[last + 1] > bottom + 0.5 && last > first) last--;
        return (first, Math.Max(first, last));
    }

    private void FocusRow(int index) {
        SelectedItem = _rows[index];
        ScrollIntoView(_rows[index]);
        if (_visualAnchorRow >= 0) UpdateVisualSelection(index);
    }

    void GitKay.Core.Vim.IVimHost.ToggleVisual(bool linewise) => ToggleVisualMode(linewise);

    bool GitKay.Core.Vim.IVimHost.CancelSelection() {
        if (_textSelection == null && _visualAnchorRow < 0) return false;
        _visualAnchorRow = -1;
        _textSelection = null;
        InvalidateVisual();
        return true;
    }

    void GitKay.Core.Vim.IVimHost.CopySelection() => CopySelection();

    void GitKay.Core.Vim.IVimHost.CopyRange(int from, int until, bool wholeLine) {
        if (SelectedLineIndex is not (var row and >= 0)) return;
        var side = _rows[row] is DiffLineProjection line ? CaretSide(line) : 0;
        var text = RowText(row, side) ?? "";
        from = Math.Clamp(from, 0, text.Length);
        until = Math.Clamp(until, from, text.Length);
        CopyText(text[from..until]);
        LastYank = (row, side, from, until);
        TextCopied?.Invoke(this, wholeLine ? 1 : 0);
    }

    void GitKay.Core.Vim.IVimHost.CopyCommitReference(bool subject) => VimCommands?.CopyCommitReference(subject);

    void GitKay.Core.Vim.IVimHost.FindWord(string word, bool forward) {
        VimCommands?.FindWord(word, forward);
        PlaceCaretOnFindMatch();
    }

    void GitKay.Core.Vim.IVimHost.FindNext(bool forward) {
        VimCommands?.FindNext(GitKay.Core.Vim.VimPane.Diff, forward);
        PlaceCaretOnFindMatch();
    }

    void GitKay.Core.Vim.IVimHost.GoToParent(int index) => VimCommands?.GoToParent(index);

    void GitKay.Core.Vim.IVimHost.GoToChild() => VimCommands?.GoToChild();

    void GitKay.Core.Vim.IVimHost.PaneCommand(GitKay.Core.Vim.VimPaneCommand command) => VimCommands?.PaneCommand(command);

    void GitKay.Core.Vim.IVimHost.OpenSearch(bool forward) => VimCommands?.OpenSearch(GitKay.Core.Vim.VimPane.Diff, forward);

    /// <summary>The caret and scroll position, so a cancelled search can put them back.</summary>
    public (IDiffRowProjection? Row, int Caret, double Offset) SaveViewPosition() => (SelectedItem, _caretChar, _scrollViewer?.Offset.Y ?? 0);

    public void RestoreViewPosition((IDiffRowProjection? Row, int Caret, double Offset) position) {
        SelectedItem = position.Row;
        _caretChar = position.Caret;
        if (_scrollViewer != null) _scrollViewer.Offset = _scrollViewer.Offset.WithY(position.Offset);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Delta.Y != 0) {
            // Over a picture, the code font size means nothing; zoom what is actually under the pointer.
            if (ImageRowAt(e.GetPosition(this)) is { } image)
                ZoomImage(image, e.Delta.Y > 0
                    ? GitKay.Core.Presentation.ZoomChange.ZoomIn
                    : GitKay.Core.Presentation.ZoomChange.ZoomOut);
            else CodeFontSize = Math.Clamp(CodeFontSize + Math.Sign(e.Delta.Y), 7, 32);
            e.Handled = true;
            return;
        }

        // Shift explicitly scrolls columns. For a touchpad, choose the dominant axis: treating any tiny X component
        // as horizontal swallowed the Y component of slow, slightly diagonal gestures and made vertical scrolling stop.
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var sideways = shift ? (e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) : e.Delta.X;
        if (sideways != 0 && (shift || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))) {
            SetHorizontalOffset(_horizontalOffset - sideways * HorizontalWheelStep);
            e.Handled = true;
            return;
        }

        base.OnPointerWheelChanged(e);
    }

    private async void CopyText(string text) {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    protected override void OnKeyUp(KeyEventArgs e) {
        TrackExpandAll(e.KeyModifiers);
        base.OnKeyUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        TrackExpandAll(e.KeyModifiers | (e.Key is Key.LeftCtrl or Key.RightCtrl ? KeyModifiers.Control : KeyModifiers.None));
        // With a picture selected, the zoom keys zoom it; 0 puts it back to fitting the pane.
        if (SelectedItem is ImagePreviewRowProjection selectedImage && !e.KeyModifiers.HasFlag(KeyModifiers.Alt)) {
            var zoom = e.Key switch {
                Key.OemPlus or Key.Add => GitKay.Core.Presentation.ZoomChange.ZoomIn,
                Key.OemMinus or Key.Subtract => GitKay.Core.Presentation.ZoomChange.ZoomOut,
                Key.D0 or Key.NumPad0 => GitKay.Core.Presentation.ZoomChange.ZoomToFit,
                _ => null,
            };
            if (zoom is { } change) {
                ZoomImage(selectedImage, change);
                e.Handled = true;
                return;
            }
        }

        // Inside the main window, the window sends keys to its shared session before they reach this view.
        if (SharedVim == null && _ownVim.Handle(this, VimKeys.From(e))) {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control) {
            CopySelection();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control && SelectedItem != null) {
            // Select all code in the current file.
            var index = Array.IndexOf(_rows, SelectedItem);
            if (index >= 0) {
                var first = index;
                while (first > 0 && _rows[first - 1] is not DiffFileHeaderProjection) first--;
                var last = index;
                while (last + 1 < _rows.Length && _rows[last + 1] is not DiffFileHeaderProjection) last++;
                while (first <= last && _rows[first] is not DiffLineProjection) first++;
                while (last >= first && _rows[last] is not DiffLineProjection) last--;
                if (first <= last && _rows[last] is DiffLineProjection lastLine) {
                    var side = DiffLayout.IsSideBySide ? 1 : 0;
                    _textSelection = new TextSelection(new TextPosition(first, 0), new TextPosition(last, TextFor(lastLine, side).Length), side);
                    InvalidateVisual();
                }
            }

            e.Handled = true;
            return;
        }

        // Enter / Space act on the focused row: collapse or expand a file, or reveal a gap's hidden lines.
        if (e.Key is Key.Enter or Key.Space && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift) {
            var index = SelectedItem == null ? -1 : Array.IndexOf(_rows, SelectedItem);
            if (index >= 0 && _rows[index] is DiffFileHeaderProjection fileHeader) {
                ToggleFileAnchored(fileHeader, e.KeyModifiers == KeyModifiers.Shift ? ToggleFileContextCommand : ToggleFileCommand);

                e.Handled = true;
                return;
            }

            if (index >= 0 && _rows[index] is DiffGapProjection selectedGap) {
                RequestExpansion(index, selectedGap, GitKay.Core.DiffExpansion.ExpandDirection.All);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && index >= 0 && _rows[index] is RenderedMarkdownRowProjection rendered) {
                var target = rendered.NewSpans.Concat(rendered.OldSpans).FirstOrDefault(span => span.Style == GitKay.Core.MarkdownSpanStyle.Link)?.Target;
                if (target is { } link) { RenderedLinkRequested?.Invoke(this, link.Value); e.Handled = true; return; }
            }
        }

        base.OnKeyDown(e);
    }

    private void ShowLineMenu(int index, DiffLineProjection line) {
        var menu = new ContextMenu();
        void Add(string header, bool enabled, Action action) {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        if (LineMenuOpening != null) {
            LineMenuOpening.Invoke(menu);
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        }
        Add("Copy", HasTextSelection, CopySelection);
        Add("Copy line", true, () => CopyText(LineText(line)));
        var header = index;
        while (header >= 0 && _rows[header] is not DiffFileHeaderProjection) header--;
        if (header >= 0 && _rows[header] is DiffFileHeaderProjection file) {
            var path = GitKay.Core.FileChange.currentPath(file.File.Key.OldPath, file.File.Key.NewPath);
            if (FileContextRequested == null) {
                Add("Copy file path", true, () => CopyText(path));
            }
            else {
                menu.Items.Add(new Separator());
                FileContextRequested.Invoke(this, new DiffFileMenuEventArgs(file.File, line.NewLineNo ?? line.OldLineNo, menu));
            }
        }

        menu.Open(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton == MouseButton.Left) {
            var point = e.GetPosition(this);
            var link = _renderedLinks.FirstOrDefault(candidate => candidate.Bounds.Contains(point));
            if (!string.IsNullOrEmpty(link.Target)) { RenderedLinkRequested?.Invoke(this, link.Target); e.Handled = true; return; }
        }
        if (_panningImage != null) {
            _panningImage = null;
            e.Pointer.Capture(null);
            Cursor = Cursor.Default;
            return;
        }

        if (_selectingText) {
            _selectingText = false;
            e.Pointer.Capture(null);
            // The caret follows the end of a drag, so keyboard selection continues from there.
            if (_textSelection is { } dragged && (uint)dragged.Active.Row < (uint)_rows.Length && _rows[dragged.Active.Row] is DiffLineProjection or RenderedMarkdownRowProjection) {
                SelectedItem = _rows[dragged.Active.Row];
                _caretChar = dragged.Active.Char;
                InvalidateVisual();
            }
            return;
        }

        if (_pressedGapAction.IsNone) return;
        var pressed = _pressedGapAction;
        _pressedGapAction = GapActionHit.None;
        e.Pointer.Capture(null);
        InvalidateVisual();

        // Only a release inside the same bounded control activates it.
        if (GapActionAt(e.GetPosition(this)) != pressed) return;
        if (_rows[pressed.Row] is DiffFileHeaderProjection header) {
            if (pressed.Action == HeaderPreviewAction) PreviewRequested?.Invoke(this, header.File);
            else if (pressed.Action == HeaderChangesOnlyAction) ChangesOnlyRequested?.Invoke(this, header.File);
            else ToggleFileAnchored(header, pressed.Action == HeaderChevronAction ? ToggleFileCommand : ToggleFileContextCommand);
            e.Handled = true;
            return;
        }
        if (_rows[pressed.Row] is not DiffGapProjection gap) return;
        var cells = GapCells(gap, _tops[pressed.Row]);
        if ((uint)pressed.Action >= (uint)cells.Count) return;
        // Ctrl turns any of a gap's arrows into "all of it", the way the glyph under the pointer says it will.
        var direction = e.KeyModifiers.HasFlag(KeyModifiers.Control)
            ? GitKay.Core.DiffExpansion.ExpandDirection.All
            : cells[pressed.Action].Direction;
        RequestExpansion(pressed.Row, gap, direction);
        e.Handled = true;
    }

    /// <summary>Visible row count, for half-page movement.</summary>
    public int ViewportRowCount => _scrollViewer == null ? 20 : Math.Max(1, (int)(_scrollViewer.Viewport.Height / LineHeight));

    /// <summary>Selects the next or previous hunk (or gap) boundary, like vim's ]c / [c.</summary>
    public bool MoveToHeading(string target) {
        var wanted = target.TrimStart('#');
        for (var i = 0; i < _rows.Length; i++)
            if (_rows[i] is RenderedMarkdownRowProjection { Located.Block: GitKay.Core.MarkdownBlock.Heading heading }
                && string.Equals(GitKay.Core.Markdown.headingSlug(string.Concat(heading.Item2.Select(GitKay.Core.Markdown.inlineText))), wanted, StringComparison.OrdinalIgnoreCase)) {
                SelectedItem = _rows[i]; ScrollIntoView(_rows[i]); return true;
            }
        return false;
    }

    public void MoveToHunk(int direction) {
        if (_rows.Length == 0) return;
        var focus = SelectedItem == null ? -1 : Array.IndexOf(_rows, SelectedItem);
        // Rendered Markdown uses changed blocks as hunk boundaries.
        var rendered = direction > 0
            ? Enumerable.Range(Math.Max(0, focus + 1), Math.Max(0, _rows.Length - Math.Max(0, focus + 1))).FirstOrDefault(i => _rows[i] is RenderedMarkdownRowProjection { IsChanged: true }, -1)
            : Enumerable.Range(0, Math.Max(0, focus)).Reverse().FirstOrDefault(i => _rows[i] is RenderedMarkdownRowProjection { IsChanged: true }, -1);
        if (rendered >= 0) { SelectedItem = _rows[rendered]; ScrollIntoView(_rows[rendered]); return; }
        // The first line of the hunk, so the change itself is in view.
        var index = GitKay.Core.DiffNavigation.hunkTarget(NavigationRows, focus, direction > 0);
        if (index < 0) return;
        SelectedItem = _rows[index];
        ScrollIntoView(_rows[index]);
    }

    public void MoveSelection(int delta) {
        if (_rows.Length == 0) return;
        var current = SelectedItem == null ? -1 : Array.IndexOf(_rows, SelectedItem);
        // Nothing selected (or the selection is gone): start from what's on screen, not the top of the diff.
        if (current < 0 && _scrollViewer != null) current = FindRow(_scrollViewer.Offset.Y) - (delta > 0 ? 1 : 0);
        var next = Math.Clamp(current + delta, 0, _rows.Length - 1);
        SelectedItem = _rows[next];
        ScrollIntoView(SelectedItem);
        if (_visualAnchorRow >= 0) UpdateVisualSelection(next);
    }

    /// <summary>Scrolls so the item sits at the top of the viewport (jumping to a file puts its header first).</summary>
    public void ScrollToTop(IDiffRowProjection item) {
        if (_scrollViewer == null) return;
        var index = Array.IndexOf(_rows, item);
        if (index < 0) return;
        _scrollViewer.Offset = _scrollViewer.Offset.WithY(Math.Max(0, Math.Min(_tops[index], _tops[^1] - _scrollViewer.Viewport.Height)));
    }

    public void ScrollIntoView(IDiffRowProjection item) {
        if (_scrollViewer == null) return;
        var index = Array.IndexOf(_rows, item);
        if (index < 0) return;
        var top = _tops[index];
        var bottom = _tops[index + 1];
        var offset = _scrollViewer.Offset;
        if (top < offset.Y) _scrollViewer.Offset = offset.WithY(top);
        else if (bottom > offset.Y + _scrollViewer.Viewport.Height) _scrollViewer.Offset = offset.WithY(Math.Max(0, bottom - _scrollViewer.Viewport.Height));
    }
}
