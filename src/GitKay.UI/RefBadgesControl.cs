using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GitKay.UI;

public sealed class RefBadgesControl : Control {
    public static readonly StyledProperty<IReadOnlyList<CommitRefProjection>> BadgesProperty =
        AvaloniaProperty.Register<RefBadgesControl, IReadOnlyList<CommitRefProjection>>(nameof(Badges));

    public IReadOnlyList<CommitRefProjection> Badges {
        get => GetValue(BadgesProperty);
        set => SetValue(BadgesProperty, value);
    }

    static RefBadgesControl() {
        AffectsRender<RefBadgesControl>(BadgesProperty);
        AffectsMeasure<RefBadgesControl>(BadgesProperty);
    }

    protected override Size MeasureOverride(Size availableSize) {
        if (Badges == null || Badges.Count == 0) return new Size(0, 0);

        var typeface = new Typeface(FontFamily.Default);
        var fontSize = 11.0;
        var totalWidth = 0.0;
        var maxHeight = 0.0;

        foreach (var badge in Badges) {
            var ft = new FormattedText(badge.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black);
            totalWidth += ft.Width + 12; // 6px padding on each side
            maxHeight = Math.Max(maxHeight, ft.Height + 4);
            totalWidth += 4; // Spacing
        }

        return new Size(totalWidth, maxHeight);
    }

    public override void Render(DrawingContext context) {
        if (Badges == null || Badges.Count == 0) return;

        var typeface = new Typeface(FontFamily.Default);
        var fontSize = 11.0;
        var x = 0.0;

        foreach (var badge in Badges) {
            var ft = new FormattedText(badge.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, badge.Foreground);
            var badgeWidth = ft.Width + 12;
            var badgeHeight = ft.Height + 4;
            var rect = new Rect(x, (Bounds.Height - badgeHeight) / 2, badgeWidth, badgeHeight);

            context.DrawRectangle(badge.Background, null, rect, 3, 3);
            context.DrawText(ft, new Point(x + 6, (Bounds.Height - ft.Height) / 2));

            x += badgeWidth + 4;
        }
    }
}
