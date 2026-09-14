using Avalonia;
using System;

namespace GitKay.UI;

class Program {
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args) {
        if (args.Length > 0 && args[0] == "--self-test") {
            DiagnosticsLog.Initialize(args);
            NativeGitOptions.Configure();
            var repository = GitKay.Core.GitService.tryDiscoverRepositoryPath();
            if (string.IsNullOrEmpty(repository)) {
                Console.Error.WriteLine("Self-test must run inside a Git repository.");
                return 2;
            }
            return SelfTest.Run(repository);
        }

        var optionsResult = GitKay.Core.GitStartup.parseStartupOptions(args);
        if (optionsResult.IsOk) {
            var options = optionsResult.ResultValue;
            if (options.HelpRequested) {
                Console.WriteLine(GitKay.Core.GitStartup.getHelpText());
                return 0;
            }
            if (options.VersionRequested) {
                var version = typeof(Program).Assembly.GetName().Version;
                Console.WriteLine($"gitkay version {version}");
                return 0;
            }

            if (options.LogFile != null && Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(options.LogFile)) {
                var logPath = options.LogFile.Value;
                try {
                    var writer = new System.IO.StreamWriter(logPath, true) { AutoFlush = true };
                    System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(writer));
                    System.Diagnostics.Trace.WriteLine($"--- Log started at {DateTime.Now} ---");
                }
                catch (Exception ex) {
                    Console.Error.WriteLine($"Failed to open log file: {ex.Message}");
                }
            }
        }

        DiagnosticsLog.Initialize(args);
        NativeGitOptions.Configure();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => System.Diagnostics.Trace.WriteLine("GitKay exiting");

        App.StartupArgs = args;

        return BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
