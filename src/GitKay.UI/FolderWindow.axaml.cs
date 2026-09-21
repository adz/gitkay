using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GitKay.UI;

/// <summary>Which of the two folder jobs this window is doing.</summary>
public enum FolderMode {
    /// <summary>Two folders side by side, showing what differs.</summary>
    Compare,
    /// <summary>One folder, read rather than compared.</summary>
    Preview,
}

/// <summary>
/// GitKay over plain folders: comparing two, or reading one. No repository is involved — the file list, the diff
/// surface and every preview are the same ones commits use, over diffs built from files on disk.
/// </summary>
public sealed partial class FolderProjection : ObservableObject {
    private readonly List<GitKay.Core.Folder.Pair> _pairs;

    public FolderProjection(FolderMode mode, string leftRoot, string? rightRoot, IReadOnlyList<GitKay.Core.Folder.Pair> pairs) {
        Mode = mode;
        LeftRoot = leftRoot;
        RightRoot = rightRoot;
        _pairs = pairs.ToList();
        foreach (var pair in _pairs) {
            // Browsing shows files, not changes: the pairing calls them added because there is no other side, but
            // nothing was added, so they are named plainly and their change glyph is left off.
            var displayPath = mode == FolderMode.Compare
                ? GitKay.Core.Folder.displayPathOf(pair)
                : GitKay.Core.FileChange.currentPath(pair.OldPath, pair.NewPath);
            Files.Add(new DiffFileProjection(new GitKay.Core.GitService.DiffFileSummary(pair.OldPath, pair.NewPath, displayPath)) {
                ShowsAsChange = mode == FolderMode.Compare,
            });
        }

        WindowTitle = mode == FolderMode.Compare
            ? $"{System.IO.Path.GetFileName(leftRoot.TrimEnd('/', '\\'))} ↔ {System.IO.Path.GetFileName((rightRoot ?? "").TrimEnd('/', '\\'))} — GitKay"
            : $"{System.IO.Path.GetFileName(leftRoot.TrimEnd('/', '\\'))} — GitKay";
        Subtitle = mode == FolderMode.Compare ? $"{leftRoot}  ↔  {rightRoot}" : leftRoot;
        SelectedFile = Files.FirstOrDefault();
    }

    public FolderMode Mode { get; }
    public string LeftRoot { get; }
    public string? RightRoot { get; }
    /// <summary>What the left-hand folder is called here: one folder is just the folder, two have sides.</summary>
    public string LeftLabel => Mode == FolderMode.Compare ? "Left" : "Folder";
    public string ChangeFoldersLabel => Mode == FolderMode.Compare ? "Change folders…" : "Change folder…";
    public string WindowTitle { get; }
    public string Subtitle { get; }
    public string ModeLabel => Mode == FolderMode.Compare ? "COMPARE" : "PREVIEW";

    public ObservableCollection<DiffFileProjection> Files { get; } = new();
    public AvaloniaList<IDiffRowProjection> Rows { get; } = new();

    [ObservableProperty] private DiffFileProjection? _selectedFile;
    [ObservableProperty] private IDiffRowProjection? _selectedRow;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private GitKay.Core.DiffLayout _layout = GitKay.Core.DiffLayout.Unified;

    public bool IsEmpty => Files.Count == 0;

    public string EmptyMessage => Mode == FolderMode.Compare
        ? "These folders hold the same files."
        : "This folder has no files to show.";

    public string FileCountLabel =>
        Files.Count == 1 ? "1 file" : $"{Files.Count} files";

    /// <summary>Browsing has nothing to compare, so added and removed counts would be the file's own length.</summary>
    public bool ShowsChanges => Mode == FolderMode.Compare;

    public bool HasTotals => ShowsChanges && Files.Any(file => file.IsLoaded);
    public string TotalAddedText => $"+{Files.Sum(file => file.AddedLines)}";
    public string TotalRemovedText => $"−{Files.Sum(file => file.RemovedLines)}";

    /// <summary>The pair behind a file row, for reading it off disk when it is selected.</summary>
    private GitKay.Core.Folder.Pair? PairFor(DiffFileProjection file) {
        var index = Files.IndexOf(file);
        return index >= 0 && index < _pairs.Count ? _pairs[index] : null;
    }

    partial void OnSelectedFileChanged(DiffFileProjection? value) {
        if (value == null) { Rows.Clear(); return; }
        Load(value);
        Rebuild();
    }

    /// <summary>
    /// Reads and diffs one file the first time it is looked at. A whole folder is not diffed up front: most of the
    /// files in it are never opened, and reading them all would make opening the window as slow as the largest tree.
    /// </summary>
    private void Load(DiffFileProjection file) {
        if (file.IsLoaded || PairFor(file) is not { } pair) return;
        var diff = GitKay.Core.FolderSource.diff(pair);
        file.ApplyContent(diff);
        OnPropertyChanged(nameof(HasTotals));
        OnPropertyChanged(nameof(TotalAddedText));
        OnPropertyChanged(nameof(TotalRemovedText));
    }

