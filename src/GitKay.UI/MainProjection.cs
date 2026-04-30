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
        SelectedDiffFiles.Clear();

        if (model.SelectedDiff != null)
        {
            foreach (var file in model.SelectedDiff.Value)
            {
                SelectedDiffFiles.Add(new DiffFileProjection(file));
            }
        }

        Commits.SyncWith(
            model.Commits,
            m => m.Commit.Hash,
            vm => vm.FullHash,
            _ => new CommitProjection(),
            _dispatch!
        );

        if (model.SelectedHash != null)
        {
            var hash = model.SelectedHash.Value;
            foreach (var commit in Commits)
            {
                if (commit.FullHash == hash)
                {
                    SelectedCommit = commit;
                    break;
                }
            }
        }
        else
        {
            SelectedCommit = null;
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
        if (value != null)
        {
            var startedAtTicks = Stopwatch.GetTimestamp();
            LogTiming($"commit click hash={value.FullHash}");
            _dispatch?.Invoke(GitKay.Core.App.Msg.NewSelectCommit(value.FullHash, startedAtTicks));
        }
    }

    public void RereadRefs() => _dispatch?.Invoke(GitKay.Core.App.Msg.RereadRefs);
}
