using System;
using System.Collections.Concurrent;
using Avalonia.Media;

namespace GitKay.UI;

/// <summary>
/// Resolves comma-separated font stacks to the first family installed under that exact name, once.
/// Avalonia resolves every member of a composite stack on each text layout, and names that the system only
/// substitutes (Arial, sans-serif, Monospace on Linux) miss its cache, costing several milliseconds per layout —
/// enough to make resizing and list rebuilds visibly slow.
/// </summary>
public static class FontStacks {
    public const string UiStack = "Inter,Segoe UI,Arial,sans-serif";
    public const string MonoStack = "SF Mono,Menlo,Consolas,Cascadia Code,Liberation Mono,Noto Sans Mono,DejaVu Sans Mono,monospace";

    private static readonly ConcurrentDictionary<string, FontFamily> Resolved = new(StringComparer.Ordinal);

    public static FontFamily Ui => Resolve(UiStack);
    public static FontFamily Mono => Resolve(MonoStack);

    public static FontFamily Resolve(string? stack) {
        if (string.IsNullOrWhiteSpace(stack)) {
            return FontFamily.Default;
        }

        return Resolved.GetOrAdd(stack, static value => {
            try {
                foreach (var candidate in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) {
                    if (FontManager.Current.TryGetGlyphTypeface(new Typeface(candidate), out var glyphTypeface)
                        && string.Equals(glyphTypeface.FamilyName, candidate, StringComparison.OrdinalIgnoreCase)) {
                        return new FontFamily(candidate);
                    }
                }

                return FontFamily.Default;
            }
            catch (Exception) {
                // No platform font manager (e.g. unit tests): keep the stack as written.
                return new FontFamily(value);
            }
        });
    }
}
