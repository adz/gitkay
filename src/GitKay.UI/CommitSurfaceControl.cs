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
public sealed class CommitSurfaceControl : Control, IOverviewSource, GitKay.Core.Vim.IVimHost {
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
    private const double LaneWidth = 11;
    private const double GraphInset = 2;
    private static readonly Typeface TextTypeface = new(FontStacks.Resolve(GitKay.Core.SettingsModule.defaults.CommitRowFontFamily));
    private static readonly Typeface MonoTypeface = new(FontStacks.Resolve(GitKay.Core.SettingsModule.defaults.CommitRowMonoFontFamily));
    // Search matches are shown in bold, like gitk.
    private static readonly Typeface BoldTextTypeface = new(TextTypeface.FontFamily, FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface BoldMonoTypeface = new(MonoTypeface.FontFamily, FontStyle.Normal, FontWeight.Bold);
    private static readonly IBrush SubjectBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220)).ToImmutable();
    private static readonly IBrush MetaBrush = new SolidColorBrush(Color.FromRgb(170, 170, 170)).ToImmutable();
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.FromRgb(136, 136, 136)).ToImmutable();
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromRgb(51, 51, 51)).ToImmutable();
    private static readonly IBrush[] LaneBrushes =
        new[] { "#F47067", "#57AB5A", "#539BF5", "#E0823D", "#B083F0", "#39C5CF", "#FC8DC7", "#DAAA3F", "#8DDB8C", "#6CB6FF" }
            .Select(hex => (IBrush)new SolidColorBrush(Color.Parse(hex)).ToImmutable()).ToArray();
    private static readonly IBrush WindowFallback = new SolidColorBrush(Color.FromRgb(0x0D, 0x11, 0x17)).ToImmutable();
    private IBrush[] _laneBrushes = LaneBrushes;
    private IPen[] _lanePens = LaneBrushes.Select(LanePen).ToArray();

    private static IPen LanePen(IBrush brush) => new Pen(brush, 1.75, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round).ToImmutable();

    public static readonly StyledProperty<IEnumerable<CommitProjection>?> ItemsSourceProperty =
        AvaloniaProperty.Register<CommitSurfaceControl, IEnumerable<CommitProjection>?>(nameof(ItemsSource));
    public static readonly StyledProperty<CommitProjection?> SelectedItemProperty =
        AvaloniaProperty.Register<CommitSurfaceControl, CommitProjection?>(nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<bool> ShowOnlyMatchesProperty =
        AvaloniaProperty.Register<CommitSurfaceControl, bool>(nameof(ShowOnlyMatches));
    public static readonly StyledProperty<GitKay.Core.GitSearch.Highlight?> SearchHighlightProperty =
        AvaloniaProperty.Register<CommitSurfaceControl, GitKay.Core.GitSearch.Highlight?>(nameof(SearchHighlight));
    public static readonly StyledProperty<GitKay.Core.GitSearch.Highlight?> QuickFindHighlightProperty =
        AvaloniaProperty.Register<CommitSurfaceControl, GitKay.Core.GitSearch.Highlight?>(nameof(QuickFindHighlight));
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
    public GitKay.Core.GitSearch.Highlight? SearchHighlight { get => GetValue(SearchHighlightProperty); set => SetValue(SearchHighlightProperty, value); }
    /// <summary>A / search in the commit list: underlined on every commit it matches, whether or not a search is applied.</summary>
    public GitKay.Core.GitSearch.Highlight? QuickFindHighlight { get => GetValue(QuickFindHighlightProperty); set => SetValue(QuickFindHighlightProperty, value); }
    public bool SearchActive { get => GetValue(SearchActiveProperty); set => SetValue(SearchActiveProperty, value); }

    /// <summary>
    /// How much room the visible rows' branch and tag badges need, so the commit column can widen rather than cut
    /// them off, as gitk does. Raised only when the figure changes.
    /// </summary>
    public event Action<double>? BadgeWidthMeasured;
    private double _lastBadgeWidth = -1;

    /// <summary>Raised by the row context menu: (field, value) where a null value asks the host to prompt for one.</summary>
    public event Action<string, string?>? FilterRequested;
    /// <summary>The commit window was asked for from the uncommitted changes row, or to amend the commit at HEAD.</summary>
    public event Action? CommitWindowRequested;
    public event Action? AmendRequested;
    /// <summary>History limited to what a branch or tag reaches was requested.</summary>
    public event Action<string>? HistoryRequested;

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
        QuickFindHighlightProperty.Changed.AddClassHandler<CommitSurfaceControl>((control, _) => control.InvalidateVisual());
        SelectedItemProperty.Changed.AddClassHandler<CommitSurfaceControl>((control, _) => {
            control._keyboardSelection = null;
            control.InvalidateVisual();
        });
        AffectsRender<CommitSurfaceControl>(GraphWidthProperty, SubjectWidthProperty, HashWidthProperty, AuthorWidthProperty, DateWidthProperty);
    }

    public CommitSurfaceControl() {
        Focusable = true;
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
        MeasureVisibleBadges();
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
        if (e.PropertyName == nameof(CommitProjection.RefBadges) || e.PropertyName == nameof(CommitProjection.HasRefBadges))
            MeasureVisibleBadges();
        InvalidateVisual();
        if (e.PropertyName == nameof(CommitProjection.HasSearchMatch)) OverviewChanged?.Invoke(this, EventArgs.Empty);
    }
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) {
        MeasureVisibleBadges();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) {
        EnsureRows();
        return MeasureRows(availableSize);
    }

    private Size MeasureRows(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width, _rows.Length * RowHeight);

    /// <summary>Measures the badges on screen; the commit column may need to change width because of them.</summary>
    private void MeasureVisibleBadges() {
        var offset = _scrollViewer?.Offset.Y ?? 0;
        var viewport = _scrollViewer?.Viewport.Height ?? Bounds.Height;
        if (viewport <= 0) viewport = 600;
        var first = Math.Clamp((int)(offset / RowHeight), 0, Math.Max(0, _rows.Length));
        var last = Math.Min(_rows.Length, first + (int)Math.Ceiling(viewport / RowHeight) + 2);
        MeasureBadges(first, last);
    }

    public override void Render(DrawingContext context) {
        EnsureRows();
        var offset = _scrollViewer?.Offset.Y ?? 0;
        var viewport = _scrollViewer?.Viewport.Height ?? Bounds.Height;
        context.FillRectangle(Brushes.Transparent, new Rect(0, offset, Bounds.Width, viewport));
        var first = Math.Clamp((int)(offset / RowHeight), 0, _rows.Length);
        var last = Math.Min(_rows.Length, first + (int)Math.Ceiling(viewport / RowHeight) + 2);
        for (var index = first; index < last; index++) {
            if (_rows[index].IsWorkingTree) DrawWorkingTreeRow(context, _rows[index], index * RowHeight, index + 1 < _rows.Length);
            else DrawRow(context, _rows[index], index * RowHeight, index > 0 && _rows[index - 1].IsWorkingTree);
        }

        if (_filtered && _rows.Length == 0) {
            var message = Layout("No matching commits", 13, ThemeBrush("GitKayMutedTextBrush", MutedBrush), TextTypeface);
            context.DrawText(message, new Point(Math.Max(12, (Bounds.Width - message.Width) / 2), offset + 16));
        }
    }

    /// <summary>The uncommitted changes row: a hollow dashed node joined to HEAD below, and its counts.</summary>
    private void DrawWorkingTreeRow(DrawingContext context, CommitProjection row, double y, bool hasCommitBelow) {
        if (ReferenceEquals(row, _keyboardSelection ?? SelectedItem))
            context.FillRectangle(ThemeBrush("GitKaySelectionBrush", SelectionBrush), new Rect(0, y, Bounds.Width, RowHeight));
        var mutedBrush = ThemeBrush("GitKayMutedTextBrush", MutedBrush);
        var graphWidth = EffectiveWidth(GraphWidth, 48);
        var centerY = y + RowHeight / 2;
        if (!_filtered) {
            using (context.PushClip(new Rect(0, y, graphWidth, RowHeight))) {
                var cx = LaneX(row.Lane);
                var pen = new Pen(mutedBrush, 1.5, new DashStyle([2, 2], 0));
                if (hasCommitBelow) context.DrawLine(pen, new Point(cx, centerY + 4), new Point(cx, y + RowHeight));
                context.DrawEllipse(null, pen, new Rect(cx - 4, centerY - 4, 8, 8));
            }
        }

        var subjectX = graphWidth + 5;
        var hashX = graphWidth + EffectiveWidth(SubjectWidth, 120);
        using (context.PushClip(new Rect(subjectX, y, Math.Max(0, hashX - subjectX - 3), RowHeight))) {
            var label = Layout(row.Subject, 13, ThemeBrush("GitKayTextBrush", SubjectBrush), TextTypeface);
            context.DrawText(label, new Point(subjectX, y + 3));
            DrawText(context, row.SecondarySummary, subjectX + label.Width + 10, y + 4, 12, mutedBrush, TextTypeface);
        }
    }

    /// <summary>The widest run of badges on screen, so the host can give the commit column room for it.</summary>
    private void MeasureBadges(int first, int last) {
        var widest = 0.0;
        for (var index = first; index < last; index++) {
            var width = 0.0;
            foreach (var badge in _rows[index].RefBadges)
                width += Layout(badge.Text, 11, Brushes.Transparent, TextTypeface).Width + (badge.Kind == CommitRefKind.Tag ? 19 : 16);
            widest = Math.Max(widest, width);
        }

        if (Math.Abs(widest - _lastBadgeWidth) < 1) return;
        _lastBadgeWidth = widest;
        // Resizing a column from inside the render pass is not allowed, so the host hears about it just after.
        Dispatcher.UIThread.Post(() => BadgeWidthMeasured?.Invoke(widest), DispatcherPriority.Background);
    }

    private void DrawRow(DrawingContext context, CommitProjection commit, double y, bool joinsWorkingTree = false) {
        var selectionBrush = ThemeBrush("GitKaySelectionBrush", SelectionBrush);
        var subjectBrush = ThemeBrush("GitKayTextBrush", SubjectBrush);
        var metaBrush = ThemeBrush("GitKaySecondaryTextBrush", MetaBrush);
        var mutedBrush = ThemeBrush("GitKayMutedTextBrush", MutedBrush);

        var isSelected = ReferenceEquals(commit, _keyboardSelection ?? SelectedItem);
        if (isSelected)
            context.FillRectangle(selectionBrush, new Rect(0, y, Bounds.Width, RowHeight));
        var rowBackground = isSelected ? selectionBrush : ThemeBrush("GitKayWindowBrush", WindowFallback);
        var textTypeface = commit.HasSearchMatch ? BoldTextTypeface : TextTypeface;
        var monoTypeface = commit.HasSearchMatch ? BoldMonoTypeface : MonoTypeface;

        var graphWidth = EffectiveWidth(GraphWidth, 48);
        if (!_filtered)
            using (context.PushClip(new Rect(0, y, graphWidth, RowHeight)))
                DrawGraph(context, commit, y, joinsWorkingTree, rowBackground);

        var subjectWidth = EffectiveWidth(SubjectWidth, 120);
        var hashWidth = EffectiveWidth(HashWidth, 54);
        var authorWidth = EffectiveWidth(AuthorWidth, 110);
        var dateWidth = EffectiveWidth(DateWidth, 110);
        var subjectX = graphWidth;
        var hashX = subjectX + subjectWidth;
        var authorX = hashX + hashWidth;
        var dateX = authorX + authorWidth;
        // A / search underlines wherever it matches; an applied search only on the commits it matched.
        var highlight = QuickFindHighlight is { IsEmpty: false } quick ? quick : commit.HasSearchMatch ? SearchHighlight : null;
        var underline = ThemeBrush("GitKayAccentBrush", UnderlineFallback);
        var badgeX = subjectX + 5;
        if (!_filtered && commit.RefBadges.Count > 0) {
            // Like gitk: a line from the commit's node runs out to its branches and tags and strings them together.
            var badgesEnd = badgeX + commit.RefBadges.Sum(badge => Layout(badge.Text, 11, Brushes.Transparent, TextTypeface).Width + (badge.Kind == CommitRefKind.Tag ? 19 : 16)) - 10;
            var connector = new Pen(_laneBrushes[Math.Abs(commit.GraphColor) % _laneBrushes.Length], 1.25);
            var nodeX = LaneX(commit.Lane);
            using (context.PushOpacity(0.7))
                context.DrawLine(connector, new Point(nodeX + 5.5, y + RowHeight / 2), new Point(badgesEnd, y + RowHeight / 2));
        }
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
    private void Underline(DrawingContext context, string text, Microsoft.FSharp.Collections.FSharpList<GitKay.Kit.TextQuery> queries, double x, double baseline, double size, Typeface typeface, GitKay.Core.GitSearch.Highlight highlight, IBrush brush) {
        if (string.IsNullOrEmpty(text) || queries.IsEmpty) return;
        foreach (var (start, length) in GitKay.Core.GitSearch.spans(queries, text)) {
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
        _lanePens = _laneBrushes.Select(LanePen).ToArray();
    }

    private static double LaneX(int lane) => GraphInset + (lane + 0.5) * LaneWidth;

    /// <summary>
    /// Draws a row's slice of the graph so lines meet their neighbours' exactly: every line enters at the row's top and
    /// leaves at its bottom vertically, lane changes are smooth curves, and each branch line keeps its colour.
    /// </summary>
    private void DrawGraph(DrawingContext context, CommitProjection commit, double y, bool joinsWorkingTree, IBrush rowBackground) {
        var top = y;
        var bottom = y + RowHeight;
        var centerY = y + RowHeight / 2;
        var nodeX = LaneX(commit.Lane);
        IPen PenFor(int color) => _lanePens[Math.Abs(color) % _lanePens.Length];

        // Lines passing by first, so the commit's own lines and node sit on top.
        foreach (var segment in commit.Segments) {
            if (segment.IsCommit) continue;
            var from = new Point(LaneX(segment.Lane), top);
            var to = new Point(LaneX(segment.TargetLane), bottom);
            if (segment.Lane == segment.TargetLane) context.DrawLine(PenFor(segment.Color), from, to);
            else context.DrawGeometry(null, PenFor(segment.Color), Curve(from, new Point(from.X, top + RowHeight * 0.55), new Point(to.X, bottom - RowHeight * 0.55), to));
        }

        // A halo in the row's background separates the node from lines passing by, drawn under the commit's own lines.
        var brush = _laneBrushes[Math.Abs(commit.GraphColor) % _laneBrushes.Length];
        var center = new Point(nodeX, centerY);
        context.DrawEllipse(rowBackground, null, center, 5.5, 5.5);

        if (commit.HasIncoming)
            context.DrawLine(PenFor(commit.GraphColor), new Point(nodeX, top), new Point(nodeX, centerY));
        else if (joinsWorkingTree)
            context.DrawLine(new Pen(ThemeBrush("GitKayMutedTextBrush", MutedBrush), 1.5, new DashStyle([2, 2], 0)), new Point(nodeX, top), new Point(nodeX, centerY));

        foreach (var segment in commit.Segments) {
            if (!segment.IsCommit) continue;
            var to = new Point(LaneX(segment.TargetLane), bottom);
            if (segment.TargetLane == commit.Lane) context.DrawLine(PenFor(segment.Color), new Point(nodeX, centerY), to);
            // Leaves the node downwards, sweeps across, and arrives vertically to join the next row.
            else context.DrawGeometry(null, PenFor(segment.Color), Curve(new Point(nodeX, centerY), new Point(nodeX, centerY + RowHeight * 0.3), new Point(to.X, bottom - RowHeight * 0.3), to));
        }

        // Merges are rings.
        if (commit.IsMerge)
            context.DrawEllipse(rowBackground, new Pen(brush, 2), center, 3.25, 3.25);
        else
            context.DrawEllipse(brush, null, center, 4, 4);
    }

    private static StreamGeometry Curve(Point from, Point control1, Point control2, Point to) {
        var geometry = new StreamGeometry();
        using (var sink = geometry.Open()) {
            sink.BeginFigure(from, false);
            sink.CubicBezierTo(control1, control2, to);
            sink.EndFigure(false);
        }
        return geometry;
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
        const double top = 3, height = 16, point = 7;
        if (badge.Kind == CommitRefKind.Tag) {
            // A luggage tag, as gitk draws them: pointed left end with a hole, text in the body.
            var width = point + text.Width + 8;
            var geometry = new StreamGeometry();
            using (var sink = geometry.Open()) {
                var middle = y + top + height / 2;
                sink.BeginFigure(new Point(x, middle), true);
                sink.LineTo(new Point(x + point, y + top));
                sink.LineTo(new Point(x + width - 2, y + top));
                sink.ArcTo(new Point(x + width, y + top + 2), new Size(2, 2), 0, false, SweepDirection.Clockwise);
                sink.LineTo(new Point(x + width, y + top + height - 2));
                sink.ArcTo(new Point(x + width - 2, y + top + height), new Size(2, 2), 0, false, SweepDirection.Clockwise);
                sink.LineTo(new Point(x + point, y + top + height));
                sink.EndFigure(true);
            }
            context.DrawGeometry(background, new Pen(foreground, 1, lineJoin: PenLineJoin.Round), geometry);
            context.DrawEllipse(ThemeBrush("GitKayWindowBrush", WindowFallback), new Pen(foreground, 1), new Point(x + point - 1, y + top + height / 2), 1.6, 1.6);
            context.DrawText(text, new Point(x + point + 3, y + 4));
            return width + 4;
        }

        var pillWidth = text.Width + 12;
        // Branches and remotes are boxes, as in gitk.
        context.DrawRectangle(background, null, new Rect(x + 0.5, y + top + 0.5, pillWidth - 1, height - 1), 3, 3);
        context.DrawText(text, new Point(x + 6, y + 4));
        return pillWidth + 4;
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
        // After the click finishes, so the pointer release doesn't give focus back to this window.
        if (e.ClickCount == 2 && BadgeAt(_lastContextPoint) is { Kind: CommitRefKind.Branch or CommitRefKind.Remote or CommitRefKind.Tag } badge)
            Dispatcher.UIThread.Post(() => RevisionComparisonRequested?.Invoke(badge.Text, false), DispatcherPriority.Background);
        else if (e.ClickCount == 2 && SelectedItem is { IsWorkingTree: true })
            Dispatcher.UIThread.Post(() => CommitWindowRequested?.Invoke(), DispatcherPriority.Background);
    }

    private void OnCommitContextRequested(object? sender, ContextRequestedEventArgs e) {
        // Built and opened here for the clicked commit and column. Assigning the ContextMenu property instead let
        // Avalonia's handler (registered first) open the menu built for the previous selection, hiding branch items.
        if (e.TryGetPosition(this, out var point)) {
            _lastContextPoint = point;
            SelectAt(point.Y);
        }
        BuildContextMenu().Open(this);
        e.Handled = true;
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
            // The row shows "yyyy-MM-dd HH:mm": offer the day, and the exact minute for a tighter range.
            var day = commit.Date.Length >= 10 ? commit.Date[..10] : commit.Date;
            var minute = commit.Date.Length >= 16 ? commit.Date[..16].Replace(' ', 'T') : day;
            yield return Filter($"Commits on or after {day}", "after", day);
            yield return Filter($"Commits before {day}", "before", day);
            if (minute != day) {
                yield return Filter($"Commits on or after {commit.Date}", "after", minute);
                yield return Filter($"Commits before {commit.Date}", "before", minute);
            }
        }
        else if (x >= authorX) {
            yield return Filter($"Only commits by {commit.Author}", "author", commit.Author);
            yield return Filter("Filter by author…", "author", null);
        }
        else if (x >= hashX) {
            // A hash names one commit, so going to it beats filtering the list down to it.
            yield return Filter("Go to commit…", "goto", null);
        }
        else {
            foreach (var badge in commit.RefBadges) {
                var history = new MenuItem { Header = $"Only commits on {badge.Text}" };
                ToolTip.SetTip(history, $"History from {badge.Text} back, like gitk {badge.Text}");
                var name = badge.Text;
                history.Click += (_, _) => HistoryRequested?.Invoke(name);
                yield return history;
            }
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

    /// <summary>vim's {count}G / {count}gg: selects the commit at that 1-based position.</summary>
    public void SelectPosition(int position) {
        EnsureRows();
        if (_rows.Length == 0) return;
        var currentItem = _keyboardSelection ?? SelectedItem;
        var current = currentItem == null ? -1 : Array.IndexOf(_rows, currentItem);
        MoveSelection(Math.Clamp(position - 1, 0, _rows.Length - 1) - current);
    }

    /// <summary>vim's zt / zz / zb: scrolls so the selected commit sits at the top, centre or bottom of the viewport.</summary>
    public void ScrollSelectionTo(GitKay.Core.Vim.VimScroll position) {
        EnsureRows();
        if (_scrollViewer == null || (_keyboardSelection ?? SelectedItem) is not { } item || Array.IndexOf(_rows, item) is var index && index < 0) return;
        var viewport = _scrollViewer.Viewport.Height;
        var top = index * RowHeight;
        var offset = position switch {
            GitKay.Core.Vim.VimScroll.Top => top,
            GitKay.Core.Vim.VimScroll.Bottom => top + RowHeight - viewport,
            _ => top - (viewport - RowHeight) / 2,
        };
        _scrollViewer.Offset = _scrollViewer.Offset.WithY(Math.Clamp(offset, 0, Math.Max(0, _rows.Length * RowHeight - viewport)));
    }

    /// <summary>The commits shown, in order (only matches when the list is filtered).</summary>
    public IReadOnlyList<CommitProjection> VisibleCommits {
        get {
            EnsureRows();
            return _rows;
        }
    }

    /// <summary>The commit keys act on: a keyboard move that hasn't settled yet, or the selection.</summary>
    public CommitProjection? FocusedCommit => _keyboardSelection ?? SelectedItem;

    public void FocusCommit(CommitProjection commit) {
        EnsureRows();
        var target = Array.IndexOf(_rows, commit);
        if (target < 0) return;
        // MoveSelection counts from the focused row, or from before the first row when none is focused.
        var current = FocusedCommit is { } focused ? Array.IndexOf(_rows, focused) : -1;
        MoveSelection(target - current);
    }

    public double ScrollOffset {
        get => _scrollViewer?.Offset.Y ?? 0;
        set { if (_scrollViewer != null) _scrollViewer.Offset = _scrollViewer.Offset.WithY(value); }
    }

    // ----- vim keys: the commit list moves rows; commit-level actions go to the window. -----

    public IVimCommands? VimCommands { get; set; }

    GitKay.Core.Vim.VimPane GitKay.Core.Vim.IVimHost.Pane => GitKay.Core.Vim.VimPane.Commits;
    string GitKay.Core.Vim.IVimHost.LineText => null!;
    int GitKay.Core.Vim.IVimHost.Caret => 0;
    string GitKay.Core.Vim.IVimHost.OtherSideText => null!;
    int GitKay.Core.Vim.IVimHost.Side => 0;
    bool GitKay.Core.Vim.IVimHost.HasSelection => false;
    int GitKay.Core.Vim.IVimHost.HalfPageRows => Math.Max(1, ViewportRowCount / 2);
    void GitKay.Core.Vim.IVimHost.SetCaret(int column) { }
    void GitKay.Core.Vim.IVimHost.SwitchSide(int column) { }
    void GitKay.Core.Vim.IVimHost.MoveRows(int delta) => MoveSelection(delta);
    void GitKay.Core.Vim.IVimHost.MoveToEdge(bool last) => MoveSelection(last ? int.MaxValue / 2 : int.MinValue / 2);
    void GitKay.Core.Vim.IVimHost.GoToPosition(int position) => SelectPosition(position);
    void GitKay.Core.Vim.IVimHost.MoveToHunk(int direction) { }
    void GitKay.Core.Vim.IVimHost.ScrollFocus(GitKay.Core.Vim.VimScroll position) => ScrollSelectionTo(position);
    int GitKay.Core.Vim.IVimHost.PageRows => Math.Max(1, ViewportRowCount - 2);

    void GitKay.Core.Vim.IVimHost.FocusScreenRow(GitKay.Core.Vim.VimScreenRow row, int offset) {
        EnsureRows();
        if (_scrollViewer == null || _rows.Length == 0) return;
        var (first, last) = VisibleRows(_scrollViewer.Offset.Y);
        var target = row switch {
            GitKay.Core.Vim.VimScreenRow.Top => Math.Min(last, first + offset),
            GitKay.Core.Vim.VimScreenRow.Bottom => Math.Max(first, last - offset),
            _ => (first + last) / 2,
        };
        SelectPosition(target + 1);
    }

    void GitKay.Core.Vim.IVimHost.ScrollRows(int delta) {
        EnsureRows();
        if (_scrollViewer == null || _rows.Length == 0) return;
        var maximum = Math.Max(0, _rows.Length * RowHeight - _scrollViewer.Viewport.Height);
        var top = Math.Floor(_scrollViewer.Offset.Y / RowHeight) + delta;
        var offset = Math.Clamp(top * RowHeight, 0, maximum);
        _scrollViewer.Offset = _scrollViewer.Offset.WithY(offset);
        // Like vim, the selection stays put until scrolling would take it off screen.
        var (first, last) = VisibleRows(offset);
        var current = (_keyboardSelection ?? SelectedItem) is { } item ? Array.IndexOf(_rows, item) : -1;
        if (current >= 0 && (current < first || current > last)) SelectPosition((current < first ? first : last) + 1);
    }

    /// <summary>The first and last rows wholly inside the viewport at a scroll offset.</summary>
    private (int First, int Last) VisibleRows(double offset) {
        var first = (int)Math.Ceiling(offset / RowHeight - 0.01);
        var last = (int)Math.Floor((offset + _scrollViewer!.Viewport.Height) / RowHeight + 0.01) - 1;
        first = Math.Clamp(first, 0, _rows.Length - 1);
        return (first, Math.Clamp(last, first, _rows.Length - 1));
    }
    void GitKay.Core.Vim.IVimHost.ToggleVisual(bool linewise) { }
    bool GitKay.Core.Vim.IVimHost.CancelSelection() => false;
    void GitKay.Core.Vim.IVimHost.CopySelection() { }
    void GitKay.Core.Vim.IVimHost.CopyRange(int from, int until, bool wholeLine) { }
    void GitKay.Core.Vim.IVimHost.CopyCommitReference(bool subject) => VimCommands?.CopyCommitReference(subject);
    void GitKay.Core.Vim.IVimHost.FindWord(string word, bool forward) { }
    void GitKay.Core.Vim.IVimHost.FindNext(bool forward) => VimCommands?.FindNext(GitKay.Core.Vim.VimPane.Commits, forward);
    void GitKay.Core.Vim.IVimHost.GoToParent(int index) => VimCommands?.GoToParent(index);
    void GitKay.Core.Vim.IVimHost.GoToChild() => VimCommands?.GoToChild();
    void GitKay.Core.Vim.IVimHost.PaneCommand(GitKay.Core.Vim.VimPaneCommand command) => VimCommands?.PaneCommand(command);
    void GitKay.Core.Vim.IVimHost.OpenSearch(bool forward) => VimCommands?.OpenSearch(GitKay.Core.Vim.VimPane.Commits, forward);

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

    /// <summary>A branch or tag badge requested a comparison; chooseBase is true for the ad-hoc “Diff from…” action.</summary>
    public event Action<string, bool>? RevisionComparisonRequested;

    /// <summary>Supplies the configured comparison base for explicit context-menu wording.</summary>
    public Func<string>? RevisionComparisonBaseRequested;

    private CommitRefProjection? BadgeAt(Point point) {
        if (_filtered || SelectedItem is not { } commit) return null;
        var row = (int)(point.Y / RowHeight);
        EnsureRows();
        if (row < 0 || row >= _rows.Length || !ReferenceEquals(_rows[row], commit)) return null;
        var x = EffectiveWidth(GraphWidth, 48) + 5;
        foreach (var badge in commit.RefBadges) {
            var width = Layout(badge.Text, 11, Brushes.Transparent, TextTypeface).Width + (badge.Kind == CommitRefKind.Tag ? 19 : 16);
            if (point.X >= x && point.X < x + width) return badge;
            x += width;
        }
        return null;
    }

    /// <summary>Text the user asked to copy (a branch or tag name).</summary>
    public event Action<string>? CopyRequested;

    /// <summary>The commit row's right-click menu. Internal so tests can read what it offers for a given row.</summary>
    internal ContextMenu BuildContextMenu() {
        MenuItem Item(string title, Func<CommitProjection, System.Windows.Input.ICommand> command) {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => { var selected = SelectedItem; if (selected != null) command(selected).Execute(null); };
            return item;
        }
        var menu = new ContextMenu();
        // The uncommitted changes row has no commit to act on; staging and committing belong to the commit window.
        if (SelectedItem is { IsWorkingTree: true }) {
            var open = new MenuItem { Header = "Open commit window…", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift) };
            open.Click += (_, _) => Dispatcher.UIThread.Post(() => CommitWindowRequested?.Invoke(), DispatcherPriority.Background);
            menu.Items.Add(open);
            return menu;
        }
        if (SelectedItem is { } selected) {
            if (BadgeAt(_lastContextPoint) is { Kind: CommitRefKind.Branch or CommitRefKind.Remote or CommitRefKind.Tag } badge) {
                var target = badge.Text;
                var comparisonBase = RevisionComparisonBaseRequested?.Invoke();
                var configuredFrom = string.IsNullOrWhiteSpace(comparisonBase) ? "configured base" : comparisonBase;
                var configured = new MenuItem { Header = $"Diff from {configuredFrom} to {target}" };
                configured.Click += (_, _) => RevisionComparisonRequested?.Invoke(target, false);
                var choose = new MenuItem { Header = $"Diff from… to {target}" };
                choose.Click += (_, _) => RevisionComparisonRequested?.Invoke(target, true);
                menu.Items.Add(configured);
                menu.Items.Add(choose);
                menu.Items.Add(new Separator());
            }
            foreach (var filter in BuildFilterItems(selected)) menu.Items.Add(filter);
            menu.Items.Add(new Separator());

            // The same two the palette and y / Y offer, where a right-click already asks about this commit.
            var copyHash = new MenuItem { Header = "Copy commit hash", InputGesture = new KeyGesture(Key.Y) };
            ToolTip.SetTip(copyHash, selected.FullHash);
            copyHash.Click += (_, _) => CopyRequested?.Invoke(selected.FullHash);
            menu.Items.Add(copyHash);

            var copySubject = new MenuItem { Header = "Copy commit subject", InputGesture = new KeyGesture(Key.Y, KeyModifiers.Shift) };
            ToolTip.SetTip(copySubject, selected.Subject);
            copySubject.Click += (_, _) => CopyRequested?.Invoke(selected.Subject);
            menu.Items.Add(copySubject);
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
        // Amending rewrites the last commit, so it's offered only on the commit HEAD is at.
        if (SelectedItem is { IsHead: true }) {
            var amend = new MenuItem { Header = "Amend this commit…" };
            ToolTip.SetTip(amend, "Opens the commit window with the last commit's files and message");
            amend.Click += (_, _) => Dispatcher.UIThread.Post(() => AmendRequested?.Invoke(), DispatcherPriority.Background);
            menu.Items.Add(amend);
            menu.Items.Add(new Separator());
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
