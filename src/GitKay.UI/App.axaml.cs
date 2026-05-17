using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Elmish.Avalonia.Glue;
using GitKay.Core;

namespace GitKay.UI;

public partial class App : Application
{
    public static string[] StartupArgs { get; set; } = Array.Empty<string>();
    private static Exception? _startupInitializationFailure;

    public override void Initialize()
    {
        try
        {
            AvaloniaXamlLoader.Load(this);
        }
        catch (Exception ex)
        {
            _startupInitializationFailure = ex;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            FatalErrorPresenter.Initialize(desktop);

            if (_startupInitializationFailure != null)
            {
                FatalErrorPresenter.ShowStartupFailure(desktop, _startupInitializationFailure);
                _startupInitializationFailure = null;
                base.OnFrameworkInitializationCompleted();
                return;
            }

            try
            {
                var settingsStore = new AppSettingsStore();
                var uiStateStore = new AppUiStateStore();
                var persistedSettings = settingsStore.Load();
                var persistedUiState = uiStateStore.Load();
                var repoKey = GitService.tryDiscoverRepositoryPath();
                var currentUiState = persistedUiState;
                var startupArgs = persistedSettings.ToStartupArgs();

                if (!string.IsNullOrWhiteSpace(repoKey))
                {
                    // No longer restoring SelectedCommitHash from state.
                }

                if (StartupArgs.Length > 0)
                {
                    var mergedArgs = new string[startupArgs.Length + StartupArgs.Length];
                    startupArgs.CopyTo(mergedArgs, 0);
                    StartupArgs.CopyTo(mergedArgs, startupArgs.Length);
                    startupArgs = mergedArgs;
                }

                var mainWindow = new MainWindow();
                if (persistedUiState.WindowWidth.HasValue)
                {
                    mainWindow.Width = persistedUiState.WindowWidth.Value;
                }

                if (persistedUiState.WindowHeight.HasValue)
                {
                    mainWindow.Height = persistedUiState.WindowHeight.Value;
                }

                var projection = new MainProjection();
                projection.ApplySettings(persistedSettings);
                mainWindow.DataContext = projection;

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

                projection.PropertyChanged += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.PropertyName) || !persistedPropertyNames.Contains(e.PropertyName))
                    {
                        if (!string.Equals(e.PropertyName, nameof(MainProjection.SelectedCommit), StringComparison.Ordinal)
                            || string.IsNullOrWhiteSpace(repoKey)
                            || projection.SelectedCommit == null)
                        {
                            return;
                        }

                        currentUiState = currentUiState.WithRepoSelection(repoKey, projection.SelectedCommit.FullHash);
                        uiStateStore.Save(currentUiState);
                        return;
                    }

                    settingsStore.Save(projection.CaptureSettings());
                };

                var host = ElmishHost.startAndBind(
                    GitKay.Core.App.program(startupArgs),
                    model => projection.Update(model),
                    dispatch => projection.SetDispatch(dispatch)
                );

                desktop.MainWindow = mainWindow;
                desktop.Exit += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(repoKey) && projection.SelectedCommit != null)
                    {
                        currentUiState = currentUiState.WithRepoSelection(repoKey, projection.SelectedCommit.FullHash);
                    }

                    currentUiState = currentUiState.WithWindowSize(mainWindow.Bounds.Width, mainWindow.Bounds.Height);
                    uiStateStore.Save(currentUiState);
                    ((IDisposable)host).Dispose();
                };
            }
            catch (Exception ex)
            {
                FatalErrorPresenter.ShowStartupFailure(desktop, ex);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
