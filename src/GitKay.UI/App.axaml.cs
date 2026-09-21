using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Elmish.Avalonia.Glue;
using GitKay.Core;

namespace GitKay.UI;

public partial class App : Application {
    public static string[] StartupArgs { get; set; } = Array.Empty<string>();
    public static GitStartup.LaunchMode LaunchMode { get; set; } = GitStartup.LaunchMode.History;
    private static Exception? _startupInitializationFailure;

    public override void Initialize() {
        try {
            AvaloniaXamlLoader.Load(this);
#if DEBUG
            this.AttachDeveloperTools();
#endif
        }
        catch (Exception ex) {
            _startupInitializationFailure = ex;
        }
    }

    public override void OnFrameworkInitializationCompleted() {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            FatalErrorPresenter.Initialize(desktop);
            DiagnosticsLog.StartHangWatchdog();

            if (_startupInitializationFailure != null) {
                FatalErrorPresenter.ShowStartupFailure(desktop, _startupInitializationFailure);
                _startupInitializationFailure = null;
                base.OnFrameworkInitializationCompleted();
                return;
            }

            _ = InitializeAppAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// gitkay diff &lt;a&gt; &lt;b&gt; and gitkay browse &lt;dir&gt;: GitKay over plain folders, with no repository
    /// discovered, opened or needed. Closing the window exits.
    /// </summary>
    private async Task InitializeFolderWindowAsync(IClassicDesktopStyleApplicationLifetime desktop, FolderMode mode, string left, string? right) {
        try {
            var settings = await Task.Run(() => new AppSettingsStore().Load());
            RequestedThemeVariant =
                settings.Theme.IsLightTheme ? Avalonia.Styling.ThemeVariant.Light
                : settings.Theme.IsDarkTheme ? Avalonia.Styling.ThemeVariant.Dark
                : Avalonia.Styling.ThemeVariant.Default;

            var built = await Task.Run(() => BuildFolderPairs(mode, left, right));
            if (built.Error is { } message) {
                Console.Error.WriteLine(message);
                desktop.Shutdown(2);
                return;
            }

            var projection = new FolderProjection(mode, left, right, built.Pairs, built.Changed);
            // The saved settings that mean something away from a repository: layout, previews, remote images.
            projection.ApplySettings(settings);
            var window = new FolderWindow(projection) {
                WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen,
            };
            desktop.MainWindow = window;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch (Exception ex) {
            FatalErrorPresenter.ShowStartupFailure(desktop, ex);
        }
    }

    /// <summary>Reads the folders and pairs their files, off the UI thread. A folder that cannot be read says so.</summary>
    private static (IReadOnlyList<GitKay.Core.Folder.Pair> Pairs, IReadOnlyList<GitKay.Core.Folder.Pair> Changed, string? Error) BuildFolderPairs(FolderMode mode, string left, string? right) {
        var empty = Array.Empty<GitKay.Core.Folder.Pair>();
        var leftEntries = GitKay.Core.FolderSource.read(left);
        if (leftEntries.IsError) return (empty, empty, GitKay.Core.GitErrorModule.describe(leftEntries.ErrorValue));

        if (mode == FolderMode.Preview) {
            // One folder: every file paired against nothing, so it reads as the folder's contents rather than a diff.
            var only = GitKay.Core.Folder.pair(Microsoft.FSharp.Collections.SeqModule.Empty<GitKay.Core.Folder.Entry>(), leftEntries.ResultValue).ToList();
            return (only, only, null);
        }

        var rightEntries = GitKay.Core.FolderSource.read(right ?? "");
        if (rightEntries.IsError) return (empty, empty, GitKay.Core.GitErrorModule.describe(rightEntries.ErrorValue));

        var pairs = GitKay.Core.Folder.pair(leftEntries.ResultValue, rightEntries.ResultValue);
        return (pairs.ToList(), GitKay.Core.FolderSource.changedPairs(pairs).ToList(), null);
    }

    /// <summary>gitkay gui: only the commit window, with the saved theme; closing it exits.</summary>
    private async Task InitializeCommitWindowAsync(IClassicDesktopStyleApplicationLifetime desktop) {
        try {
            var (settings, repository) = await Task.Run(() => (new AppSettingsStore().Load(), GitService.tryDiscoverRepositoryPath()));
            RequestedThemeVariant =
                settings.Theme.IsLightTheme ? Avalonia.Styling.ThemeVariant.Light
                : settings.Theme.IsDarkTheme ? Avalonia.Styling.ThemeVariant.Dark
                : Avalonia.Styling.ThemeVariant.Default;
            var directory = string.IsNullOrEmpty(repository) ? null : GitService.workingDirectory(repository);
            if (directory == null) {
                Console.Error.WriteLine("gitkay gui: run it inside a Git repository with a working tree.");
                desktop.Shutdown(2);
                return;
            }

            var window = new CommitWindow(repository!, System.IO.Path.GetFileName(directory)) { WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen };
            window.OpenInVsCode = (path, line) => {
                var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, path));
                try {
                    ExternalTools.StartVsCode(line is { } number ? [directory, "--goto", $"{full}:{number}"] : [directory, "--goto", full], directory);
                }
                catch (Exception) {
                    // VS Code isn't on PATH; nothing else to do from a commit-only window.
                }
            };
            desktop.MainWindow = window;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch (Exception ex) {
            FatalErrorPresenter.ShowStartupFailure(desktop, ex);
        }
    }

    private async Task InitializeAppAsync(IClassicDesktopStyleApplicationLifetime desktop) {
        if (LaunchMode.IsCommit) {
            await InitializeCommitWindowAsync(desktop);
            return;
        }
        if (LaunchMode is GitStartup.LaunchMode.FolderCompare compare) {
            await InitializeFolderWindowAsync(desktop, FolderMode.Compare, compare.left, compare.right);
            return;
        }
        if (LaunchMode is GitStartup.LaunchMode.FolderPreview preview) {
            await InitializeFolderWindowAsync(desktop, FolderMode.Preview, preview.folder, null);
            return;
        }
        try {
            var (persistedSettings, persistedUiState, repoKey) = await Task.Run(() => {
                var settingsStore = new AppSettingsStore();
                var uiStateStore = new AppUiStateStore();
                return (settingsStore.Load(), uiStateStore.Load(), GitService.tryDiscoverRepositoryPath());
            });

            // Started somewhere that is not a repository and given nothing to show: browse the folder instead of
            // opening a history window with no history in it.
            if (string.IsNullOrEmpty(repoKey) && StartupArgs.Length == 0) {
                await InitializeFolderWindowAsync(desktop, FolderMode.Preview, Environment.CurrentDirectory, null);
                return;
            }

            var settingsStore = new AppSettingsStore();
            var uiStateStore = new AppUiStateStore();
            var currentUiState = persistedUiState;
            var startupArgs = Microsoft.FSharp.Collections.ListModule.ToArray(GitKay.Core.GitStartup.settingsArguments(persistedSettings));

            if (StartupArgs.Length > 0) {
                var mergedArgs = new string[startupArgs.Length + StartupArgs.Length];
                startupArgs.CopyTo(mergedArgs, 0);
                StartupArgs.CopyTo(mergedArgs, startupArgs.Length);
                startupArgs = mergedArgs;
            }

            var mainWindow = new MainWindow();
            if (persistedUiState.WindowWidth is { } savedWidth) mainWindow.Width = savedWidth.Value;
            if (persistedUiState.WindowHeight is { } savedHeight) mainWindow.Height = savedHeight.Value;

            mainWindow.ApplyLayout(persistedUiState.Layout);

            var projection = new MainProjection { RepositoryPath = repoKey };
            projection.ApplySettings(persistedSettings);
            projection.LoadRecentSearches(persistedUiState.RecentSearches);
            projection.ApplyViewPreferences(persistedUiState.ViewPreferences.ToDictionary());
            mainWindow.DataContext = projection;
            desktop.MainWindow = mainWindow;

            var persistedPropertyNames = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(MainProjection.ShowBranchRefs),
                nameof(MainProjection.ShowStashes),
                nameof(MainProjection.DiffContextLineCount),
                nameof(MainProjection.SelectedDiffContextLineCount),
                nameof(MainProjection.SelectedDiffPresentationMode),
                nameof(MainProjection.CommitRowFontFamily),
                nameof(MainProjection.CommitRowMonoFontFamily),
                nameof(MainProjection.CommitRowTextFontSize),
                nameof(MainProjection.CommitRowMetaFontSize),
                nameof(MainProjection.CommitRowBadgeFontSize),
                nameof(MainProjection.SearchDebounceSeconds),
            };

            projection.PropertyChanged += (_, e) => {
                if (string.IsNullOrWhiteSpace(e.PropertyName) || !persistedPropertyNames.Contains(e.PropertyName)) {
                    if (!string.Equals(e.PropertyName, nameof(MainProjection.SelectedCommit), StringComparison.Ordinal)
                        || string.IsNullOrWhiteSpace(repoKey)
                        || projection.SelectedCommit is null or { IsWorkingTree: true }) {
                        return;
                    }

                    currentUiState = UiStateModule.withSelectedCommit(repoKey, projection.SelectedCommit.FullHash, currentUiState);
                    uiStateStore.Save(currentUiState);
                    return;
                }

                settingsStore.Save(projection.CaptureSettings());
            };

            // Present and compose the opaque shell before repository initialization can
            // occupy the UI thread. This guarantees the first native frame is the static
            // loading surface rather than an unpainted compositor window.
            mainWindow.Show();
            await Dispatcher.UIThread.InvokeAsync(mainWindow.InvalidateVisual, DispatcherPriority.Render);
            await Task.Delay(1);

            var host = ElmishHost.startAndBind(
                GitKay.Core.App.program(startupArgs),
                model => projection.Update(model),
                dispatch => projection.SetDispatch(dispatch)
            );

            var watcher = WorkingTreeWatcher.TryStart(projection.WorkingDirectory, repoKey,
                () => Dispatcher.UIThread.Post(projection.RefreshWorkingTree));
            mainWindow.Activated += (_, _) => projection.RefreshWorkingTree();

            desktop.Exit += (s, e) => {
                if (!string.IsNullOrWhiteSpace(repoKey) && projection.SelectedCommit is { IsWorkingTree: false }) {
                    currentUiState = UiStateModule.withSelectedCommit(repoKey, projection.SelectedCommit.FullHash, currentUiState);
                }

                currentUiState = UiStateModule.withLayout(mainWindow.CaptureLayout(),
                    UiStateModule.withWindowSize(mainWindow.Bounds.Width, mainWindow.Bounds.Height, currentUiState));
                currentUiState = UiStateModule.withViewPreferences(
                    projection.CaptureViewPreferences().Select(entry => Tuple.Create(entry.Key, entry.Value)),
                    UiStateModule.withRecentSearches(projection.RecentSearches, currentUiState));
                uiStateStore.Save(currentUiState);
                watcher?.Dispose();
                ((IDisposable)host).Dispose();
                GitKay.Core.App.stopRuntime();
            };

        }
        catch (Exception ex) {
            FatalErrorPresenter.ShowStartupFailure(desktop, ex);
        }
    }
}
