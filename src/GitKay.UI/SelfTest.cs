using System;
using System.IO;
using System.Linq;

namespace GitKay.UI;

/// <summary>
/// <c>gitkay --self-test</c>: runs without a window and exercises code that only fails in the shipped NativeAOT binary
/// (reflection-based formatting, Axial diagnostics, LibGit2Sharp). Release builds run it after publishing.
/// </summary>
public static class SelfTest {
    public static int Run(string repositoryPath) {
        var failures = 0;
        void Report(string name, bool passed, string detail) {
            Console.WriteLine(passed ? $"ok   {name}" : $"FAIL {name}: {detail}");
            if (!passed) failures++;
        }

        void Check(string name, Func<string?> run) {
            try {
                var problem = run();
                Report(name, problem == null, problem ?? "");
            }
            catch (Exception exception) {
                Report(name, false, $"{exception.GetType().FullName}: {exception.Message}");
            }
        }

        foreach (var result in GitKay.Core.SelfTest.run(repositoryPath))
            Report(result.Name, result.Passed, result.Detail);

        Check("diagnostics window data refreshes", () => {
            var diagnostics = new DiagnosticsProjection();
            diagnostics.Refresh(force: true);
            if (diagnostics.Settled.Count == 0) return "no settled flows shown";
            if (!diagnostics.Settled.Any(row => row.Status == "Failed")) return "failed flow status not shown";
            if (diagnostics.Failures.Count == 0) return "no failures shown";
            return diagnostics.Stats.Count == 0 ? "no flow totals" : null;
        });

        Check("diagnostics snapshot and report", () => {
            var diagnostics = new DiagnosticsProjection();
            diagnostics.Refresh(force: true);
            var snapshot = diagnostics.BuildSnapshot();
            if (!snapshot.Contains("Running flows")) return "snapshot missing running flows";
            var path = DiagnosticsLog.WriteReport("selftest", snapshot);
            if (path == null || !File.Exists(path)) return "report not written";
            File.Delete(path);
            return null;
        });

        Check("history targets render", () => {
            var projection = new MainProjection();
            projection.Update(GitKay.Core.App.init([]).Item1);
            return null;
        });

        Console.WriteLine(failures == 0 ? "Self-test passed." : $"Self-test: {failures} failure(s).");
        return failures == 0 ? 0 : 1;
    }
}