    public void TogglePreview(DiffFileProjection file) {
        if (PairFor(file) is not { } pair) return;
        var path = file.ContentPath;
        var kind = GitKay.Core.Markdown.previewKind(path);

        if (file.IsRenderedMarkdown || file.IsFormattedPreview || file.IsImagePreview) {
            file.ClearRendered();
            file.ClearFormatted();
            file.ClearPreviewImage();
            Rebuild();
            return;
        }

        if (kind.IsImagePreview) {
            var bytes = GitKay.Core.FolderSource.previewBytes(pair);
            if (bytes.IsError) { Status = GitKay.Core.GitErrorModule.describe(bytes.ErrorValue); return; }
            try { file.ApplyPreviewImage(new Avalonia.Media.Imaging.Bitmap(new System.IO.MemoryStream(bytes.ResultValue)), bytes.ResultValue.Length); }
            catch (Exception error) { Status = $"Cannot preview image: {error.Message}"; return; }
        }
        else if (kind is GitKay.Core.PreviewKind.FormattedPreview formatted) {
            if (!ApplyFormatted(file, pair, formatted.format)) return;
        }
        else if (kind.IsMarkdownPreview) {
            var rendered = GitKay.Core.GitService.loadFolderRenderedMarkdown(
                LeftRoot, RightRoot ?? LeftRoot, pair, LoadRemoteImages);
            if (rendered.IsError) { Status = GitKay.Core.GitErrorModule.describe(rendered.ErrorValue); return; }
            ApplyRendered(file, rendered.ResultValue);
        }

        Rebuild();
    }

    /// <summary>Both sides reformatted and re-diffed, so the preview compares documents rather than indentation.</summary>
    private bool ApplyFormatted(DiffFileProjection file, GitKay.Core.Folder.Pair pair, GitKay.Core.PreviewFormat format) {
        var texts = GitKay.Core.FolderSource.sideTexts(pair);
        if (texts.IsError) { Status = GitKay.Core.GitErrorModule.describe(texts.ErrorValue); return false; }

        var (oldText, newText) = texts.ResultValue;
        var oldFormatted = oldText.Length == 0 ? "" : Formatted(format, oldText);
        var newFormatted = newText.Length == 0 ? "" : Formatted(format, newText);
        if (oldFormatted == null || newFormatted == null) return false;

        file.ApplyFormatted(GitKay.Core.Folder.diffOf(pair, oldFormatted, newFormatted));
        return true;
    }

    private string? Formatted(GitKay.Core.PreviewFormat format, string text) {
        var result = GitKay.Core.Markdown.formatForPreview(format, text);
        if (result.IsOk) return result.ResultValue;
        Status = result.ErrorValue;
        return null;
    }

    /// <summary>
    /// Whether rendered Markdown may fetch images from the internet. Off by default, as it is for repositories:
    /// fetching one tells its host that this document was opened.
    /// </summary>
    [ObservableProperty] private bool _loadRemoteImages;

    partial void OnLoadRemoteImagesChanged(bool value) {
        // Re-render what is on screen, so the setting takes effect on the document being read rather than the next one.
        if (SelectedFile is { IsRenderedMarkdown: true } file) { TogglePreview(file); TogglePreview(file); }
    }

    [ObservableProperty] private IReadOnlyDictionary<string, Avalonia.Media.Imaging.Bitmap> _oldImages =
        new Dictionary<string, Avalonia.Media.Imaging.Bitmap>();
    [ObservableProperty] private IReadOnlyDictionary<string, Avalonia.Media.Imaging.Bitmap> _images =
        new Dictionary<string, Avalonia.Media.Imaging.Bitmap>();

    /// <summary>Decodes the images the rendering asked for, so the surface can draw them beside their text.</summary>
    private void ApplyRendered(DiffFileProjection file, GitKay.Core.RenderedMarkdownContent content) {
        file.ApplyRenderedContent(content);
        var oldImages = new Dictionary<string, Avalonia.Media.Imaging.Bitmap>(StringComparer.Ordinal);
        var newImages = new Dictionary<string, Avalonia.Media.Imaging.Bitmap>(StringComparer.Ordinal);
        foreach (var image in content.Images) {
            if (image.Bytes == null) continue;
            try {
                var bitmap = new Avalonia.Media.Imaging.Bitmap(new System.IO.MemoryStream(image.Bytes.Value));
                (image.Side.IsOld ? oldImages : newImages)[image.Source] = bitmap;
            }
            catch { /* An image that will not decode is simply not drawn; its text stays. */ }
        }
        foreach (var bitmap in OldImages.Values) bitmap.Dispose();
        foreach (var bitmap in Images.Values) bitmap.Dispose();
        OldImages = oldImages;
        Images = newImages;
    }

    private void Rebuild() {
        var rows = new List<IDiffRowProjection>();
        if (SelectedFile is { } file) DiffRowBuilder.AppendFile(rows, file, Layout);
        Rows.Clear();
        Rows.AddRange(rows);
        SelectedRow = Rows.FirstOrDefault(row => row is DiffLineProjection or ImagePreviewRowProjection);
    }

    partial void OnLayoutChanged(GitKay.Core.DiffLayout value) => Rebuild();
}

public partial class FolderWindow : Window {
    public FolderWindow() {
        InitializeComponent();
    }

    public FolderWindow(FolderProjection projection) : this() {
        DataContext = projection;
        Opened += (_, _) => Surface.Focus();
    }

    /// <summary>Choosing what this window shows, without going back to a repository window to do it.</summary>
    private async void OnChangeFoldersClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (DataContext is not FolderProjection projection) return;
        try {
            if (projection.Mode == FolderMode.Compare) {
                if (await OpenFoldersDialog.ShowAsync(this, projection.LeftRoot) is not { } folders) return;
                ExternalTools.StartGitKay(folders.Left, "diff", folders.Left, folders.Right);
            }
            else {
                var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions {
                    Title = "Browse folder",
                    AllowMultiple = false,
                    SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(new Uri(projection.LeftRoot)),
                });
                if (folders.Count == 0 || Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(folders[0]) is not { } path) return;
                ExternalTools.StartGitKay(path, "browse", path);
            }
        }
        catch (Exception error) {
            projection.Status = $"Could not open: {error.Message}";
        }
    }

    private void OnPreviewRequested(object? sender, DiffFileProjection file) {
        if (DataContext is FolderProjection projection) projection.TogglePreview(file);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        base.OnKeyDown(e);
    }
}
