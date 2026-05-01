using Avalonia;
using System;

namespace GitKay.UI;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        var optionsResult = GitKay.Core.GitService.parseStartupOptions(args);
        if (optionsResult.IsOk)
        {
            var options = optionsResult.ResultValue;
            if (options.HelpRequested)
            {
                Console.WriteLine(GitKay.Core.GitService.getHelpText());
                return;
            }
            if (options.VersionRequested)
            {
                var version = typeof(Program).Assembly.GetName().Version;
                Console.WriteLine($"gitkay version {version}");
                return;
            }
        }

        App.StartupArgs = args;

        BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
