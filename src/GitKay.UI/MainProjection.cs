using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Elmish.Glue.Core;
using GitKay.Core;

namespace GitKay.UI;

public partial class MainProjection : ObservableObject, IProjection<GitKay.Core.App.Model, GitKay.Core.App.Msg>
{
    private readonly long _createdAtTicks = Stopwatch.GetTimestamp();
    private bool _firstPaintLogged;
    private bool _suppressSelectionDispatch;
    private bool _suppressDiffSelectionSync;
    private object? _commitsSource;
    private string? _selectedDiffHash;
    private object? _selectedDiffFilesSource;
    private DiffFileKey? _selectedDiffFileKey;
    private object? _selectedDiffFileSource;

    [ObservableProperty] private string _status = "";

    public ObservableCollection<CommitProjection> Commits { get; } = new();
    public ObservableCollection<DiffFileProjection> SelectedDiffFiles { get; } = new();
    public ObservableCollection<IDiffRowProjection> SelectedDiffRows { get; } = new();

    [ObservableProperty] private CommitProjection? _selectedCommit;
    [ObservableProperty] private DiffFileProjection? _selectedDiffFile;
    [ObservableProperty] private IDiffRowProjection? _selectedDiffRow;

    private static void LogTiming(string message)
    {
        var line = $"[timing] {message}";
        Trace.WriteLine(line);
        try
        {
            Console.Error.WriteLine(line);
        }
        catch
        {
        }
    }

    public void Update(GitKay.Core.App.Model model)
    {
        var startedAtTicks = Stopwatch.GetTimestamp();
        Status = model.Status;

        UpdateDiffState(model);
        UpdateCommits(model);
        UpdateSelectedCommit(model);

        var elapsed = Stopwatch.GetElapsedTime(startedAtTicks);
        LogTiming($"ui projection elapsed={elapsed.TotalMilliseconds:F1}ms commits={model.Commits.Length} diffFiles={SelectedDiffFiles.Count} diffRows={SelectedDiffRows.Count}");
    }

