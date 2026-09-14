using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GitKay.UI;

/// <summary>A remote operation: a title and a script of git commands run through a <see cref="GitRunner"/>.</summary>
public sealed record GitOperation(string Title, Func<GitRunner, Task<bool>> Run);

/// <summary>Push, pull and fetch, run through the git CLI so its progress, credentials and hooks behave as in a terminal.</summary>
public static class GitOperations {
    public static GitOperation FetchAll() =>
        new("Fetch all remotes", runner => runner.RunAsync("fetch", "--all", "--prune", "--progress"));

    public static GitOperation ForBranch(string operation, BranchTarget branch) => operation switch {
        "push" => Push(branch),
        "pull" => Pull(branch),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    private static GitOperation Push(BranchTarget branch) => new($"Push {branch.Name}", async runner => {
        var (remote, merge) = await UpstreamAsync(runner, branch.Name);
        if (remote != null && merge != null) {
            runner.Log($"Pushing {branch.Name} to {remote} ({merge})");
            return await runner.RunAsync("push", "--progress", remote, $"refs/heads/{branch.Name}:{merge}");
        }

        var remotes = (await runner.CaptureAsync("remote") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var target = remotes.Contains("origin") ? "origin" : remotes.FirstOrDefault();
        if (target == null) {
            runner.Log("This repository has no remotes to push to.");
            return false;
        }

        runner.Log($"{branch.Name} has no upstream; pushing to {target} and setting it as upstream");
        return await runner.RunAsync("push", "--progress", "--set-upstream", target, branch.Name);
    });

    private static GitOperation Pull(BranchTarget branch) => new($"Pull {branch.Name}", async runner => {
        if (branch.IsCurrentHead)
            return await runner.RunAsync("pull", "--progress", "--no-edit");

        // A branch that isn't checked out can only move by fast-forward: fetch its upstream straight into it.
        var (remote, merge) = await UpstreamAsync(runner, branch.Name);
        if (remote == null || merge == null) {
            runner.Log($"{branch.Name} has no upstream branch configured.");
            return false;
        }

        runner.Log($"Fast-forwarding {branch.Name} from {remote} ({merge})");
        return await runner.RunAsync("fetch", "--progress", remote, $"{merge}:refs/heads/{branch.Name}");
    });

    public static GitOperation DeleteBranch(BranchTarget branch, bool force) =>
        new(force ? $"Force delete {branch.Name}" : $"Delete {branch.Name}", async runner => {
            runner.Log(force
                ? $"Deleting local branch {branch.Name} even though it is not merged into HEAD"
                : $"Deleting local branch {branch.Name}");
            var deleted = await runner.RunAsync("branch", force ? "-D" : "-d", branch.Name);
            if (deleted) runner.Log($"Deleted {branch.Name}. Remote branches, if any, are unchanged.");
            return deleted;
        });

    private static async Task<(string? Remote, string? Merge)> UpstreamAsync(GitRunner runner, string branch) {
        var remote = (await runner.CaptureAsync("config", "--get", $"branch.{branch}.remote"))?.Trim();
        var merge = (await runner.CaptureAsync("config", "--get", $"branch.{branch}.merge"))?.Trim();
        return (string.IsNullOrEmpty(remote) ? null : remote, string.IsNullOrEmpty(merge) ? null : merge);
    }
}

/// <summary>Runs git in a working directory, streaming output to a log; carriage-return progress rewrites the last line.</summary>
public sealed class GitRunner(string workingDirectory, Action<string, bool> onLine, CancellationToken cancellation) {
    public void Log(string line) => onLine(line, false);

    public async Task<bool> RunAsync(params string[] arguments) {
        onLine("$ git " + string.Join(' ', arguments.Select(argument => argument.Contains(' ') ? $"\"{argument}\"" : argument)), false);
        using var process = Start(arguments);
        await using var registration = cancellation.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } });
        await Task.WhenAll(Pump(process.StandardError), Pump(process.StandardOutput), process.WaitForExitAsync());
        if (cancellation.IsCancellationRequested) {
            onLine("Cancelled.", false);
            return false;
        }

        if (process.ExitCode != 0) onLine($"git exited with code {process.ExitCode}", false);
        return process.ExitCode == 0;
    }

    /// <summary>Output of a quiet command, or null when it fails.</summary>
    public async Task<string?> CaptureAsync(params string[] arguments) {
        using var process = Start(arguments);
        var output = process.StandardOutput.ReadToEndAsync(cancellation);
        _ = process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);
        return process.ExitCode == 0 ? await output : null;
    }

    private Process Start(string[] arguments) {
        var info = new ProcessStartInfo("git") {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        // Never block on a terminal prompt nobody can answer; credential helpers and agents still work.
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start git");
    }

    private async Task Pump(System.IO.StreamReader reader) {
        var buffer = new char[1024];
        var line = new StringBuilder();
        var rewriting = false;
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellation)) > 0) {
            for (var i = 0; i < read; i++) {
                var c = buffer[i];
                if (c is '\r' or '\n') {
                    if (line.Length > 0) onLine(line.ToString(), rewriting);
                    line.Clear();
                    rewriting = c == '\r';
                }
                else {
                    line.Append(c);
                }
            }
        }

        if (line.Length > 0) onLine(line.ToString(), rewriting);
    }
}

