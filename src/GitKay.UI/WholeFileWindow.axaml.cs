using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
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
        var previewKind = GitKay.Core.Markdown.previewKind(target.Path);
        IsMarkdown = previewKind.IsMarkdownPreview;
        IsImage = previewKind.IsImagePreview;
        _formattedAs = previewKind is GitKay.Core.PreviewKind.FormattedPreview formatted ? formatted.format : null;
        Rebuild();
    }

    public MainProjection Main { get; }
    public string ShortHash { get; }
    public string Subject { get; }
    public string WindowTitle { get; }
    public string DocumentPath => _target.Path;
    public bool IsMarkdown { get; }
    public bool IsImage { get; }
    /// <summary>The format this file can be reformatted as for reading, or null when it cannot.</summary>
    private readonly string? _formattedAs;
    public bool IsFormattable => _formattedAs != null;
    public bool IsPreviewable => (IsMarkdown || IsImage || IsFormattable) && PreviewAvailable;
    public bool IsMarkdownPreview => IsPreview && IsMarkdown;
    public bool IsImagePreview => IsPreview && IsImage;
    public AvaloniaList<IDiffRowProjection> Rows { get; } = new();
    [ObservableProperty] private IDiffRowProjection? _selectedRow;
    [ObservableProperty] private GitKay.Core.DiffLayout _layout;
    [ObservableProperty] private string _loadStatus = "Loading…";
    [ObservableProperty] private bool _isPreview;
    [ObservableProperty] private Bitmap? _imageSource;
    [ObservableProperty] private bool _previewAvailable = true;
    [ObservableProperty] private IReadOnlyDictionary<string, Bitmap> _images = new Dictionary<string, Bitmap>();
    [ObservableProperty] private IReadOnlyDictionary<string, Bitmap> _oldImages = new Dictionary<string, Bitmap>();
    private GitKay.Core.RenderedMarkdownContent? _renderedContent;

    public bool IsSource => !IsPreview;
    public bool IsUnifiedMode => Layout.IsUnified;
    public bool IsSideBySideMode => Layout.IsSideBySide;
    public bool IsNewMode => Layout.IsNewFile;
    public bool IsOldMode => Layout.IsOldFile;

    partial void OnPreviewAvailableChanged(bool value) => OnPropertyChanged(nameof(IsPreviewable));

    partial void OnIsPreviewChanged(bool value) {
        OnPropertyChanged(nameof(IsSource));
        OnPropertyChanged(nameof(IsMarkdownPreview));
        OnPropertyChanged(nameof(IsImagePreview));
        // The file header's preview icon reads this, so it lights up with the window's own toggle.
        _file.IsRenderedMarkdown = value && IsMarkdown;
        Rebuild();
    }

    partial void OnLayoutChanged(GitKay.Core.DiffLayout value) {
        OnPropertyChanged(nameof(IsUnifiedMode));
        OnPropertyChanged(nameof(IsSideBySideMode));
        OnPropertyChanged(nameof(IsNewMode));
        OnPropertyChanged(nameof(IsOldMode));
        Rebuild();
    }

    [RelayCommand]
    private void TogglePreview() {
        if (IsPreviewable) IsPreview = !IsPreview;
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

    public void ApplyPayload(GitKay.Core.GitService.WholeFilePayload payload) {
        _file.ApplyContent(payload.File);
        LoadStatus = payload.File.Hunks.IsEmpty ? "Binary or empty file" : "";
        if (payload.Rendered is { } rendered) {
            _renderedContent = rendered.Value;
            var oldImages = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
            var newImages = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
            foreach (var image in rendered.Value.Images) {
                if (image.Bytes == null) continue;
                try {
                    var bitmap = new Bitmap(new System.IO.MemoryStream(image.Bytes.Value));
                    (image.Side.IsOld ? oldImages : newImages)[image.Source] = bitmap;
                }
                catch { }
            }
            foreach (var bitmap in OldImages.Values) bitmap.Dispose();
            foreach (var bitmap in Images.Values) bitmap.Dispose();
            OldImages = oldImages;
            Images = newImages;
        }
        if (payload.ImageBytes != null) ApplyImage(payload.ImageBytes.Value);
        Rebuild();
    }

    public void ApplyImage(byte[] bytes) {
        try { ImageSource = new Bitmap(new System.IO.MemoryStream(bytes)); LoadStatus = ""; }
        catch (Exception error) { LoadStatus = $"Cannot preview image: {error.Message}"; IsPreview = false; }
    }

    private void Rebuild() {
        var rows = new List<IDiffRowProjection>();
        if (IsPreview && _formattedAs is { } format) {
            rows.Add(_file.Header);
            var source = _file.WholeText(Layout.IsOldFile);
            var formatted = GitKay.Core.Markdown.formatForPreview(format, source);
            if (formatted.IsOk) {
                LoadStatus = "";
                var lines = formatted.ResultValue.Replace("\r\n", "\n").Split('\n');
                for (var i = 0; i < lines.Length; i++)
                    rows.Add(new DiffLineProjection(new GitKay.Core.Models.DiffLine(
                        GitKay.Core.Models.LineType.Context, lines[i],
                        Microsoft.FSharp.Core.FSharpOption<int>.Some(i + 1),
                        Microsoft.FSharp.Core.FSharpOption<int>.Some(i + 1))));
            }
            else {
                // A file that will not parse is shown as it is rather than as an empty pane.
                LoadStatus = formatted.ErrorValue;
                DiffRowBuilder.AppendFile(rows, _file, Layout);
                Rows.Clear();
                Rows.AddRange(rows);
                return;
            }
        }
        else if (IsPreview && IsMarkdown) {
            rows.Add(_file.Header);
            if (_renderedContent is { } content) {
                var projected = Layout.IsOldFile ? content.OldRows : Layout.IsNewFile ? content.NewRows : content.DiffRows;
                rows.AddRange(projected.Select(row => new RenderedMarkdownRowProjection(row)));
            }
        }
        else DiffRowBuilder.AppendFile(rows, _file, Layout);
        Rows.Clear();
        Rows.AddRange(rows);
    }
}

