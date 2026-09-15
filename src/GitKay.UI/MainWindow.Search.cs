using System;
using System.Collections.Generic;
using System.Linq;
using GitKay.Kit;
using Avalonia.Input;
using Avalonia.Interactivity;
using GitKay.Core;

namespace GitKay.UI;

// ----- vim's / and ? prompt in the status bar: incremental, Enter keeps the search for n / N, Esc restores. -----
public partial class MainWindow {
    private sealed record SearchOrigin(Vim.VimPane Pane, bool Forward, string FindQuery, bool FindRegex, bool FindBackward,
        (IDiffRowProjection? Row, int Caret, double Offset) Diff, CommitProjection? Commit, double CommitOffset);

    private SearchOrigin? _searchOrigin;
    private readonly List<string> _searchHistory = new();
    private int _searchHistoryIndex;
    private bool _promptClosing;

    /// <summary>The commit list's / search: a quick match over loaded commits, stepped by n / N until the next search.</summary>
    private TextQuery? _commitQuickFind;
    private string _commitQuickFindInput = "";
    private bool _commitQuickFindForward = true;

    /// <summary>The / or ? that opened the prompt, whose text input can arrive after focus moves into it.</summary>
    private string? _promptOpeningText;

    private void InitializeSearchPrompt() {
        SearchPromptBox.AddHandler(TextInputEvent, (_, e) => {
            if (_promptOpeningText != null && e.Text == _promptOpeningText) e.Handled = true;
            _promptOpeningText = null;
        }, RoutingStrategies.Tunnel);
        SearchPromptBox.TextChanged += (_, _) => OnSearchPromptChanged();
        SearchPromptBox.AddHandler(KeyDownEvent, OnSearchPromptKeyDown, RoutingStrategies.Tunnel);
        SearchPromptBox.LostFocus += (_, _) => { if (_searchOrigin != null && !_promptClosing) CloseSearchPrompt(accept: false); };
    }

