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
using Microsoft.FSharp.Core;

namespace GitKay.UI;

public partial class App : Application {
    public static string[] StartupArgs { get; set; } = Array.Empty<string>();
    public static GitStartup.LaunchMode LaunchMode { get; set; } = GitStartup.LaunchMode.History;
    private static Exception? _startupInitializationFailure;

    /// <summary>
    /// The app's runtime, which this host starts once the UI dispatcher exists and stops at exit. Windows and controls
    /// with no program of their own (links, the about and diagnostics windows) launch external programs on it.
    /// </summary>
    public static Axial.ElmishFlows.ElmishRuntime<GitService.GitEnv>? Runtime { get; private set; }

    /// <summary>Starts the app's runtime for a repository (or none), and stops it when the app exits.</summary>
    private static Axial.ElmishFlows.ElmishRuntime<GitService.GitEnv> StartRuntime(IClassicDesktopStyleApplicationLifetime desktop, string repository) {
        var runtime = GitKay.Core.Host.startRepositoryRuntime(UiPost, repository);
        Runtime = runtime;
        desktop.Exit += (_, _) => GitKay.Core.Host.stop(runtime, TimeSpan.FromSeconds(2));
        return runtime;
    }

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
            var settingsResult = (await ReadStartupAsync()).Settings;
            var settings = settingsResult.Value;
            RequestedThemeVariant =
                settings.Theme.IsLightTheme ? Avalonia.Styling.ThemeVariant.Light
                : settings.Theme.IsDarkTheme ? Avalonia.Styling.ThemeVariant.Dark
                : Avalonia.Styling.ThemeVariant.Default;

            var runtime = StartRuntime(desktop, "");
            var built = await UiFlows.RunAsync("read folders", runtime, LoadFolders(mode, left, right));
            if (built.IsError) {
                Console.Error.WriteLine(built.ErrorValue);
                desktop.Shutdown(2);
                return;
            }

            var projection = new FolderProjection(mode, left, right, built.ResultValue.Pairs.ToList(), built.ResultValue.Changed.ToList());
            // The saved settings that mean something away from a repository: layout, previews, remote images.
            projection.ApplySettings(settings);
            var window = new FolderWindow(projection) {
                WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen,
            };
            desktop.MainWindow = window;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            window.Show();

