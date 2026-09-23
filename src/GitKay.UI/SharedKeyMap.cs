using Avalonia.Input;

namespace GitKay.UI;

/// <summary>Translate Avalonia keys once before consulting the shared core key map.</summary>
internal static class SharedKeyMap {
    internal static GitKay.Core.Keys.Command? CommandFor(KeyEventArgs e) =>
        GitKay.Core.Keys.command(GitKay.Core.Keys.chord(
            e.Key switch {
                Key.OemPlus or Key.Add => "Plus",
                Key.OemMinus or Key.Subtract => "Minus",
                Key.NumPad0 => "D0",
                _ => e.Key.ToString(),
            },
            e.KeyModifiers.HasFlag(KeyModifiers.Control),
            e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key is not (Key.OemPlus or Key.Add),
            e.KeyModifiers.HasFlag(KeyModifiers.Alt)))?.Value;
}
