namespace GitKay.Tests

open System
open System.IO
open System.Text
open Xunit
open Swensen.Unquote
open Axial
open Axial.FileSystem
open GitKay.Core

/// An environment that only supplies a file system, for flows whose contract is just `IHasFileSystem`.
type FileSystemEnv =
    { FileSystem: IFileSystem }
    interface IHasFileSystem with
        member this.FileSystem = this.FileSystem

module FileSystemRecoveryTests =

    /// Delegates to Axial's in-memory test double but lets individual members fail, so a test can
    /// simulate the races and I/O failures the recovery paths exist for.
    type FaultyFileSystem(inner: IFileSystem) =
        member val FileExistsImpl : (string -> bool) option = None with get, set
        member val GetFileLengthImpl : (string -> int64) option = None with get, set
        member val ReadAllTextImpl : (string -> string) option = None with get, set
        member val OpenReadImpl : (string -> Stream) option = None with get, set
        member val DirectoryExistsImpl : (string -> bool) option = None with get, set
        member val GetDirectoriesImpl : (string * string * SearchOption -> string array) option = None with get, set
        member val DeleteDirectoryImpl : (string * bool -> unit) option = None with get, set

        interface IFileSystem with
            member this.Watch(path, includeSubdirectories, onChange, onError) =
                inner.Watch(path, includeSubdirectories, onChange, onError)

            member this.ReadAllText(path) =
                match this.ReadAllTextImpl with Some read -> read path | None -> inner.ReadAllText path

            member _.ReadAllText(path, encoding) = inner.ReadAllText(path, encoding)
            member _.ReadAllTextAsync(path) = inner.ReadAllTextAsync path
            member _.ReadAllLines(path) = inner.ReadAllLines path
            member _.ReadAllLines(path, encoding) = inner.ReadAllLines(path, encoding)
            member _.ReadAllLinesAsync(path) = inner.ReadAllLinesAsync path
            member _.ReadAllBytes(path) = inner.ReadAllBytes path
            member _.ReadAllBytesAsync(path) = inner.ReadAllBytesAsync path
            member _.WriteAllText(path, contents) = inner.WriteAllText(path, contents)
            member _.WriteAllText(path, contents, encoding) = inner.WriteAllText(path, contents, encoding)
            member _.WriteAllTextAsync(path, contents) = inner.WriteAllTextAsync(path, contents)
            member _.WriteAllLines(path, contents) = inner.WriteAllLines(path, contents)
            member _.WriteAllLines(path, contents, encoding) = inner.WriteAllLines(path, contents, encoding)
            member _.WriteAllLinesAsync(path, contents) = inner.WriteAllLinesAsync(path, contents)
            member _.WriteAllBytes(path, contents) = inner.WriteAllBytes(path, contents)
            member _.WriteAllBytesAsync(path, contents) = inner.WriteAllBytesAsync(path, contents)
            member _.AppendAllText(path, contents) = inner.AppendAllText(path, contents)
            member _.AppendAllText(path, contents, encoding) = inner.AppendAllText(path, contents, encoding)
            member _.AppendAllTextAsync(path, contents) = inner.AppendAllTextAsync(path, contents)
            member _.AppendAllLines(path, contents) = inner.AppendAllLines(path, contents)
            member _.AppendAllLines(path, contents, encoding) = inner.AppendAllLines(path, contents, encoding)

            member this.FileExists(path) =
                match this.FileExistsImpl with Some exists -> exists path | None -> inner.FileExists path

            member _.DeleteFile(path) = inner.DeleteFile path
            member _.CopyFile(sourcePath, destinationPath, overwrite) = inner.CopyFile(sourcePath, destinationPath, overwrite)
            member _.MoveFile(sourcePath, destinationPath, overwrite) = inner.MoveFile(sourcePath, destinationPath, overwrite)
            member _.CreateFileSymbolicLink(linkPath, targetPath) = inner.CreateFileSymbolicLink(linkPath, targetPath)
            member _.CreateDirectorySymbolicLink(linkPath, targetPath) = inner.CreateDirectorySymbolicLink(linkPath, targetPath)
            member _.GetSymbolicLinkTarget(path) = inner.GetSymbolicLinkTarget path
            member _.ResolveSymbolicLinkTarget(path, returnFinalTarget) = inner.ResolveSymbolicLinkTarget(path, returnFinalTarget)
            member _.OpenFile(path, mode) = inner.OpenFile(path, mode)
            member _.OpenFile(path, mode, access) = inner.OpenFile(path, mode, access)
            member _.OpenFile(path, mode, access, share) = inner.OpenFile(path, mode, access, share)

            member this.OpenRead(path) =
                match this.OpenReadImpl with Some read -> read path | None -> inner.OpenRead path

            member _.OpenText(path) = inner.OpenText path
            member _.OpenWrite(path) = inner.OpenWrite path
            member _.CreateFile(path) = inner.CreateFile path
            member _.CreateText(path) = inner.CreateText path
            member _.AppendText(path) = inner.AppendText path

            member this.GetFileLength(path) =
                match this.GetFileLengthImpl with Some length -> length path | None -> inner.GetFileLength path

            member _.GetFileAttributes(path) = inner.GetFileAttributes path
            member _.SetFileAttributes(path, attributes) = inner.SetFileAttributes(path, attributes)
            member _.GetFileCreationTime(path) = inner.GetFileCreationTime path
            member _.GetFileCreationTimeUtc(path) = inner.GetFileCreationTimeUtc path
            member _.SetFileCreationTime(path, creationTime) = inner.SetFileCreationTime(path, creationTime)
            member _.SetFileCreationTimeUtc(path, creationTimeUtc) = inner.SetFileCreationTimeUtc(path, creationTimeUtc)
            member _.GetFileLastAccessTime(path) = inner.GetFileLastAccessTime path
            member _.GetFileLastAccessTimeUtc(path) = inner.GetFileLastAccessTimeUtc path
            member _.SetFileLastAccessTime(path, lastAccessTime) = inner.SetFileLastAccessTime(path, lastAccessTime)
            member _.SetFileLastAccessTimeUtc(path, lastAccessTimeUtc) = inner.SetFileLastAccessTimeUtc(path, lastAccessTimeUtc)
            member _.GetFileLastWriteTime(path) = inner.GetFileLastWriteTime path
            member _.GetFileLastWriteTimeUtc(path) = inner.GetFileLastWriteTimeUtc path
            member _.SetFileLastWriteTime(path, lastWriteTime) = inner.SetFileLastWriteTime(path, lastWriteTime)
            member _.SetFileLastWriteTimeUtc(path, lastWriteTimeUtc) = inner.SetFileLastWriteTimeUtc(path, lastWriteTimeUtc)

            member this.DirectoryExists(path) =
                match this.DirectoryExistsImpl with Some exists -> exists path | None -> inner.DirectoryExists path

            member _.CreateDirectory(path) = inner.CreateDirectory path

            member this.DeleteDirectory(path, recursive) =
                match this.DeleteDirectoryImpl with Some delete -> delete (path, recursive) | None -> inner.DeleteDirectory(path, recursive)

            member _.MoveDirectory(sourcePath, destinationPath) = inner.MoveDirectory(sourcePath, destinationPath)
            member _.EnumerateFiles(path, searchPattern, searchOption) = inner.EnumerateFiles(path, searchPattern, searchOption)
            member _.GetFiles(path, searchPattern, searchOption) = inner.GetFiles(path, searchPattern, searchOption)
            member _.EnumerateDirectories(path, searchPattern, searchOption) = inner.EnumerateDirectories(path, searchPattern, searchOption)

            member this.GetDirectories(path, searchPattern, searchOption) =
                match this.GetDirectoriesImpl with
                | Some directories -> directories (path, searchPattern, searchOption)
                | None -> inner.GetDirectories(path, searchPattern, searchOption)

            member _.EnumerateFileSystemEntries(path, searchPattern, searchOption) = inner.EnumerateFileSystemEntries(path, searchPattern, searchOption)
            member _.EnumerateFileSystemInfos(path, searchPattern, searchOption) = inner.EnumerateFileSystemInfos(path, searchPattern, searchOption)
            member _.GetFileSystemEntries(path, searchPattern, searchOption) = inner.GetFileSystemEntries(path, searchPattern, searchOption)
            member _.GetLogicalDrives() = inner.GetLogicalDrives()
            member _.GetDirectoryRoot(path) = inner.GetDirectoryRoot path
            member _.GetParent(path) = inner.GetParent path
            member _.GetCurrentDirectory() = inner.GetCurrentDirectory()
            member _.SetCurrentDirectory(path) = inner.SetCurrentDirectory path
            member _.GetDirectoryCreationTime(path) = inner.GetDirectoryCreationTime path
            member _.GetDirectoryCreationTimeUtc(path) = inner.GetDirectoryCreationTimeUtc path
            member _.SetDirectoryCreationTime(path, creationTime) = inner.SetDirectoryCreationTime(path, creationTime)
            member _.SetDirectoryCreationTimeUtc(path, creationTimeUtc) = inner.SetDirectoryCreationTimeUtc(path, creationTimeUtc)
            member _.GetDirectoryLastAccessTime(path) = inner.GetDirectoryLastAccessTime path
            member _.GetDirectoryLastAccessTimeUtc(path) = inner.GetDirectoryLastAccessTimeUtc path
            member _.SetDirectoryLastAccessTime(path, lastAccessTime) = inner.SetDirectoryLastAccessTime(path, lastAccessTime)
            member _.SetDirectoryLastAccessTimeUtc(path, lastAccessTimeUtc) = inner.SetDirectoryLastAccessTimeUtc(path, lastAccessTimeUtc)
            member _.GetDirectoryLastWriteTime(path) = inner.GetDirectoryLastWriteTime path
            member _.GetDirectoryLastWriteTimeUtc(path) = inner.GetDirectoryLastWriteTimeUtc path
            member _.SetDirectoryLastWriteTime(path, lastWriteTime) = inner.SetDirectoryLastWriteTime(path, lastWriteTime)
            member _.SetDirectoryLastWriteTimeUtc(path, lastWriteTimeUtc) = inner.SetDirectoryLastWriteTimeUtc(path, lastWriteTimeUtc)
            member _.Combine(paths) = inner.Combine(paths)
            member _.ChangeExtension(path, extension) = inner.ChangeExtension(path, extension)
            member _.GetDirectoryName(path) = inner.GetDirectoryName path
            member _.GetInvalidFileNameChars() = inner.GetInvalidFileNameChars()
            member _.GetInvalidPathChars() = inner.GetInvalidPathChars()
            member _.GetExtension(path) = inner.GetExtension path
            member _.GetFileName(path) = inner.GetFileName path
            member _.GetFileNameWithoutExtension(path) = inner.GetFileNameWithoutExtension path
            member _.GetFullPath(path) = inner.GetFullPath path
            member _.GetPathRoot(path) = inner.GetPathRoot path
            member _.GetRelativePath(relativeTo, path) = inner.GetRelativePath(relativeTo, path)
            member _.GetTempPath() = inner.GetTempPath()
            member _.GetTempFileName() = inner.GetTempFileName()
            member _.GetRandomFileName() = inner.GetRandomFileName()
            member _.HasExtension(path) = inner.HasExtension path
            member _.EndsInDirectorySeparator(path) = inner.EndsInDirectorySeparator path
            member _.TrimEndingDirectorySeparator(path) = inner.TrimEndingDirectorySeparator path
            member _.IsPathFullyQualified(path) = inner.IsPathFullyQualified path
            member _.IsPathRooted(path) = inner.IsPathRooted path

    let private tempRoot () =
        let root = Path.Combine(Path.GetTempPath(), "gitkay-tests-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        root

    let private withTempRoot action =
        let root = tempRoot ()
        try action root
        finally
            try Directory.Delete(root, true) with _ -> ()

    let private requireOk (result: Result<'value, 'error>) =
        match result with
        | Ok value -> value
        | Error error -> failwithf "Expected success, got %A" error

    let private entry path afterDiscard : Trash.Entry =
        { Path = path; BackupFile = None; AfterDiscard = afterDiscard }

    let private backup workingDirectory entries : Trash.Backup =
        { Directory = Path.Combine(workingDirectory, "backup")
          Entries = entries
          Description = "test"
          CreatedAt = DateTimeOffset.UtcNow }

    [<Fact>]
    let ``untracked read shows an empty diff when the file vanishes between listing and reading`` () =
        withTempRoot (fun root ->
            let inner = FileSystem.test () :> IFileSystem
            let full = Path.Combine(root, "a.txt")
            inner.WriteAllText(full, "hello")

            let faulty = FaultyFileSystem(inner)
            faulty.ReadAllTextImpl <- Some(fun _ -> raise (FileNotFoundException("File vanished between status and read")))

            let env = { GitService.environmentWithoutCaches root with Files = faulty :> IFileSystem }
            let result =
                Flow.run env (GitService.fetchWorkingTreeFile WorkingTree.Untracked FileChange.missing "a.txt")
                |> Exit.toResult

            let file = requireOk result
            test <@ file.OldPath = FileChange.missing @>
            test <@ file.NewPath = "a.txt" @>
            test <@ file.Hunks = [] @>
            test <@ file.NewLineCount = None @>)

    [<Fact>]
    let ``seal records no hash when opening the file fails`` () =
        withTempRoot (fun root ->
            let cases =
                [ "FileNotFound", (fun () -> raise (FileNotFoundException("gone")) : Stream)
                  "PathTooLong", (fun () -> raise (PathTooLongException("too long")) : Stream)
                  "Io", (fun () -> raise (IOException("io failure")) : Stream) ]

            for name, failOpen in cases do
                let inner = FileSystem.test () :> IFileSystem
                let full = Path.Combine(root, "a.txt")
                inner.WriteAllText(full, "content")

                let faulty = FaultyFileSystem(inner)
                faulty.OpenReadImpl <- Some(fun _ -> failOpen ())

                let env = { FileSystem = faulty :> IFileSystem }
                let result =
                    Flow.run env (Trash.seal root (backup root [ entry "a.txt" (Some "old-hash") ]))
                    |> Exit.toResult

                let sealedBackup = requireOk result
                test <@ sealedBackup.Entries.[0].AfterDiscard = None @>)

    [<Fact>]
    let ``prune ignores a directory deleted by a concurrent scan`` () =
        withTempRoot (fun root ->
            let inner = FileSystem.test () :> IFileSystem
            let discarded = Trash.directory root
            inner.WriteAllText(Path.Combine(discarded, "a", "f.txt"), "x")
            inner.WriteAllText(Path.Combine(discarded, "b", "f.txt"), "x")

            let faulty = FaultyFileSystem(inner)
            faulty.DeleteDirectoryImpl <- Some(fun (_, _) -> raise (DirectoryNotFoundException("Directory vanished during prune")))

            let env = { FileSystem = faulty :> IFileSystem }
            let result = Flow.run env (Trash.prune root 1) |> Exit.toResult
            requireOk result |> ignore)
