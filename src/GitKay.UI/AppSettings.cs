using System;
using System.Collections.Generic;

namespace GitKay.UI;

public sealed record AppSettings {
    public const string DefaultCommitRowFontFamily = "Helvetica,Arial,Liberation Sans,Noto Sans,sans-serif";
    public const string DefaultCommitRowMonoFontFamily = "Courier,Courier New,Liberation Mono,Monospace";
    public const double DefaultCommitRowTextFontSize = 13;
    public const double DefaultCommitRowMetaFontSize = 12;
    public const double DefaultCommitRowBadgeFontSize = 11;
    public const int DefaultDiffContextLines = 3;
    public const string DefaultDiffPresentationModeKey = "diff";
    public const double DefaultSearchDebounceSeconds = 0.5;

    public bool ShowBranchRefs { get; init; }

    public bool ShowStashes { get; init; }

    public int DiffContextLines { get; init; } = DefaultDiffContextLines;

    public string DiffPresentationModeKey { get; init; } = DefaultDiffPresentationModeKey;

    public string CommitRowFontFamily { get; init; } = DefaultCommitRowFontFamily;

    public string CommitRowMonoFontFamily { get; init; } = DefaultCommitRowMonoFontFamily;

    public double CommitRowTextFontSize { get; init; } = DefaultCommitRowTextFontSize;

    public double CommitRowMetaFontSize { get; init; } = DefaultCommitRowMetaFontSize;

    public double CommitRowBadgeFontSize { get; init; } = DefaultCommitRowBadgeFontSize;

    public double SearchDebounceSeconds { get; init; } = DefaultSearchDebounceSeconds;

    public static AppSettings Default { get; } = new();

    public static AppSettings Create(
        bool showBranchRefs = false,
        bool showStashes = false,
        int diffContextLines = DefaultDiffContextLines,
        string diffPresentationModeKey = DefaultDiffPresentationModeKey,
        string commitRowFontFamily = DefaultCommitRowFontFamily,
        string commitRowMonoFontFamily = DefaultCommitRowMonoFontFamily,
        double commitRowTextFontSize = DefaultCommitRowTextFontSize,
        double commitRowMetaFontSize = DefaultCommitRowMetaFontSize,
        double commitRowBadgeFontSize = DefaultCommitRowBadgeFontSize,
        double searchDebounceSeconds = DefaultSearchDebounceSeconds) {
        return new AppSettings {
            ShowBranchRefs = showBranchRefs,
            ShowStashes = showStashes,
            DiffContextLines = diffContextLines,
            DiffPresentationModeKey = diffPresentationModeKey,
            CommitRowFontFamily = commitRowFontFamily,
            CommitRowMonoFontFamily = commitRowMonoFontFamily,
            CommitRowTextFontSize = commitRowTextFontSize,
            CommitRowMetaFontSize = commitRowMetaFontSize,
            CommitRowBadgeFontSize = commitRowBadgeFontSize,
            SearchDebounceSeconds = searchDebounceSeconds,
        };
    }

    public AppSettings Normalize() {
        var normalizedDiffPresentationModeKey =
            NormalizeDiffPresentationModeKey(DiffPresentationModeKey) ?? DefaultDiffPresentationModeKey;

        return this with {
            DiffContextLines = Math.Max(0, DiffContextLines),
            DiffPresentationModeKey = normalizedDiffPresentationModeKey,
            CommitRowFontFamily = NormalizeFontFamily(CommitRowFontFamily, DefaultCommitRowFontFamily),
            CommitRowMonoFontFamily = NormalizeFontFamily(CommitRowMonoFontFamily, DefaultCommitRowMonoFontFamily),
            CommitRowTextFontSize = NormalizeFontSize(CommitRowTextFontSize, DefaultCommitRowTextFontSize),
            CommitRowMetaFontSize = NormalizeFontSize(CommitRowMetaFontSize, DefaultCommitRowMetaFontSize),
            CommitRowBadgeFontSize = NormalizeFontSize(CommitRowBadgeFontSize, DefaultCommitRowBadgeFontSize),
            SearchDebounceSeconds = Math.Max(0d, SearchDebounceSeconds),
        };
    }

    public string[] ToStartupArgs() {
        var normalized = Normalize();
        var args = new List<string>();

        if (normalized.ShowBranchRefs) {
            args.Add("--show-branch-refs");
        }

        if (normalized.ShowStashes) {
            args.Add("--show-stashes");
        }

        if (normalized.DiffContextLines != DefaultDiffContextLines) {
            args.Add($"--diff-context={normalized.DiffContextLines}");
        }

        if (!string.Equals(normalized.DiffPresentationModeKey, DefaultDiffPresentationModeKey, StringComparison.Ordinal)) {
            args.Add($"--diff-presentation={normalized.DiffPresentationModeKey}");
        }

        return args.ToArray();
    }

    private static string? NormalizeDiffPresentationModeKey(string? value) {
        if (string.IsNullOrWhiteSpace(value)) {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch {
            "diff" => "diff",
            "side-by-side" => "side-by-side",
            "new" => "new",
            "old" => "old",
            _ => null,
        };
    }

    private static string NormalizeFontFamily(string? value, string fallback) {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static double NormalizeFontSize(double value, double fallback) {
        return double.IsFinite(value) && value > 0d ? value : fallback;
    }
}
