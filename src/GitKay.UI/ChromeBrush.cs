using System;
using Avalonia.Controls;
using Avalonia.Media;

namespace GitKay.UI;

/// <summary>
/// The fill behind the window's chrome — the toolbar, the history column headers, and the header bar above each of
/// the diff and file panes. Chrome drawn in the content's own background blends into it, so the default is the
/// theme's surface tone, a step up from the content the way the file list reads against the window.
/// </summary>
internal static class ChromeBrush {
    /// <summary>How much of a chosen colour is laid over the window's background. Enough to read as chrome, not enough to fight the text on it.</summary>
    private const byte TintAlpha = 0x2E;

    /// <summary>The brush the chrome should use, or null when the theme's brushes aren't reachable yet.</summary>
    public static IBrush? Resolve(Control host, GitKay.Core.ChromeBackground background, GitKay.Core.PaneEffectColor color) {
        IBrush? themed(string key) => host.TryFindResource(key, host.ActualThemeVariant, out var found) ? found as IBrush : null;

        if (background.IsTransparentChrome) return themed("GitKayWindowBrush");
        if (background.IsSurfaceChrome) return themed("GitKaySurfaceBrush");

        var tint = GitKay.Core.PaneEffectColorModule.hex(color) is { } hex && Color.TryParse(hex.Value, out var chosen)
            ? chosen
            : themed("GitKayAccentBrush") is ISolidColorBrush accent ? accent.Color : Color.FromRgb(0x58, 0xA6, 0xFF);

        // Over the window's own background rather than on its own: a translucent bar would let content scroll through it.
        var under = themed("GitKayWindowBrush") is ISolidColorBrush window ? window.Color : Colors.Black;
        var blended = GitKay.Core.Presentation.Colour.blend(under.R, under.G, under.B, tint.R, tint.G, tint.B, TintAlpha);
        return new SolidColorBrush(Color.FromRgb(blended.Item1, blended.Item2, blended.Item3)).ToImmutable();
    }
}
