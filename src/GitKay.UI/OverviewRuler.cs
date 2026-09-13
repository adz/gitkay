using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace GitKay.UI;

public enum OverviewMarkKind {
    Added,
    Removed,
    SearchMatch,
    FindMatch,
}

/// <summary>A span of the document, as fractions of its total height.</summary>
public readonly record struct OverviewMark(double Top, double Height, OverviewMarkKind Kind);

/// <summary>A scrollable surface that can describe where its notable rows are.</summary>
public interface IOverviewSource {
    IReadOnlyList<OverviewMark> OverviewMarks { get; }
    ScrollViewer? OverviewScrollViewer { get; }
    event EventHandler? OverviewChanged;
}

/// <summary>
/// A thin strip beside a scrollable list showing changes and matches across the whole document, plus the
/// visible region. Clicking or dragging jumps there.
/// </summary>
public sealed class OverviewRuler : Control {
    private static readonly IBrush AddedFallback = new SolidColorBrush(Color.FromRgb(63, 185, 80)).ToImmutable();
    private static readonly IBrush RemovedFallback = new SolidColorBrush(Color.FromRgb(248, 81, 73)).ToImmutable();
    private static readonly IBrush SearchFallback = new SolidColorBrush(Color.FromRgb(88, 166, 255)).ToImmutable();
    private static readonly IBrush FindFallback = new SolidColorBrush(Color.FromRgb(210, 153, 34)).ToImmutable();
    private static readonly IBrush ViewportFallback = new SolidColorBrush(Color.FromArgb(40, 140, 150, 160)).ToImmutable();

    public static readonly StyledProperty<IOverviewSource?> SourceProperty =
        AvaloniaProperty.Register<OverviewRuler, IOverviewSource?>(nameof(Source));

    private IOverviewSource? _attached;
    private ScrollViewer? _scrollViewer;

    static OverviewRuler() {
        SourceProperty.Changed.AddClassHandler<OverviewRuler>((ruler, _) => ruler.Attach());
    }

    public IOverviewSource? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    private void Attach() {
        if (_attached != null) _attached.OverviewChanged -= OnOverviewChanged;
        _attached = Source;
        if (_attached != null) _attached.OverviewChanged += OnOverviewChanged;
        OnOverviewChanged(this, EventArgs.Empty);
    }

    private void OnOverviewChanged(object? sender, EventArgs e) {
        var scrollViewer = _attached?.OverviewScrollViewer;
        if (!ReferenceEquals(scrollViewer, _scrollViewer)) {
            if (_scrollViewer != null) _scrollViewer.ScrollChanged -= OnScrollChanged;
            _scrollViewer = scrollViewer;
            if (_scrollViewer != null) _scrollViewer.ScrollChanged += OnScrollChanged;
        }

        InvalidateVisual();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context) {
        var height = Bounds.Height;
        var width = Bounds.Width;
        if (height <= 0) return;

        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        // Deliberately quiet: a faint visible-region band, soft change marks, and slightly stronger match ticks.
        if (_scrollViewer is { } scroll && scroll.Extent.Height > scroll.Viewport.Height) {
            var top = scroll.Offset.Y / scroll.Extent.Height * height;
            var size = Math.Max(12, scroll.Viewport.Height / scroll.Extent.Height * height);
            using (context.PushOpacity(0.5))
                context.FillRectangle(Brush("GitKayHoverBrush", ViewportFallback), new Rect(0, top, width, size));
        }

        if (_attached == null) return;
        foreach (var mark in _attached.OverviewMarks) {
            var y = mark.Top * height;
            var isMatch = mark.Kind is OverviewMarkKind.SearchMatch or OverviewMarkKind.FindMatch;
            var markHeight = Math.Max(isMatch ? 2 : 1, mark.Height * height);
            var brush = mark.Kind switch {
                OverviewMarkKind.Added => Brush("GitKayAddedAccentBrush", AddedFallback),
                OverviewMarkKind.Removed => Brush("GitKayRemovedAccentBrush", RemovedFallback),
                OverviewMarkKind.SearchMatch => Brush("GitKayAccentBrush", SearchFallback),
                _ => FindFallback,
            };
            using (context.PushOpacity(isMatch ? 0.9 : 0.45))
                context.FillRectangle(brush, new Rect(0, y, isMatch ? width : Math.Max(1, width - 1), markHeight));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        ScrollTo(e.GetPosition(this).Y);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        if (ReferenceEquals(e.Pointer.Captured, this)) ScrollTo(e.GetPosition(this).Y);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
    }

    private void ScrollTo(double y) {
        if (_scrollViewer is not { } scroll || Bounds.Height <= 0) return;
        var target = y / Bounds.Height * scroll.Extent.Height - scroll.Viewport.Height / 2;
        scroll.Offset = scroll.Offset.WithY(Math.Clamp(target, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
    }

    private IBrush Brush(string key, IBrush fallback) =>
        ActualThemeVariant != Avalonia.Styling.ThemeVariant.Dark
        && this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : fallback;
}
