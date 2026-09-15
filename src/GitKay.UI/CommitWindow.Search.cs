using System;
using System.Collections.Generic;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GitKay.Core;

namespace GitKay.UI;

// ----- The commit window's / and ? prompt and Ctrl+P palette, working like the main window's. -----
public partial class CommitWindow {
    private (IDiffRowProjection? Row, int Caret, double Offset)? _searchOrigin;
    private bool _searchForward = true;
    private readonly List<string> _searchHistory = new();
    private int _searchHistoryIndex;
    private string? _searchOpeningText;
    private bool _searchClosing;

    private void InitializeSearch() {
        // The / or ? that opened the prompt arrives as text input after focus moves into it.
        SearchBox.AddHandler(TextInputEvent, (_, e) => {
            if (_searchOpeningText != null && e.Text == _searchOpeningText) e.Handled = true;
            _searchOpeningText = null;
        }, RoutingStrategies.Tunnel);
        SearchBox.TextChanged += (_, _) => OnSearchChanged();
        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        SearchBox.LostFocus += (_, _) => { if (_searchOrigin != null && !_searchClosing) CloseSearch(accept: false); };
        PaletteBox.AddHandler(KeyDownEvent, (_, e) => OnPaletteKey(e), RoutingStrategies.Tunnel);
    }

    void IVimCommands.OpenSearch(Vim.VimPane pane, bool forward) {
        if (Projection is not { } projection) return;
        _searchOrigin = Surface.SaveViewPosition();
        _searchForward = forward;
        _searchHistoryIndex = _searchHistory.Count;
        SearchPrefix.Text = forward ? "/" : "?";
        _searchOpeningText = SearchPrefix.Text;
        _searchClosing = true;
        SearchBox.Text = "";
        _searchClosing = false;
        SearchInfo.Text = Vim.describeSearch(Vim.parseSearch("")) + " · Enter keep · Esc cancel · ↑↓ history";
        projection.IsSearchOpen = true;
        Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Input);
    }

    void IVimCommands.FindNext(Vim.VimPane pane, bool forward) {
        if (Projection?.FindNext(forward) is { } summary) {
            Surface.PlaceCaretOnFindMatch();
            Projection.Status = $"{(_searchForward ? "/" : "?")}{LastSearch} · {summary}";
        }
    }

    void IVimCommands.FindWord(string word, bool forward) {
        if (Projection is not { } projection) return;
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
        var pattern = System.Text.RegularExpressions.Regex.Escape(word);
        if (IsWord(word[0])) pattern = @"\b" + pattern;
        if (IsWord(word[^1])) pattern += @"\b";
        var summary = projection.SearchDiff(pattern, forward, Surface.SelectedItem, includeStart: false);
        Surface.PlaceCaretOnFindMatch();
        projection.Status = $"{(forward ? "*" : "#")} {word} · {summary}";
    }

    private string LastSearch => _searchHistory.Count > 0 ? _searchHistory[^1] : "";

    private void OnSearchChanged() {
        if (_searchOrigin is not { } origin || _searchClosing || Projection is not { } projection) return;
        var pattern = Vim.parseSearch(SearchBox.Text ?? "");
        if (pattern.IsEmpty) {
            SearchInfo.Text = Vim.describeSearch(pattern);
            projection.ClearFind();
            Surface.RestoreViewPosition(origin);
            return;
        }
        if (pattern.Error != null) {
            SearchInfo.Text = Vim.describeSearch(pattern) + " · invalid";
            return;
        }
        var summary = projection.SearchDiff(pattern.Regex, _searchForward, origin.Row, includeStart: true);
        Surface.PlaceCaretOnFindMatch();
        SearchInfo.Text = Vim.describeSearch(pattern) + " · " + summary;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e) {
        _searchOpeningText = null;
        switch (e.Key) {
            case Key.Enter:
                CloseSearch(accept: true);
                e.Handled = true;
                break;
            case Key.Escape:
                CloseSearch(accept: false);
                e.Handled = true;
                break;
            case Key.Up or Key.Down when _searchHistory.Count > 0:
                _searchHistoryIndex = Math.Clamp(_searchHistoryIndex + (e.Key == Key.Up ? -1 : 1), 0, _searchHistory.Count);
                SearchBox.Text = _searchHistoryIndex < _searchHistory.Count ? _searchHistory[_searchHistoryIndex] : "";
                SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
                e.Handled = true;
                break;
            case Key.R or Key.C when e.KeyModifiers == KeyModifiers.Alt:
                SearchBox.Text = e.Key == Key.R ? Vim.toggleSearchRegex(SearchBox.Text ?? "") : Vim.toggleSearchCase(SearchBox.Text ?? "");
                SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
                e.Handled = true;
                break;
        }
    }

    private void CloseSearch(bool accept) {
        if (_searchOrigin is not { } origin || Projection is not { } projection) return;
        _searchClosing = true;
        var text = SearchBox.Text ?? "";
        // An empty / repeats the last search, as in vim.
        if (accept && text.Length == 0 && LastSearch.Length > 0) {
            text = LastSearch;
            var repeat = Vim.parseSearch(text);
            if (repeat.Error == null) projection.SearchDiff(repeat.Regex, _searchForward, origin.Row, includeStart: false);
        }
        var pattern = Vim.parseSearch(text);
        if (accept && !pattern.IsEmpty && pattern.Error == null) {
            if (LastSearch != text) _searchHistory.Add(text);
            projection.Status = $"{(_searchForward ? "/" : "?")}{text} · n / N for more";
        }
        else {
            projection.ClearFind();
            Surface.RestoreViewPosition(origin);
        }
        _searchOrigin = null;
        projection.IsSearchOpen = false;
        Surface.Focus();
        _searchClosing = false;
    }

    // ----- Ctrl+P -----

    private void OnPaletteKey(KeyEventArgs e) {
        if (Projection is not { IsFilePaletteOpen: true } projection) return;
        switch (e.Key) {
            case Key.Escape:
                projection.IsFilePaletteOpen = false;
                Surface.Focus();
                e.Handled = true;
                break;
            case Key.Enter:
                projection.RunPalette();
                Surface.Focus();
                e.Handled = true;
                break;
            case Key.Down or Key.Up:
            case Key.J or Key.K when e.KeyModifiers == KeyModifiers.Control:
                var step = e.Key is Key.Down or Key.J ? 1 : -1;
                if (projection.PaletteFiles.Count > 0)
                    projection.PaletteIndex = (projection.PaletteIndex + step + projection.PaletteFiles.Count) % projection.PaletteFiles.Count;
                e.Handled = true;
                break;
        }
    }

    private void OnPaletteBackdropPressed(object? sender, PointerPressedEventArgs e) {
        if (Projection != null && ReferenceEquals(e.Source, sender)) Projection.IsFilePaletteOpen = false;
    }

    private void OnPaletteDoubleTapped(object? sender, TappedEventArgs e) {
        Projection?.RunPalette();
        Surface.Focus();
    }
}