    private void UpdateDiffState(GitKay.Core.App.Model model)
    {
        var selectedDiffHash =
            model.SelectedCommitHash != null
            && model.SelectedDiffHash != null
            && model.SelectedCommitHash.Value == model.SelectedDiffHash.Value
            && model.SelectedDiffFiles != null
                ? model.SelectedDiffHash.Value
                : null;

        var selectedDiffFilesSource =
            selectedDiffHash != null && model.SelectedDiffFiles != null
                ? (object?)model.SelectedDiffFiles.Value
                : null;

        var diffCollectionChanged =
            !string.Equals(_selectedDiffHash, selectedDiffHash, StringComparison.Ordinal)
            || !ReferenceEquals(_selectedDiffFilesSource, selectedDiffFilesSource);

        if (selectedDiffHash == null)
        {
            if (SelectedDiffFiles.Count > 0 || SelectedDiffRows.Count > 0 || _selectedDiffHash != null)
            {
                SyncSelectedDiffSelection(null);
                SelectedDiffFiles.Clear();
                SelectedDiffRows.Clear();
            }

            _selectedDiffHash = null;
            _selectedDiffFilesSource = null;
            _selectedDiffFileKey = null;
            _selectedDiffFileSource = null;
            return;
        }

        if (diffCollectionChanged)
        {
            var previousSelectedDiffFileKey = SelectedDiffFile?.Key;

            SyncSelectedDiffSelection(null);
            SelectedDiffFiles.Clear();
            SelectedDiffRows.Clear();

            if (model.SelectedDiffFiles != null)
            {
                SyncSelectedDiffFiles(model.SelectedDiffFiles.Value);
            }

            _selectedDiffHash = selectedDiffHash;
            _selectedDiffFilesSource = selectedDiffFilesSource;

            var selectedDiffFile = ResolveSelectedDiffFile(model, previousSelectedDiffFileKey);

            if (selectedDiffFile != null && model.SelectedDiffFile != null)
            {
                var loadedFile = model.SelectedDiffFile.Value;
                var loadedKey = new DiffFileKey(loadedFile.OldPath, loadedFile.NewPath);

                if (selectedDiffFile.Key.Equals(loadedKey))
                {
                    selectedDiffFile.ApplyContent(loadedFile);
                    _selectedDiffFileSource = (object?)loadedFile;
                }
                else
                {
                    _selectedDiffFileSource = null;
                }
            }
            else
            {
                _selectedDiffFileSource = null;
            }

            SyncSelectedDiffSelection(selectedDiffFile);
            RenderSelectedDiffRows(selectedDiffFile);
            _selectedDiffFileKey = selectedDiffFile?.Key;
            return;
        }

        if (model.SelectedDiffFiles != null)
        {
            var selectedDiffFile = ResolveSelectedDiffFile(model, _selectedDiffFileKey);
            var selectedDiffFileContent = model.SelectedDiffFile != null ? (object?)model.SelectedDiffFile.Value : null;
            var selectedDiffFileKey = selectedDiffFile?.Key;

            if (selectedDiffFile != null && model.SelectedDiffFile != null)
            {
                var loadedFile = model.SelectedDiffFile.Value;
                var loadedKey = new DiffFileKey(loadedFile.OldPath, loadedFile.NewPath);

                if (selectedDiffFile.Key == loadedKey)
                {
                    selectedDiffFile.ApplyContent(loadedFile);
                    selectedDiffFileContent = (object?)loadedFile;
                }
            }

            if (!Nullable.Equals(_selectedDiffFileKey, selectedDiffFileKey) || !ReferenceEquals(_selectedDiffFileSource, selectedDiffFileContent))
            {
                SyncSelectedDiffSelection(selectedDiffFile);
                RenderSelectedDiffRows(selectedDiffFile);
                _selectedDiffFileKey = selectedDiffFileKey;
                _selectedDiffFileSource = selectedDiffFileContent;
            }
        }
    }

    private void UpdateCommits(GitKay.Core.App.Model model)
    {
        if (!ReferenceEquals(_commitsSource, model.Commits))
        {
            Commits.SyncWith(
                model.Commits,
                m => m.Commit.Hash,
                vm => vm.FullHash,
                _ => new CommitProjection(),
                _dispatch!);

            _commitsSource = model.Commits;
        }
    }

    private void UpdateSelectedCommit(GitKay.Core.App.Model model)
    {
        CommitProjection? selectedCommit = null;

        if (model.SelectedCommitHash != null)
        {
            var hash = model.SelectedCommitHash.Value;
            foreach (var commit in Commits)
            {
                if (commit.FullHash == hash)
                {
                    selectedCommit = commit;
                    break;
                }
            }
        }

        if (selectedCommit != null)
        {
            _suppressSelectionDispatch = true;
            try
            {
                SelectedCommit = selectedCommit;
            }
            finally
            {
                _suppressSelectionDispatch = false;
            }
        }
        else if (SelectedCommit != null)
        {
            _suppressSelectionDispatch = true;
            try
            {
                SelectedCommit = null;
            }
            finally
            {
                _suppressSelectionDispatch = false;
            }
        }
    }

    private Action<GitKay.Core.App.Msg>? _dispatch;

    public void SetDispatch(Action<GitKay.Core.App.Msg> dispatch)
    {
        _dispatch = dispatch;
    }

    public void LogFirstPaint()
    {
        if (_firstPaintLogged)
        {
            return;
        }

        _firstPaintLogged = true;
        var firstPaintElapsed = Stopwatch.GetElapsedTime(_createdAtTicks);
        LogTiming($"first paint elapsed={firstPaintElapsed.TotalMilliseconds:F1}ms");
    }

    partial void OnSelectedCommitChanged(CommitProjection? value)
    {
        if (_suppressSelectionDispatch)
        {
            return;
        }

        if (value != null)
        {
            var startedAtTicks = Stopwatch.GetTimestamp();
            LogTiming($"commit click hash={value.FullHash}");
            _dispatch?.Invoke(GitKay.Core.App.Msg.NewSelectCommit(value.FullHash, startedAtTicks));
        }
    }

