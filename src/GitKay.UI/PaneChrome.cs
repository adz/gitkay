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
    private GitKay.Core.PaneHoverColor _color = GitKay.Core.SettingsModule.defaults.PaneHoverColor;

    /// <summary>Adds a pane: the border that draws its effect, and the elements that make up the pane.</summary>
    public void Add(Border effect, params Control[] parts) {
        var pane = new Pane(effect, parts);
        _panes.Add(pane);
        effect.IsHitTestVisible = false;
        // Above the pane, so the glow reads on its edge as well as in the gap; it never takes pointer input.
        effect.ZIndex = 20;
        effect.CornerRadius = new CornerRadius(6);
        effect.Transitions = [new Avalonia.Animation.DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(140) }];
        effect.Opacity = 0;
        foreach (var part in parts)
            part.PropertyChanged += (_, e) => {
                if (e.Property == InputElement.IsPointerOverProperty) ApplyHover(pane);
            };
        ApplyPane(pane);
    }

    /// <summary>Re-reads the settings; call when the pane gap, hover effect or its colour changes.</summary>
    public void Update(double gap, GitKay.Core.PaneHoverEffect effect, GitKay.Core.PaneHoverColor color) {
        _gap = Math.Clamp(gap, 0, 8);
        _effect = effect;
        _color = color;
        foreach (var pane in _panes) ApplyPane(pane);
    }

    private static bool IsHovered(Pane pane) => pane.Parts.Any(part => part.IsPointerOver);

    /// <summary>The chosen colour, or the theme's accent when following it.</summary>
    private Color EffectColor(Border effect) {
        if (GitKay.Core.PaneHoverColorModule.hex(_color) is { } hex && Color.TryParse(hex.Value, out var chosen)) return chosen;
        return effect.FindResource("GitKayAccentBrush") is ISolidColorBrush accent ? accent.Color : Color.FromRgb(0x58, 0xA6, 0xFF);
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private void ApplyPane(Pane pane) {
        var color = EffectColor(pane.Effect);
        var isDark = pane.Effect.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
                     || (pane.Effect.FindResource("GitKayWindowBrush") is ISolidColorBrush window && window.Color.R + window.Color.G + window.Color.B < 3 * 128);

        pane.Effect.BorderThickness = new Thickness(_effect.IsHoverGlow || _effect.IsHoverHighlight ? 1 : 0);
        pane.Effect.BorderBrush = new SolidColorBrush(WithAlpha(color, _effect.IsHoverGlow ? (byte)0xEE : (byte)0xCC));
        pane.Effect.Margin = new Thickness(Math.Max(0, _gap - 1));
        pane.Effect.BoxShadow = _effect switch {
            // A halo in the gap plus an inset glow along the pane's edge, so it glows rather than just outlines.
            { IsHoverGlow: true } => new BoxShadows(
                new BoxShadow { Blur = 12, Spread = 2, Color = WithAlpha(color, 0xCC) },
                [new BoxShadow { Blur = 30, Spread = 8, Color = WithAlpha(color, 0x66) },
                 new BoxShadow { Blur = 20, Color = WithAlpha(color, 0xBB), IsInset = true },
                 new BoxShadow { Blur = 6, Color = WithAlpha(color, 0xCC), IsInset = true }]),
            // A black shadow vanishes on a dark background: there the pane is lifted with light instead.
            { IsHoverShadow: true } => isDark
                ? new BoxShadows(
                    new BoxShadow { Blur = 18, Spread = 3, Color = WithAlpha(Colors.White, 0x66) },
                    [new BoxShadow { Blur = 12, OffsetY = 5, Color = WithAlpha(Colors.Black, 0xDD) },
                     new BoxShadow { Blur = 10, Color = WithAlpha(Colors.White, 0x33), IsInset = true }])
                : new BoxShadows(
                    new BoxShadow { Blur = 16, OffsetY = 4, Spread = 1, Color = Color.FromArgb(0x99, 0, 0, 0) },
                    [new BoxShadow { Blur = 6, OffsetY = 1, Color = Color.FromArgb(0x55, 0, 0, 0) },
                     new BoxShadow { Blur = 8, Color = Color.FromArgb(0x33, 0, 0, 0), IsInset = true }]),
            _ => default,
        };
        ApplyHover(pane);
    }

    private void ApplyHover(Pane pane) {
        pane.Effect.Opacity = IsHovered(pane) && !_effect.IsNoHoverEffect ? 1 : 0;
        var margin = new Thickness(_gap);
        foreach (var part in pane.Parts)
            if (part.Margin != margin) part.Margin = margin;
    }
}
