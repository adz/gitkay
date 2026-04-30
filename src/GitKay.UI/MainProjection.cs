using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Elmish.Glue.Core;
using GitKay.Core;

namespace GitKay.UI;

public partial class MainProjection : ObservableObject, IProjection<GitKay.Core.App.Model, GitKay.Core.App.Msg>
{
    private readonly long _createdAtTicks = Stopwatch.GetTimestamp();
    private bool _firstPaintLogged;
    private bool _suppressSelectionDispatch;
    private string? _selectedDiffHash;

    [ObservableProperty] private string _status = "";

    public ObservableCollection<CommitProjection> Commits { get; } = new();
    public ObservableCollection<DiffFileProjection> SelectedDiffFiles { get; } = new();

    [ObservableProperty] private CommitProjection? _selectedCommit;

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

        // The core diff is cached by commit hash, so only repopulate the UI collection when the selected diff changes.
        var selectedDiffHash =
            model.SelectedCommitHash != null
            && model.SelectedDiffHash != null
            && model.SelectedCommitHash.Value == model.SelectedDiffHash.Value
            && model.SelectedDiff != null
                ? model.SelectedDiffHash.Value
                : null;

        if (!string.Equals(_selectedDiffHash, selectedDiffHash, StringComparison.Ordinal))
        {
            SelectedDiffFiles.Clear();

            if (selectedDiffHash != null && model.SelectedDiff != null)
            {
                foreach (var file in model.SelectedDiff.Value)
                {
                    SelectedDiffFiles.Add(new DiffFileProjection(file));
                }
            }

            _selectedDiffHash = selectedDiffHash;
        }

        Commits.SyncWith(
            model.Commits,
            m => m.Commit.Hash,
            vm => vm.FullHash,
            _ => new CommitProjection(),
            _dispatch!
        );

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

        var elapsed = Stopwatch.GetElapsedTime(startedAtTicks);
        LogTiming($"ui projection elapsed={elapsed.TotalMilliseconds:F1}ms commits={model.Commits.Length} diffFiles={SelectedDiffFiles.Count}");
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

    public void RereadRefs() => _dispatch?.Invoke(GitKay.Core.App.Msg.RereadRefs);
}