    partial void OnSelectedDiffFileChanged(DiffFileProjection? value)
    {
        if (_suppressDiffSelectionSync)
        {
            return;
        }

        SyncSelectedDiffSelection(value);
        RenderSelectedDiffRows(value);

        if (value == null || SelectedCommit == null)
        {
            return;
        }

        var startedAtTicks = Stopwatch.GetTimestamp();
        LogTiming($"file click hash={SelectedCommit.FullHash} path={value.DisplayPath}");
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSelectDiffFile(SelectedCommit.FullHash, value.Key.OldPath, value.Key.NewPath, startedAtTicks));
    }

    partial void OnSelectedDiffRowChanged(IDiffRowProjection? value)
    {
        if (_suppressDiffSelectionSync)
        {
            return;
        }

        if (value is DiffFileHeaderProjection fileHeader)
        {
            SyncSelectedDiffSelection(fileHeader.File);
        }
    }

    public void RereadRefs() => _dispatch?.Invoke(GitKay.Core.App.Msg.RereadRefs);

    private void SyncSelectedDiffFiles(IReadOnlyList<GitKay.Core.GitService.DiffFileSummary> summaries)
    {
        var existingByKey = SelectedDiffFiles.ToDictionary(file => file.Key);

        SelectedDiffFiles.Clear();
        foreach (var summary in summaries)
        {
            var key = new DiffFileKey(summary.OldPath, summary.NewPath);

            if (!existingByKey.TryGetValue(key, out var fileProjection))
            {
                fileProjection = new DiffFileProjection(summary);
            }
            else
            {
                fileProjection.UpdateSummary(summary);
            }

            SelectedDiffFiles.Add(fileProjection);
        }
    }

    private static bool MatchesKey(DiffFileKey uiKey, GitKay.Core.GitService.DiffFileKey coreKey) =>
        uiKey.OldPath == coreKey.OldPath && uiKey.NewPath == coreKey.NewPath;

    private DiffFileProjection? ResolveSelectedDiffFile(GitKay.Core.App.Model model, DiffFileKey? previousSelectedDiffFileKey)
    {
        if (model.SelectedDiffFileKey != null)
        {
            var key = model.SelectedDiffFileKey.Value;
            var selectedDiffFile = SelectedDiffFiles.FirstOrDefault(file => MatchesKey(file.Key, key));
            if (selectedDiffFile != null)
            {
                return selectedDiffFile;
            }
        }

        if (previousSelectedDiffFileKey != null)
        {
            var key = previousSelectedDiffFileKey.Value;
            var selectedDiffFile = SelectedDiffFiles.FirstOrDefault(file => file.Key == key);
            if (selectedDiffFile != null)
            {
                return selectedDiffFile;
            }
        }

        return SelectedDiffFiles.FirstOrDefault();
    }

    private void RenderSelectedDiffRows(DiffFileProjection? selectedDiffFile)
    {
        SelectedDiffRows.Clear();

        if (selectedDiffFile == null)
        {
            return;
        }

        SelectedDiffRows.Add(selectedDiffFile.Header);

        if (!selectedDiffFile.IsLoaded)
        {
            return;
        }

        foreach (var hunk in selectedDiffFile.Hunks)
        {
            SelectedDiffRows.Add(new DiffHunkHeaderProjection(hunk));

            foreach (var line in hunk.Lines)
            {
                SelectedDiffRows.Add(line);
            }
        }
    }

    private void SyncSelectedDiffSelection(DiffFileProjection? selectedDiffFile)
    {
        _suppressDiffSelectionSync = true;
        try
        {
            SelectedDiffFile = selectedDiffFile;
            SelectedDiffRow = selectedDiffFile?.Header;
        }
        finally
        {
            _suppressDiffSelectionSync = false;
        }
    }
}
