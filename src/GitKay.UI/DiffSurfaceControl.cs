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
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GitKay.UI;

/// <summary>Index-addressable diff viewport. It realizes no child controls and draws only visible rows.</summary>
public sealed class DiffSurfaceControl : Control
{
    private const double LineHeight = 19;
    private const double HunkHeight = 28;
    private const double GapHeight = 32;
    private const double FileHeight = 40;
    private const int MaxHighlightedLineLength = 240;
    private const int MaxLayoutCacheEntries = 2048;
    private static readonly Typeface CodeTypeface = new("Cascadia Code,Consolas,Monospace");
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
    public static readonly StyledProperty<ICommand?> ExpandBlockCommandProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, ICommand?>(nameof(ExpandBlockCommand));
    public static readonly StyledProperty<ICommand?> ExpandGapCommandProperty =
        AvaloniaProperty.Register<DiffSurfaceControl, ICommand?>(nameof(ExpandGapCommand));

    private IDiffRowProjection[] _rows = Array.Empty<IDiffRowProjection>();
    private double[] _tops = [0];
    private INotifyCollectionChanged? _collection;
    private ScrollViewer? _scrollViewer;
    private CancellationTokenSource? _idle;
    private ViewportAnchor? _pendingAnchor;
    private int _generation;
    private int _hoveredGapAction;
    private bool _scrolling;
    private readonly Dictionary<string, FormattedText> _plainLayouts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FormattedText> _colouredLayouts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

    public IEnumerable<IDiffRowProjection>? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public IDiffRowProjection? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public string Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public ICommand? ExpandBlockCommand { get => GetValue(ExpandBlockCommandProperty); set => SetValue(ExpandBlockCommandProperty, value); }
    public ICommand? ExpandGapCommand { get => GetValue(ExpandGapCommandProperty); set => SetValue(ExpandGapCommandProperty, value); }