            if (settingsResult.Failure?.Value is { } settingsFailure) {
                await SettingsLoadFailureDialog.ShowAsync(window,
                    [new SettingsLoadFailureDialog.Failure("settings", settingsFailure.Path, settingsFailure.Reason)]);
            }
        }
        catch (Exception ex) {
            FatalErrorPresenter.ShowStartupFailure(desktop, ex);
        }
    }

    /// <summary>Reads the folders and pairs their files, as a flow for the app's runtime. A folder that cannot be read says so.</summary>
    internal static Axial.Flow<GitService.GitEnv, string, GitKay.Core.FolderSource.Listing> LoadFolders(FolderMode mode, string left, string? right) =>
        GitKay.Core.FolderSource.load<GitService.GitEnv>(left, mode == FolderMode.Preview || right == null ? null : Microsoft.FSharp.Core.FSharpOption<string>.Some(right));

    /// <summary>gitkay gui: only the commit window, with the saved theme; closing it exits.</summary>
    private async Task InitializeCommitWindowAsync(IClassicDesktopStyleApplicationLifetime desktop) {
        try {
            var reads = await ReadStartupAsync();
            var (settingsResult, repository) = (reads.Settings, reads.Repository);
            var settings = settingsResult.Value;
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

            // The commit window alone gets the runtime the history window would have: closing it stops the runtime.
            var runtime = StartRuntime(desktop, repository!);
            var window = new CommitWindow(runtime, repository!, System.IO.Path.GetFileName(directory)) { WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen };
            window.OpenInVsCode = (path, line) => {
                var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, path));
                // VS Code not on PATH is logged; there is nothing else to do from a commit-only window.
                ExternalTools.StartVsCode(line is { } number ? [directory, "--goto", $"{full}:{number}"] : [directory, "--goto", full], directory);
            };
            desktop.MainWindow = window;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            window.Show();

            if (settingsResult.Failure?.Value is { } settingsFailure) {
                await SettingsLoadFailureDialog.ShowAsync(window,
                    [new SettingsLoadFailureDialog.Failure("settings", settingsFailure.Path, settingsFailure.Reason)]);
            }
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
        Axial.ElmishFlows.ElmishRuntime<GitService.GitEnv>? runtime = null;
        try {
            var reads = await ReadStartupAsync();
            var (settingsResult, uiStateResult, repoKey) = (reads.Settings, reads.UiState, reads.Repository);
            var persistedSettings = settingsResult.Value;
            var persistedUiState = uiStateResult.Value;

            // Started somewhere that is not a repository and given nothing to show: browse the folder instead of
            // opening a history window with no history in it.
            if (string.IsNullOrEmpty(repoKey) && StartupArgs.Length == 0) {
                await InitializeFolderWindowAsync(desktop, FolderMode.Preview, Environment.CurrentDirectory, null);
                return;
            }

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
                    // Not on the click: the write can wait on the disk, and the window must not wait with it.
                    GitKay.Serialization.UiStateStore.saveInBackgroundDefault(currentUiState);
                    return;
                }

                GitKay.Serialization.SettingsStore.saveDefault(projection.CaptureSettings());
            };

            // Present and compose the opaque shell before repository initialization can
            // occupy the UI thread. This guarantees the first native frame is the static
            // loading surface rather than an unpainted compositor window.
            mainWindow.Show();
            await Dispatcher.UIThread.InvokeAsync(mainWindow.InvalidateVisual, DispatcherPriority.Render);
            await Task.Delay(1);

            var failures = new List<SettingsLoadFailureDialog.Failure>();
            if (settingsResult.Failure?.Value is { } settingsFailure)
                failures.Add(new SettingsLoadFailureDialog.Failure("settings", settingsFailure.Path, settingsFailure.Reason));
            if (uiStateResult.Failure?.Value is { } uiStateFailure)
                failures.Add(new SettingsLoadFailureDialog.Failure("UI state", uiStateFailure.Path, uiStateFailure.Reason));
            if (failures.Count > 0)
                await SettingsLoadFailureDialog.ShowAsync(mainWindow, failures);

            // The runtime is created once the UI dispatcher exists, with the post that delivers to it; the program,
            // its commands and its subscriptions (the search debounce and the working tree watcher) all run on it.
            var appRuntime = GitKay.Core.Host.startRepositoryRuntime(UiPost, repoKey ?? "");
            runtime = appRuntime;
            Runtime = appRuntime;
            projection.Runtime = appRuntime;
            var flags = GitKay.Core.Host.flags(repoKey ?? "", startupArgs, projection.SearchDebounce);

            var host = ElmishHost.startAndBind(
                GitKay.Core.Host.mainProgram(appRuntime, flags),
                model => projection.Update(model),
                dispatch => projection.SetDispatch(dispatch)
            );
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
                GitKay.Serialization.UiStateStore.saveDefault(currentUiState);
                ((IDisposable)host).Dispose();
                // Stops the commands, the subscriptions and the repository caches the runtime's layer built.
                GitKay.Core.Host.stop(appRuntime, TimeSpan.FromSeconds(2));
            };

        }
        catch (Exception ex) {
            if (runtime != null) GitKay.Core.Host.stop(runtime, TimeSpan.FromSeconds(2));
            FatalErrorPresenter.ShowStartupFailure(desktop, ex);
        }
    }

    /// <summary>The saved settings and UI state and the launch repository, read off the UI thread before any runtime exists.</summary>
    private static async Task<GitKay.Serialization.StartupReads> ReadStartupAsync() =>
        GitKay.Serialization.Stores.value(await GitKay.Serialization.Startup.read.StartAsTask(GitKay.Serialization.Stores.live, null));

    /// <summary>The runtime's Post: inline when already on the UI thread, otherwise posted to it.</summary>
    private static readonly Action<Action> UiPost = callback =>
        ElmishHost.post(FuncConvert.ToFSharpFunc<Microsoft.FSharp.Core.Unit>(_ => callback()));
}
