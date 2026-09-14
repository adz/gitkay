using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace GitKay.UI;

/// <summary>Launches external programs: VS Code, and new GitKay instances for other repositories.</summary>
public static class ExternalTools {
    /// <summary>Runs the <c>code</c> CLI; on Windows it is a .cmd shim, and on macOS the app bundle is a fallback.</summary>
    public static void StartVsCode(IReadOnlyList<string> arguments, string workingDirectory) {
        if (OperatingSystem.IsWindows()) {
            var start = new ProcessStartInfo("cmd.exe") { WorkingDirectory = workingDirectory, CreateNoWindow = true, UseShellExecute = false };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("code");
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            Process.Start(start);
            return;
        }

        if (OperatingSystem.IsMacOS() && FindOnPath("code") == null) {
            var open = new ProcessStartInfo("open") { WorkingDirectory = workingDirectory, UseShellExecute = false };
            foreach (var argument in new[] { "-b", "com.microsoft.VSCode", "--args" }.Concat(arguments)) open.ArgumentList.Add(argument);
            Process.Start(open);
            return;
        }

        var info = new ProcessStartInfo(FindOnPath("code") ?? "code") { WorkingDirectory = workingDirectory, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        Process.Start(info);
    }

    /// <summary>Starts another GitKay in <paramref name="directory"/>; repository discovery starts from the working directory.</summary>
    public static void StartGitKay(string directory) {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown GitKay executable path");
        var info = new ProcessStartInfo(path) { WorkingDirectory = directory, UseShellExecute = false };
        // `dotnet gitkay.dll` runs under the dotnet host; pass the app's entry dll along.
        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "gitkay.dll"));
        Process.Start(info);
    }

    private static string? FindOnPath(string name) {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