/// <summary>Shows a git operation's live output and progress, and its outcome.</summary>
public sealed class GitOperationWindow : Window {
    private static readonly Regex Percent = new(@"(\d{1,3})%", RegexOptions.Compiled);
    private readonly List<string> _lines = new();
    private readonly SelectableTextBlock _log;
    private readonly ScrollViewer _logScroll;
    private readonly ProgressBar _progress;
    private readonly TextBlock _status;
    private readonly Button _button;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _finished;
    private bool _lastLineRewritable;

    public GitOperationWindow(GitOperation operation, string workingDirectory, Action<bool> onFinished) {
        Title = operation.Title;
        Width = 640;
        Height = 380;
        MinWidth = 380;
        MinHeight = 220;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppIcon.Window;
        this[!BackgroundProperty] = this.GetResourceObservable("GitKaySurfaceBrush").ToBinding();

        _status = new TextBlock { Text = $"{operation.Title}…", FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _status[!TextBlock.ForegroundProperty] = this.GetResourceObservable("GitKayTextBrush").ToBinding();
        _progress = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 100, Height = 4, MinHeight = 4, Margin = new Thickness(0, 10, 0, 10) };
        _log = new SelectableTextBlock { FontFamily = FontStacks.Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _log[!TextBlock.ForegroundProperty] = this.GetResourceObservable("GitKaySecondaryTextBrush").ToBinding();
        _logScroll = new ScrollViewer { Content = _log, Padding = new Thickness(8), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var logBorder = new Border { Child = _logScroll, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
        logBorder[!Border.BackgroundProperty] = this.GetResourceObservable("GitKayWindowBrush").ToBinding();
        logBorder[!Border.BorderBrushProperty] = this.GetResourceObservable("GitKayBorderBrush").ToBinding();
        _button = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(16, 4) };
        _button.Click += (_, _) => {
            if (_finished) Close();
            else _cancellation.Cancel();
        };

        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(_progress, Dock.Top);
        DockPanel.SetDock(_button, Dock.Bottom);
        layout.Children.Add(_status);
        layout.Children.Add(_progress);
        layout.Children.Add(_button);
        layout.Children.Add(logBorder);
        Content = layout;

        KeyDown += (_, e) => {
            if (e.Key == Avalonia.Input.Key.Escape && _finished) Close();
        };
        Closed += (_, _) => _cancellation.Cancel();
        Opened += async (_, _) => {
            var runner = new GitRunner(workingDirectory, (line, rewrite) => Dispatcher.UIThread.Post(() => Append(line, rewrite)), _cancellation.Token);
            bool succeeded;
            try {
                succeeded = await Task.Run(() => operation.Run(runner));
            }
            catch (Exception exception) when (exception is not OperationCanceledException) {
                Append(exception.Message, false);
                succeeded = false;
            }
            catch (OperationCanceledException) {
                succeeded = false;
            }

            Finish(operation.Title, succeeded);
            onFinished(succeeded);
        };
    }

    private void Append(string line, bool rewrite) {
        // "Receiving objects:  42% (…)\r" updates replace the previous progress line.
        if (rewrite && _lastLineRewritable && _lines.Count > 0) _lines[^1] = line;
        else _lines.Add(line);
        _lastLineRewritable = true;
        if (Percent.Match(line) is { Success: true } match && int.TryParse(match.Groups[1].Value, out var percent)) {
            _progress.IsIndeterminate = false;
            _progress.Value = Math.Clamp(percent, 0, 100);
        }

        _log.Text = string.Join('\n', _lines);
        _logScroll.ScrollToEnd();
    }

    private void Finish(string title, bool succeeded) {
        Dispatcher.UIThread.Post(() => {
            _finished = true;
            _progress.IsIndeterminate = false;
            _progress.Value = 100;
            _status.Text = succeeded ? $"✓  {title} — done" : _cancellation.IsCancellationRequested ? $"{title} — cancelled" : $"✗  {title} — failed";
            _status[!TextBlock.ForegroundProperty] = this.GetResourceObservable(succeeded ? "GitKayAddedAccentBrush" : "GitKayRemovedAccentBrush").ToBinding();
            _progress[!ProgressBar.ForegroundProperty] = this.GetResourceObservable(succeeded ? "GitKayAddedAccentBrush" : "GitKayRemovedAccentBrush").ToBinding();
            _button.Content = "Close";
            if (_lines.Count == 0) Append(succeeded ? "Nothing to report." : "No output.", false);
        });
    }
}