    void IVimCommands.OpenSearch(Vim.VimPane pane, bool forward) {
        if (_projection is not { } projection) return;
        // The files list searches the diff it lists.
        if (pane == Vim.VimPane.Files) pane = Vim.VimPane.Diff;
        _searchOrigin = new SearchOrigin(pane, forward, projection.CommitFindQuery, projection.CommitFindUseRegex, projection.DiffFindBackward,
            DiffRowsListBox.SaveViewPosition(), CommitListBox.FocusedCommit, CommitListBox.ScrollOffset);
        _searchHistoryIndex = _searchHistory.Count;
        SearchPromptPrefix.Text = forward ? "/" : "?";
        _promptOpeningText = SearchPromptPrefix.Text;
        _promptClosing = true;
        SearchPromptBox.Text = "";
        _promptClosing = false;
        SearchPromptInfo.Text = Vim.describeSearch(Vim.parseSearch(""));
        projection.IsSearchPromptOpen = true;
        // Keys trim before the input: cap them at what's left after the input's minimum and the match summary.
        SearchPromptKeys.MaxWidth = Math.Max(0, Bounds.Width - 16 - 160 - 240);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => SearchPromptBox.Focus(), Avalonia.Threading.DispatcherPriority.Input);
    }

    private void OnSearchPromptChanged() {
        if (_searchOrigin is not { } origin || _promptClosing) return;
        var pattern = Vim.parseSearch(SearchPromptBox.Text ?? "");
        if (pattern.IsEmpty) {
            SearchPromptInfo.Text = Vim.describeSearch(pattern);
            RestoreSearchOrigin(origin);
            return;
        }
        if (pattern.Error != null) {
            SearchPromptInfo.Text = Vim.describeSearch(pattern) + " · invalid";
            return;
        }
        SearchPromptInfo.Text = Vim.describeSearch(pattern) + " · " + RunSearch(origin, pattern, fromOrigin: true);
    }

    /// <summary>Moves to the nearest match from where the search started; returns the match summary for the prompt.</summary>
    private string RunSearch(SearchOrigin origin, Vim.SearchPattern pattern, bool fromOrigin) {
        if (_projection is not { } projection) return "";
        if (origin.Pane == Vim.VimPane.Commits) {
            var query = TextQuery.Create(true, pattern.Regex);
            CommitListBox.QuickFindHighlight = GitSearch.commitHighlight(query);
            var start = fromOrigin ? origin.Commit : CommitListBox.FocusedCommit;
            return FindCommit(query, origin.Forward ? Direction.Forward : Direction.Backward, start, includeStart: fromOrigin);
        }

        projection.SearchDiffFrom(pattern.Regex, origin.Forward, fromOrigin ? origin.Diff.Row : DiffRowsListBox.SelectedItem);
        DiffRowsListBox.PlaceCaretOnFindMatch();
        return projection.CommitFindStatusText;
    }

    private string FindCommit(TextQuery query, Direction direction, CommitProjection? start, bool includeStart) {
        var commits = CommitListBox.VisibleCommits;
        var matches = Cycle.positionsWhere(commits.Count, index => {
            var commit = commits[index];
            return query.IsMatch(commit.Subject) || query.IsMatch(commit.Author) || query.IsMatch(commit.AuthorEmail) || query.IsMatch(commit.FullHash);
        });
        var focus = start == null ? -1 : IndexOf(commits, start);
        var next = Cycle.step(matches, focus, direction, includeStart);
        if (next >= 0) CommitListBox.FocusCommit(commits[matches[next]]);
        return Cycle.summary(next, matches.Length);
    }

    private static int IndexOf(IReadOnlyList<CommitProjection> commits, CommitProjection commit) {
        for (var i = 0; i < commits.Count; i++)
            if (ReferenceEquals(commits[i], commit)) return i;
        return -1;
    }

    private void ClearCommitQuickFind() {
        _commitQuickFind = null;
        _commitQuickFindInput = "";
        CommitListBox.QuickFindHighlight = null;
    }

    /// <summary>n / N in the commit list after a / search there.</summary>
    private bool TryStepCommitQuickFind(bool forward) {
        if (_commitQuickFind is not { } query) return false;
        var direction = forward == _commitQuickFindForward ? Direction.Forward : Direction.Backward;
        var summary = FindCommit(query, direction, CommitListBox.FocusedCommit, includeStart: false);
        if (_projection != null) _projection.Status = $"{(_commitQuickFindForward ? "/" : "?")}{_commitQuickFindInput} · {summary}";
        return true;
    }

    private void OnSearchPromptKeyDown(object? sender, KeyEventArgs e) {
        // A real key press comes before its own text input, so the opening / can no longer be pending.
        _promptOpeningText = null;
        switch (e.Key) {
            case Key.Enter:
                CloseSearchPrompt(accept: true);
                e.Handled = true;
                break;
            case Key.Escape:
                CloseSearchPrompt(accept: false);
                e.Handled = true;
                break;
            case Key.Up or Key.Down when _searchHistory.Count > 0:
                _searchHistoryIndex = Math.Clamp(_searchHistoryIndex + (e.Key == Key.Up ? -1 : 1), 0, _searchHistory.Count);
                SearchPromptBox.Text = _searchHistoryIndex < _searchHistory.Count ? _searchHistory[_searchHistoryIndex] : "";
                SearchPromptBox.CaretIndex = SearchPromptBox.Text?.Length ?? 0;
                e.Handled = true;
                break;
            case Key.R or Key.C when e.KeyModifiers == KeyModifiers.Alt:
                SearchPromptBox.Text = e.Key == Key.R ? Vim.toggleSearchRegex(SearchPromptBox.Text ?? "") : Vim.toggleSearchCase(SearchPromptBox.Text ?? "");
                SearchPromptBox.CaretIndex = SearchPromptBox.Text.Length;
                e.Handled = true;
                break;
        }
    }

    private void CloseSearchPrompt(bool accept) {
        if (_searchOrigin is not { } origin || _projection is not { } projection) return;
        _promptClosing = true;
        var input = SearchPromptBox.Text ?? "";
        // Like vim, an empty / repeats the last search.
        if (accept && input.Length == 0 && _searchHistory.Count > 0) input = _searchHistory[^1];
        var pattern = Vim.parseSearch(input);

        if (accept && !pattern.IsEmpty && pattern.Error == null) {
            _searchHistory.Remove(input);
            _searchHistory.Add(input);
            if (_searchHistory.Count > 50) _searchHistory.RemoveAt(0);
            var summary = RunSearch(origin, pattern, fromOrigin: true);
            if (origin.Pane == Vim.VimPane.Commits) {
                _commitQuickFind = TextQuery.Create(true, pattern.Regex);
                _commitQuickFindInput = input;
                _commitQuickFindForward = origin.Forward;
            }
            projection.Status = $"{(origin.Forward ? "/" : "?")}{input} · {summary}";
        }
        else {
            RestoreSearchOrigin(origin);
        }

        _searchOrigin = null;
        projection.IsSearchPromptOpen = false;
        FocusPaneFromKeyboard(origin.Pane == Vim.VimPane.Commits ? Pane.Commits : Pane.Diff);
        _promptClosing = false;
    }

    private void RestoreSearchOrigin(SearchOrigin origin) {
        if (_projection is not { } projection) return;
        if (origin.Pane == Vim.VimPane.Commits) {
            // Back to the previous / search's underline, or none.
            CommitListBox.QuickFindHighlight = _commitQuickFind is { } previous ? GitSearch.commitHighlight(previous) : null;
            if (origin.Commit != null) CommitListBox.FocusCommit(origin.Commit);
            CommitListBox.ScrollOffset = origin.CommitOffset;
            return;
        }
        projection.RestoreDiffFind(origin.FindQuery, origin.FindRegex, origin.FindBackward);
        DiffRowsListBox.RestoreViewPosition(origin.Diff);
    }
}