    static DiffSurfaceControl()
    {
        ItemsSourceProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.RebuildRows());
        SelectedItemProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.InvalidateVisual());
        ModeProperty.Changed.AddClassHandler<DiffSurfaceControl>((control, _) => control.InvalidateVisual());
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
        CaptureViewportAnchor();
        DetachCollection();
        _rows = ItemsSource?.ToArray() ?? Array.Empty<IDiffRowProjection>();
        _tops = new double[_rows.Length + 1];
        for (var i = 0; i < _rows.Length; i++) _tops[i + 1] = _tops[i] + RowHeight(_rows[i]);
        if (ItemsSource is INotifyCollectionChanged collection)
        {
            _collection = collection;
            _collection.CollectionChanged += OnCollectionChanged;
        }
        Interlocked.Increment(ref _generation);
        _pending.Clear();
        _plainLayouts.Clear();
        _colouredLayouts.Clear();
        InvalidateMeasure();
        InvalidateVisual();
        RestoreViewportAnchor();
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
        var index = Array.FindIndex(_rows, row => row is DiffLineProjection line
            && line.OldLineNo == anchor.OldLineNo
            && line.NewLineNo == anchor.NewLineNo
            && line.Content == anchor.Content);
        if (index < 0)
        {
            // Context reloads temporarily leave only file headers. Keep the anchor until
            // source rows arrive; a genuinely different diff clears it after that.
            if (_rows.Any(row => row is DiffLineProjection))
                _pendingAnchor = null;
            return;
        }
        _pendingAnchor = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (_scrollViewer != null)
                _scrollViewer.Offset = _scrollViewer.Offset.WithY(Math.Max(0, _tops[index] - anchor.ViewportOffset));
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

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
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
        var first = FindRow(offset);
        var last = Math.Min(_rows.Length, FindRow(offset + viewport) + 2);
        for (var i = first; i < last; i++) DrawRow(context, _rows[i], _tops[i], i);
    }

    private void DrawRow(DrawingContext context, IDiffRowProjection row, double y, int index)
    {
        if (ReferenceEquals(row, SelectedItem)) context.FillRectangle(ThemeBrush("GitKaySelectionBrush", SelectionBrush), new Rect(0, y, Bounds.Width, RowHeight(row)));
        switch (row)
        {
            case DiffFileHeaderProjection file:
                context.FillRectangle(ThemeBrush("GitKayRaisedBrush", file.RowBackground), new Rect(0, y, Bounds.Width, FileHeight - 1));
                DrawPlain(context, file.DisplayPath, 12, y + 13, 13, ThemeBrush("GitKayTextBrush", FileBrush));
                break;
            case DiffHunkHeaderProjection hunk:
                context.FillRectangle(ThemeBrush("GitKayAccentMutedBrush", Brushes.Transparent), new Rect(0, y, Bounds.Width, HunkHeight - 1));
                DrawPlain(context, hunk.Header, 12, y + 7, 11, ThemeBrush("GitKaySecondaryTextBrush", HunkBrush));
                break;
            case DiffGapProjection gap:
                var actionStart = GapActionStart();
                var raised = ThemeBrush("GitKayRaisedBrush", Brushes.Transparent);
                var border = ThemeBrush("GitKayBorderBrush", HunkBrush);
                context.FillRectangle(ThemeBrush("GitKayWindowBrush", Brushes.Transparent), new Rect(0, y, Bounds.Width, GapHeight));
                context.FillRectangle(border, new Rect(0, y + 15, Math.Max(0, actionStart - 8), 1));
                context.FillRectangle(raised, new Rect(actionStart, y + 3, 220, 25));
                if (_hoveredGapAction == 1)
                    context.FillRectangle(ThemeBrush("GitKayHoverBrush", raised), new Rect(actionStart, y + 3, 108, 25));
                else if (_hoveredGapAction == 2)
                    context.FillRectangle(ThemeBrush("GitKayHoverBrush", raised), new Rect(actionStart + 109, y + 3, 111, 25));
                context.FillRectangle(border, new Rect(actionStart + 108, y + 6, 1, 19));
                context.FillRectangle(border, new Rect(actionStart + 228, y + 15, Math.Max(0, Bounds.Width - actionStart - 228), 1));
                DrawPlain(context, "↓  Show 10 lines", actionStart + 10, y + 8, 11, ThemeBrush("GitKayAccentBrush", FileBrush));
                DrawPlain(context, $"↕  Show all {gap.HiddenLineCount}", actionStart + 120, y + 8, 11, ThemeBrush("GitKayAccentBrush", FileBrush));
                break;
            case DiffLineProjection line:
                DrawLine(context, line, y);
                break;
        }
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
        var gutter = ThemeBrush("GitKayGutterBrush", Brushes.Transparent);
        context.FillRectangle(gutter, new Rect(0, y, 56, LineHeight - 1));
        var lineNumber = line.IsRemoved ? line.OldLineNoText : line.NewLineNoText;
        DrawLineNumber(context, lineNumber, 4, y, ThemeBrush("GitKayMutedTextBrush", line.LineNumberForeground));
        DrawPlain(context, line.Prefix, 44, y + 2, 12, (line.IsAdded ? ThemeBrush("GitKayAddedAccentBrush", line.PrefixForeground) : line.IsRemoved ? ThemeBrush("GitKayRemovedAccentBrush", line.PrefixForeground) : ThemeBrush("GitKayMutedTextBrush", line.PrefixForeground)));
        using (context.PushClip(new Rect(60, y, Math.Max(0, Bounds.Width - 60), LineHeight)))
            DrawCode(context, line.Content, 60, y + 2, ThemeBrush("GitKayTextBrush", line.Foreground));
    }

    private void DrawSingleSideLine(
        DrawingContext context,
        string lineNumber,
        string content,
        double y,
        DiffLineProjection line)
    {
        var gutter = ThemeBrush("GitKayGutterBrush", Brushes.Transparent);
        context.FillRectangle(gutter, new Rect(0, y, 48, LineHeight - 1));
        DrawLineNumber(context, lineNumber, 8, y, ThemeBrush("GitKayMutedTextBrush", line.LineNumberForeground));
        using (context.PushClip(new Rect(52, y, Math.Max(0, Bounds.Width - 52), LineHeight)))
            DrawCode(context, content, 52, y + 2, ThemeBrush("GitKayTextBrush", line.Foreground));
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
        context.FillRectangle(gutter, new Rect(0, y, 48, LineHeight - 1));
        context.FillRectangle(gutter, new Rect(middle + 1, y, 48, LineHeight - 1));
        context.FillRectangle(border, new Rect(middle, y, 1, LineHeight));

        DrawIntralineHighlights(context, line.OldContent, line.NewContent, 56, middle + 57, y);

        DrawLineNumber(context, line.OldLineNoText, 8, y, ThemeBrush("GitKayMutedTextBrush", line.LineNumberForeground));
        using (context.PushClip(new Rect(56, y, Math.Max(0, middle - 64), LineHeight)))
            DrawCode(context, line.OldContent, 56, y + 2, ThemeBrush("GitKayTextBrush", line.Foreground));

        DrawLineNumber(context, line.NewLineNoText, middle + 9, y, ThemeBrush("GitKayMutedTextBrush", line.LineNumberForeground));
        using (context.PushClip(new Rect(middle + 57, y, Math.Max(0, Bounds.Width - middle - 57), LineHeight)))
            DrawCode(context, line.NewContent, middle + 57, y + 2, ThemeBrush("GitKayTextBrush", line.Foreground));
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
                    var layout = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, CodeTypeface, 12, foreground);
                    var offset = 0;
                    foreach (var token in tokens)
                    {
                        var brush = TokenBrush(token.Kind, foreground);
                        if (token.Kind != HighlightKind.Plain) layout.SetForegroundBrush(brush, offset, token.Text.Length);
                        offset += token.Text.Length;
                    }
                    if (_colouredLayouts.Count >= MaxLayoutCacheEntries)
                        _colouredLayouts.Clear();
                    _colouredLayouts[text] = layout;
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

    private IBrush ThemeBrush(string key, IBrush fallback) =>
        this.TryFindResource(key, out var value) && value is IBrush brush ? brush : fallback;

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

    private static double RowHeight(IDiffRowProjection row) => row switch
    {
        DiffFileHeaderProjection => FileHeight,
        DiffHunkHeaderProjection => HunkHeight,
        DiffGapProjection => GapHeight,
        _ => LineHeight
    };

    private double GapActionStart() => Math.Max(8, (Bounds.Width - 220) / 2);

    private int GapActionAt(double x)
    {
        var start = GapActionStart();
        if (x >= start && x < start + 108) return 1;
        if (x >= start + 109 && x <= start + 220) return 2;
        return 0;
    }

    private readonly record struct ViewportAnchor(int? OldLineNo, int? NewLineNo, string Content, double ViewportOffset);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        var index = FindRow(position.Y);
        var action = (uint)index < (uint)_rows.Length && _rows[index] is DiffGapProjection
            ? GapActionAt(position.X)
            : 0;
        if (action == _hoveredGapAction) return;
        _hoveredGapAction = action;
        Cursor = action == 0 ? Cursor.Default : new Cursor(StandardCursorType.Hand);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoveredGapAction == 0) return;
        _hoveredGapAction = 0;
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var position = e.GetPosition(this);
        var index = FindRow(position.Y);
        if ((uint)index < (uint)_rows.Length)
        {
            var row = _rows[index];
            if (row is DiffGapProjection gap)
            {
                var action = GapActionAt(position.X);
                if (action == 2 && ExpandGapCommand?.CanExecute(gap.HiddenLineCount) == true)
                    ExpandGapCommand.Execute(gap.HiddenLineCount);
                else if (action == 1 && ExpandBlockCommand?.CanExecute(null) == true)
                    ExpandBlockCommand.Execute(null);
            }
            else if (row is not DiffHunkHeaderProjection)
            {
                SelectedItem = row;
            }
        }
        e.Handled = true;
    }

    public void MoveSelection(int delta)
    {
        if (_rows.Length == 0) return;
        var current = SelectedItem == null ? -1 : Array.IndexOf(_rows, SelectedItem);
        var next = Math.Clamp(current + delta, 0, _rows.Length - 1);
        SelectedItem = _rows[next];
        ScrollIntoView(SelectedItem);
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
