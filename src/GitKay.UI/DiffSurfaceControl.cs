using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
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
        var searchMatches = string.IsNullOrEmpty(search) ? null : GitKay.Core.GitSearch.matcher(SearchHighlightUseRegex, search);
        var findMatches = string.IsNullOrEmpty(find) ? null : GitKay.Core.GitSearch.matcher(FindUseRegex, find);
        var runStart = -1;
        OverviewMarkKind runKind = OverviewMarkKind.Added;

        void CloseRun(int end) {
            if (runStart < 0) return;
            _overviewMarks.Add(new OverviewMark(_tops[runStart] / total, (_tops[end] - _tops[runStart]) / total, runKind));
            runStart = -1;
        }

        for (var i = 0; i < _rows.Length; i++) {
            if (_rows[i] is not DiffLineProjection line) {
                CloseRun(i);
                continue;
            }

            OverviewMarkKind? kind = line.IsAdded ? OverviewMarkKind.Added : line.IsRemoved ? OverviewMarkKind.Removed : null;
            if (kind != runKind || kind == null) CloseRun(i);
            if (kind is { } changed && runStart < 0) { runStart = i; runKind = changed; }

            var top = _tops[i] / total;
            var height = (_tops[i + 1] - _tops[i]) / total;
            if (searchMatches != null && searchMatches.Invoke(line.Content)) _overviewMarks.Add(new OverviewMark(top, height, OverviewMarkKind.SearchMatch));
            if (findMatches != null && findMatches.Invoke(line.Content)) _overviewMarks.Add(new OverviewMark(top, height, OverviewMarkKind.FindMatch));
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
    private const double FileCardTop = 12;
    private const double FileChevronWidth = 32;
    private const int HeaderChevronAction = 100;
    private const int HeaderContextAction = 101;
    private const int MaxHighlightedLineLength = 240;
    private const int MaxLayoutCacheEntries = 2048;
    private static readonly Typeface CodeTypeface = new(FontStacks.Mono);
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromRgb(51, 51, 51)).ToImmutable();
    private static readonly IBrush FileBrush = new SolidColorBrush(Color.FromRgb(157, 167, 179)).ToImmutable();
    private static readonly IBrush HunkBrush = new SolidColorBrush(Color.FromRgb(136, 136, 136)).ToImmutable();
    private static readonly SemaphoreSlim HighlightWorkers = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));

    public static readonly StyledProperty<IEnumerable<IDiffRowProjection>?> ItemsSourceProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, IEnumerable<IDiffRowProjection>?>(nameof(ItemsSource));
    public static readonly StyledProperty<IDiffRowProjection?> SelectedItemProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, IDiffRowProjection?>(nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> CodeFontSizeProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, double>(nameof(CodeFontSize), DefaultCodeFontSize);
    public static readonly StyledProperty<string> ModeProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, string>(nameof(Mode), "diff");
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
    public static readonly StyledProperty<ICommand?> ExpandGapCommandProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, ICommand?>(nameof(ExpandGapCommand));

    private IDiffRowProjection[] _rows = Array.Empty<IDiffRowProjection>();
    private double[] _tops = [0];
    private INotifyCollectionChanged? _collection;
    private ScrollViewer? _scrollViewer;
    private CancellationTokenSource? _idle;
    private ViewportAnchor? _pendingAnchor;
    private int _generation;
    private GapActionHit _hoveredGapAction = GapActionHit.None;
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
    private bool _scrolling;
    private bool _programmaticScroll;
    private readonly Dictionary<string, FormattedText> _plainLayouts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FormattedText> _colouredLayouts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

    public event EventHandler<DiffFileMenuEventArgs>? FileContextRequested;
    /// <summary>Raised after text is copied, with the number of lines copied (0 for part of a line).</summary>
    public event EventHandler<int>? TextCopied;

    public IEnumerable<IDiffRowProjection>? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public IDiffRowProjection? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public string Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
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
    public ICommand? ExpandGapCommand { get => GetValue(ExpandGapCommandProperty); set => SetValue(ExpandGapCommandProperty, value); }

    static DiffSurfaceControl() {
        ItemsSourceProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.RebuildRows());
        SelectedItemProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.InvalidateVisual());
        ModeProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.InvalidateVisual());
        CodeFontSizeProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.OnCodeFontSizeChanged());
        FindQueryProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => { control.InvalidateVisual(); control.InvalidateOverview(); });
        FindUseRegexProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.InvalidateVisual());
        SearchHighlightQueryProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => { control.InvalidateVisual(); control.InvalidateOverview(); });
        SearchPathQueryProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.InvalidateVisual());
        SearchHighlightUseRegexProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.InvalidateVisual());
    }

    public DiffSurfaceControl() {
        Focusable = true;
        ActualThemeVariantChanged += (_, _) => {
            Interlocked.Increment(ref _generation);
            _plainLayouts.Clear();
            _colouredLayouts.Clear();
            _pending.Clear();
            InvalidateVisual();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _scrollViewer = this.FindAncestorOfType<ScrollViewer>();
        if (_scrollViewer != null) _scrollViewer.ScrollChanged += OnScrollChanged;
        OverviewChanged?.Invoke(this, EventArgs.Empty);
        RebuildRows();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        if (_scrollViewer != null) _scrollViewer.ScrollChanged -= OnScrollChanged;
        DetachCollection();
        _idle?.Cancel();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnCodeFontSizeChanged() {
        // Keep the row at the top of the viewport in place while rows change height.
        var anchorIndex = _scrollViewer == null || _rows.Length == 0 ? -1 : FindRow(_scrollViewer.Offset.Y);
        var within = anchorIndex < 0 ? 0 : (_scrollViewer!.Offset.Y - _tops[anchorIndex]) / Math.Max(1, _tops[anchorIndex + 1] - _tops[anchorIndex]);
        Interlocked.Increment(ref _generation);
        _colouredLayouts.Clear();
        _pending.Clear();
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
        // A different diff (or reshaped rows) invalidates row-based selection positions.
        if (_textSelection != null && (previousRows.Length != _rows.Length || !ReferenceEquals(ItemsSource, _lastItemsSource))) {
            _textSelection = null;
            _visualAnchorRow = -1;
        }
        if (!ReferenceEquals(ItemsSource, _lastItemsSource)) _horizontalOffset = 0;
        _lastItemsSource = ItemsSource;
        _hoveredGapAction = GapActionHit.None;
        _pressedGapAction = GapActionHit.None;
        TrackInsertedRows();
        ComputeTops();
        if (ItemsSource is INotifyCollectionChanged collection) {
            _collection = collection;
            _collection.CollectionChanged += OnCollectionChanged;
        }
        // Layout caches are keyed by text, so rows that survive a rebuild keep their colouring.
        HighlightGrowingRows();
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
        Dispatcher.UIThread.Post(() => {
            if (_scrollViewer == null) return;
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
    private void ToggleFileAnchored(DiffFileHeaderProjection header, ICommand? command) {
        if (command?.CanExecute(header.File) != true) return;
        var index = Array.IndexOf(_rows, header);
        if (_scrollViewer != null && index >= 0) {
            _expansionAnchor = null;
            _pendingAnchor = new ViewportAnchor(null, null, "", _tops[index] - _scrollViewer.Offset.Y, header);
        }
        command.Execute(header.File);
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
        Dispatcher.UIThread.Post(() => {
            if (_scrollViewer == null) return;
            var current = Array.IndexOf(_rows, row);
            if (current >= 0)
                SetOffsetWithoutScrolling(Math.Max(0, _tops[current] - anchor.ViewportOffset));
        }, DispatcherPriority.Loaded);
    }

    private void DetachCollection() {
        if (_collection != null) _collection.CollectionChanged -= OnCollectionChanged;
        _collection = null;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildRows();

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 1000 : availableSize.Width, _tops[^1]);

    /// <summary>Anchoring adjusts the offset without the user scrolling; keep full-quality rendering.</summary>
    private void SetOffsetWithoutScrolling(double y) {
        if (_scrollViewer == null) return;
        _programmaticScroll = true;
        try { _scrollViewer.Offset = _scrollViewer.Offset.WithY(y); }
        finally { _programmaticScroll = false; }
    }

    private void HighlightGrowingRows() {
        foreach (var row in _growingRows) {
            if (row is not DiffLineProjection line) continue;
            var foreground = ThemeBrush("GitKayTextBrush", line.Foreground);
            if (Mode == "side-by-side") {
                HighlightNow(line.OldContent, foreground);
                HighlightNow(line.NewContent, foreground);
            }
            else if (Mode == "new") HighlightNow(line.NewContent, foreground);
            else if (Mode == "old") HighlightNow(line.OldContent, foreground);
            else HighlightNow(line.Content, foreground);
        }
    }

    /// <summary>Colours a newly inserted line before its first frame so it never flashes plain.</summary>
    private void HighlightNow(string text, IBrush foreground) {
        if (string.IsNullOrEmpty(text) || text.Length > MaxHighlightedLineLength || _colouredLayouts.ContainsKey(text)) return;
        StoreColouredLayout(text, foreground, SyntaxHighlighting.Tokenize(text));
    }

    private void StoreColouredLayout(string text, IBrush foreground, IReadOnlyList<HighlightToken> tokens) {
        var layout = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, CodeTypeface, CodeFontSize, foreground);
        var offset = 0;
        foreach (var token in tokens) {
            if (token.Kind != HighlightKind.Plain) layout.SetForegroundBrush(TokenBrush(token.Kind, foreground), offset, token.Text.Length);
            offset += token.Text.Length;
        }
        if (_colouredLayouts.Count >= MaxLayoutCacheEntries)
            _colouredLayouts.Clear();
        _colouredLayouts[text] = layout;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) {
        if (_programmaticScroll) {
            InvalidateVisual();
            return;
        }
        _scrolling = true;
        Interlocked.Increment(ref _generation);
        _idle?.Cancel();
        var idle = new CancellationTokenSource();
        _idle = idle;
        InvalidateVisual();
        _ = EndScrollingAsync(idle);
    }

    private async Task EndScrollingAsync(CancellationTokenSource idle) {
        try {
            await Task.Delay(40, idle.Token);
            await Dispatcher.UIThread.InvokeAsync(() => {
                if (ReferenceEquals(_idle, idle)) {
                    _scrolling = false;
                    PrefetchAroundViewport();
                    InvalidateVisual();
                }
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { }
    }

    public override void Render(DrawingContext context) {
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
        }

        if (header < 0 || _tops[header] + FileCardTop >= offset) return;

        var next = header + 1;
        while (next < _rows.Length && _rows[next] is not DiffFileHeaderProjection) next++;
        var cardHeight = FileHeight - FileCardTop;
        var rowTop = offset - FileCardTop;
        if (next < _rows.Length)
            rowTop = Math.Min(rowTop, _tops[next] + FileCardTop - cardHeight - FileCardTop);

        _stickyIndex = header;
        _stickyRowTop = rowTop;
        // Opaque: rows scroll underneath the pinned header.
        var backdrop = new Rect(0, rowTop + FileCardTop - 1, Bounds.Width, cardHeight + 2);
        context.FillRectangle(ThemeBrush("GitKayWindowBrush", StickyWindowFallback), backdrop);
        context.FillRectangle(ThemeBrush("GitKaySurfaceBrush", StickySurfaceFallback), backdrop.Deflate(new Thickness(0.5, 1, 0.5, 1)));
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
        }
    }

    private Rect FileCardRect(double y) => new(0.5, y + FileCardTop + 0.5, Math.Max(0, Bounds.Width - 1), FileHeight - FileCardTop - 1);

    private Rect FileChevronRect(double y) => new(4, y + FileCardTop + 5, FileChevronWidth - 6, FileHeight - FileCardTop - 10);

    private Rect FileContextRect(DiffFileHeaderProjection file, double y) {
        var pathWidth = Layout(file.DisplayPath, 12, FileBrush, false).Width;
        return new Rect(FileChevronWidth + 8 + pathWidth + 8, y + FileCardTop + 5, 26, FileHeight - FileCardTop - 10);
    }

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
        context.DrawText(path, new Point(FileChevronWidth + 8, centerY - path.Height / 2));
        var pathUnderline = ThemeBrush("GitKayAccentBrush", SearchMatchFallback);
        ForEachMatch(file.DisplayPath, SearchPathQuery, SearchHighlightUseRegex, (start, length) =>
            DrawDottedUnderline(context, file.DisplayPath, start, length, FileChevronWidth + 8, centerY + path.Height / 2 - 1, pathUnderline, 12));

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

    private bool IsHeaderPartActive(int index, int action, out bool pressed) {
        pressed = _pressedGapAction.Row == index && _pressedGapAction.Action == action;
        return pressed || (_hoveredGapAction.Row == index && _hoveredGapAction.Action == action);
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

        if (cell.Direction.IsDown) {
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
        var request = new DiffGapExpansionRequest(gap.Gap, direction);
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

        switch (Mode) {
            case "side-by-side":
                DrawSideBySideLine(context, line, y);
                break;
            case "new":
                DrawSingleSideLine(context, line.NewLineNoText, line.NewContent, y, line);
                break;
            case "old":
                DrawSingleSideLine(context, line.OldLineNoText, line.OldContent, y, line);
                break;
            default:
                DrawUnifiedLine(context, line, y);
                break;
        }
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
            DrawCode(context, line.Content, Z(54) - _horizontalOffset, y + CodeTextTop, ThemeBrush("GitKayTextBrush", line.Foreground));
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
            DrawCode(context, content, Z(52) - _horizontalOffset, y + CodeTextTop, ThemeBrush("GitKayTextBrush", line.Foreground));
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
            DrawCode(context, line.OldContent, Z(56) - _horizontalOffset, y + CodeTextTop, ThemeBrush("GitKayTextBrush", line.Foreground));
        }

        DrawLineNumber(context, line.NewLineNoText, middle + Z(9), y, ThemeBrush("GitKayLineNumberBrush", LineNumberFallback));
        using (context.PushClip(NewColumnClip(middle, y))) {
            DrawFindMatches(context, line.NewContent, middle + Z(57) - _horizontalOffset, y);
            DrawCode(context, line.NewContent, middle + Z(57) - _horizontalOffset, y + CodeTextTop, ThemeBrush("GitKayTextBrush", line.Foreground));
        }
    }

    private Rect OldColumnClip(double middle, double y) => new(Z(56), y, Math.Max(0, middle - Z(64)), LineHeight);

    private Rect NewColumnClip(double middle, double y) => new(middle + Z(57), y, Math.Max(0, Bounds.Width - middle - Z(57)), LineHeight);

    private void DrawIntralineHighlights(DrawingContext context, string oldText, string newText, double oldX, double newX, double middle, double y) {
        if (_scrolling || string.IsNullOrEmpty(oldText) || string.IsNullOrEmpty(newText)
            || oldText == newText || oldText.Length > MaxHighlightedLineLength || newText.Length > MaxHighlightedLineLength)
            return;

        var prefixLength = 0;
        var sharedLength = Math.Min(oldText.Length, newText.Length);
        while (prefixLength < sharedLength && oldText[prefixLength] == newText[prefixLength])
            prefixLength++;

        var suffixLength = 0;
        while (suffixLength < sharedLength - prefixLength
               && oldText[oldText.Length - suffixLength - 1] == newText[newText.Length - suffixLength - 1])
            suffixLength++;

        using (context.PushClip(OldColumnClip(middle, y)))
            DrawChangedSpan(context, oldText, prefixLength, oldText.Length - prefixLength - suffixLength, oldX - _horizontalOffset, y,
                ThemeBrush("GitKayRemovedStrongBrush", ThemeBrush("GitKayRemovedBrush", Brushes.Transparent)));
        using (context.PushClip(NewColumnClip(middle, y)))
            DrawChangedSpan(context, newText, prefixLength, newText.Length - prefixLength - suffixLength, newX - _horizontalOffset, y,
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

    private void ForEachMatch(string text, string? rawQuery, bool useRegex, Action<int, int> onMatch) {
        var query = rawQuery?.Trim();
        if (string.IsNullOrEmpty(query)) return;
        if (useRegex) {
            if (TryRegex(query) is not { } regex) return;
            foreach (System.Text.RegularExpressions.Match match in regex.Matches(text))
                if (match.Length > 0) onMatch(match.Index, match.Length);
            return;
        }

        for (var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); index >= 0;
             index = text.IndexOf(query, index + query.Length, StringComparison.OrdinalIgnoreCase))
            onMatch(index, query.Length);
    }

    private void DrawDottedUnderline(DrawingContext context, string text, int start, int length, double x, double baseline, IBrush brush, double size) {
        var left = x + (start == 0 ? 0 : Layout(text[..start], size, Brushes.Transparent, false).Width);
        var width = Layout(text.Substring(start, length), size, Brushes.Transparent, false).Width;
        for (var dot = left; dot < left + width; dot += 3)
            context.FillRectangle(brush, new Rect(dot, baseline, 1.5, 1.5));
    }

    private readonly Dictionary<string, System.Text.RegularExpressions.Regex?> _regexCache = new(StringComparer.Ordinal);

    private System.Text.RegularExpressions.Regex? TryRegex(string pattern) {
        if (_regexCache.TryGetValue(pattern, out var cached)) return cached;
        if (_regexCache.Count > 16) _regexCache.Clear();
        System.Text.RegularExpressions.Regex? regex;
        try {
            regex = new System.Text.RegularExpressions.Regex(pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException) {
            regex = null;
        }

        _regexCache[pattern] = regex;
        return regex;
    }

    private void DrawChangedSpan(DrawingContext context, string text, int start, int length, double x, double y, IBrush brush) {
        if (length <= 0) return;
        var prefixWidth = start == 0 ? 0 : Layout(text[..start], CodeFontSize, Brushes.Transparent, false).Width;
        var changedWidth = Layout(text.Substring(start, length), CodeFontSize, Brushes.Transparent, false).Width;
        context.FillRectangle(brush, new Rect(x + prefixWidth, y, changedWidth, LineHeight - 1));
    }

    private static readonly IBrush LineNumberFallback = new SolidColorBrush(Color.FromRgb(110, 118, 129)).ToImmutable();

    private void DrawLineNumber(DrawingContext context, string text, double x, double y, IBrush foreground) {
        var layout = Layout(text, CodeFontSize, foreground, false);
        context.DrawText(layout, new Point(x + Z(34) - layout.Width, y + CodeTextTop));
    }

    private void DrawCode(DrawingContext context, string text, double x, double y, IBrush foreground) {
        var plain = Layout(text, CodeFontSize, foreground, false);
        if (_colouredLayouts.TryGetValue(text, out var coloured)) {
            context.DrawText(coloured, new Point(x, y));
            return;
        }
        if (_scrolling || text.Length > MaxHighlightedLineLength) {
            context.DrawText(plain, new Point(x, y));
            return;
        }
        ScheduleHighlight(text, foreground);
        context.DrawText(plain, new Point(x, y));
    }

    private void PrefetchAroundViewport() {
        if (_scrollViewer == null || _rows.Length == 0) return;
        var viewport = Math.Max(1, _scrollViewer.Viewport.Height);
        var start = FindRow(Math.Max(0, _scrollViewer.Offset.Y - viewport * 2));
        var end = Math.Min(_rows.Length, FindRow(_scrollViewer.Offset.Y + viewport * 3) + 1);

        for (var index = start; index < end; index++) {
            if (_rows[index] is not DiffLineProjection line) continue;
            if (Mode == "side-by-side") {
                ScheduleHighlight(line.OldContent, ThemeBrush("GitKayTextBrush", line.Foreground));
                ScheduleHighlight(line.NewContent, ThemeBrush("GitKayTextBrush", line.Foreground));
            }
            else if (Mode == "new")
                ScheduleHighlight(line.NewContent, ThemeBrush("GitKayTextBrush", line.Foreground));
            else if (Mode == "old")
                ScheduleHighlight(line.OldContent, ThemeBrush("GitKayTextBrush", line.Foreground));
            else
                ScheduleHighlight(line.Content, ThemeBrush("GitKayTextBrush", line.Foreground));
        }
    }

    private void ScheduleHighlight(string text, IBrush foreground) {
        if (string.IsNullOrEmpty(text) || text.Length > MaxHighlightedLineLength || _colouredLayouts.ContainsKey(text) || !_pending.Add(text)) return;
        var generation = _generation;
        _ = Task.Run(async () => {
            try {
                if (generation != Volatile.Read(ref _generation)) {
                    Dispatcher.UIThread.Post(() => _pending.Remove(text), DispatcherPriority.Background);
                    return;
                }

                await HighlightWorkers.WaitAsync();
                if (generation != Volatile.Read(ref _generation)) {
                    HighlightWorkers.Release();
                    Dispatcher.UIThread.Post(() => _pending.Remove(text), DispatcherPriority.Background);
                    return;
                }

                IReadOnlyList<HighlightToken> tokens;
                try { tokens = SyntaxHighlighting.Tokenize(text); }
                finally { HighlightWorkers.Release(); }
                await Dispatcher.UIThread.InvokeAsync(() => {
                    _pending.Remove(text);
                    if (generation != _generation || _scrolling) return;
                    if (!_colouredLayouts.ContainsKey(text)) StoreColouredLayout(text, foreground, tokens);
                    InvalidateVisual();
                }, DispatcherPriority.Background);
            }
            catch (Exception exception) {
                System.Diagnostics.Trace.WriteLine($"[diff-highlight] {exception}");
                Dispatcher.UIThread.Post(() => _pending.Remove(text), DispatcherPriority.Background);
            }
        });
    }

    private FormattedText Layout(string text, double size, IBrush brush, bool coloured) {
        var key = $"{size}:{text}";
        var cache = coloured ? _colouredLayouts : _plainLayouts;
        if (!cache.TryGetValue(key, out var layout)) {
            layout = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, CodeTypeface, size, brush);
            if (cache.Count >= MaxLayoutCacheEntries)
                cache.Clear();
            cache[key] = layout;
        }
        return layout;
    }

    private void DrawPlain(DrawingContext context, string text, double x, double y, double size, IBrush brush) =>
        context.DrawText(Layout(text, size, brush, false), new Point(x, y));

    /// <summary>
    /// Dark keeps the established drawn colours (the fallbacks); other variants resolve the palette for the
    /// actual theme. Without the variant, lookups never reach the theme dictionaries.
    /// </summary>
    private IBrush ThemeBrush(string key, IBrush fallback) =>
        ActualThemeVariant != Avalonia.Styling.ThemeVariant.Dark
        && this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : fallback;

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
            _ => LineHeight
        };
        return _growProgress < 1 && _growingRows.Contains(row) ? height * EaseOut(_growProgress) : height;
    }

    private static double EaseOut(double t) => 1 - (1 - t) * (1 - t);

    private readonly record struct ViewportAnchor(int? OldLineNo, int? NewLineNo, string Content, double ViewportOffset, IDiffRowProjection? Row = null);
    private sealed record ExpansionAnchor(GitKay.Core.DiffExpansion.DiffGap Gap, ViewportAnchor Anchor, long StartedAt);
    private readonly record struct GapCell(GitKay.Core.DiffExpansion.ExpandDirection Direction, Rect Bounds);
    private readonly record struct GapActionHit(int Row, int Action) {
        public static readonly GapActionHit None = new(-1, -1);
        public bool IsNone => Row < 0;
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
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

        var hit = GapActionAt(e.GetPosition(this));
        if (hit == _hoveredGapAction) return;
        _hoveredGapAction = hit;
        Cursor = hit.IsNone ? Cursor.Default : new Cursor(StandardCursorType.Hand);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e) {
        base.OnPointerExited(e);
        if (_hoveredGapAction.IsNone && _pressedGapAction.IsNone) return;
        _hoveredGapAction = GapActionHit.None;
        _pressedGapAction = GapActionHit.None;
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
            else if ((uint)rowIndex < (uint)_rows.Length && _rows[rowIndex] is DiffLineProjection menuLine)
                ShowLineMenu(rowIndex, menuLine);
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
            if ((uint)index < (uint)_rows.Length && _rows[index] is not (DiffHunkHeaderProjection or DiffGapProjection))
                SelectedItem = _rows[index];
            if ((uint)index < (uint)_rows.Length && _rows[index] is DiffFileHeaderProjection clickedHeader && e.ClickCount == 2)
                ToggleFileAnchored(clickedHeader, ToggleFileCommand);
            if ((uint)index < (uint)_rows.Length && _rows[index] is DiffLineProjection)
                BeginTextSelection(index, position, e.ClickCount, e.KeyModifiers.HasFlag(KeyModifiers.Shift), e.Pointer);
        }
        e.Handled = true;
    }

    // ----- Text selection: drag across code, double-click word, triple-click line, Shift+click extends. -----

    private readonly record struct TextPosition(int Row, int Char);
    private sealed record TextSelection(TextPosition Anchor, TextPosition Active, int Side);

    private TextSelection? _textSelection;
    private bool _selectingText;
    private int _drawingRowIndex = -1;
    private object? _lastItemsSource;

    /// <summary>Selects code between two row/character positions (used by tooling and tests).</summary>
    internal void SelectText(int startRow, int startChar, int endRow, int endChar, int side = 0) {
        _textSelection = new TextSelection(new TextPosition(startRow, startChar), new TextPosition(endRow, endChar), side);
        InvalidateVisual();
    }

    internal int RowIndexAt(double documentY) => FindRow(documentY);

    internal string RowKindAt(double documentY) {
        var index = FindRow(documentY);
        return (uint)index < (uint)_rows.Length ? _rows[index] switch { DiffLineProjection => "line", DiffFileHeaderProjection => "header", DiffGapProjection => "gap", _ => "hunk" } : "none";
    }

    internal string HitDebug(Point documentPoint) => $"row={FindRow(documentPoint.Y)} sticky={_stickyIndex} gap={GapActionAt(documentPoint)}";

    internal int FirstLineRowIndex(int skip = 0) =>
        Enumerable.Range(0, _rows.Length).Where(i => _rows[i] is DiffLineProjection).Skip(skip).DefaultIfEmpty(-1).First();

    public bool HasTextSelection => _visualAnchorRow >= 0 || _textSelection is { } selection && selection.Anchor != selection.Active;

    private static bool SameLineAt(IDiffRowProjection[] rows, int index) =>
        (uint)index < (uint)rows.Length && rows[index] is DiffLineProjection;

    /// <summary>Which content column a point is in: 0 for the only / old side, 1 for the new side in side-by-side.</summary>
    private int SideAt(double x) => Mode == "side-by-side" && x >= Bounds.Width / 2 ? 1 : 0;

    /// <summary>Where a content column's text starts, before horizontal scrolling.</summary>
    private double ColumnOrigin(int side) => Mode switch {
        "side-by-side" => side == 0 ? Z(56) : Bounds.Width / 2 + Z(57),
        "new" or "old" => Z(52),
        _ => Z(54),
    };

    private double ContentOrigin(int side) => ColumnOrigin(side) - _horizontalOffset;

    private double ColumnWidth(int side) => Mode == "side-by-side"
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
            if (_rows[index] is not DiffLineProjection line) continue;
            if (Mode == "side-by-side") {
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

    private string TextFor(DiffLineProjection line, int side) => Mode switch {
        "side-by-side" => side == 0 ? line.OldContent : line.NewContent,
        "new" => line.NewContent,
        "old" => line.OldContent,
        _ => line.Content,
    };

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

    private TextPosition PositionAt(Point point, int side) {
        var index = Math.Clamp(FindRow(point.Y), 0, Math.Max(0, _rows.Length - 1));
        // Header, hunk and gap rows aren't selectable text: snap to the nearest line in the drag direction.
        if (_rows[index] is not DiffLineProjection) {
            var anchorRow = _textSelection?.Anchor.Row ?? index;
            var step = index >= anchorRow ? -1 : 1;
            while (index >= 0 && index < _rows.Length && _rows[index] is not DiffLineProjection) index += step;
            index = Math.Clamp(index, 0, _rows.Length - 1);
            if (_rows[index] is not DiffLineProjection line0) return new TextPosition(index, 0);
            return new TextPosition(index, step < 0 ? TextFor(line0, side).Length : 0);
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
        var text = TextFor((DiffLineProjection)_rows[index], side);

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
        var (a, b) = (selection.Anchor, selection.Active);
        return a.Row < b.Row || (a.Row == b.Row && a.Char <= b.Char) ? (a, b) : (b, a);
    }

    private static readonly IBrush TextSelectionFallback = new SolidColorBrush(Color.FromArgb(110, 56, 139, 253)).ToImmutable();

    private void DrawTextSelection(DrawingContext context, string text, double x, double y) {
        if (OrderedSelection() is not var (start, end) || _textSelection == null) return;
        var row = _drawingRowIndex;
        if (row < start.Row || row > end.Row) return;
        var side = Mode == "side-by-side" && x + _horizontalOffset >= Bounds.Width / 2 ? 1 : 0;
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
        if (OrderedSelection() is var (start, end) && _textSelection != null) {
            var side = _textSelection.Side;
            var lines = new List<string>();
            for (var row = start.Row; row <= end.Row; row++) {
                if (_rows[row] is not DiffLineProjection line) continue;
                var text = TextFor(line, side);
                var from = row == start.Row ? Math.Min(start.Char, text.Length) : 0;
                var to = row == end.Row ? Math.Min(end.Char, text.Length) : text.Length;
                lines.Add(text[from..Math.Max(from, to)]);
            }

            return string.Join("\n", lines);
        }

        return SelectedItem is DiffLineProjection selected ? LineText(selected) : null;
    }

    private string LineText(DiffLineProjection line) =>
        Mode == "side-by-side" && !string.IsNullOrEmpty(line.NewContent) ? line.NewContent : TextFor(line, 0);

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

    private int SelectedLineIndex => SelectedItem is DiffLineProjection && Array.IndexOf(_rows, SelectedItem) is var index and >= 0 ? index : -1;

    /// <summary>The caret's column: side-by-side starts on the new side, and an empty side (an added or removed line) yields to the other.</summary>
    private int CaretSide(DiffLineProjection line) {
        if (Mode != "side-by-side") return 0;
        var side = _caretSide < 0 ? 1 : _caretSide;
        if (TextFor(line, side).Length == 0 && TextFor(line, 1 - side).Length > 0) side = 1 - side;
        return side;
    }

    private void DrawCaret(DrawingContext context, string text, double x, double y) {
        if (!IsKeyboardFocusWithin || _drawingRowIndex != SelectedLineIndex || SelectedItem is not DiffLineProjection line) return;
        var side = Mode == "side-by-side" && x + _horizontalOffset >= Bounds.Width / 2 ? 1 : 0;
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
        while (index >= 0 && index < _rows.Length && _rows[index] is not DiffLineProjection) index++;
        if (index < 0 || index >= _rows.Length || _rows[index] is not DiffLineProjection line) return;
        SelectedItem = line;
        _caretSide = CaretSide(line);
        _caretChar = Math.Min(_caretChar, TextFor(line, _caretSide).Length);
        _visualAnchorRow = index;
        _visualAnchorChar = _caretChar;
        _visualLinewise = linewise;
        UpdateVisualSelection(index);
    }

    private void UpdateVisualSelection(int activeRow) {
        if (_visualAnchorRow < 0 || _visualAnchorRow >= _rows.Length || _rows[_visualAnchorRow] is not DiffLineProjection anchorLine) return;
        var side = CaretSide(anchorLine);
        if (_visualLinewise) {
            var (start, end) = activeRow < _visualAnchorRow ? (activeRow, _visualAnchorRow) : (_visualAnchorRow, activeRow);
            while (start < end && _rows[start] is not DiffLineProjection) start++;
            while (end > start && _rows[end] is not DiffLineProjection) end--;
            if (_rows[end] is not DiffLineProjection last) return;
            _textSelection = new TextSelection(new TextPosition(start, 0), new TextPosition(end, TextFor(last, side).Length), side);
        }
        else {
            var anchor = new TextPosition(_visualAnchorRow, _visualAnchorChar);
            var active = new TextPosition(activeRow, _caretChar);
            // Vim's characterwise selection includes the character under the caret at both ends.
            var forward = active.Row > anchor.Row || (active.Row == anchor.Row && active.Char >= anchor.Char);
            var (from, to) = forward ? (anchor, active) : (active, anchor);
            var toText = _rows[to.Row] is DiffLineProjection toLine ? TextFor(toLine, side) : "";
            _textSelection = new TextSelection(from, to with { Char = Math.Min(to.Char + 1, toText.Length) }, side);
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
        foreach (var side in Mode == "side-by-side" ? new[] { preferred, 1 - preferred } : new[] { 0 }) {
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
        var fileStart = focus;
        while (fileStart > 0 && _rows[fileStart] is not DiffFileHeaderProjection) fileStart--;
        IDiffRowProjection? best = null;
        var bestNumber = int.MaxValue;
        for (var index = fileStart + 1; index < _rows.Length && _rows[index] is not DiffFileHeaderProjection; index++) {
            if (_rows[index] is not DiffLineProjection line) continue;
            var lineNumber = (Mode == "old" ? line.OldLineNo : line.NewLineNo) ?? line.OldLineNo ?? int.MaxValue;
            // The exact line, or the nearest shown line after it when that line is hidden context.
            if (lineNumber >= number && lineNumber < bestNumber) {
                best = line;
                bestNumber = lineNumber;
            }
        }
        if (best == null) return;
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

    string GitKay.Core.Vim.IVimHost.LineText =>
        SelectedLineIndex is var row && row >= 0 && _rows[row] is DiffLineProjection line ? TextFor(line, CaretSide(line)) : null!;

    int GitKay.Core.Vim.IVimHost.Caret =>
        SelectedItem is DiffLineProjection line ? Math.Min(_caretChar, TextFor(line, CaretSide(line)).Length) : 0;

    string GitKay.Core.Vim.IVimHost.OtherSideText {
        get {
            if (Mode != "side-by-side" || SelectedItem is not DiffLineProjection line) return null!;
            var other = TextFor(line, 1 - CaretSide(line));
            return other.Length == 0 ? null! : other;
        }
    }

    int GitKay.Core.Vim.IVimHost.Side => SelectedItem is DiffLineProjection line ? CaretSide(line) : 0;

    bool GitKay.Core.Vim.IVimHost.HasSelection => HasTextSelection;

    int GitKay.Core.Vim.IVimHost.HalfPageRows => Math.Max(1, ViewportRowCount / 2);

    void GitKay.Core.Vim.IVimHost.SetCaret(int column) {
        if (SelectedLineIndex is var row && row >= 0 && _rows[row] is DiffLineProjection line) PlaceCaret(row, CaretSide(line), column);
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
        if (SelectedLineIndex is not (var row and >= 0) || _rows[row] is not DiffLineProjection line) return;
        var side = CaretSide(line);
        var text = TextFor(line, side);
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

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Delta.Y != 0) {
            CodeFontSize = Math.Clamp(CodeFontSize + Math.Sign(e.Delta.Y), 7, 32);
            e.Handled = true;
            return;
        }

        // Shift+wheel, a tilting wheel, or a sideways touchpad swipe scrolls the code columns.
        var sideways = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? (e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) : e.Delta.X;
        if (sideways != 0) {
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

    protected override void OnKeyDown(KeyEventArgs e) {
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
                    var side = Mode == "side-by-side" ? 1 : 0;
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

        Add("Copy", HasTextSelection, CopySelection);
        Add("Copy line", true, () => CopyText(LineText(line)));
        var header = index;
        while (header >= 0 && _rows[header] is not DiffFileHeaderProjection) header--;
        if (header >= 0 && _rows[header] is DiffFileHeaderProjection file) {
            var path = file.File.Key.NewPath == "/dev/null" ? file.File.Key.OldPath : file.File.Key.NewPath;
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
        if (_selectingText) {
            _selectingText = false;
            e.Pointer.Capture(null);
            // The caret follows the end of a drag, so keyboard selection continues from there.
            if (_textSelection is { } dragged && (uint)dragged.Active.Row < (uint)_rows.Length && _rows[dragged.Active.Row] is DiffLineProjection) {
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
            ToggleFileAnchored(header, pressed.Action == HeaderChevronAction ? ToggleFileCommand : ToggleFileContextCommand);
            e.Handled = true;
            return;
        }
        if (_rows[pressed.Row] is not DiffGapProjection gap) return;
        var cells = GapCells(gap, _tops[pressed.Row]);
        if ((uint)pressed.Action >= (uint)cells.Count) return;
        RequestExpansion(pressed.Row, gap, cells[pressed.Action].Direction);
        e.Handled = true;
    }

    /// <summary>Visible row count, for half-page movement.</summary>
    public int ViewportRowCount => _scrollViewer == null ? 20 : Math.Max(1, (int)(_scrollViewer.Viewport.Height / LineHeight));

    /// <summary>Selects the next or previous hunk (or gap) boundary, like vim's ]c / [c.</summary>
    public void MoveToHunk(int direction) {
        if (_rows.Length == 0) return;
        var current = SelectedItem == null ? (direction > 0 ? -1 : _rows.Length) : Array.IndexOf(_rows, SelectedItem);
        for (var index = current + direction; index >= 0 && index < _rows.Length; index += direction) {
            if (_rows[index] is not (DiffHunkHeaderProjection or DiffGapProjection)) continue;
            // Select the first line of the hunk so the change itself is in view.
            var target = index + 1 < _rows.Length && _rows[index + 1] is DiffLineProjection ? _rows[index + 1] : _rows[index];
            SelectedItem = target;
            ScrollIntoView(target);
            return;
        }
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
