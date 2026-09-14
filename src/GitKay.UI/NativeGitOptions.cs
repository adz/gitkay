using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace GitKay.UI;

/// <summary>
/// libgit2 settings LibGit2Sharp doesn't expose. libgit2 caches commits and trees but not blobs by default; search reads
/// the same blob versions from neighbouring commits over and over, so caching blobs roughly halves diff search time.
/// </summary>
public static unsafe class NativeGitOptions {
    private const int SetCacheObjectLimit = 6;   // GIT_OPT_SET_CACHE_OBJECT_LIMIT
    private const int BlobObjectType = 3;        // GIT_OBJECT_BLOB
    private const nuint BlobCacheLimit = 4 * 1024 * 1024;

    public static string Status { get; private set; } = "not configured";

    public static void Configure() {
        try {
            // git_libgit2_opts is variadic; Apple silicon passes variadic arguments on the stack, so a fixed-signature
            // call would be wrong there. Other supported platforms pass them like ordinary arguments.
            if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64) {
                Status = "skipped on macOS arm64";
                return;
            }

            // Loading LibGit2Sharp initializes libgit2 (git_libgit2_init) in the same native module we look up below.
            _ = LibGit2Sharp.GlobalSettings.Version;
            var library = FindLibgit2();
            if (library == null || !NativeLibrary.TryLoad(library, out var handle) || !NativeLibrary.TryGetExport(handle, "git_libgit2_opts", out var export)) {
                Status = "libgit2 not found";
                return;
            }

            var opts = (delegate* unmanaged[Cdecl]<int, int, nuint, int>)export;
            var result = opts(SetCacheObjectLimit, BlobObjectType, BlobCacheLimit);
            Status = result == 0 ? $"blob cache limit {BlobCacheLimit / 1048576} MB per object" : $"git_libgit2_opts returned {result}";
        }
        catch (Exception exception) {
            Status = $"failed: {exception.Message}";
        }
        finally {
            Trace.WriteLine($"[libgit2] {Status}");
        }
    }

    private static string? FindLibgit2() {
        var patterns = OperatingSystem.IsWindows() ? new[] { "git2-*.dll" }
            : OperatingSystem.IsMacOS() ? new[] { "libgit2-*.dylib" }
            : new[] { "libgit2-*.so" };
        var roots = new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native") };
        return roots.Where(Directory.Exists)
            .SelectMany(root => patterns.SelectMany(pattern => Directory.EnumerateFiles(root, pattern)))
            .FirstOrDefault();
    }
}
