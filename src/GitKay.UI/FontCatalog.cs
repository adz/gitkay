using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Avalonia.Media;

namespace GitKay.UI;

/// <summary>One installed font family: its name, and the family itself so a list can show it in its own typeface.</summary>
public sealed class InstalledFont(string name, bool isMonospaced) {
    public string Name { get; } = name;
    public bool IsMonospaced { get; } = isMonospaced;
    public FontFamily Family { get; } = new(name);
    public override string ToString() => Name;
}

/// <summary>
/// The fonts installed on this machine, read once. Which of them are monospaced is measured rather than guessed from
/// the name: a family is monospaced when its narrowest and widest common glyphs are the same width.
/// </summary>
public static class FontCatalog {
    private static IReadOnlyList<InstalledFont>? _all;

    /// <summary>Every installed family, by name. Empty when there is no platform font manager (unit tests).</summary>
    public static IReadOnlyList<InstalledFont> All => _all ??= Read();

    /// <summary>The families whose glyphs all take the same width, for the diff and other code text.</summary>
    public static IReadOnlyList<InstalledFont> Monospaced => All.Where(font => font.IsMonospaced).ToArray();

    private static IReadOnlyList<InstalledFont> Read() {
        try {
            return FontManager.Current.SystemFonts
                .Select(family => family.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .Select(name => new InstalledFont(name, IsMonospaced(name)))
                .ToArray();
        }
        catch (Exception exception) {
            System.Diagnostics.Trace.WriteLine($"[fonts] could not list installed fonts: {exception.Message}");
            return Array.Empty<InstalledFont>();
        }
    }

    private static bool IsMonospaced(string name) {
        try {
            if (!FontManager.Current.TryGetGlyphTypeface(new Typeface(name), out _)) return false;
            var typeface = new Typeface(name);
            double Width(string text) =>
                new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 32, Brushes.Black).Width;

            var narrow = Width("iiii");
            // Within a rounding error of each other: in a proportional family "W" is several times the width of "i".
            return narrow > 0 && Math.Abs(Width("WWWW") - narrow) < 0.5 && Math.Abs(Width("mmmm") - narrow) < 0.5;
        }
        catch (Exception) {
            return false;
        }
    }

}
