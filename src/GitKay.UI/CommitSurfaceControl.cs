using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GitKay.UI;

/// <summary>Fixed-row commit history renderer with O(visible rows) scrolling cost.</summary>
public sealed class CommitSurfaceControl : Control, IOverviewSource {
    public IReadOnlyList<OverviewMark> OverviewMarks {
        get {
            var marks = new List<OverviewMark>();
            if (_rows.Length == 0 || _filtered) return marks;
            for (var i = 0; i < _rows.Length; i++)
                if (_rows[i].HasSearchMatch)
                    marks.Add(new OverviewMark((double)i / _rows.Length, 1.0 / _rows.Length, OverviewMarkKind.SearchMatch));
            return marks;
        }
    }

    public ScrollViewer? OverviewScrollViewer => _scrollViewer;
    public event EventHandler? OverviewChanged;

    private const double RowHeight = 22;
    private const double LaneWidth = 9;
    private static readonly Typeface TextTypeface = new(FontStacks.Resolve(AppSettings.DefaultCommitRowFontFamily));
    private static readonly Typeface MonoTypeface = new(FontStacks.Resolve(AppSettings.DefaultCommitRowMonoFontFamily));
    // Search matches are shown in bold, like gitk.
    private static readonly Typeface BoldTextTypeface = new(TextTypeface.FontFamily, FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface BoldMonoTypeface = new(MonoTypeface.FontFamily, FontStyle.Normal, FontWeight.Bold);
    private static readonly IBrush SubjectBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220)).ToImmutable();
    private static readonly IBrush MetaBrush = new SolidColorBrush(Color.FromRgb(170, 170, 170)).ToImmutable();
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.FromRgb(136, 136, 136)).ToImmutable();
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromRgb(51, 51, 51)).ToImmutable();
    private static readonly IBrush[] LaneBrushes =
    [
        Brushes.Red, Brushes.Green, Brushes.Blue, Brushes.Orange, Brushes.Purple,
        Brushes.Cyan, Brushes.Magenta, Brushes.Yellow, Brushes.LightGreen, Brushes.LightBlue
    ];
    private IBrush[] _laneBrushes = LaneBrushes;
    private IPen[] _lanePens = LaneBrushes.Select(brush => new Pen(brush, 1.5).ToImmutable()).ToArray();

    public static readonly StyledProperty<IEnumerable<CommitProjection>?> ItemsSourceProperty =
        AvaloniaProperty.Register<CommitSurfaceControl, IEnumerable<CommitProjection>?>(nameof(ItemsSource));
    public static readonly StyledProperty<CommitProjection?> SelectedItemProperty =
        AvaloniaProperty.Register<CommitSurfaceControl, CommitProjection?>(nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<bool> ShowOnlyMatchesProperty =
        AvaloniaProperty.Register<CommitSurfaceControl, bool>(nameof(ShowOnlyMatches));
    public static readonly StyledProperty<SearchHighlight?> SearchHighlightProperty =
        AvaloniaProperty.Register<CommitSurfaceControl, SearchHighlight?>(nameof(SearchHighlight));
    public static readonly StyledProperty<bool> SearchActiveProperty =
        AvaloniaProperty.Register<CommitSurfaceControl, bool>(nameof(SearchActive));
    public static readonly StyledProperty<double> GraphWidthProperty = AvaloniaProperty.Register<CommitSurfaceControl, double>(nameof(GraphWidth), 48);
    public static readonly StyledProperty<double> SubjectWidthProperty = AvaloniaProperty.Register<CommitSurfaceControl, double>(nameof(SubjectWidth), 120);
    public static readonly StyledProperty<double> HashWidthProperty = AvaloniaProperty.Register<CommitSurfaceControl, double>(nameof(HashWidth), 54);
    public static readonly StyledProperty<double> AuthorWidthProperty = AvaloniaProperty.Register<CommitSurfaceControl, double>(nameof(AuthorWidth), 110);
    public static readonly StyledProperty<double> DateWidthProperty = AvaloniaProperty.Register<CommitSurfaceControl, double>(nameof(DateWidth), 110);

    private CommitProjection[] _all = Array.Empty<CommitProjection>();
    /// <summary>Displayed rows: all commits, or only search matches when <see cref="ShowOnlyMatches"/> is on.</summary>
    private CommitProjection[] _rows = Array.Empty<CommitProjection>();
    private bool _filtered;
    private Point _lastContextPoint;
    private INotifyCollectionChanged? _collection;
    private ScrollViewer? _scrollViewer;
    private readonly Dictionary<LayoutKey, FormattedText> _layouts = new();
    private CommitProjection? _keyboardSelection;
    private CancellationTokenSource? _keyboardSelectionCancellation;

    public IEnumerable<CommitProjection>? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public CommitProjection? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public bool ShowOnlyMatches { get => GetValue(ShowOnlyMatchesProperty); set => SetValue(ShowOnlyMatchesProperty, value); }
    /// <summary>Applied search terms; matched text in matching rows gets a dotted underline.</summary>
    public SearchHighlight? SearchHighlight { get => GetValue(SearchHighlightProperty); set => SetValue(SearchHighlightProperty, value); }
    public bool SearchActive { get => GetValue(SearchActiveProperty); set => SetValue(SearchActiveProperty, value); }

    /// <summary>Raised by the row context menu: (field, value) where a null value asks the host to prompt for one.</summary>
    public event Action<string, string?>? FilterRequested;

    public double GraphWidth { get => GetValue(GraphWidthProperty); set => SetValue(GraphWidthProperty, value); }
    public double SubjectWidth { get => GetValue(SubjectWidthProperty); set => SetValue(SubjectWidthProperty, value); }
    public double HashWidth { get => GetValue(HashWidthProperty); set => SetValue(HashWidthProperty, value); }
    public double AuthorWidth { get => GetValue(AuthorWidthProperty); set => SetValue(AuthorWidthProperty, value); }
    public double DateWidth { get => GetValue(DateWidthProperty); set => SetValue(DateWidthProperty, value); }

    static CommitSurfaceControl() {
        ItemsSourceProperty.Changed.AddClassHandler<CommitSurfaceControl>((control, _) => control.Rebuild());
        ShowOnlyMatchesProperty.Changed.AddClassHandler<CommitSurfaceControl>((control, _) => control.ApplyFilter());
        SearchActiveProperty.Changed.AddClassHandler<CommitSurfaceControl>((control, _) => control.ApplyFilter());
        SearchHighlightProperty.Changed.AddClassHandler<CommitSurfaceControl>((control, _) => control.InvalidateVisual());
        SelectedItemProperty.Changed.AddClassHandler<CommitSurfaceControl>((control, _) => {
            control._keyboardSelection = null;
            control.InvalidateVisual();
        });
        AffectsRender<CommitSurfaceControl>(GraphWidthProperty, SubjectWidthProperty, HashWidthProperty, AuthorWidthProperty, DateWidthProperty);
    }

    public CommitSurfaceControl() {
        Focusable = true;
        ContextMenu = BuildContextMenu();
        ContextRequested += OnCommitContextRequested;
        ActualThemeVariantChanged += (_, _) => {
            RefreshLaneBrushes();
            _layouts.Clear();
            InvalidateVisual();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        RefreshLaneBrushes();
        _scrollViewer = this.FindAncestorOfType<ScrollViewer>();
        if (_scrollViewer != null) _scrollViewer.ScrollChanged += OnScrollChanged;
        OverviewChanged?.Invoke(this, EventArgs.Empty);
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        if (_scrollViewer != null) _scrollViewer.ScrollChanged -= OnScrollChanged;
        Detach();
        _keyboardSelectionCancellation?.Cancel();
        base.OnDetachedFromVisualTree(e);
    }

    private void Rebuild() {
        _rebuildPending = false;
        Detach();
        _all = ItemsSource?.ToArray() ?? Array.Empty<CommitProjection>();
        if (ItemsSource is INotifyCollectionChanged collection) {
            _collection = collection;
            _collection.CollectionChanged += OnCollectionChanged;
        }
        foreach (var row in _all) row.PropertyChanged += OnRowChanged;
        _layouts.Clear();
        ApplyFilter();
    }

    /// <summary>
    /// Filters to matching commits while a search is active — an empty list when nothing matches. The graph is
    /// hidden while filtered because lanes can't be drawn across hidden commits.
    /// </summary>
    private void ApplyFilter() {
        _filtered = ShowOnlyMatches && SearchActive;
        _rows = _filtered ? _all.Where(row => row.HasSearchMatch).ToArray() : _all;
        OverviewChanged?.Invoke(this, EventArgs.Empty);
        _filterPending = false;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private bool _filterPending;

    private void Detach() {
        if (_collection != null) _collection.CollectionChanged -= OnCollectionChanged;
        foreach (var row in _all) row.PropertyChanged -= OnRowChanged;
        _collection = null;
    }

    private bool _rebuildPending;

    /// <summary>
    /// Collection syncs arrive as one change per commit; rebuilding (an O(n) copy and re-subscription) on each made
    /// loading a large history quadratic and froze the UI for seconds. Coalesce into one rebuild, and let anything
    /// that reads rows before it runs catch up first.
    /// </summary>
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        if (_rebuildPending) return;
        _rebuildPending = true;
        Dispatcher.UIThread.Post(EnsureRows, DispatcherPriority.Send);
    }

    private void EnsureRows() {
        if (!_rebuildPending) return;
        _rebuildPending = false;
        Rebuild();
    }
    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(CommitProjection.HasSearchMatch) && ShowOnlyMatches && !_filterPending) {
            // Many rows change together when results arrive; refilter once.
            _filterPending = true;
            Dispatcher.UIThread.Post(ApplyFilter, DispatcherPriority.Background);
        }
        _layouts.Clear();
        InvalidateVisual();
        if (e.PropertyName == nameof(CommitProjection.HasSearchMatch)) OverviewChanged?.Invoke(this, EventArgs.Empty);
    }
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) {
        EnsureRows();
        return MeasureRows(availableSize);
    }

    private Size MeasureRows(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width, _rows.Length * RowHeight);

    public override void Render(DrawingContext context) {
        EnsureRows();
        var offset = _scrollViewer?.Offset.Y ?? 0;
        var viewport = _scrollViewer?.Viewport.Height ?? Bounds.Height;
        context.FillRectangle(Brushes.Transparent, new Rect(0, offset, Bounds.Width, viewport));
        var first = Math.Clamp((int)(offset / RowHeight), 0, _rows.Length);
        var last = Math.Min(_rows.Length, first + (int)Math.Ceiling(viewport / RowHeight) + 2);
        for (var index = first; index < last; index++) DrawRow(context, _rows[index], index * RowHeight);

        if (_filtered && _rows.Length == 0) {
            var message = Layout("No matching commits", 13, ThemeBrush("GitKayMutedTextBrush", MutedBrush), TextTypeface);
            context.DrawText(message, new Point(Math.Max(12, (Bounds.Width - message.Width) / 2), offset + 16));
        }
    }

    private void DrawRow(DrawingContext context, CommitProjection commit, double y) {
        var selectionBrush = ThemeBrush("GitKaySelectionBrush", SelectionBrush);
        var subjectBrush = ThemeBrush("GitKayTextBrush", SubjectBrush);
        var metaBrush = ThemeBrush("GitKaySecondaryTextBrush", MetaBrush);
        var mutedBrush = ThemeBrush("GitKayMutedTextBrush", MutedBrush);

        if (ReferenceEquals(commit, _keyboardSelection ?? SelectedItem))
            context.FillRectangle(selectionBrush, new Rect(0, y, Bounds.Width, RowHeight));
        var textTypeface = commit.HasSearchMatch ? BoldTextTypeface : TextTypeface;
        var monoTypeface = commit.HasSearchMatch ? BoldMonoTypeface : MonoTypeface;

        var graphWidth = EffectiveWidth(GraphWidth, 48);
        if (!_filtered)
            using (context.PushClip(new Rect(0, y, graphWidth, RowHeight)))
                DrawGraph(context, commit, y);

        var subjectWidth = EffectiveWidth(SubjectWidth, 120);
        var hashWidth = EffectiveWidth(HashWidth, 54);
        var authorWidth = EffectiveWidth(AuthorWidth, 110);
        var dateWidth = EffectiveWidth(DateWidth, 110);
        var subjectX = graphWidth;
        var hashX = subjectX + subjectWidth;
        var authorX = hashX + hashWidth;
        var dateX = authorX + authorWidth;
        var highlight = commit.HasSearchMatch ? SearchHighlight : null;
        var underline = ThemeBrush("GitKayAccentBrush", UnderlineFallback);
        var badgeX = subjectX + 5;
        foreach (var badge in commit.RefBadges) {
            var badgeWidth = DrawBadge(context, badge, badgeX, y);
            if (highlight != null)
                Underline(context, badge.Text, highlight.Ref, badgeX + 6, y + RowHeight - 3, 11, TextTypeface, highlight, underline);
            badgeX += badgeWidth;
        }

        using (context.PushClip(new Rect(badgeX, y, Math.Max(0, hashX - badgeX - 3), RowHeight))) {
            DrawText(context, commit.Subject, badgeX, y + 3, 13, subjectBrush, textTypeface);
            if (highlight != null) Underline(context, commit.Subject, highlight.Subject, badgeX, y + RowHeight - 2, 13, textTypeface, highlight, underline);
        }
        using (context.PushClip(new Rect(hashX, y, hashWidth, RowHeight))) {
            DrawText(context, commit.Hash, hashX + 2, y + 4, 12, mutedBrush, monoTypeface);
            if (highlight != null) Underline(context, commit.Hash, highlight.Hash, hashX + 2, y + RowHeight - 2, 12, monoTypeface, highlight, underline);
        }
        using (context.PushClip(new Rect(authorX, y, authorWidth - 4, RowHeight))) {
            // "Name <email>" like gitk, with the address dimmer than the name.
            var authorLayout = Layout(commit.Author, 12, mutedBrush, textTypeface);
            context.DrawText(authorLayout, new Point(authorX + 2, y + 4));
            if (highlight != null) Underline(context, commit.Author, highlight.Author, authorX + 2, y + RowHeight - 2, 12, textTypeface, highlight, underline);
            if (!string.IsNullOrEmpty(commit.AuthorEmail)) {
                using (context.PushOpacity(0.6))
                    DrawText(context, $"<{commit.AuthorEmail}>", authorX + 2 + authorLayout.Width + 4, y + 4, 12, mutedBrush, textTypeface);
            }
        }

        using (context.PushClip(new Rect(dateX, y, dateWidth, RowHeight))) {
            var date = Layout(commit.Date, 12, mutedBrush, textTypeface);
            context.DrawText(date, new Point(Math.Max(dateX + 2, dateX + dateWidth - date.Width - 4), y + 4));
        }
    }

    /// <summary>
    /// Dark keeps the established drawn colours (the fallbacks); other variants resolve the palette for the
    /// actual theme. Without the variant, lookups never reach the theme dictionaries.
    /// </summary>
    private static readonly IBrush UnderlineFallback = new SolidColorBrush(Color.FromRgb(88, 166, 255)).ToImmutable();

    /// <summary>Dotted underline beneath each matched span; the shared search-match marker.</summary>
    private void Underline(DrawingContext context, string text, IReadOnlyList<string> terms, double x, double baseline, double size, Typeface typeface, SearchHighlight highlight, IBrush brush) {
        foreach (var (start, length) in highlight.Matches(terms, text)) {
            var left = x + (start == 0 ? 0 : Layout(text[..start], size, Brushes.Transparent, typeface).Width);
            var width = Layout(text.Substring(start, length), size, Brushes.Transparent, typeface).Width;
            for (var dot = left; dot < left + width; dot += 3)
                context.FillRectangle(brush, new Rect(dot, baseline, 1.5, 1.5));
        }
    }

    private IBrush ThemeBrush(string key, IBrush fallback) =>
        ActualThemeVariant != Avalonia.Styling.ThemeVariant.Dark
        && this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : fallback;

    private static double EffectiveWidth(double value, double fallback) =>
        double.IsFinite(value) && value > 1 ? value : fallback;

    /// <summary>Lane colours come from the theme so the graph stays legible on light backgrounds.</summary>
    private void RefreshLaneBrushes() {
        _laneBrushes = LaneBrushes.Select((fallback, index) => ThemeBrush($"GitKayLane{index}Brush", fallback)).ToArray();
        _lanePens = _laneBrushes.Select(brush => (IPen)new Pen(brush, 1.5)).ToArray();
    }

    private void DrawGraph(DrawingContext context, CommitProjection commit, double y) {
        var centerY = y + RowHeight / 2;
        foreach (var segment in commit.Segments) {
            var pen = _lanePens[Math.Abs(segment.Color) % _lanePens.Length];
            var x = (segment.Lane + 1) * LaneWidth;
            if (segment.IsCommit) {
                var target = (segment.TargetLane + 1) * LaneWidth;
                context.DrawLine(pen, new Point(x, centerY), new Point(target, y + RowHeight));
            }
            else
                context.DrawLine(pen, new Point(x, y), new Point(x, y + RowHeight));
        }
        var brush = _laneBrushes[Math.Abs(commit.Lane) % _laneBrushes.Length];
        var cx = (commit.Lane + 1) * LaneWidth;
        context.DrawEllipse(brush, null, new Rect(cx - 3, centerY - 3, 6, 6));
    }

    private static readonly (IBrush Background, IBrush Foreground)[] RefPillFallbacks =
    {
        (new SolidColorBrush(Color.FromRgb(0x1A, 0x2F, 0x4A)).ToImmutable(), new SolidColorBrush(Color.FromRgb(0x79, 0xB8, 0xFF)).ToImmutable()),
        (new SolidColorBrush(Color.FromRgb(0x26, 0x2C, 0x34)).ToImmutable(), new SolidColorBrush(Color.FromRgb(0x9D, 0xA7, 0xB3)).ToImmutable()),
        (new SolidColorBrush(Color.FromRgb(0x33, 0x29, 0x0F)).ToImmutable(), new SolidColorBrush(Color.FromRgb(0xD9, 0xA9, 0x3C)).ToImmutable()),
        (new SolidColorBrush(Color.FromRgb(0x26, 0x2C, 0x34)).ToImmutable(), new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)).ToImmutable()),
    };

    /// <summary>Muted, borderless ref pills tinted by kind (branch, remote, tag, stash).</summary>
    private double DrawBadge(DrawingContext context, CommitRefProjection badge, double x, double y) {
        var (key, index) = badge.Kind switch {
            CommitRefKind.Remote => ("Remote", 1),
            CommitRefKind.Tag => ("Tag", 2),
            CommitRefKind.Stash => ("Stash", 3),
            _ => ("Branch", 0),
        };
        var background = ThemeBrush($"GitKayRef{key}Background", RefPillFallbacks[index].Background);
        var foreground = ThemeBrush($"GitKayRef{key}Foreground", RefPillFallbacks[index].Foreground);
        var text = Layout(badge.Text, 11, foreground, TextTypeface);
        var width = text.Width + 12;
        context.DrawRectangle(background, null, new Rect(x, y + 3, width, 16), 8, 8);
        context.DrawText(text, new Point(x + 6, y + 4));
        return width + 4;
    }

    private void DrawText(DrawingContext context, string text, double x, double y, double size, IBrush brush, Typeface typeface) =>
        context.DrawText(Layout(text, size, brush, typeface), new Point(x, y));

    private FormattedText Layout(string text, double size, IBrush brush, Typeface typeface) {
        var key = new LayoutKey(text, size, brush, typeface);
        if (!_layouts.TryGetValue(key, out var layout)) {
            layout = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);
            if (_layouts.Count >= 4096) _layouts.Clear();
            _layouts[key] = layout;
        }
        return layout;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        Focus();
        _lastContextPoint = e.GetPosition(this);
        SelectAt(_lastContextPoint.Y);
    }

    private void OnCommitContextRequested(object? sender, ContextRequestedEventArgs e) {
        // Rebuilt per request so filter items reflect the clicked column and commit.
        if (e.TryGetPosition(this, out var point)) _lastContextPoint = point;
        ContextMenu = BuildContextMenu();
    }

    /// <summary>Filter actions for the clicked column, above the commit operations.</summary>
    private IEnumerable<Control> BuildFilterItems(CommitProjection commit) {
        MenuItem Filter(string header, string field, string? value) {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => FilterRequested?.Invoke(field, value);
            return item;
        }

        var graphWidth = EffectiveWidth(GraphWidth, 48);
        var hashX = graphWidth + EffectiveWidth(SubjectWidth, 120);
        var authorX = hashX + EffectiveWidth(HashWidth, 54);
        var dateX = authorX + EffectiveWidth(AuthorWidth, 110);
        var x = _lastContextPoint.X;

        if (x >= dateX) {
            var day = commit.Date.Length >= 10 ? commit.Date[..10] : commit.Date;
            yield return Filter($"Commits on or after {day}", "after", day);
            yield return Filter($"Commits before {day}", "before", day);
        }
        else if (x >= authorX) {
            yield return Filter($"Only commits by {commit.Author}", "author", commit.Author);
            yield return Filter("Filter by author…", "author", null);
        }
        else if (x >= hashX) {
            yield return Filter("Filter by hash…", "hash", null);
        }
        else {
            foreach (var badge in commit.RefBadges)
                yield return Filter($"Only commits on {badge.Text}", "ref", badge.Text);
            yield return Filter("Filter by message…", "message", null);
        }
    }

    public void SelectAt(double documentY) {
        EnsureRows();
        if (_rows.Length == 0) return;
        var index = Math.Clamp((int)(documentY / RowHeight), 0, _rows.Length - 1);
        _keyboardSelectionCancellation?.Cancel();
        _keyboardSelection = null;
        SelectedItem = _rows[index];
    }

    public int ViewportRowCount => _scrollViewer == null ? 20 : Math.Max(1, (int)(_scrollViewer.Viewport.Height / RowHeight));

    public void MoveSelection(int delta) {
        EnsureRows();
        if (_rows.Length == 0) return;
        var currentItem = _keyboardSelection ?? SelectedItem;
        var current = currentItem == null ? -1 : Array.IndexOf(_rows, currentItem);
        _keyboardSelection = _rows[Math.Clamp(current + delta, 0, _rows.Length - 1)];
        ScrollIntoView(_keyboardSelection);
        InvalidateVisual();

        _keyboardSelectionCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _keyboardSelectionCancellation = cancellation;
        _ = CommitKeyboardSelectionAsync(_keyboardSelection, cancellation);
    }

    private async Task CommitKeyboardSelectionAsync(CommitProjection selection, CancellationTokenSource cancellation) {
        try {
            await Task.Delay(65, cancellation.Token);
            await Dispatcher.UIThread.InvokeAsync(() => {
                if (!cancellation.IsCancellationRequested && ReferenceEquals(_keyboardSelectionCancellation, cancellation))
                    SelectedItem = selection;
            }, DispatcherPriority.Input);
        }
        catch (OperationCanceledException) {
        }
    }

    public void ScrollIntoView(CommitProjection item) {
        EnsureRows();
        if (_scrollViewer == null) return;
        var index = Array.IndexOf(_rows, item);
        if (index < 0) return;
        var top = index * RowHeight;
        var bottom = top + RowHeight;
        var offset = _scrollViewer.Offset;
        if (top < offset.Y) _scrollViewer.Offset = offset.WithY(top);
        else if (bottom > offset.Y + _scrollViewer.Viewport.Height) _scrollViewer.Offset = offset.WithY(Math.Max(0, bottom - _scrollViewer.Viewport.Height));
    }

    /// <summary>Push or pull ("push" / "pull") requested for a local branch on the clicked commit.</summary>
    public event Action<string, BranchTarget>? BranchOperationRequested;

    /// <summary>Text the user asked to copy (a branch or tag name).</summary>
    public event Action<string>? CopyRequested;

    private ContextMenu BuildContextMenu() {
        MenuItem Item(string title, Func<CommitProjection, System.Windows.Input.ICommand> command) {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => { var selected = SelectedItem; if (selected != null) command(selected).Execute(null); };
            return item;
        }
        var menu = new ContextMenu();
        if (SelectedItem is { } selected) {
            foreach (var filter in BuildFilterItems(selected)) menu.Items.Add(filter);
            menu.Items.Add(new Separator());
            var refNames = selected.RefNames.ToArray();
            foreach (var (name, kind) in refNames) {
                var copy = new MenuItem { Header = $"Copy {kind} name “{name}”" };
                copy.Click += (_, _) => CopyRequested?.Invoke(name);
                menu.Items.Add(copy);
            }
            if (refNames.Length > 0) menu.Items.Add(new Separator());

            var branches = selected.LocalBranches.ToArray();
            foreach (var branch in branches) {
                MenuItem Operation(string title, string operation) {
                    var item = new MenuItem { Header = title };
                    item.Click += (_, _) => BranchOperationRequested?.Invoke(operation, branch);
                    return item;
                }

                menu.Items.Add(Operation($"Push {branch.Name}", "push"));
                menu.Items.Add(Operation(branch.IsCurrentHead ? $"Pull {branch.Name}" : $"Pull {branch.Name} (fast-forward)", "pull"));
                var delete = Operation($"Delete {branch.Name}…", "delete");
                // Git refuses to delete the checked-out branch; say why instead of offering it.
                if (branch.IsCurrentHead) {
                    delete.IsEnabled = false;
                    ToolTip.SetTip(delete, "This branch is checked out");
                }
                menu.Items.Add(delete);
            }
            if (branches.Length > 0) menu.Items.Add(new Separator());
        }
        foreach (var item in new Control[]
            {
                Item("Create Tag here...", row => row.CreateTagCommand),
                Item("Create Branch here...", row => row.CreateBranchCommand),
                new Separator(),
                Item("Cherry-pick this commit", row => row.CherryPickCommand),
                Item("Revert this commit", row => row.RevertCommand),
                new Separator(),
                Item("Reset current branch here (soft)", row => row.ResetSoftCommand),
                Item("Reset current branch here (hard)", row => row.ResetHardCommand)
            })
            menu.Items.Add(item);
        return menu;
    }

    private readonly record struct LayoutKey(string Text, double Size, IBrush Brush, Typeface Typeface);
}
