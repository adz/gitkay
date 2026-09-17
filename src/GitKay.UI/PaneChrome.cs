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
/// The space around each pane and what the focused pane does in it. A pane is focused by clicking into it or, when
/// "hover focuses" is on, by pointing at it; the chrome follows that focus rather than the pointer, so nothing lights
/// up while a pane merely has the pointer passing over it. The gap is a margin on the pane's own elements and the
/// effect is drawn by a border above them, so nothing moves when the focus arrives.
/// </summary>
internal sealed class PaneChrome {
    private sealed record Pane(Border Effect, IReadOnlyList<Control> Parts);

    /// <summary>What a pane's chrome should say about it. Dimming the unfocused panes is the window's own business.</summary>
    internal readonly record struct Settings(
        double Gap,
        bool DimUnfocused,
        bool FocusHighlight,
        GitKay.Core.PaneFocusEffect Effect,
        GitKay.Core.PaneEffectColor EffectColor,
        GitKay.Core.PaneEffectIntensity Intensity,
        bool Border,
        GitKay.Core.PaneBorderStyle BorderStyle,
        GitKay.Core.PaneEffectColor BorderColor,
        double BorderThickness,
        bool SplitterLinesHidden);

    private readonly List<Pane> _panes = new();
    private readonly Dictionary<string, Pane> _byKey = new(StringComparer.Ordinal);
    private string? _focused;
    private Settings _settings = Defaults;
    private double _intensity = GitKay.Core.PaneEffectIntensityModule.scale(GitKay.Core.SettingsModule.defaults.PaneEffectIntensity);
    private BoxShadows _focusShadow;

    private static Settings Defaults {
        get {
            var d = GitKay.Core.SettingsModule.defaults;
            return new Settings(d.PaneGap, d.PaneDimUnfocused, d.PaneFocusHighlight, d.PaneFocusEffect, d.PaneEffectColor,
                d.PaneEffectIntensity, d.PaneBorder, d.PaneBorderStyle, d.PaneBorderColor, d.PaneBorderThickness, d.SplitterLinesHidden);
        }
    }

