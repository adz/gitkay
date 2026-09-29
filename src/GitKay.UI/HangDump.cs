using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GitKay.UI;

/// <summary>
/// A small Windows minidump taken when the UI thread hangs. It holds every thread's stack, so the UI thread's frames can
/// be read afterwards (WinDbg <c>~0k</c>, or Visual Studio) against the release's symbols, which a managed stack trace of
/// another thread cannot give under NativeAOT. Not taken on other platforms.
/// </summary>
internal static class HangDump {
    // MiniDumpWithThreadInfo: thread times and states, so the report says which thread was running and which was waiting.
    private const uint MiniDumpNormal = 0x0;
    private const uint MiniDumpWithThreadInfo = 0x1000;

    [DllImport("dbghelp.dll")]
    private static extern int MiniDumpWriteDump(IntPtr process, uint processId, IntPtr file, uint dumpType, IntPtr exception, IntPtr userStream, IntPtr callback);

    /// <summary>Writes the dump and returns its path, or null when it could not be taken.</summary>
    public static string? TryWrite(string path) {
        if (!OperatingSystem.IsWindows()) return null;
        try {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var process = Process.GetCurrentProcess();
            var written = MiniDumpWriteDump(process.Handle, (uint)process.Id, stream.SafeFileHandle.DangerousGetHandle(),
                MiniDumpNormal | MiniDumpWithThreadInfo, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return written != 0 ? path : null;
        }
        catch {
            return null;
        }
    }
}
