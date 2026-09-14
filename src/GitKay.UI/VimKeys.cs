using Avalonia.Controls;
using Avalonia.Input;
using GitKay.Core;

namespace GitKay.UI;

/// <summary>App-level actions vim keys can trigger from a pane; the main window provides them.</summary>
public interface IVimCommands {
    void CopyCommitReference(bool subject);
    void FindWord(string word, bool forward);
    void FindNext(Vim.VimPane pane, bool forward);
    void GoToParent(int index);
    void GoToChild();
}

internal static class VimKeys {
    /// <summary>Translates an Avalonia key event for <see cref="Vim"/>.</summary>
    public static Vim.KeyStroke From(KeyEventArgs e) =>
        new(e.Key.ToString(), e.KeySymbol ?? FallbackSymbol(e), e.KeyModifiers.HasFlag(KeyModifiers.Control),
            e.KeyModifiers.HasFlag(KeyModifiers.Shift), e.KeyModifiers.HasFlag(KeyModifiers.Alt));

    // Platforms (and headless tests) that don't report the typed character: letters and unshifted digits are enough.
    private static string? FallbackSymbol(KeyEventArgs e) {
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        return e.Key switch {
            >= Key.A and <= Key.Z => ((char)((shift ? 'A' : 'a') + (e.Key - Key.A))).ToString(),
            >= Key.D0 and <= Key.D9 when !shift => ((char)('0' + (e.Key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (e.Key - Key.NumPad0))).ToString(),
            _ => null,
        };
    }
}

/// <summary>The changed-files list as a vim host: row movement and the commit-level keys.</summary>
internal sealed class ListBoxVimHost(ListBox list, IVimCommands commands) : Vim.IVimHost {
    public Vim.VimPane Pane => Vim.VimPane.Files;
    public string LineText => null!;
    public int Caret => 0;
    public string OtherSideText => null!;
    public int Side => 0;
    public bool HasSelection => false;
    public int HalfPageRows => 10;
    public int PageRows => 20;
    public void FocusScreenRow(Vim.VimScreenRow row, int offset) { }
    public void ScrollRows(int delta) { }
    public void SetCaret(int column) { }
    public void SwitchSide(int column) { }
    public void MoveRows(int delta) => MainWindowNavigation.TryMoveSelection(list, delta);
    public void MoveToEdge(bool last) => MainWindowNavigation.TryMoveSelection(list, last ? int.MaxValue / 2 : int.MinValue / 2);

    public void GoToPosition(int position) {
        if (list.ItemCount == 0) return;
        MainWindowNavigation.TryMoveSelection(list, System.Math.Clamp(position - 1, 0, list.ItemCount - 1) - System.Math.Max(0, list.SelectedIndex));
    }

    public void MoveToHunk(int direction) { }
    public void ScrollFocus(Vim.VimScroll position) { }
    public void ToggleVisual(bool linewise) { }
    public bool CancelSelection() => false;
    public void CopySelection() { }
    public void CopyRange(int from, int until, bool wholeLine) { }
    public void CopyCommitReference(bool subject) => commands.CopyCommitReference(subject);
    public void FindWord(string word, bool forward) { }
    public void FindNext(bool forward) => commands.FindNext(Vim.VimPane.Files, forward);
    public void GoToParent(int index) => commands.GoToParent(index);
    public void GoToChild() => commands.GoToChild();
}
