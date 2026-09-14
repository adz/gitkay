using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitKay.UI;

/// <summary>Asks before deleting a local branch, showing its tip and whether its commits are already in HEAD.</summary>
public sealed class DeleteBranchDialog : Window {
    public enum Choice { Delete, ForceDelete }

    private Choice? _choice;

    private DeleteBranchDialog(BranchTarget branch, string tip, bool? merged) {
        Title = $"Delete {branch.Name}?";
        Icon = AppIcon.Window;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = this.GetResourceObservable("GitKaySurfaceBrush").ToBinding();

        TextBlock Text(string text, double size, string brush, FontWeight weight = FontWeight.Normal) {
            var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
            block[!TextBlock.ForegroundProperty] = this.GetResourceObservable(brush).ToBinding();
            return block;
        }

        var panel = new StackPanel { Margin = new Thickness(20, 16), Spacing = 8 };
        panel.Children.Add(Text($"Delete local branch “{branch.Name}”?", 15, "GitKayTextBrush", FontWeight.SemiBold));
        panel.Children.Add(Text($"Tip: {tip}", 12, "GitKaySecondaryTextBrush"));
        panel.Children.Add(merged switch {
            true => Text("Its commits are already in HEAD, so nothing is lost.", 12, "GitKayAddedAccentBrush"),
            false => Text("It has commits that are not in HEAD. Force deleting makes them unreachable except through the reflog.", 12, "GitKayRemovedAccentBrush"),
            null => Text("Could not tell whether it is merged into HEAD.", 12, "GitKaySecondaryTextBrush"),
        });
        panel.Children.Add(Text("Only the local branch is deleted; remote branches are unchanged.", 11, "GitKayMutedTextBrush"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4), IsCancel = true };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);
        if (merged != false) {
            var delete = new Button { Content = "Delete", Padding = new Thickness(14, 4), IsDefault = true };
            delete.Click += (_, _) => { _choice = Choice.Delete; Close(); };
            buttons.Children.Add(delete);
        }
        else {
            var force = new Button { Content = "Force delete", Padding = new Thickness(14, 4) };
            force[!ForegroundProperty] = this.GetResourceObservable("GitKayRemovedAccentBrush").ToBinding();
            force.Click += (_, _) => { _choice = Choice.ForceDelete; Close(); };
            buttons.Children.Add(force);
        }
        panel.Children.Add(buttons);
        Content = panel;
    }

    public static async Task<Choice?> ShowAsync(Window owner, string workingDirectory, BranchTarget branch) {
        var (tip, merged) = await Task.Run(() => Inspect(workingDirectory, branch.Name));
        var dialog = new DeleteBranchDialog(branch, tip, merged);
        await dialog.ShowDialog(owner);
        return dialog._choice;
    }

    private static (string Tip, bool? Merged) Inspect(string workingDirectory, string branch) {
        string Git(params string[] arguments) {
            var info = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }

        string tip;
        try { tip = Git("log", "-1", "--format=%h %s", $"refs/heads/{branch}"); }
        catch (Exception) { tip = "(unknown)"; }

        bool? merged;
        try {
            using var check = Process.Start(new ProcessStartInfo("git") {
                WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "merge-base", "--is-ancestor", $"refs/heads/{branch}", "HEAD" },
            })!;
            check.WaitForExit();
            merged = check.ExitCode switch { 0 => true, 1 => false, _ => null };
        }
        catch (Exception) { merged = null; }

        return (tip, merged);
    }
}
