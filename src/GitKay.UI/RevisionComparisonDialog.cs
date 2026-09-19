using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;

namespace GitKay.UI;

/// <summary>Chooses arbitrary Git revisions for a three-dot comparison. Editable boxes also accept hashes and tags.</summary>
public sealed class RevisionComparisonDialog : Window {
    private readonly ComboBox _base;
    private readonly ComboBox _target;

    private RevisionComparisonDialog(IEnumerable<string> revisions, string baseRevision, string targetRevision, bool targetEditable) {
        Title = "Compare revisions";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppIcon.Window;

        var choices = revisions.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
        _base = new ComboBox { ItemsSource = choices, IsEditable = true, Text = baseRevision, HorizontalAlignment = HorizontalAlignment.Stretch };
        _target = new ComboBox { ItemsSource = choices, IsEditable = true, Text = targetRevision, IsEnabled = targetEditable, HorizontalAlignment = HorizontalAlignment.Stretch };
        var compare = new Button { Content = "Compare", IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        compare.Click += (_, _) => {
            var from = _base.Text?.Trim();
            var to = _target.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to)) Close((from, to));
        };
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel {
            Margin = new Avalonia.Thickness(18), Spacing = 10,
            Children = {
                new TextBlock { Text = "Base revision" }, _base,
                new TextBlock { Text = "Target revision" }, _target,
                new TextBlock { Text = "Branch names, tags, hashes and revision expressions are accepted.", Opacity = 0.65, FontSize = 11 },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, compare } }
            }
        };
    }

    public static System.Threading.Tasks.Task<(string Base, string Target)?> ShowAsync(
        Window owner, IEnumerable<string> revisions, string baseRevision, string targetRevision, bool targetEditable = true) =>
        new RevisionComparisonDialog(revisions, baseRevision, targetRevision, targetEditable).ShowDialog<(string, string)?>(owner);
}
