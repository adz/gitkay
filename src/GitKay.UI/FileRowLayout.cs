using System;
using Avalonia;
using Avalonia.Controls;

namespace GitKay.UI;

/// <summary>
/// A changed-file row: a name that takes what is left, and trailing items pinned to the right. The last child is
/// the first to go — when the name would have to be trimmed to fit it, it is dropped instead, so a long path wins
/// the space back from the change graph rather than being cut short for it.
/// </summary>
public sealed class FileRowLayout : Panel {
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<FileRowLayout, double>(nameof(Spacing), 7);

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    static FileRowLayout() => AffectsMeasure<FileRowLayout>(SpacingProperty);

    /// <summary>
    /// The collapsible child's width, remembered from when it was last shown. Measuring a hidden child reports
    /// zero, and deciding on that would make it fit, reappear, and stop fitting again on every pass.
    /// </summary>
    private double _collapsibleWidth;

    protected override Size MeasureOverride(Size availableSize) {
        if (Children.Count == 0) return default;
        var name = Children[0];
        var collapsible = Children.Count > 1 ? Children[^1] : null;

        var fixedWidth = 0.0;
        var height = 0.0;
        for (var i = 1; i < Children.Count - (collapsible == null ? 0 : 1); i++) {
            var child = Children[i];
            child.Measure(Size.Infinity);
            if (!child.IsVisible) continue;
            fixedWidth += child.DesiredSize.Width + Spacing;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        if (collapsible != null && collapsible.IsVisible) {
            collapsible.Measure(Size.Infinity);
            if (collapsible.DesiredSize.Width > 0) _collapsibleWidth = collapsible.DesiredSize.Width;
        }

        name.Measure(Size.Infinity);
        var nameWidth = name.DesiredSize.Width;
        height = Math.Max(height, name.DesiredSize.Height);

        var collapsibleWidth = _collapsibleWidth > 0 ? _collapsibleWidth + Spacing : 0;
        var showCollapsible = collapsible != null
            && GitKay.Core.Presentation.FileRow.showsCollapsible(
                new GitKay.Core.Presentation.RowWidths(availableSize.Width, nameWidth, fixedWidth, collapsibleWidth),
                collapsible.IsVisible);
        if (collapsible != null && collapsible.IsVisible != showCollapsible) {
            collapsible.IsVisible = showCollapsible;
            if (showCollapsible) collapsible.Measure(Size.Infinity);
        }

        var trailing = fixedWidth + (showCollapsible ? collapsibleWidth : 0);
        if (!double.IsInfinity(availableSize.Width)) {
            // The name takes what the trailing items leave; it trims itself when that is less than it wanted.
            name.Measure(new Size(Math.Max(0, availableSize.Width - trailing), availableSize.Height));
            height = Math.Max(height, name.DesiredSize.Height);
            return new Size(availableSize.Width, height);
        }

        return new Size(nameWidth + trailing, height);
    }

    protected override Size ArrangeOverride(Size finalSize) {
        if (Children.Count == 0) return finalSize;

        var right = finalSize.Width;
        for (var i = Children.Count - 1; i >= 1; i--) {
            var child = Children[i];
            if (!child.IsVisible) continue;
            var width = child.DesiredSize.Width;
            child.Arrange(new Rect(Math.Max(0, right - width), 0, width, finalSize.Height));
            right -= width + Spacing;
        }

        Children[0].Arrange(new Rect(0, 0, Math.Max(0, right), finalSize.Height));
        return finalSize;
    }
}
