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
public sealed class CommitSurfaceControl : Control
{
    private const double RowHeight = 20;
    private const double LaneWidth = 9;
    private static readonly Typeface TextTypeface = new("Helvetica,Arial,Liberation Sans,Noto Sans,sans-serif");
    private static readonly Typeface MonoTypeface = new("Courier,Courier New,Liberation Mono,Monospace");
    private static readonly IBrush SubjectBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220)).ToImmutable();
    private static readonly IBrush MetaBrush = new SolidColorBrush(Color.FromRgb(170, 170, 170)).ToImmutable();
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.FromRgb(136, 136, 136)).ToImmutable();
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromRgb(51, 51, 51)).ToImmutable();
    private static readonly IBrush[] LaneBrushes =
    [
        Brushes.Red, Brushes.Green, Brushes.Blue, Brushes.Orange, Brushes.Purple,
        Brushes.Cyan, Brushes.Magenta, Brushes.Yellow, Brushes.LightGreen, Brushes.LightBlue
    ];
    private static readonly IPen[] LanePens = LaneBrushes.Select(brush => new Pen(brush, 1.5).ToImmutable()).ToArray();

    public static readonly StyledProperty<IEnumerable<CommitProjection>?> ItemsSourceProperty =
        AvaloniaProperty.Register<CommitSurfaceControl, IEnumerable<CommitProjection>?>(nameof(ItemsSource));
    public static readonly StyledProperty<CommitProjection?> SelectedItemProperty =
        AvaloniaProperty.Register<CommitSurfaceControl, CommitProjection?>(nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> GraphWidthProperty = AvaloniaProperty.Register<CommitSurfaceControl, double>(nameof(GraphWidth), 48);
    public static readonly StyledProperty<double> SubjectWidthProperty = AvaloniaProperty.Register<CommitSurfaceControl, double>(nameof(SubjectWidth), 120);
    public static readonly StyledProperty<double> HashWidthProperty = AvaloniaProperty.Register<CommitSurfaceControl, double>(nameof(HashWidth), 54);
    public static readonly StyledProperty<double> AuthorWidthProperty = AvaloniaProperty.Register<CommitSurfaceControl, double>(nameof(AuthorWidth), 110);
    public static readonly StyledProperty<double> DateWidthProperty = AvaloniaProperty.Register<CommitSurfaceControl, double>(nameof(DateWidth), 110);

    private CommitProjection[] _rows = Array.Empty<CommitProjection>();
    private INotifyCollectionChanged? _collection;
    private ScrollViewer? _scrollViewer;
    private readonly Dictionary<LayoutKey, FormattedText> _layouts = new();
    private CommitProjection? _keyboardSelection;
    private CancellationTokenSource? _keyboardSelectionCancellation;

    public IEnumerable<CommitProjection>? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public CommitProjection? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public double GraphWidth { get => GetValue(GraphWidthProperty); set => SetValue(GraphWidthProperty, value); }
    public double SubjectWidth { get => GetValue(SubjectWidthProperty); set => SetValue(SubjectWidthProperty, value); }
    public double HashWidth { get => GetValue(HashWidthProperty); set => SetValue(HashWidthProperty, value); }
    public double AuthorWidth { get => GetValue(AuthorWidthProperty); set => SetValue(AuthorWidthProperty, value); }
    public double DateWidth { get => GetValue(DateWidthProperty); set => SetValue(DateWidthProperty, value); }

    static CommitSurfaceControl()
    {
        ItemsSourceProperty.Changed.AddClassHandler<CommitSurfaceControl>((control, _) => control.Rebuild());
        SelectedItemProperty.Changed.AddClassHandler<CommitSurfaceControl>((control, _) =>
        {
            control._keyboardSelection = null;
            control.InvalidateVisual();
        });
        AffectsRender<CommitSurfaceControl>(GraphWidthProperty, SubjectWidthProperty, HashWidthProperty, AuthorWidthProperty, DateWidthProperty);
    }

    public CommitSurfaceControl()
    {
        Focusable = true;
        ContextMenu = BuildContextMenu();
        ActualThemeVariantChanged += (_, _) =>
        {
            _layouts.Clear();
            InvalidateVisual();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scrollViewer = this.FindAncestorOfType<ScrollViewer>();
        if (_scrollViewer != null) _scrollViewer.ScrollChanged += OnScrollChanged;
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_scrollViewer != null) _scrollViewer.ScrollChanged -= OnScrollChanged;
        Detach();
        _keyboardSelectionCancellation?.Cancel();
        base.OnDetachedFromVisualTree(e);
    }

    private void Rebuild()
    {
        Detach();
        _rows = ItemsSource?.ToArray() ?? Array.Empty<CommitProjection>();
        if (ItemsSource is INotifyCollectionChanged collection)
        {
            _collection = collection;
            _collection.CollectionChanged += OnCollectionChanged;
        }
        foreach (var row in _rows) row.PropertyChanged += OnRowChanged;
        _layouts.Clear();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void Detach()
    {
        if (_collection != null) _collection.CollectionChanged -= OnCollectionChanged;
        foreach (var row in _rows) row.PropertyChanged -= OnRowChanged;
        _collection = null;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();
    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) { _layouts.Clear(); InvalidateVisual(); }
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width, _rows.Length * RowHeight);

    public override void Render(DrawingContext context)
    {
        var offset = _scrollViewer?.Offset.Y ?? 0;
        var viewport = _scrollViewer?.Viewport.Height ?? Bounds.Height;
        var first = Math.Clamp((int)(offset / RowHeight), 0, _rows.Length);
        var last = Math.Min(_rows.Length, first + (int)Math.Ceiling(viewport / RowHeight) + 2);
        for (var index = first; index < last; index++) DrawRow(context, _rows[index], index * RowHeight);
    }

    private void DrawRow(DrawingContext context, CommitProjection commit, double y)
    {
        var selectionBrush = ThemeBrush("GitKaySelectionBrush", SelectionBrush);
        var subjectBrush = ThemeBrush("GitKayTextBrush", SubjectBrush);
        var metaBrush = ThemeBrush("GitKaySecondaryTextBrush", MetaBrush);
        var mutedBrush = ThemeBrush("GitKayMutedTextBrush", MutedBrush);

        if (ReferenceEquals(commit, _keyboardSelection ?? SelectedItem))
            context.FillRectangle(selectionBrush, new Rect(0, y, Bounds.Width, RowHeight));
        else if (commit.RowBackground != Brushes.Transparent)
            context.FillRectangle(commit.RowBackground, new Rect(0, y, Bounds.Width, RowHeight));

        var graphWidth = EffectiveWidth(GraphWidth, 48);
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
        var badgeX = subjectX + 5;
        foreach (var badge in commit.RefBadges)
            badgeX += DrawBadge(context, badge, badgeX, y);

        using (context.PushClip(new Rect(badgeX, y, Math.Max(0, hashX - badgeX - 3), RowHeight)))
            DrawText(context, commit.Subject, badgeX, y + 2, 13, subjectBrush, TextTypeface);
        using (context.PushClip(new Rect(hashX, y, hashWidth, RowHeight)))
            DrawText(context, commit.Hash, hashX + 2, y + 3, 12, mutedBrush, MonoTypeface);
        using (context.PushClip(new Rect(authorX, y, authorWidth, RowHeight)))
            DrawText(context, commit.Author, authorX + 2, y + 3, 12, metaBrush, TextTypeface);

        using (context.PushClip(new Rect(dateX, y, dateWidth, RowHeight)))
        {
            var date = Layout(commit.Date, 12, mutedBrush, TextTypeface);
            context.DrawText(date, new Point(Math.Max(dateX + 2, dateX + dateWidth - date.Width - 4), y + 3));
        }
    }

    private IBrush ThemeBrush(string key, IBrush fallback) =>
        this.TryFindResource(key, out var value) && value is IBrush brush ? brush : fallback;

    private static double EffectiveWidth(double value, double fallback) =>
        double.IsFinite(value) && value > 1 ? value : fallback;

    private static void DrawGraph(DrawingContext context, CommitProjection commit, double y)
    {
        var centerY = y + RowHeight / 2;
        foreach (var segment in commit.Segments)
        {
            var pen = LanePens[Math.Abs(segment.Color) % LanePens.Length];
            var x = (segment.Lane + 1) * LaneWidth;
            if (segment.IsCommit)
            {
                var target = (segment.TargetLane + 1) * LaneWidth;
                context.DrawLine(pen, new Point(x, centerY), new Point(target, y + RowHeight));
            }
            else
                context.DrawLine(pen, new Point(x, y), new Point(x, y + RowHeight));
        }
        var brush = LaneBrushes[Math.Abs(commit.Lane) % LaneBrushes.Length];
        var cx = (commit.Lane + 1) * LaneWidth;
        context.DrawEllipse(brush, null, new Rect(cx - 3, centerY - 3, 6, 6));
    }

    private double DrawBadge(DrawingContext context, CommitRefProjection badge, double x, double y)
    {
        var text = Layout(badge.Text, 11, badge.Foreground, TextTypeface);
        var width = text.Width + 10;
        context.DrawRectangle(badge.Background, new Pen(badge.BorderBrush, 1), new Rect(x, y + 2, width, 16), 2, 2);
        context.DrawText(text, new Point(x + 5, y + 3));
        return width + 3;
    }

    private void DrawText(DrawingContext context, string text, double x, double y, double size, IBrush brush, Typeface typeface) =>
        context.DrawText(Layout(text, size, brush, typeface), new Point(x, y));

    private FormattedText Layout(string text, double size, IBrush brush, Typeface typeface)
    {
        var key = new LayoutKey(text, size, brush, typeface);
        if (!_layouts.TryGetValue(key, out var layout))
        {
            layout = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);
            if (_layouts.Count >= 4096) _layouts.Clear();
            _layouts[key] = layout;
        }
        return layout;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        SelectAt(e.GetPosition(this).Y);
    }

    public void SelectAt(double documentY)
    {
        if (_rows.Length == 0) return;
        var index = Math.Clamp((int)(documentY / RowHeight), 0, _rows.Length - 1);
        _keyboardSelectionCancellation?.Cancel();
        _keyboardSelection = null;
        SelectedItem = _rows[index];
    }

    public void MoveSelection(int delta)
    {
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

    private async Task CommitKeyboardSelectionAsync(CommitProjection selection, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(65, cancellation.Token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!cancellation.IsCancellationRequested && ReferenceEquals(_keyboardSelectionCancellation, cancellation))
                    SelectedItem = selection;
            }, DispatcherPriority.Input);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void ScrollIntoView(CommitProjection item)
    {
        if (_scrollViewer == null) return;
        var index = Array.IndexOf(_rows, item);
        if (index < 0) return;
        var top = index * RowHeight;
        var bottom = top + RowHeight;
        var offset = _scrollViewer.Offset;
        if (top < offset.Y) _scrollViewer.Offset = offset.WithY(top);
        else if (bottom > offset.Y + _scrollViewer.Viewport.Height) _scrollViewer.Offset = offset.WithY(Math.Max(0, bottom - _scrollViewer.Viewport.Height));
    }

    private ContextMenu BuildContextMenu()
    {
        MenuItem Item(string title, Func<CommitProjection, System.Windows.Input.ICommand> command)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => { var selected = SelectedItem; if (selected != null) command(selected).Execute(null); };
            return item;
        }
        return new ContextMenu
        {
            Items =
            {
                Item("Create Tag here...", row => row.CreateTagCommand),
                Item("Create Branch here...", row => row.CreateBranchCommand),
                new Separator(),
                Item("Cherry-pick this commit", row => row.CherryPickCommand),
                Item("Revert this commit", row => row.RevertCommand),
                new Separator(),
                Item("Reset current branch here (soft)", row => row.ResetSoftCommand),
                Item("Reset current branch here (hard)", row => row.ResetHardCommand)
            }
        };
    }

    private readonly record struct LayoutKey(string Text, double Size, IBrush Brush, Typeface Typeface);
}
