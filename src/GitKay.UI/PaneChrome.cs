using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace GitKay.UI;

/// <summary>
/// The space around each pane and what the pane under the pointer does in it. The gap is a margin on the pane's own
/// elements; the effect is drawn by a border behind them, so nothing moves when the pointer arrives (except the
/// indent effect, which insets the pane on purpose).
/// </summary>
internal sealed class PaneChrome {
    private sealed record Pane(Border Effect, IReadOnlyList<Control> Parts);

    private readonly List<Pane> _panes = new();
    private double _gap;
    private GitKay.Core.PaneHoverEffect _effect = GitKay.Core.SettingsModule.defaults.PaneHoverEffect;

    /// <summary>Adds a pane: the border that draws its effect, and the elements that make up the pane.</summary>
    public void Add(Border effect, params Control[] parts) {
        var pane = new Pane(effect, parts);
        _panes.Add(pane);
        effect.IsHitTestVisible = false;
        effect.CornerRadius = new CornerRadius(6);
        effect.Transitions = [new Avalonia.Animation.DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(120) }];
        effect.Opacity = 0;
        foreach (var part in parts)
            part.PropertyChanged += (_, e) => {
                if (e.Property == InputElement.IsPointerOverProperty) ApplyHover(pane);
            };
        ApplyPane(pane);
    }

    /// <summary>Re-reads the settings; call when the pane gap or hover effect changes.</summary>
    public void Update(double gap, GitKay.Core.PaneHoverEffect effect) {
        _gap = Math.Clamp(gap, 0, 8);
        _effect = effect;
        foreach (var pane in _panes) ApplyPane(pane);
    }

    private static bool IsHovered(Pane pane) => pane.Parts.Any(part => part.IsPointerOver);

    private void ApplyPane(Pane pane) {
        var brush = pane.Effect.FindResource("GitKayAccentBrush") as IBrush ?? Brushes.SteelBlue;
        pane.Effect.BorderThickness = new Thickness(_effect is { IsHoverGlow: true } or { IsHoverHighlight: true } ? 1 : 0);
        pane.Effect.BorderBrush = brush;
        pane.Effect.Margin = new Thickness(Math.Max(0, _gap - 1));
        pane.Effect.BoxShadow = _effect.IsHoverGlow
            ? new BoxShadows(new BoxShadow { Blur = 10, Color = Color.FromArgb(0x66, 0x58, 0xA6, 0xFF) })
            : _effect.IsHoverShadow
                ? new BoxShadows(new BoxShadow { Blur = 10, OffsetY = 2, Color = Color.FromArgb(0x66, 0, 0, 0) })
                : default;
        ApplyHover(pane);
    }

    private void ApplyHover(Pane pane) {
        var hovered = IsHovered(pane);
        pane.Effect.Opacity = hovered && !_effect.IsNoHoverEffect ? 1 : 0;
        // Only the indent effect changes the pane's own space; the others draw behind it.
        var inset = hovered && _effect.IsHoverIndent ? 1 : 0;
        var margin = new Thickness(_gap + inset);
        foreach (var part in pane.Parts)
            if (part.Margin != margin) part.Margin = margin;
    }
}
