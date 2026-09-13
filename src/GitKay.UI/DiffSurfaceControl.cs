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

/// <summary>Index-addressable diff viewport. It realizes no child controls and draws only visible rows.</summary>
public sealed class DiffSurfaceControl : Control, IOverviewSource
{
    private List<OverviewMark> _overviewMarks = new();
    private bool _overviewDirty = true;

    public IReadOnlyList<OverviewMark> OverviewMarks
    {
        get
        {
            if (_overviewDirty) RebuildOverview();
            return _overviewMarks;
        }
    }

    public ScrollViewer? OverviewScrollViewer => _scrollViewer;
    public event EventHandler? OverviewChanged;

    private void InvalidateOverview()
    {
        _overviewDirty = true;
        OverviewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Added/removed runs and search/find matches, as fractions of the document height.</summary>
    private void RebuildOverview()
    {
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

        void CloseRun(int end)
        {
            if (runStart < 0) return;
            _overviewMarks.Add(new OverviewMark(_tops[runStart] / total, (_tops[end] - _tops[runStart]) / total, runKind));
            runStart = -1;
        }

        for (var i = 0; i < _rows.Length; i++)
        {
            if (_rows[i] is not DiffLineProjection line)
            {
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

    private const double LineHeight = 20;
    private const double HunkHeight = 28;
    private const double GapHeight = 40;
    private const double FileHeight = 48;
    private const double FileCardTop = 10;
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

    public IEnumerable<IDiffRowProjection>? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public IDiffRowProjection? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public string Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
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

    static DiffSurfaceControl()
    {
        ItemsSourceProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.RebuildRows());
        SelectedItemProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.InvalidateVisual());
        ModeProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.InvalidateVisual());
        FindQueryProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => { control.InvalidateVisual(); control.InvalidateOverview(); });
        FindUseRegexProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.InvalidateVisual());
        SearchHighlightQueryProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => { control.InvalidateVisual(); control.InvalidateOverview(); });
        SearchPathQueryProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.InvalidateVisual());
        SearchHighlightUseRegexProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.InvalidateVisual());
    }

    public DiffSurfaceControl()
    {
        Focusable = true;
        ActualThemeVariantChanged += (_, _) =>
        {
            Interlocked.Increment(ref _generation);
            _plainLayouts.Clear();
            _colouredLayouts.Clear();
            _pending.Clear();
            InvalidateVisual();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scrollViewer = this.FindAncestorOfType<ScrollViewer>();
        if (_scrollViewer != null) _scrollViewer.ScrollChanged += OnScrollChanged;
        OverviewChanged?.Invoke(this, EventArgs.Empty);
        RebuildRows();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_scrollViewer != null) _scrollViewer.ScrollChanged -= OnScrollChanged;
        DetachCollection();
        _idle?.Cancel();
        base.OnDetachedFromVisualTree(e);
    }

    private void RebuildRows()
    {
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
        if (_textSelection != null && (previousRows.Length != _rows.Length || !ReferenceEquals(ItemsSource, _lastItemsSource))) _textSelection = null;
        _lastItemsSource = ItemsSource;
        _hoveredGapAction = GapActionHit.None;
        _pressedGapAction = GapActionHit.None;
        TrackInsertedRows();
        ComputeTops();
        if (ItemsSource is INotifyCollectionChanged collection)
        {
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

    private void ApplyPendingScrollOffset()
    {
        if (_pendingScrollOffset is not { } offset || _scrollViewer == null || !_rows.Any(row => row is DiffLineProjection)) return;
        _pendingScrollOffset = null;
        // Background priority runs after the selection's scroll-into-view, so the restored position wins.
        Dispatcher.UIThread.Post(() => SetOffsetWithoutScrolling(offset), DispatcherPriority.Background);
    }

    private int _selectionIndexToRestore = -1;

    private void RestoreSelectionPosition()
    {
        // Lists are replaced by clearing then refilling; wait for the refill.
        if (_rows.Length == 0) return;
        if (SelectedItem != null && Array.IndexOf(_rows, SelectedItem) >= 0)
        {
            _selectionIndexToRestore = -1;
            return;
        }

        if (_selectionIndexToRestore < 0) return;
        var index = Math.Clamp(_selectionIndexToRestore, 0, _rows.Length - 1);
        _selectionIndexToRestore = -1;
        if (SelectedItem != null) SelectedItem = _rows[index];
    }

    private void ComputeTops()
    {
        if (_tops.Length != _rows.Length + 1) _tops = new double[_rows.Length + 1];
        _tops[0] = 0;
        for (var i = 0; i < _rows.Length; i++) _tops[i + 1] = _tops[i] + RowHeight(_rows[i]);
    }

    /// <summary>Marks source rows that did not exist before an expansion so they grow in; other rows stay put.</summary>
    private void TrackInsertedRows()
    {
        var lineKeys = new HashSet<(int?, int?)>();
        foreach (var row in _rows)
            if (row is DiffLineProjection line) lineKeys.Add((line.OldLineNo, line.NewLineNo));

        // A replacement publishes an empty list first; only remember sets that contain source rows.
        if (lineKeys.Count == 0) return;

        if (_expansionAnchor != null && _knownLineKeys.Count > 0)
        {
            _growingRows.Clear();
            foreach (var row in _rows)
                if (row is DiffLineProjection line && !_knownLineKeys.Contains((line.OldLineNo, line.NewLineNo)))
                    _growingRows.Add(row);
            if (_growingRows.Count > 0) StartGrowAnimation();
        }
        else if (_expansionAnchor == null)
        {
            StopGrowAnimation();
        }

        _knownLineKeys = lineKeys;
    }

    private void StartGrowAnimation()
    {
        _growStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _growProgress = 0;
        _growTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => OnGrowTick());
        _growTimer.Start();
    }

    private void StopGrowAnimation()
    {
        _growTimer?.Stop();
        _growProgress = 1;
        _growingRows.Clear();
    }

    private void OnGrowTick()
    {
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_growStartedAt).TotalMilliseconds;
        _growProgress = Math.Clamp(elapsed / GrowDurationMs, 0, 1);
        var finished = _growProgress >= 1;
        if (finished) StopGrowAnimation();
        ComputeTops();
        InvalidateMeasure();
        InvalidateVisual();
        if (_expansionAnchor != null)
        {
            RestoreExpansionAnchor();
            if (finished && !_rows.Any(row => row is DiffGapProjection gap && gap.Gap.Equals(_expansionAnchor?.Gap)))
                _expansionAnchor = null;
        }
    }

    /// <summary>Keeps the boundary line next to an expanded gap at its viewport Y until the expansion is realized.</summary>
    private void RestoreExpansionAnchor()
    {
        if (_expansionAnchor is not { } expansion || _scrollViewer == null) return;
        var anchor = expansion.Anchor;
        var index = FindAnchorRow(anchor);
        if (index < 0)
        {
            // Rows are briefly empty while the list is replaced; a different diff drops the anchor.
            if (_rows.Any(row => row is DiffLineProjection)) _expansionAnchor = null;
            return;
        }

        var realized = !_rows.Any(row => row is DiffGapProjection gap && gap.Gap.Equals(expansion.Gap));
        if (realized && _growingRows.Count == 0) _expansionAnchor = null;

        var row = _rows[index];
        Dispatcher.UIThread.Post(() =>
        {
            if (_scrollViewer == null) return;
            var current = Array.IndexOf(_rows, row);
            if (current < 0) return;
            SetOffsetWithoutScrolling(Math.Max(0, _tops[current] - anchor.ViewportOffset));
        }, DispatcherPriority.Loaded);
    }

    private int FindAnchorRow(ViewportAnchor anchor) =>
        Array.FindIndex(_rows, row => row is DiffLineProjection line
            && line.OldLineNo == anchor.OldLineNo
            && line.NewLineNo == anchor.NewLineNo
            && line.Content == anchor.Content);

    private void BeginExpansion(int gapIndex, DiffGapProjection gap, GitKay.Core.DiffExpansion.ExpandDirection direction)
    {
        if (_scrollViewer == null)
        {
            _expansionAnchor = null;
            return;
        }

        // Upward expansion grows above the following content; downward and complete expansion
        // grow below the preceding content, leaving the scroll offset unchanged.
        var below = NearestLine(gapIndex, +1);
        var above = NearestLine(gapIndex, -1);
        var anchorIndex = direction.IsUp ? (below >= 0 ? below : above) : (above >= 0 ? above : below);
        if (anchorIndex < 0 || _rows[anchorIndex] is not DiffLineProjection line)
        {
            _expansionAnchor = null;
            return;
        }

        _pendingAnchor = null;
        _expansionAnchor = new ExpansionAnchor(
            gap.Gap,
            new ViewportAnchor(line.OldLineNo, line.NewLineNo, line.Content, _tops[anchorIndex] - _scrollViewer.Offset.Y),
            System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private int NearestLine(int index, int step)
    {
        for (var i = index + step; i >= 0 && i < _rows.Length; i += step)
        {
            if (_rows[i] is DiffLineProjection) return i;
            if (_rows[i] is DiffFileHeaderProjection) return -1;
        }
        return -1;
    }

    private void CaptureViewportAnchor()
    {
        if (_pendingAnchor != null || _scrollViewer == null || _rows.Length == 0) return;
        var first = FindRow(_scrollViewer.Offset.Y);
        for (var index = first; index < _rows.Length; index++)
        {
            if (_rows[index] is not DiffLineProjection line) continue;
            _pendingAnchor = new ViewportAnchor(line.OldLineNo, line.NewLineNo, line.Content, _tops[index] - _scrollViewer.Offset.Y);
            return;
        }
    }

    private void RestoreViewportAnchor()
    {
        if (_pendingAnchor is not { } anchor || _scrollViewer == null || _rows.Length == 0) return;
        var index = FindAnchorRow(anchor);
        if (index < 0)
        {
            // Context reloads temporarily leave only file headers. Keep the anchor until
            // source rows arrive; a genuinely different diff clears it after that.
            if (_rows.Any(row => row is DiffLineProjection))
                _pendingAnchor = null;
            return;
        }
        _pendingAnchor = null;
        var row = _rows[index];
        Dispatcher.UIThread.Post(() =>
        {
            if (_scrollViewer == null) return;
            var current = Array.IndexOf(_rows, row);
            if (current >= 0)
                SetOffsetWithoutScrolling(Math.Max(0, _tops[current] - anchor.ViewportOffset));
        }, DispatcherPriority.Loaded);
    }

    private void DetachCollection()
    {
        if (_collection != null) _collection.CollectionChanged -= OnCollectionChanged;
        _collection = null;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildRows();

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 1000 : availableSize.Width, _tops[^1]);

    /// <summary>Anchoring adjusts the offset without the user scrolling; keep full-quality rendering.</summary>
    private void SetOffsetWithoutScrolling(double y)
    {
        if (_scrollViewer == null) return;
        _programmaticScroll = true;
        try { _scrollViewer.Offset = _scrollViewer.Offset.WithY(y); }
        finally { _programmaticScroll = false; }
    }

    private void HighlightGrowingRows()
    {
        foreach (var row in _growingRows)
        {
            if (row is not DiffLineProjection line) continue;
            var foreground = ThemeBrush("GitKayTextBrush", line.Foreground);
            if (Mode == "side-by-side")
            {
                HighlightNow(line.OldContent, foreground);
                HighlightNow(line.NewContent, foreground);
            }
            else if (Mode == "new") HighlightNow(line.NewContent, foreground);
            else if (Mode == "old") HighlightNow(line.OldContent, foreground);
            else HighlightNow(line.Content, foreground);
        }
    }

    /// <summary>Colours a newly inserted line before its first frame so it never flashes plain.</summary>
    private void HighlightNow(string text, IBrush foreground)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxHighlightedLineLength || _colouredLayouts.ContainsKey(text)) return;
        StoreColouredLayout(text, foreground, SyntaxHighlighting.Tokenize(text));
    }

    private void StoreColouredLayout(string text, IBrush foreground, IReadOnlyList<HighlightToken> tokens)
    {
        var layout = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, CodeTypeface, 12, foreground);
        var offset = 0;
        foreach (var token in tokens)
        {
            if (token.Kind != HighlightKind.Plain) layout.SetForegroundBrush(TokenBrush(token.Kind, foreground), offset, token.Text.Length);
            offset += token.Text.Length;
        }
        if (_colouredLayouts.Count >= MaxLayoutCacheEntries)
            _colouredLayouts.Clear();
        _colouredLayouts[text] = layout;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_programmaticScroll)
        {
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

    private async Task EndScrollingAsync(CancellationTokenSource idle)
    {
        try
        {
            await Task.Delay(40, idle.Token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(_idle, idle))
                {
                    _scrolling = false;
                    PrefetchAroundViewport();
                    InvalidateVisual();
                }
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { }
    }

    public override void Render(DrawingContext context)
    {
        var offset = _scrollViewer?.Offset.Y ?? 0;
        var viewport = _scrollViewer?.Viewport.Height ?? Bounds.Height;
        // Hit testing follows what was drawn: paint a transparent backdrop so blank areas (context lines,
        // space past the end of code) still receive clicks.
        context.FillRectangle(Brushes.Transparent, new Rect(0, offset, Bounds.Width, viewport));
        var first = FindRow(offset);
        var last = Math.Min(_rows.Length, FindRow(offset + viewport) + 2);
        for (var i = first; i < last; i++)
        {
            var height = _tops[i + 1] - _tops[i];
            if (height <= 0) continue;
            if (_growingRows.Count > 0 && _growingRows.Contains(_rows[i]))
            {
                using (context.PushClip(new Rect(0, _tops[i], Bounds.Width, height)))
                    DrawRow(context, _rows[i], _tops[i], i);
            }
            else
            {
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
    private void DrawStickyHeader(DrawingContext context, double offset, int first)
    {
        _stickyIndex = -1;
        if (_rows.Length == 0) return;

        var header = -1;
        for (var i = Math.Min(first, _rows.Length - 1); i >= 0; i--)
        {
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
    private int RowAt(Point position, out double rowTop)
    {
        if (_stickyIndex >= 0 && position.Y >= _stickyRowTop + FileCardTop && position.Y < _stickyRowTop + FileHeight)
        {
            rowTop = _stickyRowTop;
            return _stickyIndex;
        }

        var index = FindRow(position.Y);
        rowTop = (uint)index < (uint)_rows.Length ? _tops[index] : 0;
        return index;
    }

    private void DrawRow(DrawingContext context, IDiffRowProjection row, double y, int index)
    {
        _drawingRowIndex = index;
        if (ReferenceEquals(row, SelectedItem) && row is not DiffFileHeaderProjection) context.FillRectangle(ThemeBrush("GitKaySelectionBrush", SelectionBrush), new Rect(0, y, Bounds.Width, RowHeight(row)));
        switch (row)
        {
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

    private Rect FileContextRect(DiffFileHeaderProjection file, double y)
    {
        var pathWidth = Layout(file.DisplayPath, 12, FileBrush, false).Width;
        return new Rect(FileChevronWidth + 8 + pathWidth + 8, y + FileCardTop + 5, 26, FileHeight - FileCardTop - 10);
    }

    private static bool HasContextToggle(DiffFileProjection file) =>
        file.IsLoaded && (file.HasHiddenContext || file.HasRevealedContext);

    /// <summary>GitHub-style file card header: chevron, path, expand-all context toggle, and change stats.</summary>
    private void DrawFileHeader(DrawingContext context, DiffFileHeaderProjection header, double y, int index)
    {
        var file = header.File;
        var card = FileCardRect(y);
        // Soft file headers: a faint card edge and a secondary-coloured path, so code stays the focus.
        var selected = ReferenceEquals(header, SelectedItem);
        using (context.PushOpacity(selected ? 1 : 0.55))
        {
            var border = new Pen(ThemeBrush("GitKayBorderBrush", HunkBrush), 1);
            var fill = selected
                ? ThemeBrush("GitKaySelectionBrush", SelectionBrush)
                : ThemeBrush("GitKaySurfaceBrush", Brushes.Transparent);
            context.DrawRectangle(fill, border, card, 6, 6);
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
        if (file.IsCollapsed)
        {
            context.DrawLine(chevronPen, new Point(cx - 2, centerY - 4), new Point(cx + 2, centerY));
            context.DrawLine(chevronPen, new Point(cx + 2, centerY), new Point(cx - 2, centerY + 4));
        }
        else
        {
            context.DrawLine(chevronPen, new Point(cx - 4, centerY - 2), new Point(cx, centerY + 2));
            context.DrawLine(chevronPen, new Point(cx, centerY + 2), new Point(cx + 4, centerY - 2));
        }

        var path = Layout(file.DisplayPath, 12, text, false);
        context.DrawText(path, new Point(FileChevronWidth + 8, centerY - path.Height / 2));
        var pathUnderline = ThemeBrush("GitKayAccentBrush", SearchMatchFallback);
        ForEachMatch(file.DisplayPath, SearchPathQuery, SearchHighlightUseRegex, (start, length) =>
            DrawDottedUnderline(context, file.DisplayPath, start, length, FileChevronWidth + 8, centerY + path.Height / 2 - 1, pathUnderline));

        if (HasContextToggle(file))
        {
            var toggle = FileContextRect(header, y);
            if (IsHeaderPartActive(index, HeaderContextAction, out var togglePressed))
                context.DrawRectangle(togglePressed ? ThemeBrush("GitKaySelectionBrush", SelectionBrush) : hover, null, toggle, 4, 4);
            DrawContextToggleIcon(context, toggle, file.HasHiddenContext, secondary);
            if (file.IsContextLoading)
                DrawPlain(context, "Loading…", toggle.Right + 6, centerY - 7, 11, ThemeBrush("GitKayMutedTextBrush", HunkBrush));
        }

        DrawDiffStat(context, file, card.Right - 12, centerY);
    }

    private bool IsHeaderPartActive(int index, int action, out bool pressed)
    {
        pressed = _pressedGapAction.Row == index && _pressedGapAction.Action == action;
        return pressed || (_hoveredGapAction.Row == index && _hoveredGapAction.Action == action);
    }

    /// <summary>Arrows pointing away from (expand) or toward (collapse) a dotted centre line.</summary>
    private static void DrawContextToggleIcon(DrawingContext context, Rect bounds, bool expand, IBrush brush)
    {
        var pen = new Pen(brush, 1.4, lineCap: PenLineCap.Round);
        var cx = bounds.X + bounds.Width / 2;
        var cy = bounds.Y + bounds.Height / 2;
        for (var x = cx - 6; x <= cx + 6; x += 3)
            context.FillRectangle(brush, new Rect(x - 0.6, cy - 0.6, 1.3, 1.3));

        void Arrow(double tipY, double tailY)
        {
            var head = tipY < tailY ? 3 : -3;
            context.DrawLine(pen, new Point(cx, tailY), new Point(cx, tipY));
            context.DrawLine(pen, new Point(cx - 3, tipY + head), new Point(cx, tipY));
            context.DrawLine(pen, new Point(cx + 3, tipY + head), new Point(cx, tipY));
        }

        if (expand)
        {
            Arrow(cy - 8, cy - 3);
            Arrow(cy + 8, cy + 3);
        }
        else
        {
            Arrow(cy - 3, cy - 8);
            Arrow(cy + 3, cy + 8);
        }
    }

    /// <summary>"+N −M" followed by five blocks sized to the change ratio, right-aligned at <paramref name="right"/>.</summary>
    private void DrawDiffStat(DrawingContext context, DiffFileProjection file, double right, double centerY)
    {
        if (!file.IsLoaded) return;
        const double block = 8, spacing = 2;
        var added = ThemeBrush("GitKayAddedAccentBrush", Brushes.Green);
        var removed = ThemeBrush("GitKayRemovedAccentBrush", Brushes.Red);
        var neutral = ThemeBrush("GitKayDiffStatNeutralBrush", HunkBrush);

        var (green, red) = DiffStatBar.Blocks(file.AddedLines, file.RemovedLines);

        var x = right - (5 * block + 4 * spacing);
        for (var i = 0; i < 5; i++)
        {
            var brush = i < green ? added : i < green + red ? removed : neutral;
            context.DrawRectangle(brush, null, new Rect(x + i * (block + spacing), centerY - block / 2, block, block), 2, 2);
        }

        x -= 8;
        if (file.RemovedLines > 0)
        {
            var removedText = Layout($"−{file.RemovedLines}", 12, removed, false);
            x -= removedText.Width;
            context.DrawText(removedText, new Point(x, centerY - removedText.Height / 2));
            x -= 6;
        }
        if (file.AddedLines > 0 || file.RemovedLines == 0)
        {
            var addedText = Layout($"+{file.AddedLines}", 12, added, false);
            x -= addedText.Width;
            context.DrawText(addedText, new Point(x, centerY - addedText.Height / 2));
        }
    }

    private void DrawGap(DrawingContext context, DiffGapProjection gap, double y, int index)
    {
        var accent = ThemeBrush("GitKayAccentBrush", FileBrush);
        var muted = ThemeBrush("GitKayMutedTextBrush", HunkBrush);
        context.FillRectangle(ThemeBrush("GitKayHunkBrush", Brushes.Transparent), new Rect(0, y, Bounds.Width, GapHeight));
        context.FillRectangle(ThemeBrush("GitKayGutterBrush", Brushes.Transparent), new Rect(0, y, GapGutterWidth, GapHeight));

        var cells = GapCells(gap, y);
        for (var i = 0; i < cells.Count; i++)
        {
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
    private void DrawExpandIcon(DrawingContext context, GapCell cell, IBrush brush)
    {
        var centerX = cell.Bounds.X + cell.Bounds.Width / 2;
        var centerY = cell.Bounds.Y + cell.Bounds.Height / 2;
        var pen = new Pen(brush, 1.5, lineCap: PenLineCap.Round);

        void Dots(double dotY)
        {
            for (var x = centerX - 6; x <= centerX + 6; x += 3)
                context.FillRectangle(brush, new Rect(x - 0.5, dotY, 1.5, 1.5));
        }

        void Arrow(double tipY, double tailY)
        {
            var head = tipY < tailY ? 3.5 : -3.5;
            context.DrawLine(pen, new Point(centerX, tailY), new Point(centerX, tipY));
            context.DrawLine(pen, new Point(centerX - 3.5, tipY + head), new Point(centerX, tipY));
            context.DrawLine(pen, new Point(centerX + 3.5, tipY + head), new Point(centerX, tipY));
        }

        if (cell.Direction.IsDown)
        {
            Dots(centerY - 6);
            Arrow(centerY + 5, centerY - 2);
        }
        else if (cell.Direction.IsUp)
        {
            Arrow(centerY - 5, centerY + 2);
            Dots(centerY + 5);
        }
        else
        {
            Arrow(centerY - 7, centerY - 1);
            Arrow(centerY + 7, centerY + 1);
        }
    }

    /// <summary>Incremental expanders stack in the gutter; a gap of ten lines or fewer offers one "all" expander.</summary>
    private static List<GapCell> GapCells(DiffGapProjection gap, double y)
    {
        var directions = gap.Directions.Where(direction => !direction.IsAll).ToList();
        if (directions.Count == 0) directions = gap.Directions.ToList();
        var cells = new List<GapCell>(directions.Count);
        var height = GapHeight / Math.Max(1, directions.Count);
        for (var i = 0; i < directions.Count; i++)
            cells.Add(new GapCell(directions[i], new Rect(0, y + i * height, GapGutterWidth, height)));
        return cells;
    }

    private GapActionHit GapActionAt(Point position)
    {
        var index = RowAt(position, out var headerTop);
        if ((uint)index < (uint)_rows.Length && _rows[index] is DiffFileHeaderProjection header)
        {
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

    private void ShowGapMenu(int index, DiffGapProjection gap)
    {
        var menu = new ContextMenu();
        void Add(string header, GitKay.Core.DiffExpansion.ExpandDirection direction)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => RequestExpansion(index, gap, direction);
            menu.Items.Add(item);
        }

        Add(gap.HiddenLineCount is { } count ? $"Load all {count} lines" : "Load all", GitKay.Core.DiffExpansion.ExpandDirection.All);
        foreach (var direction in gap.Directions.Where(direction => !direction.IsAll))
            Add(gap.ActionLabel(direction), direction);
        menu.Open(this);
    }

    private void RequestExpansion(int index, DiffGapProjection gap, GitKay.Core.DiffExpansion.ExpandDirection direction)
    {
        // Rows may have been rebuilt while a menu was open; act only on the same gap.
        if ((uint)index >= (uint)_rows.Length || !ReferenceEquals(_rows[index], gap)) return;
        var request = new DiffGapExpansionRequest(gap.Gap, direction);
        if (ExpandGapCommand?.CanExecute(request) != true) return;
        BeginExpansion(index, gap, direction);
        ExpandGapCommand.Execute(request);
    }

    private void DrawLine(DrawingContext context, DiffLineProjection line, double y)
    {
        var rowBackground = line.IsAdded
            ? ThemeBrush("GitKayAddedBrush", line.RowBackground)
            : line.IsRemoved
                ? ThemeBrush("GitKayRemovedBrush", line.RowBackground)
                : Brushes.Transparent;
        if (rowBackground != Brushes.Transparent)
            context.FillRectangle(rowBackground, new Rect(0, y, Bounds.Width, LineHeight - 1));

        switch (Mode)
        {
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

    private void DrawUnifiedLine(DrawingContext context, DiffLineProjection line, double y)
    {
        var gutter = line.IsAdded ? ThemeBrush("GitKayAddedGutterBrush", Brushes.Transparent)
            : line.IsRemoved ? ThemeBrush("GitKayRemovedGutterBrush", Brushes.Transparent)
            : ThemeBrush("GitKayGutterBrush", Brushes.Transparent);
        context.FillRectangle(gutter, new Rect(0, y, 56, LineHeight - 1));
        var lineNumber = line.IsRemoved ? line.OldLineNoText : line.NewLineNoText;
        DrawLineNumber(context, lineNumber, 4, y, ThemeBrush("GitKayMutedTextBrush", line.LineNumberForeground));
        DrawPlain(context, line.Prefix, 44, y + 2, 12, (line.IsAdded ? ThemeBrush("GitKayAddedAccentBrush", line.PrefixForeground) : line.IsRemoved ? ThemeBrush("GitKayRemovedAccentBrush", line.PrefixForeground) : ThemeBrush("GitKayMutedTextBrush", line.PrefixForeground)));
        using (context.PushClip(new Rect(60, y, Math.Max(0, Bounds.Width - 60), LineHeight)))
        {
            DrawFindMatches(context, line.Content, 60, y);
            DrawCode(context, line.Content, 60, y + 2, ThemeBrush("GitKayTextBrush", line.Foreground));
        }
    }

    private void DrawSingleSideLine(
        DrawingContext context,
        string lineNumber,
        string content,
        double y,
        DiffLineProjection line)
    {
        var gutter = line.IsAdded ? ThemeBrush("GitKayAddedGutterBrush", Brushes.Transparent)
            : line.IsRemoved ? ThemeBrush("GitKayRemovedGutterBrush", Brushes.Transparent)
            : ThemeBrush("GitKayGutterBrush", Brushes.Transparent);
        context.FillRectangle(gutter, new Rect(0, y, 48, LineHeight - 1));
        DrawLineNumber(context, lineNumber, 8, y, ThemeBrush("GitKayMutedTextBrush", line.LineNumberForeground));
        using (context.PushClip(new Rect(52, y, Math.Max(0, Bounds.Width - 52), LineHeight)))
        {
            DrawFindMatches(context, content, 52, y);
            DrawCode(context, content, 52, y + 2, ThemeBrush("GitKayTextBrush", line.Foreground));
        }
    }

    private void DrawSideBySideLine(DrawingContext context, DiffLineProjection line, double y)
    {
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
        context.FillRectangle(line.IsRemoved || isPairedChange ? ThemeBrush("GitKayRemovedGutterBrush", gutter) : line.IsAdded ? Brushes.Transparent : gutter, new Rect(0, y, 48, LineHeight - 1));
        context.FillRectangle(line.IsAdded || isPairedChange ? ThemeBrush("GitKayAddedGutterBrush", gutter) : line.IsRemoved ? Brushes.Transparent : gutter, new Rect(middle + 1, y, 48, LineHeight - 1));
        context.FillRectangle(border, new Rect(middle, y, 1, LineHeight));

        DrawIntralineHighlights(context, line.OldContent, line.NewContent, 56, middle + 57, y);

        DrawLineNumber(context, line.OldLineNoText, 8, y, ThemeBrush("GitKayMutedTextBrush", line.LineNumberForeground));
        using (context.PushClip(new Rect(56, y, Math.Max(0, middle - 64), LineHeight)))
        {
            DrawFindMatches(context, line.OldContent, 56, y);
            DrawCode(context, line.OldContent, 56, y + 2, ThemeBrush("GitKayTextBrush", line.Foreground));
        }

        DrawLineNumber(context, line.NewLineNoText, middle + 9, y, ThemeBrush("GitKayMutedTextBrush", line.LineNumberForeground));
        using (context.PushClip(new Rect(middle + 57, y, Math.Max(0, Bounds.Width - middle - 57), LineHeight)))
        {
            DrawFindMatches(context, line.NewContent, middle + 57, y);
            DrawCode(context, line.NewContent, middle + 57, y + 2, ThemeBrush("GitKayTextBrush", line.Foreground));
        }
    }

    private void DrawIntralineHighlights(DrawingContext context, string oldText, string newText, double oldX, double newX, double y)
    {
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

        DrawChangedSpan(context, oldText, prefixLength, oldText.Length - prefixLength - suffixLength, oldX, y,
            ThemeBrush("GitKayRemovedStrongBrush", ThemeBrush("GitKayRemovedBrush", Brushes.Transparent)));
        DrawChangedSpan(context, newText, prefixLength, newText.Length - prefixLength - suffixLength, newX, y,
            ThemeBrush("GitKayAddedStrongBrush", ThemeBrush("GitKayAddedBrush", Brushes.Transparent)));
    }

    private static readonly IBrush FindMatchFallback = new SolidColorBrush(Color.FromArgb(110, 187, 128, 9)).ToImmutable();

    private static readonly IBrush SearchMatchFallback = new SolidColorBrush(Color.FromRgb(88, 166, 255)).ToImmutable();

    /// <summary>
    /// Marks the commit search's diff term with a dotted underline, and find-in-diff text with a solid highlight.
    /// </summary>
    private void DrawFindMatches(DrawingContext context, string text, double x, double y)
    {
        DrawTextSelection(context, text, x, y);
        if (string.IsNullOrEmpty(text)) return;
        var underline = ThemeBrush("GitKayAccentBrush", SearchMatchFallback);
        ForEachMatch(text, SearchHighlightQuery, SearchHighlightUseRegex, (start, length) => DrawDottedUnderline(context, text, start, length, x, y + LineHeight - 3, underline));
        var highlight = ThemeBrush("GitKayFindMatchBrush", FindMatchFallback);
        ForEachMatch(text, FindQuery, FindUseRegex, (start, length) => DrawChangedSpan(context, text, start, length, x, y, highlight));
    }

    private void ForEachMatch(string text, string? rawQuery, bool useRegex, Action<int, int> onMatch)
    {
        var query = rawQuery?.Trim();
        if (string.IsNullOrEmpty(query)) return;
        if (useRegex)
        {
            if (TryRegex(query) is not { } regex) return;
            foreach (System.Text.RegularExpressions.Match match in regex.Matches(text))
                if (match.Length > 0) onMatch(match.Index, match.Length);
            return;
        }

        for (var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); index >= 0;
             index = text.IndexOf(query, index + query.Length, StringComparison.OrdinalIgnoreCase))
            onMatch(index, query.Length);
    }

    private void DrawDottedUnderline(DrawingContext context, string text, int start, int length, double x, double baseline, IBrush brush)
    {
        var left = x + (start == 0 ? 0 : Layout(text[..start], 12, Brushes.Transparent, false).Width);
        var width = Layout(text.Substring(start, length), 12, Brushes.Transparent, false).Width;
        for (var dot = left; dot < left + width; dot += 3)
            context.FillRectangle(brush, new Rect(dot, baseline, 1.5, 1.5));
    }

    private readonly Dictionary<string, System.Text.RegularExpressions.Regex?> _regexCache = new(StringComparer.Ordinal);

    private System.Text.RegularExpressions.Regex? TryRegex(string pattern)
    {
        if (_regexCache.TryGetValue(pattern, out var cached)) return cached;
        if (_regexCache.Count > 16) _regexCache.Clear();
        System.Text.RegularExpressions.Regex? regex;
        try
        {
            regex = new System.Text.RegularExpressions.Regex(pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException)
        {
            regex = null;
        }

        _regexCache[pattern] = regex;
        return regex;
    }

    private void DrawChangedSpan(DrawingContext context, string text, int start, int length, double x, double y, IBrush brush)
    {
        if (length <= 0) return;
        var prefixWidth = start == 0 ? 0 : Layout(text[..start], 12, Brushes.Transparent, false).Width;
        var changedWidth = Layout(text.Substring(start, length), 12, Brushes.Transparent, false).Width;
        context.FillRectangle(brush, new Rect(x + prefixWidth, y, changedWidth, LineHeight - 1));
    }

    private void DrawLineNumber(DrawingContext context, string text, double x, double y, IBrush foreground)
    {
        var layout = Layout(text, 12, foreground, false);
        context.DrawText(layout, new Point(x + 34 - layout.Width, y + 2));
    }

    private void DrawCode(DrawingContext context, string text, double x, double y, IBrush foreground)
    {
        var plain = Layout(text, 12, foreground, false);
        if (_colouredLayouts.TryGetValue(text, out var coloured))
        {
            context.DrawText(coloured, new Point(x, y));
            return;
        }
        if (_scrolling || text.Length > MaxHighlightedLineLength)
        {
            context.DrawText(plain, new Point(x, y));
            return;
        }
        ScheduleHighlight(text, foreground);
        context.DrawText(plain, new Point(x, y));
    }

    private void PrefetchAroundViewport()
    {
        if (_scrollViewer == null || _rows.Length == 0) return;
        var viewport = Math.Max(1, _scrollViewer.Viewport.Height);
        var start = FindRow(Math.Max(0, _scrollViewer.Offset.Y - viewport * 2));
        var end = Math.Min(_rows.Length, FindRow(_scrollViewer.Offset.Y + viewport * 3) + 1);

        for (var index = start; index < end; index++)
        {
            if (_rows[index] is not DiffLineProjection line) continue;
            if (Mode == "side-by-side")
            {
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

    private void ScheduleHighlight(string text, IBrush foreground)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxHighlightedLineLength || _colouredLayouts.ContainsKey(text) || !_pending.Add(text)) return;
        var generation = _generation;
        _ = Task.Run(async () =>
        {
            try
            {
                if (generation != Volatile.Read(ref _generation))
                {
                    Dispatcher.UIThread.Post(() => _pending.Remove(text), DispatcherPriority.Background);
                    return;
                }

                await HighlightWorkers.WaitAsync();
                if (generation != Volatile.Read(ref _generation))
                {
                    HighlightWorkers.Release();
                    Dispatcher.UIThread.Post(() => _pending.Remove(text), DispatcherPriority.Background);
                    return;
                }

                IReadOnlyList<HighlightToken> tokens;
                try { tokens = SyntaxHighlighting.Tokenize(text); }
                finally { HighlightWorkers.Release(); }
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _pending.Remove(text);
                    if (generation != _generation || _scrolling) return;
                    if (!_colouredLayouts.ContainsKey(text)) StoreColouredLayout(text, foreground, tokens);
                    InvalidateVisual();
                }, DispatcherPriority.Background);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.WriteLine($"[diff-highlight] {exception}");
                Dispatcher.UIThread.Post(() => _pending.Remove(text), DispatcherPriority.Background);
            }
        });
    }

    private FormattedText Layout(string text, double size, IBrush brush, bool coloured)
    {
        var key = $"{size}:{text}";
        var cache = coloured ? _colouredLayouts : _plainLayouts;
        if (!cache.TryGetValue(key, out var layout))
        {
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

    private IBrush TokenBrush(HighlightKind kind, IBrush fallback) => kind switch
    {
        HighlightKind.Keyword => ThemeBrush("GitKaySyntaxKeywordBrush", SyntaxHighlighting.GetKeywordBrush()),
        HighlightKind.String => ThemeBrush("GitKaySyntaxStringBrush", SyntaxHighlighting.GetStringBrush()),
        HighlightKind.Number => ThemeBrush("GitKaySyntaxNumberBrush", SyntaxHighlighting.GetNumberBrush()),
        HighlightKind.Comment => ThemeBrush("GitKaySyntaxCommentBrush", SyntaxHighlighting.GetCommentBrush()),
        HighlightKind.TypeName => ThemeBrush("GitKaySyntaxTypeBrush", SyntaxHighlighting.GetTypeBrush()),
        _ => fallback
    };

    private int FindRow(double y)
    {
        var index = Array.BinarySearch(_tops, y);
        if (index < 0) index = ~index - 1;
        return Math.Clamp(index, 0, Math.Max(0, _rows.Length - 1));
    }

    private double RowHeight(IDiffRowProjection row)
    {
        var height = row switch
        {
            DiffFileHeaderProjection => FileHeight,
            DiffHunkHeaderProjection => HunkHeight,
            DiffGapProjection => GapHeight,
            _ => LineHeight
        };
        return _growProgress < 1 && _growingRows.Contains(row) ? height * EaseOut(_growProgress) : height;
    }

    private static double EaseOut(double t) => 1 - (1 - t) * (1 - t);

    private readonly record struct ViewportAnchor(int? OldLineNo, int? NewLineNo, string Content, double ViewportOffset);
    private sealed record ExpansionAnchor(GitKay.Core.DiffExpansion.DiffGap Gap, ViewportAnchor Anchor, long StartedAt);
    private readonly record struct GapCell(GitKay.Core.DiffExpansion.ExpandDirection Direction, Rect Bounds);
    private readonly record struct GapActionHit(int Row, int Action)
    {
        public static readonly GapActionHit None = new(-1, -1);
        public bool IsNone => Row < 0;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_selectingText)
        {
            UpdateTextSelection(e.GetPosition(this));
            return;
        }

        var hit = GapActionAt(e.GetPosition(this));
        if (hit == _hoveredGapAction) return;
        _hoveredGapAction = hit;
        Cursor = hit.IsNone ? Cursor.Default : new Cursor(StandardCursorType.Hand);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoveredGapAction.IsNone && _pressedGapAction.IsNone) return;
        _hoveredGapAction = GapActionHit.None;
        _pressedGapAction = GapActionHit.None;
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var position = e.GetPosition(this);
        var rowIndex = FindRow(position.Y);
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            if ((uint)rowIndex < (uint)_rows.Length && _rows[rowIndex] is DiffGapProjection menuGap)
                ShowGapMenu(rowIndex, menuGap);
            else if ((uint)rowIndex < (uint)_rows.Length && _rows[rowIndex] is DiffLineProjection menuLine)
                ShowLineMenu(rowIndex, menuLine);
            e.Handled = true;
            return;
        }

        var hit = GapActionAt(position);
        if (!hit.IsNone)
        {
            _pressedGapAction = hit;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
        else
        {
            var index = RowAt(position, out _);
            if ((uint)index < (uint)_rows.Length && _rows[index] is not (DiffHunkHeaderProjection or DiffGapProjection))
                SelectedItem = _rows[index];
            if ((uint)index < (uint)_rows.Length && _rows[index] is DiffFileHeaderProjection clickedHeader && e.ClickCount == 2
                && ToggleFileCommand?.CanExecute(clickedHeader.File) == true)
                ToggleFileCommand.Execute(clickedHeader.File);
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
    internal void SelectText(int startRow, int startChar, int endRow, int endChar, int side = 0)
    {
        _textSelection = new TextSelection(new TextPosition(startRow, startChar), new TextPosition(endRow, endChar), side);
        InvalidateVisual();
    }

    internal int RowIndexAt(double documentY) => FindRow(documentY);

    internal string RowKindAt(double documentY)
    {
        var index = FindRow(documentY);
        return (uint)index < (uint)_rows.Length ? _rows[index] switch { DiffLineProjection => "line", DiffFileHeaderProjection => "header", DiffGapProjection => "gap", _ => "hunk" } : "none";
    }

    internal string HitDebug(Point documentPoint) => $"row={FindRow(documentPoint.Y)} sticky={_stickyIndex} gap={GapActionAt(documentPoint)}";

    internal int FirstLineRowIndex(int skip = 0) =>
        Enumerable.Range(0, _rows.Length).Where(i => _rows[i] is DiffLineProjection).Skip(skip).DefaultIfEmpty(-1).First();

    public bool HasTextSelection => _textSelection is { } selection && selection.Anchor != selection.Active;

    private static bool SameLineAt(IDiffRowProjection[] rows, int index) =>
        (uint)index < (uint)rows.Length && rows[index] is DiffLineProjection;

    /// <summary>Which content column a point is in: 0 for the only / old side, 1 for the new side in side-by-side.</summary>
    private int SideAt(double x) => Mode == "side-by-side" && x >= Bounds.Width / 2 ? 1 : 0;

    private double ContentOrigin(int side) => Mode switch
    {
        "side-by-side" => side == 0 ? 56 : Bounds.Width / 2 + 57,
        "new" or "old" => 52,
        _ => 60,
    };

    private string TextFor(DiffLineProjection line, int side) => Mode switch
    {
        "side-by-side" => side == 0 ? line.OldContent : line.NewContent,
        "new" => line.NewContent,
        "old" => line.OldContent,
        _ => line.Content,
    };

    private int CharIndexAt(string text, double originX, double x)
    {
        var target = x - originX;
        if (target <= 0 || text.Length == 0) return 0;
        int low = 0, high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (Layout(text[..mid], 12, Brushes.Transparent, false).Width <= target) low = mid; else high = mid - 1;
        }

        if (low < text.Length)
        {
            var before = Layout(text[..low], 12, Brushes.Transparent, false).Width;
            var after = Layout(text[..(low + 1)], 12, Brushes.Transparent, false).Width;
            if (target - before > after - target) low++;
        }

        return low;
    }

    private TextPosition PositionAt(Point point, int side)
    {
        var index = Math.Clamp(FindRow(point.Y), 0, Math.Max(0, _rows.Length - 1));
        // Header, hunk and gap rows aren't selectable text: snap to the nearest line in the drag direction.
        if (_rows[index] is not DiffLineProjection)
        {
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

    private void BeginTextSelection(int index, Point point, int clickCount, bool extend, IPointer pointer)
    {
        var side = extend && _textSelection != null ? _textSelection.Side : SideAt(point.X);
        var position = PositionAt(point, side);
        var text = TextFor((DiffLineProjection)_rows[index], side);

        if (clickCount >= 3)
        {
            _textSelection = new TextSelection(new TextPosition(index, 0), new TextPosition(index, text.Length), side);
        }
        else if (clickCount == 2)
        {
            static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
            var start = Math.Min(position.Char, text.Length);
            var end = start;
            while (start > 0 && IsWord(text[start - 1])) start--;
            while (end < text.Length && IsWord(text[end])) end++;
            _textSelection = new TextSelection(new TextPosition(index, start), new TextPosition(index, end), side);
        }
        else if (extend && _textSelection != null)
        {
            _textSelection = _textSelection with { Active = position };
        }
        else
        {
            _textSelection = new TextSelection(position, position, side);
            _selectingText = true;
            pointer.Capture(this);
        }

        InvalidateVisual();
    }

    private void UpdateTextSelection(Point point)
    {
        if (_textSelection == null) return;
        _textSelection = _textSelection with { Active = PositionAt(point, _textSelection.Side) };

        // Scroll while dragging past the top or bottom edge.
        if (_scrollViewer != null)
        {
            var top = _scrollViewer.Offset.Y;
            var bottom = top + _scrollViewer.Viewport.Height;
            if (point.Y < top) _scrollViewer.Offset = _scrollViewer.Offset.WithY(Math.Max(0, point.Y));
            else if (point.Y > bottom) _scrollViewer.Offset = _scrollViewer.Offset.WithY(point.Y - _scrollViewer.Viewport.Height);
        }

        InvalidateVisual();
    }

    private (TextPosition Start, TextPosition End)? OrderedSelection()
    {
        if (_textSelection is not { } selection || selection.Anchor == selection.Active) return null;
        var (a, b) = (selection.Anchor, selection.Active);
        return a.Row < b.Row || (a.Row == b.Row && a.Char <= b.Char) ? (a, b) : (b, a);
    }

    private static readonly IBrush TextSelectionFallback = new SolidColorBrush(Color.FromArgb(110, 56, 139, 253)).ToImmutable();

    private void DrawTextSelection(DrawingContext context, string text, double x, double y)
    {
        if (OrderedSelection() is not var (start, end) || _textSelection == null) return;
        var row = _drawingRowIndex;
        if (row < start.Row || row > end.Row) return;
        var side = Mode == "side-by-side" && x >= Bounds.Width / 2 ? 1 : 0;
        if (side != _textSelection.Side) return;

        var from = row == start.Row ? Math.Min(start.Char, text.Length) : 0;
        var to = row == end.Row ? Math.Min(end.Char, text.Length) : text.Length;
        var left = x + (from == 0 ? 0 : Layout(text[..from], 12, Brushes.Transparent, false).Width);
        var right = x + (to == 0 ? 0 : Layout(text[..to], 12, Brushes.Transparent, false).Width);
        // Lines continuing past this row show a little of the line break, like an editor.
        if (row < end.Row) right += 6;
        if (right > left)
            context.FillRectangle(ThemeBrush("GitKayTextSelectionBrush", TextSelectionFallback), new Rect(left, y, right - left, LineHeight - 1));
    }

    /// <summary>The selected code (no line numbers or +/- markers); the focused line when nothing is selected.</summary>
    public string? GetCopyText()
    {
        if (OrderedSelection() is var (start, end) && _textSelection != null)
        {
            var side = _textSelection.Side;
            var lines = new List<string>();
            for (var row = start.Row; row <= end.Row; row++)
            {
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

    public async void CopySelection()
    {
        if (GetCopyText() is { } text && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    private async void CopyText(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control && SelectedItem != null)
        {
            // Select all code in the current file.
            var index = Array.IndexOf(_rows, SelectedItem);
            if (index >= 0)
            {
                var first = index;
                while (first > 0 && _rows[first - 1] is not DiffFileHeaderProjection) first--;
                var last = index;
                while (last + 1 < _rows.Length && _rows[last + 1] is not DiffFileHeaderProjection) last++;
                while (first <= last && _rows[first] is not DiffLineProjection) first++;
                while (last >= first && _rows[last] is not DiffLineProjection) last--;
                if (first <= last && _rows[last] is DiffLineProjection lastLine)
                {
                    var side = Mode == "side-by-side" ? 1 : 0;
                    _textSelection = new TextSelection(new TextPosition(first, 0), new TextPosition(last, TextFor(lastLine, side).Length), side);
                    InvalidateVisual();
                }
            }

            e.Handled = true;
            return;
        }

        // Enter / Space act on the focused row: collapse or expand a file, or reveal a gap's hidden lines.
        if (e.Key is Key.Enter or Key.Space && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            var index = SelectedItem == null ? -1 : Array.IndexOf(_rows, SelectedItem);
            if (index >= 0 && _rows[index] is DiffFileHeaderProjection fileHeader)
            {
                if (e.KeyModifiers == KeyModifiers.Shift)
                {
                    if (ToggleFileContextCommand?.CanExecute(fileHeader.File) == true) ToggleFileContextCommand.Execute(fileHeader.File);
                }
                else if (ToggleFileCommand?.CanExecute(fileHeader.File) == true)
                {
                    ToggleFileCommand.Execute(fileHeader.File);
                }

                e.Handled = true;
                return;
            }

            if (index >= 0 && _rows[index] is DiffGapProjection selectedGap)
            {
                RequestExpansion(index, selectedGap, GitKay.Core.DiffExpansion.ExpandDirection.All);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Escape && _textSelection != null)
        {
            _textSelection = null;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void ShowLineMenu(int index, DiffLineProjection line)
    {
        var menu = new ContextMenu();
        void Add(string header, bool enabled, Action action)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("Copy", HasTextSelection, CopySelection);
        Add("Copy line", true, () => CopyText(LineText(line)));
        var header = index;
        while (header >= 0 && _rows[header] is not DiffFileHeaderProjection) header--;
        if (header >= 0 && _rows[header] is DiffFileHeaderProjection file)
        {
            var path = file.File.Key.NewPath == "/dev/null" ? file.File.Key.OldPath : file.File.Key.NewPath;
            Add("Copy file path", true, () => CopyText(path));
        }

        menu.Open(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_selectingText)
        {
            _selectingText = false;
            e.Pointer.Capture(null);
            return;
        }

        if (_pressedGapAction.IsNone) return;
        var pressed = _pressedGapAction;
        _pressedGapAction = GapActionHit.None;
        e.Pointer.Capture(null);
        InvalidateVisual();

        // Only a release inside the same bounded control activates it.
        if (GapActionAt(e.GetPosition(this)) != pressed) return;
        if (_rows[pressed.Row] is DiffFileHeaderProjection header)
        {
            var command = pressed.Action == HeaderChevronAction ? ToggleFileCommand : ToggleFileContextCommand;
            if (command?.CanExecute(header.File) == true) command.Execute(header.File);
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
    public void MoveToHunk(int direction)
    {
        if (_rows.Length == 0) return;
        var current = SelectedItem == null ? (direction > 0 ? -1 : _rows.Length) : Array.IndexOf(_rows, SelectedItem);
        for (var index = current + direction; index >= 0 && index < _rows.Length; index += direction)
        {
            if (_rows[index] is not (DiffHunkHeaderProjection or DiffGapProjection)) continue;
            // Select the first line of the hunk so the change itself is in view.
            var target = index + 1 < _rows.Length && _rows[index + 1] is DiffLineProjection ? _rows[index + 1] : _rows[index];
            SelectedItem = target;
            ScrollIntoView(target);
            return;
        }
    }

    public void MoveSelection(int delta)
    {
        if (_rows.Length == 0) return;
        var current = SelectedItem == null ? -1 : Array.IndexOf(_rows, SelectedItem);
        // Nothing selected (or the selection is gone): start from what's on screen, not the top of the diff.
        if (current < 0 && _scrollViewer != null) current = FindRow(_scrollViewer.Offset.Y) - (delta > 0 ? 1 : 0);
        var next = Math.Clamp(current + delta, 0, _rows.Length - 1);
        SelectedItem = _rows[next];
        ScrollIntoView(SelectedItem);
    }

    /// <summary>Scrolls so the item sits at the top of the viewport (jumping to a file puts its header first).</summary>
    public void ScrollToTop(IDiffRowProjection item)
    {
        if (_scrollViewer == null) return;
        var index = Array.IndexOf(_rows, item);
        if (index < 0) return;
        _scrollViewer.Offset = _scrollViewer.Offset.WithY(Math.Max(0, Math.Min(_tops[index], _tops[^1] - _scrollViewer.Viewport.Height)));
    }

    public void ScrollIntoView(IDiffRowProjection item)
    {
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
