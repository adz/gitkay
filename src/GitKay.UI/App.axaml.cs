using System;
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
                var mainWindow = new MainWindow();
                var projection = new MainProjection();
                mainWindow.DataContext = projection;

                var host = ElmishHost.startAndBind(
                    GitKay.Core.App.program(StartupArgs),
                    model => projection.Update(model),
                    dispatch => projection.SetDispatch(dispatch)
                );

                desktop.MainWindow = mainWindow;
                desktop.Exit += (s, e) => ((IDisposable)host).Dispose();
            }
            catch (Exception ex)
            {
                FatalErrorPresenter.ShowStartupFailure(desktop, ex);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
