using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GitKay.UI;

/// <summary>One file at a commit shown whole — its change in full context, or all of it when unchanged.</summary>
public sealed partial class WholeFileProjection : ObservableObject {
    private readonly DiffFileProjection _file;
    private readonly FileTarget _target;

    public WholeFileProjection(MainProjection main, string shortHash, string subject, FileTarget target) {
        Main = main;
        ShortHash = shortHash;
        Subject = subject;
        _target = target;
        _file = new DiffFileProjection(new GitKay.Core.GitService.DiffFileSummary(target.OldPath, target.NewPath, target.DisplayPath));
        _layout = main.DiffLayout;
        WindowTitle = $"{target.DisplayPath} @ {shortHash}";
        Rebuild();
    }

    public MainProjection Main { get; }
    public string ShortHash { get; }
    public string Subject { get; }
    public string WindowTitle { get; }
    public AvaloniaList<IDiffRowProjection> Rows { get; } = new();
    [ObservableProperty] private IDiffRowProjection? _selectedRow;
    [ObservableProperty] private GitKay.Core.DiffLayout _layout;
    [ObservableProperty] private string _loadStatus = "Loading…";

    public bool IsUnifiedMode => Layout.IsUnified;
    public bool IsSideBySideMode => Layout.IsSideBySide;
    public bool IsNewMode => Layout.IsNewFile;
    public bool IsOldMode => Layout.IsOldFile;

    partial void OnLayoutChanged(GitKay.Core.DiffLayout value) {
        OnPropertyChanged(nameof(IsUnifiedMode));
        OnPropertyChanged(nameof(IsSideBySideMode));
        OnPropertyChanged(nameof(IsNewMode));
        OnPropertyChanged(nameof(IsOldMode));
        Rebuild();
    }

    [RelayCommand]
    private void SetMode(string key) {
        if (GitKay.Core.DiffLayoutModule.tryParse(key) is { } layout) Layout = layout.Value;
    }

    [RelayCommand]
    private void ToggleCollapsed(DiffFileProjection file) {
        file.IsCollapsed = !file.IsCollapsed;
        Rebuild();
    }

    [RelayCommand]
    private void OpenInVsCode() {
        var line = SelectedRow is DiffLineProjection selected ? selected.NewLineNo ?? selected.OldLineNo : null;
        Main.OpenInVsCode(_target, line);
    }

    public async Task LoadAsync(Func<Microsoft.FSharp.Core.FSharpResult<GitKay.Core.Models.FileDiff, GitKay.Core.GitError>> load, string unchangedNote) {
        var result = await Task.Run(load);
        if (result.IsError) {
            LoadStatus = GitKay.Core.GitErrorModule.describe(result.ErrorValue);
            return;
        }

        var content = result.ResultValue;
        _file.ApplyContent(content);
        LoadStatus = content.Hunks.IsEmpty ? "Binary or empty file" : _target.Changed == null ? unchangedNote : "";
        Rebuild();
    }

    private void Rebuild() {
        var rows = new List<IDiffRowProjection>();
        DiffRowBuilder.AppendFile(rows, _file, Layout);
        Rows.Clear();
        Rows.AddRange(rows);
    }
}

public partial class WholeFileWindow : Window {
    private Key? _pendingG;

    public WholeFileWindow() {
        InitializeComponent();
        Icon = AppIcon.Window;
    }

    public WholeFileWindow(MainProjection main, string repositoryPath, string hash, string shortHash, FileTarget target)
        : this(main, shortHash, main.SelectedCommit?.Subject ?? "", target,
            () => GitKay.Core.GitService.loadWholeFile(repositoryPath, hash, target.OldPath, target.NewPath), "unchanged in this commit") {
    }

    /// <summary>A file with its whole content; <paramref name="label"/> names where it's from (a short hash, "staged", "HEAD").</summary>
    public WholeFileWindow(MainProjection main, string label, string subject, FileTarget target,
        Func<Microsoft.FSharp.Core.FSharpResult<GitKay.Core.Models.FileDiff, GitKay.Core.GitError>> load, string unchangedNote) : this() {
        var projection = new WholeFileProjection(main, label, subject, target);
        DataContext = projection;
        Surface.TextCopied += (_, lines) => projection.LoadStatus = lines switch { 0 => "Copied", 1 => "Copied 1 line", _ => $"Copied {lines} lines" };
        AddHandler(KeyDownEvent, OnWindowKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += async (_, _) => {
            Surface.Focus();
            await projection.LoadAsync(load, unchangedNote);
        };
    }

    /// <summary>The diff pane's movement keys: j / k, gg / G, Ctrl+D / Ctrl+U, ]c / [c, zoom, and Esc or q to close.</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e) {
        if (DataContext is not WholeFileProjection projection) return;
        var none = e.KeyModifiers == KeyModifiers.None;
        var prefix = _pendingG;
        _pendingG = null;

        if (MainWindow.DiffZoomDirection(e) is { } zoom) { projection.Main.ZoomDiff(zoom); e.Handled = true; return; }
        if (prefix == Key.G && none && e.Key == Key.G) { Surface.MoveSelection(int.MinValue / 2); e.Handled = true; return; }
        if (prefix is Key.OemCloseBrackets or Key.OemOpenBrackets && none && e.Key == Key.C) {
            Surface.MoveToHunk(prefix == Key.OemCloseBrackets ? 1 : -1);
            e.Handled = true;
            return;
        }

        switch (e.Key) {
            case Key.J or Key.Down when none: Surface.MoveSelection(1); break;
            case Key.K or Key.Up when none: Surface.MoveSelection(-1); break;
            case Key.G or Key.OemCloseBrackets or Key.OemOpenBrackets when none: _pendingG = e.Key; break;
            case Key.G when e.KeyModifiers == KeyModifiers.Shift: Surface.MoveSelection(int.MaxValue / 2); break;
            case Key.D when e.KeyModifiers == KeyModifiers.Control: Surface.MoveSelection(Math.Max(1, Surface.ViewportRowCount / 2)); break;
            case Key.U when e.KeyModifiers == KeyModifiers.Control: Surface.MoveSelection(-Math.Max(1, Surface.ViewportRowCount / 2)); break;
            case Key.PageDown when none: Surface.MoveSelection(Surface.ViewportRowCount); break;
            case Key.PageUp when none: Surface.MoveSelection(-Surface.ViewportRowCount); break;
            case Key.Q when none: Close(); break;
            case Key.Escape when !Surface.HasTextSelection: Close(); break;
            default: return;
        }

        e.Handled = true;
    }
}
