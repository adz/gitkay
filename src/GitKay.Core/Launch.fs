namespace GitKay.Core

open System
open System.IO
open Axial
open Axial.Process

/// <summary>
/// The programs GitKay hands work to: a browser for a link, the file manager for a folder it owns, VS Code for a file,
/// and another GitKay. Each is a flow on the environment's process service, so a failure to start is a typed
/// <c>ProcessError</c> and a test asserts the launch without starting anything. Nothing waits for the program.
/// </summary>
module Launch =

    /// <summary>The platform a launch is shaped for, as the host reports it.</summary>
    type Platform =
        | Windows
        | MacOS
        | OtherPlatform

    /// <summary>Opens a link from a document or a commit message in the browser. Only http and https URLs reach the
    /// opener: anything else could name a program to run.</summary>
    let openLink (url: string) : Flow<'env, ProcessError, unit> when 'env :> IHasProcess =
        match Uri.TryCreate(url, UriKind.Absolute) with
        | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps -> Process.shellOpen uri.AbsoluteUri
        | _ -> Flow.fail (ProcessError.ShellOpenRejected { Target = url; Reason = "not an http or https URL" })

    /// <summary>Opens a file or folder GitKay itself wrote (its log folder, its settings file) with the default
    /// handler: the file manager for a folder. Never pass a path that came from a repository's contents.</summary>
    let openOwnedPath (path: string) : Flow<'env, ProcessError, unit> when 'env :> IHasProcess =
        Process.shellOpen (Path.GetFullPath path)

    /// <summary>The executable VS Code's <c>code</c> script runs. On Windows <c>code</c> is <c>bin\code.cmd</c>, which
    /// runs <c>Code.exe</c> in the folder above it; starting that directly passes each argument as one, where
    /// <c>cmd.exe /c</c> would parse <c>&amp;</c>, <c>|</c>, <c>^</c> and <c>%</c> in a file name as shell syntax.</summary>
    let codeExecutableFor (codeScript: string) =
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName codeScript, "..", "Code.exe"))

    /// <summary>
    /// Opens VS Code with <paramref name="arguments"/> in <paramref name="workingDirectory"/>. <paramref name="codeOnPath"/>
    /// is the <c>code</c> found on PATH, if any (on Windows, the executable it runs). On macOS without the CLI on PATH,
    /// the app bundle is opened instead.
    /// </summary>
    let openInVsCode (platform: Platform) (codeOnPath: string option) (arguments: string list) (workingDirectory: string)
        : Flow<'env, ProcessError, unit> when 'env :> IHasProcess =
        let launch executable args = Process.commandArgs executable args |> Process.workingDirectory workingDirectory |> Process.launch
        match platform, codeOnPath with
        | MacOS, None -> launch "open" ([ "-b"; "com.microsoft.VSCode"; "--args" ] @ arguments)
        | _, Some executable -> launch executable arguments
        | _, None -> launch "code" arguments

    /// <summary>Starts another GitKay in <paramref name="directory"/>, where repository discovery starts. Under the
    /// dotnet host (<c>dotnet gitkay.dll</c>), <paramref name="entryAssembly"/> is passed first.</summary>
    let startGitKay (executable: string) (entryAssembly: string option) (directory: string) (arguments: string list)
        : Flow<'env, ProcessError, unit> when 'env :> IHasProcess =
        Process.commandArgs executable (Option.toList entryAssembly @ arguments)
        |> Process.workingDirectory directory
        |> Process.launch
