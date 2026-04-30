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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
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

        base.OnFrameworkInitializationCompleted();
    }
}
