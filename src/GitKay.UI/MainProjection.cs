using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Elmish.Glue.Core;
using GitKay.Core;

namespace GitKay.UI;

public partial class MainProjection : ObservableObject, IProjection<GitKay.Core.App.Model, GitKay.Core.App.Msg>
{
    [ObservableProperty] private string _status = "";

    public ObservableCollection<CommitProjection> Commits { get; } = new();
    public ObservableCollection<DiffFileProjection> SelectedDiffFiles { get; } = new();

    [ObservableProperty] private CommitProjection? _selectedCommit;

    public void Update(GitKay.Core.App.Model model)
    {
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
    }

    private Action<GitKay.Core.App.Msg>? _dispatch;

    public void SetDispatch(Action<GitKay.Core.App.Msg> dispatch)
    {
        _dispatch = dispatch;
    }

    partial void OnSelectedCommitChanged(CommitProjection? value)
    {
        if (value != null)
        {
            _dispatch?.Invoke(GitKay.Core.App.Msg.NewSelectCommit(value.FullHash));
        }
    }

    public void RereadRefs() => _dispatch?.Invoke(GitKay.Core.App.Msg.RereadRefs);
}
