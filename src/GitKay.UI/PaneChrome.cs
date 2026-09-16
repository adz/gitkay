using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    private double _intensity = GitKay.Core.PaneHoverIntensityModule.scale(GitKay.Core.SettingsModule.defaults.PaneHoverIntensity);
    private bool _border = GitKay.Core.SettingsModule.defaults.PaneBorder;
    private BoxShadows _hoverShadow;

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
        // The pane's parts each carry the gap as a margin, so there is a dead strip between them (a pane header and
        // its content, say). Asking the parts whether they are hovered makes the effect blink off as the pointer
        // crosses that strip, so hover is measured against the effect border instead: it covers the whole pane.
        effect.AttachedToVisualTree += (_, _) => Watch(effect);
        if (TopLevel.GetTopLevel(effect) is not null) Watch(effect);
        ApplyPane(pane);
    }

    private readonly List<Control> _separators = new();

    /// <summary>
    /// A hairline drawn between two panes. With pane borders on, the panes draw their own edges, so the line between
    /// them would read as a third: it is hidden instead. The splitter itself still takes the pointer.
    /// </summary>
    public void AddSeparator(Control line) {
        _separators.Add(line);
        ApplySeparators();
    }

    private void ApplySeparators() {
        foreach (var line in _separators) line.Opacity = _border ? 0 : 1;
    }

    /// <summary>Re-reads the settings; call when the pane gap, hover effect or its colour changes.</summary>
    public void Update(double gap, GitKay.Core.PaneHoverEffect effect, GitKay.Core.PaneHoverColor color, GitKay.Core.PaneHoverIntensity intensity, bool border) {
        _gap = Math.Clamp(gap, 0, 8);
        _effect = effect;
        _color = color;
        _intensity = GitKay.Core.PaneHoverIntensityModule.scale(intensity);
        _border = border;
        foreach (var pane in _panes) ApplyPane(pane);
        ApplySeparators();
    }

    private readonly HashSet<TopLevel> _watched = new();
    private PointerPoint? _pointer;

    /// <summary>Follows the pointer across the window, so a pane knows it is hovered even between its parts.</summary>
    private void Watch(Border effect) {
        if (TopLevel.GetTopLevel(effect) is not { } top || !_watched.Add(top)) return;
        top.AddHandler(InputElement.PointerMovedEvent, (_, e) => {
            _pointer = e.GetCurrentPoint(top);
            foreach (var pane in _panes) ApplyHover(pane);
        }, RoutingStrategies.Tunnel);
        top.AddHandler(InputElement.PointerExitedEvent, (_, _) => {
            _pointer = null;
            foreach (var pane in _panes) ApplyHover(pane);
        }, RoutingStrategies.Tunnel);
    }

    private bool IsHovered(Pane pane) {
        if (_pointer is not { } pointer || TopLevel.GetTopLevel(pane.Effect) is not { } top) return pane.Parts.Any(part => part.IsPointerOver);
        if (!pane.Effect.IsVisible || pane.Effect.Bounds.Width <= 0) return false;
        var position = top.TranslatePoint(pointer.Position, pane.Effect);
        return position is { } point && new Rect(pane.Effect.Bounds.Size).Contains(point);
    }

    /// <summary>The chosen colour, or the theme's accent when following it.</summary>
    private Color EffectColor(Border effect) {
        if (GitKay.Core.PaneHoverColorModule.hex(_color) is { } hex && Color.TryParse(hex.Value, out var chosen)) return chosen;
        return effect.FindResource("GitKayAccentBrush") is ISolidColorBrush accent ? accent.Color : Color.FromRgb(0x58, 0xA6, 0xFF);
    }

    /// <summary>The colour at an alpha scaled by the chosen intensity.</summary>
    private Color WithAlpha(Color color, byte alpha) => Color.FromArgb((byte)Math.Round(alpha * _intensity), color.R, color.G, color.B);

    private void ApplyPane(Pane pane) {
        var color = EffectColor(pane.Effect);
        var isDark = pane.Effect.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
                     || (pane.Effect.FindResource("GitKayWindowBrush") is ISolidColorBrush window && window.Color.R + window.Color.G + window.Color.B < 3 * 128);

        // The outline stays readable at low intensities, so it fades more gently than the halo.
        var outline = (byte)Math.Round((_effect.IsHoverGlow ? 0xD0 : 0xCC) * Math.Sqrt(_intensity));
        _hoverOutline = new SolidColorBrush(Color.FromArgb(outline, color.R, color.G, color.B));
        _restOutline = pane.Effect.FindResource("GitKayBorderBrush") as IBrush ?? Brushes.Gray;
        pane.Effect.Margin = new Thickness(Math.Max(0, _gap - 1));
        _hoverShadow = _effect switch {
            // A halo in the gap plus an inset glow along the pane's edge, so it glows rather than just outlines.
            { IsHoverGlow: true } => new BoxShadows(
                new BoxShadow { Blur = 11, Spread = 2, Color = WithAlpha(color, 0xB0) },
                [new BoxShadow { Blur = 26, Spread = 5, Color = WithAlpha(color, 0x4D) },
                 new BoxShadow { Blur = 16, Color = WithAlpha(color, 0x77), IsInset = true },
                 new BoxShadow { Blur = 5, Color = WithAlpha(color, 0x99), IsInset = true }]),
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

    private IBrush _hoverOutline = Brushes.SteelBlue;
    private IBrush _restOutline = Brushes.Gray;

    private void ApplyHover(Pane pane) {
        var hovered = IsHovered(pane) && !_effect.IsNoHoverEffect;
        // The outline belongs to the border setting alone. Highlight is the effect that colours it; glow and shadow
        // speak with light, so they leave the frame as the border setting drew it (or absent).
        var outlined = _border || (hovered && _effect.IsHoverHighlight);
        pane.Effect.BorderThickness = new Thickness(outlined ? 1 : 0);
        pane.Effect.BorderBrush = hovered && _effect.IsHoverHighlight ? _hoverOutline : _restOutline;
        pane.Effect.BoxShadow = hovered ? _hoverShadow : default;
        pane.Effect.Opacity = hovered || _border ? 1 : 0;
        var margin = new Thickness(_gap);
        foreach (var part in pane.Parts)
            if (part.Margin != margin) part.Margin = margin;
    }
}
