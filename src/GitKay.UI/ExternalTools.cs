using System;
using System.Collections.Generic;

namespace GitKay.UI;

/// <summary>
/// Launches external programs (VS Code, the browser, the file manager, another GitKay) through Core's
/// <c>Launch</c> flows on the app's runtime. A program that fails to start reports a typed failure, passed to
/// <c>onFailure</c> as text; nothing here throws.
/// </summary>
public static class ExternalTools {
    private static bool TryRuntime(Action<string>? onFailure, out Axial.ElmishFlows.ElmishRuntime<GitKay.Core.GitService.GitEnv> runtime) {
        if (App.Runtime is { } started) {
            runtime = started;
            return true;
        }
        runtime = null!;
        onFailure?.Invoke("GitKay is not running yet");
        return false;
    }

    /// <summary>Opens VS Code with these arguments in <paramref name="workingDirectory"/>.</summary>
    public static void StartVsCode(IReadOnlyList<string> arguments, string workingDirectory, Action<string>? onFailure = null) {
        if (TryRuntime(onFailure, out var runtime)) GitKay.Core.UiLaunch.openInVsCode(runtime, arguments, workingDirectory, onFailure);
    }

    /// <summary>Starts another GitKay in <paramref name="directory"/>; repository discovery starts from the working directory.</summary>
    public static void StartGitKay(Action<string>? onFailure, string directory, params string[] arguments) {
        if (TryRuntime(onFailure, out var runtime)) GitKay.Core.UiLaunch.startGitKay(runtime, directory, arguments, onFailure);
    }

    /// <summary>Opens a file or folder GitKay wrote with the OS default handler: the file manager for a folder.</summary>
    public static void OpenInDefaultApp(string path, Action<string>? onFailure = null) {
        if (TryRuntime(onFailure, out var runtime)) GitKay.Core.UiLaunch.openOwnedPath(runtime, path, onFailure);
    }

    /// <summary>Opens an http or https link in the browser; anything else is refused.</summary>
    public static void OpenLink(string url, Action<string>? onFailure = null) {
        if (TryRuntime(onFailure, out var runtime)) GitKay.Core.UiLaunch.openLink(runtime, url, onFailure);
    }
}

/// <summary>Awaits a Core flow run on the app's runtime (see <c>GitKay.Core.UiFlow</c>), in place of <c>Task.Run</c>.</summary>
public static class UiFlows {
    /// <summary>Runs <paramref name="flow"/> on <paramref name="runtime"/>; the task completes on the UI thread with its result.</summary>
    public static System.Threading.Tasks.Task<Microsoft.FSharp.Core.FSharpResult<TValue, TError>> RunAsync<TValue, TError>(
        string name, Axial.ElmishFlows.ElmishRuntime<GitKay.Core.GitService.GitEnv> runtime, Axial.Flow<GitKay.Core.GitService.GitEnv, TError, TValue> flow) {
        var completion = new System.Threading.Tasks.TaskCompletionSource<Microsoft.FSharp.Core.FSharpResult<TValue, TError>>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        GitKay.Core.UiFlow.run(name, runtime, flow, completion.SetResult);
        return completion.Task;
    }

    /// <summary>The app's runtime; it exists from the moment the app's first window can be shown.</summary>
    public static Axial.ElmishFlows.ElmishRuntime<GitKay.Core.GitService.GitEnv> AppRuntime =>
        App.Runtime ?? throw new InvalidOperationException("GitKay's runtime has not started");
}