    /// <summary>Adds a pane under a key the window uses to say which one has focus.</summary>
    public void Add(string key, Border effect, params Control[] parts) {
        var pane = new Pane(effect, parts);
        _panes.Add(pane);
        _byKey[key] = pane;
        effect.IsHitTestVisible = false;
        // Above the pane, so the glow reads on its edge as well as in the gap; it never takes pointer input.
        effect.ZIndex = 20;
        effect.CornerRadius = new CornerRadius(6);
        effect.Transitions = [new Avalonia.Animation.DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(140) }];
        effect.Opacity = 0;
        // Theme brushes can only be found once the pane is in the tree, and they change with the theme: look again
        // whenever either happens, or every style resolves to the same fallback colour.
        effect.AttachedToVisualTree += (_, _) => ApplyPane(pane);
        effect.ActualThemeVariantChanged += (_, _) => ApplyPane(pane);
        ApplyPane(pane);
    }

    /// <summary>Says which pane holds the keys; null when none does. The window decides, from focus or hover.</summary>
    public void SetFocused(string? key) {
        if (string.Equals(_focused, key, StringComparison.Ordinal)) return;
        _focused = key;
        foreach (var pane in _panes) ApplyFocus(pane);
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
        foreach (var line in _separators) line.Opacity = _settings.SplitterLinesHidden ? 0 : 1;
    }

    /// <summary>Re-reads the settings; call when anything in the Panes section of settings changes.</summary>
    public void Update(Settings settings) {
        _settings = settings with { Gap = Math.Clamp(settings.Gap, 0, 8) };
        _intensity = GitKay.Core.PaneEffectIntensityModule.scale(settings.Intensity);
        foreach (var pane in _panes) ApplyPane(pane);
        ApplySeparators();
    }

    /// <summary>A theme brush by name, or null when it isn't there (a pane not yet in the tree, or a bare test theme).</summary>
    private static IBrush? Brush(Border effect, string key) =>
        effect.TryFindResource(key, effect.ActualThemeVariant, out var found) ? found as IBrush : null;

    /// <summary>One of the chosen colours, or the theme's accent when following it.</summary>
    private static Color Resolve(Border effect, GitKay.Core.PaneEffectColor color) {
        if (GitKay.Core.PaneEffectColorModule.hex(color) is { } hex && Color.TryParse(hex.Value, out var chosen)) return chosen;
        return Brush(effect, "GitKayAccentBrush") is ISolidColorBrush accent ? accent.Color : Color.FromRgb(0x58, 0xA6, 0xFF);
    }

    /// <summary>The colour at an alpha scaled by the chosen intensity.</summary>
    private Color WithAlpha(Color color, byte alpha) => Color.FromArgb((byte)Math.Round(alpha * _intensity), color.R, color.G, color.B);

    private void ApplyPane(Pane pane) {
        var color = Resolve(pane.Effect, _settings.EffectColor);
        var isDark = pane.Effect.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
                     || (Brush(pane.Effect, "GitKayWindowBrush") is ISolidColorBrush window && window.Color.R + window.Color.G + window.Color.B < 3 * 128);

        // The highlight stays readable at low intensities, so it fades more gently than the halo.
        var outline = (byte)Math.Round(0xD0 * Math.Sqrt(_intensity));
        _focusOutline = new SolidColorBrush(Color.FromArgb(outline, color.R, color.G, color.B));
        _restOutline = _settings.BorderStyle switch {
            { IsCustomBorder: true } => new SolidColorBrush(Resolve(pane.Effect, _settings.BorderColor)),
            { IsNormalBorder: true } => Brush(pane.Effect, "GitKayBorderBrush") ?? Brushes.Gray,
            _ => Brush(pane.Effect, "GitKayPaneSubtleBorderBrush") ?? Brush(pane.Effect, "GitKayHairlineBrush") ?? Brushes.DimGray,
        };
        pane.Effect.Margin = new Thickness(Math.Max(0, _settings.Gap - 1));
        _focusShadow = _settings.Effect switch {
            // A halo in the gap plus an inset glow along the pane's edge, so it glows rather than just outlines.
            { IsPaneGlow: true } => new BoxShadows(
                new BoxShadow { Blur = 11, Spread = 2, Color = WithAlpha(color, 0xB0) },
                [new BoxShadow { Blur = 26, Spread = 5, Color = WithAlpha(color, 0x4D) },
                 new BoxShadow { Blur = 16, Color = WithAlpha(color, 0x77), IsInset = true },
                 new BoxShadow { Blur = 5, Color = WithAlpha(color, 0x99), IsInset = true }]),
            // A black shadow vanishes on a dark background: there the pane is lifted with light instead.
            { IsPaneShadow: true } => isDark
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
        ApplyFocus(pane);
    }

    private IBrush _focusOutline = Brushes.SteelBlue;
    private IBrush _restOutline = Brushes.Gray;

    private void ApplyFocus(Pane pane) {
        // Everything the focused pane does is one switch away from everything else: the highlight brightens its edge,
        // the effect lights the space around it, and the border is drawn whether a pane is focused or not.
        var focused = _byKey.TryGetValue(_focused ?? "", out var it) && ReferenceEquals(it, pane);
        var highlighted = focused && _settings.FocusHighlight;
        var borderThickness = _settings.BorderStyle.IsCustomBorder ? _settings.BorderThickness : 1;
        pane.Effect.BorderThickness = new Thickness(_settings.Border ? borderThickness : highlighted ? 1 : 0);
        pane.Effect.BorderBrush = highlighted ? _focusOutline : _restOutline;
        pane.Effect.BoxShadow = focused ? _focusShadow : default;
        pane.Effect.Opacity = _settings.Border || highlighted || (focused && !_settings.Effect.IsNoPaneEffect) ? 1 : 0;
        var margin = new Thickness(_settings.Gap);
        foreach (var part in pane.Parts)
            if (part.Margin != margin) part.Margin = margin;
    }
}
