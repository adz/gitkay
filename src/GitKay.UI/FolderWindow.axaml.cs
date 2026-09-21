using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
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

    /// <param name="pairs">Every pair under the folders; the ones that did not change are listed only by "All folders".</param>
    /// <param name="changed">The subset worth showing as changes, already decided by reading the files.</param>
    public FolderProjection(FolderMode mode, string leftRoot, string? rightRoot,
                            IReadOnlyList<GitKay.Core.Folder.Pair> pairs,
                            IReadOnlyList<GitKay.Core.Folder.Pair>? changed = null) {
        Mode = mode;
        LeftRoot = leftRoot;
        RightRoot = rightRoot;
        _allPaths.AddRange(pairs.Select(pair => GitKay.Core.FileChange.currentPath(pair.OldPath, pair.NewPath)));
        _pairs = (changed ?? pairs).ToList();
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
        ChosenLeft = leftRoot;
        ChosenRight = rightRoot;
        RebuildFileRows();
        SelectedFile = Files.FirstOrDefault();
    }

    public FolderMode Mode { get; }
    public string LeftRoot { get; }
    public string? RightRoot { get; }
    /// <summary>What the left-hand folder is called here: one folder is just the folder, two have sides.</summary>
    public string LeftLabel => Mode == FolderMode.Compare ? "Left" : "Folder";
    public string CompareLabel => Mode == FolderMode.Compare ? "Compare" : "Open";
    public string WindowTitle { get; }
    public string Subtitle { get; }
    public string ModeLabel => Mode == FolderMode.Compare ? "COMPARE" : "PREVIEW";

    public ObservableCollection<DiffFileProjection> Files { get; } = new();

    /// <summary>The file list as it is shown: full paths, a tree of what changed, or a tree of every file.</summary>
    public ObservableCollection<object> FileRows { get; } = new();

    [ObservableProperty] private CommitFileListMode _fileListMode = CommitFileListMode.Patch;

    private readonly HashSet<string> _collapsedFolders = new(StringComparer.Ordinal);

    public void SetFileListMode(string key) =>
        FileListMode = key switch {
            "tree" => CommitFileListMode.Tree,
            "all" => CommitFileListMode.All,
            _ => CommitFileListMode.Patch,
        };

    partial void OnFileListModeChanged(CommitFileListMode value) => RebuildFileRows();

    /// <summary>Folds a folder away, or opens it again, keeping the file that is selected selected.</summary>
    public void ToggleFolder(string path) {
        if (!_collapsedFolders.Remove(path)) _collapsedFolders.Add(path);
        RebuildFileRows();
    }

    /// <summary>
    /// The rows the list shows, through the same path tree the commit window uses, so a folder comparison groups
    /// its files exactly as a commit does.
    /// </summary>
    private void RebuildFileRows() {
        FileRows.Clear();
        if (FileListMode == CommitFileListMode.Patch) {
            foreach (var file in Files) {
                file.ListLabel = file.DisplayPath;
                file.ListIndent = default;
                FileRows.Add(file);
            }
            return;
        }

        var changed = Files.ToDictionary(file => file.ContentPath, file => file, StringComparer.Ordinal);
        var entries = Files
            .Select(file => Tuple.Create(file.ContentPath, Microsoft.FSharp.Core.FSharpOption<DiffFileProjection>.Some(file)))
            .Concat(FileListMode == CommitFileListMode.All
                ? _allPaths.Where(path => !changed.ContainsKey(path))
                    .Select(path => Tuple.Create(path, Microsoft.FSharp.Core.FSharpOption<DiffFileProjection>.None))
                : []);

        // In the tree a folder is open unless it was folded away; in the all-folders tree only folders holding a
        // change start open, and folding or opening one flips that.
        var expanded = Microsoft.FSharp.Core.FuncConvert.FromFunc<string, bool, bool>((path, hasChange) =>
            FileListMode == CommitFileListMode.All
                ? hasChange != _collapsedFolders.Contains(path)
                : !_collapsedFolders.Contains(path));

        foreach (var row in GitKay.Kit.PathTree.rows(expanded, entries)) {
            switch (row) {
                case GitKay.Kit.PathTreeRow<DiffFileProjection>.FolderRow folder:
                    FileRows.Add(new CommitFolderRow(folder.name, folder.path, folder.depth, folder.expanded, folder.items.Length));
                    break;
                case GitKay.Kit.PathTreeRow<DiffFileProjection>.FileRow { item: null } unchanged:
                    FileRows.Add(new RepoFileRow(unchanged.path, unchanged.name, unchanged.depth));
                    break;
                case GitKay.Kit.PathTreeRow<DiffFileProjection>.FileRow file:
                    file.item.Value.ListLabel = file.name;
                    file.item.Value.ListIndent = new Avalonia.Thickness(file.depth * CommitFileList.IndentWidth, 0, 0, 0);
                    FileRows.Add(file.item.Value);
                    break;
            }
        }
    }

    /// <summary>Every file under the folders, so "All folders" can show what did not change as well.</summary>
    private readonly List<string> _allPaths = new();
    public AvaloniaList<IDiffRowProjection> Rows { get; } = new();

    /// <summary>
    /// The folders as the bar currently shows them, which is what Compare opens. They start as the ones this
    /// window is showing, so changing one and pressing Compare is the whole gesture.
    /// </summary>
    [ObservableProperty] private string _chosenLeft = "";
    [ObservableProperty] private string? _chosenRight;

    /// <summary>Swaps the two, so a comparison can be read the other way round without retyping either path.</summary>
    public void SwapFolders() => (ChosenLeft, ChosenRight) = (ChosenRight ?? "", ChosenLeft);

    [ObservableProperty] private DiffFileProjection? _selectedFile;
    [ObservableProperty] private IDiffRowProjection? _selectedRow;
    [ObservableProperty] private string _status = "";
    /// <summary>What the diff surface underlines, from the find bar.</summary>
    [ObservableProperty] private string _findQuery = "";
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

    /// <summary>A folder path is a field: clicking it picks a new one, starting where the current one is.</summary>
    private async System.Threading.Tasks.Task<string?> PickFolder(string title, string startAt) {
        var options = new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        try {
            if (System.IO.Directory.Exists(startAt))
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(new Uri(startAt));
        }
        catch (Exception) { /* An unresolvable folder just means the picker opens where it likes. */ }
        var folders = await StorageProvider.OpenFolderPickerAsync(options);
        return folders.Count == 0 ? null : Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(folders[0]);
    }

    private async void OnPickLeftClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (DataContext is not FolderProjection projection) return;
        if (await PickFolder(projection.Mode == FolderMode.Compare ? "The folder on the left" : "Browse folder", projection.LeftRoot) is { } path)
            projection.ChosenLeft = path;
    }

    private async void OnPickRightClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (DataContext is not FolderProjection projection) return;
        if (await PickFolder("The folder on the right", projection.ChosenRight ?? projection.ChosenLeft) is { } path)
            projection.ChosenRight = path;
    }

    private void OnSwapFoldersClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (DataContext is FolderProjection projection) projection.SwapFolders();
    }

    /// <summary>Opens what is chosen in the bar, in a new window, leaving this one where it is.</summary>
    private void OnCompareClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (DataContext is not FolderProjection projection) return;
        try {
            if (projection.Mode == FolderMode.Compare) {
                var left = projection.ChosenLeft;
                var right = projection.ChosenRight ?? "";
                if (OpenFoldersDialog.Problem(left, right) is { } problem) { projection.Status = problem; return; }
                ExternalTools.StartGitKay(left, "diff", left, right);
            }
            else ExternalTools.StartGitKay(projection.ChosenLeft, "browse", projection.ChosenLeft);
        }
        catch (Exception error) {
            projection.Status = $"Could not open: {error.Message}";
        }
    }

    /// <summary>Double-clicking a folder folds it away or opens it again.</summary>
    private void OnFileRowDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e) {
        if (DataContext is FolderProjection projection
            && (e.Source as Control)?.DataContext is CommitFolderRow folder)
            projection.ToggleFolder(folder.Path);
    }

    private void OnPreviewRequested(object? sender, DiffFileProjection file) {
        if (DataContext is FolderProjection projection) projection.TogglePreview(file);
    }

    // ----- Menu -----

    private void OnFindMenuItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OpenFind();
    private void OnGoToFileMenuItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OpenFilePalette();
    private void OnCloseMenuItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
    private void OnAboutMenuItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => new AboutWindow().ShowDialog(this);
    private void OnSettingsMenuItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (DataContext is FolderProjection projection)
            projection.Status = "Settings live in the repository window";
    }

    private void OnOpenRepositoryMenuItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OpenElsewhere("repository");
    private void OnOpenFolderMenuItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OpenElsewhere("folder");
    private void OnCompareFoldersMenuItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OpenElsewhere("compare");

    private async void OpenElsewhere(string what) {
        if (DataContext is not FolderProjection projection) return;
        try {
            switch (what) {
                case "repository": {
                    if (await PickFolder("Open Git repository", projection.ChosenLeft) is { } path)
                        ExternalTools.StartGitKay(path);
                    break;
                }
                case "folder": {
                    if (await PickFolder("Browse folder", projection.ChosenLeft) is { } path)
                        ExternalTools.StartGitKay(path, "browse", path);
                    break;
                }
                case "compare": {
                    if (await OpenFoldersDialog.ShowAsync(this, projection.ChosenLeft) is { } folders)
                        ExternalTools.StartGitKay(folders.Left, "diff", folders.Left, folders.Right);
                    break;
                }
            }
        }
        catch (Exception error) { projection.Status = $"Could not open: {error.Message}"; }
    }

    private void OnLayoutMenuItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (DataContext is not FolderProjection projection || sender is not MenuItem { Tag: string key }) return;
        if (GitKay.Core.DiffLayoutModule.tryParse(key) is { } layout) projection.Layout = layout.Value;
    }

    private void OnFileModeMenuItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (DataContext is FolderProjection projection && sender is MenuItem { Tag: string key }) projection.SetFileListMode(key);
    }

    // ----- Find, and going to a file -----

    private void OpenFind() {
        FindOverlay.IsVisible = true;
        FindBox.Text = (DataContext as FolderProjection)?.FindQuery ?? "";
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void CloseFind() {
        FindOverlay.IsVisible = false;
        Surface.Focus();
    }

    private void OpenFilePalette() {
        if (DataContext is not FolderProjection projection) return;
        FilePaletteOverlay.IsVisible = true;
        FilePaletteBox.Text = "";
        FilePaletteList.ItemsSource = projection.Files.ToList();
        FilePaletteList.SelectedIndex = projection.Files.Count > 0 ? 0 : -1;
        FilePaletteBox.Focus();
    }

    private void CloseFilePalette() {
        FilePaletteOverlay.IsVisible = false;
        Surface.Focus();
    }

    /// <summary>Narrows the list as you type, on the path as it is shown.</summary>
    private void FilterFilePalette() {
        if (DataContext is not FolderProjection projection) return;
        var query = (FilePaletteBox.Text ?? "").Trim();
        var matches = query.Length == 0
            ? projection.Files.ToList()
            : projection.Files.Where(file => file.DisplayPath.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        FilePaletteList.ItemsSource = matches;
        FilePaletteList.SelectedIndex = matches.Count > 0 ? 0 : -1;
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        if (DataContext is not FolderProjection projection) { base.OnKeyDown(e); return; }

        if (FilePaletteOverlay.IsVisible) {
            switch (e.Key) {
                case Key.Escape: CloseFilePalette(); e.Handled = true; return;
                case Key.Down: FilePaletteList.SelectedIndex = Math.Min(FilePaletteList.SelectedIndex + 1, FilePaletteList.ItemCount - 1); e.Handled = true; return;
                case Key.Up: FilePaletteList.SelectedIndex = Math.Max(FilePaletteList.SelectedIndex - 1, 0); e.Handled = true; return;
                case Key.Enter:
                    if (FilePaletteList.SelectedItem is DiffFileProjection chosen) projection.SelectedFile = chosen;
                    CloseFilePalette();
                    e.Handled = true;
                    return;
                default:
                    Dispatcher.UIThread.Post(FilterFilePalette, DispatcherPriority.Input);
                    return;
            }
        }

        if (FindOverlay.IsVisible) {
            switch (e.Key) {
                case Key.Escape: projection.FindQuery = ""; CloseFind(); e.Handled = true; return;
                case Key.Enter: projection.FindQuery = FindBox.Text ?? ""; CloseFind(); e.Handled = true; return;
                default: return;
            }
        }

        if (e.Key == Key.P && e.KeyModifiers == KeyModifiers.Control) { OpenFilePalette(); e.Handled = true; return; }
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control) { OpenFind(); e.Handled = true; return; }
        if (e.Key == Key.Oem2 && e.KeyModifiers == KeyModifiers.None && !Surface.IsFocused) { OpenFind(); e.Handled = true; return; }
        if (MainWindow.DiffZoomDirection(e) is { } zoom && DataContext is FolderProjection) {
            Surface.CodeFontSize = Math.Clamp(Surface.CodeFontSize + (zoom == 0 ? 0 : zoom), 7, 32);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        base.OnKeyDown(e);
    }
}