public partial class WholeFileWindow : Window {
    private Key? _pendingG;
    private string _previewFind = "";
    private bool _previewFindForward = true;
    private Func<string, bool>? _relativeLinkRequested;

    public WholeFileWindow() {
        InitializeComponent();
        Icon = AppIcon.Window;
    }

    public WholeFileWindow(MainProjection main, string label, string subject, FileTarget target,
        GitKay.Core.App.WholeFileState state, bool preview = false) : this() {
        var projection = new WholeFileProjection(main, label, subject, target) { IsPreview = preview };
        DataContext = projection;
        projection.ApplyPayload(state.Payload!.Value);
        void ApplyChanged(GitKay.Core.App.WholeFileState changed) {
            if (changed.RequestId != state.RequestId || changed.Payload == null) return;
            var anchor = Surface.CaptureMarkdownViewAnchor();
            projection.ApplyPayload(changed.Payload.Value);
            Surface.RestoreMarkdownViewAnchor(anchor);
        }
        main.WholeFileChanged += ApplyChanged;
        Closed += (_, _) => {
            main.WholeFileChanged -= ApplyChanged;
            foreach (var bitmap in projection.OldImages.Values) bitmap.Dispose();
            foreach (var bitmap in projection.Images.Values) bitmap.Dispose();
            projection.ImageSource?.Dispose();
        };
        Surface.TextCopied += (_, lines) => projection.LoadStatus = lines switch { 0 => "Copied", 1 => "Copied 1 line", _ => $"Copied {lines} lines" };
        Surface.RenderedLinkRequested += (_, link) => OpenRenderedLink(projection, link);
        // The header's preview icon toggles this window's own preview; without this it fires into nothing.
        Surface.PreviewRequested += (_, _) => {
            if (!projection.IsPreviewable) return;
            var anchor = Surface.CaptureMarkdownViewAnchor();
            projection.TogglePreviewCommand.Execute(null);
            Surface.RestoreMarkdownViewAnchor(anchor);
        };
        AddHandler(KeyDownEvent, OnWindowKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _relativeLinkRequested = link => {
            var resolved = GitKay.Core.Markdown.resolveTarget(target.Path, link.Split('#')[0]);
            if (resolved is not GitKay.Core.MarkdownTarget.RepositoryPath path) return false;
            if (!path.Item.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !path.Item.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)) return false;
            main.RequestWholeFile(new FileTarget(path.Item, path.Item, path.Item, null), preview: true);
            return true;
        };
        Opened += (_, _) => Surface.Focus();
    }

    private void OpenRenderedLink(WholeFileProjection projection, string link) {
        if (link.StartsWith("gitkay-load-image:", StringComparison.Ordinal)) { projection.Main.LoadRemoteMarkdownImage(link[18..]); return; }
        if (link.StartsWith('#')) { Surface.MoveToHeading(link); return; }
        var resolved = GitKay.Core.Markdown.resolveTarget(projection.DocumentPath, link);
        if (resolved is GitKay.Core.MarkdownTarget.RemoteUrl remote) {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(remote.Item) { UseShellExecute = true }); }
            catch (Exception error) { projection.LoadStatus = $"Could not open link: {error.Message}"; }
        }
        else if (_relativeLinkRequested?.Invoke(link) != true) projection.LoadStatus = $"Cannot open link: {link}";
    }

    /// <summary>The diff pane's movement keys: j / k, gg / G, Ctrl+D / Ctrl+U, ]c / [c, zoom, and Esc or q to close.</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e) {
        if (DataContext is not WholeFileProjection projection) return;
        var none = e.KeyModifiers == KeyModifiers.None;
        var prefix = _pendingG;
        _pendingG = null;

        if (PreviewSearchOverlay.IsVisible) {
            if (e.Key == Key.Escape) { PreviewSearchOverlay.IsVisible = false; Surface.Focus(); e.Handled = true; return; }
            if (e.Key == Key.Enter) {
                _previewFind = PreviewSearchBox.Text ?? "";
                Surface.FindQuery = _previewFind;
                PreviewSearchOverlay.IsVisible = false; Surface.Focus(); e.Handled = true; return;
            }
            return;
        }
        if (e.Key == Key.V && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && projection.IsPreviewable) {
            projection.TogglePreviewCommand.Execute(null); e.Handled = true; return;
        }
        if (MainWindow.DiffZoomDirection(e) is { } zoom) { projection.Main.ZoomDiff(zoom); e.Handled = true; return; }
        if (projection.IsPreview && e.Key == Key.Oem2 && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift) {
            _previewFindForward = e.KeyModifiers == KeyModifiers.None;
            PreviewSearchOverlay.IsVisible = true; PreviewSearchBox.Text = _previewFind; PreviewSearchBox.Focus(); PreviewSearchBox.SelectAll();
            e.Handled = true; return;
        }
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
