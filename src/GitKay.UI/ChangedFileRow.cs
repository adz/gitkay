using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitKay.UI;

/// <summary>
/// One changed file as every list shows it: a state marker, a coloured change badge, the name, and the counts in
/// columns that line up down the list. The history, commit and folder windows had grown three copies of this, which
/// is how they came to describe the same file three different ways; there is one now.
/// </summary>
public sealed class ChangedFileRow : TemplatedControl {
    public static readonly StyledProperty<string?> MarkerProperty = AvaloniaProperty.Register<ChangedFileRow, string?>(nameof(Marker));
    public static readonly StyledProperty<string?> GlyphProperty = AvaloniaProperty.Register<ChangedFileRow, string?>(nameof(Glyph));
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<ChangedFileRow, string?>(nameof(Label));
    public static readonly StyledProperty<string?> AddedTextProperty = AvaloniaProperty.Register<ChangedFileRow, string?>(nameof(AddedText));
    public static readonly StyledProperty<string?> RemovedTextProperty = AvaloniaProperty.Register<ChangedFileRow, string?>(nameof(RemovedText));
    public static readonly StyledProperty<bool> ShowsCountsProperty = AvaloniaProperty.Register<ChangedFileRow, bool>(nameof(ShowsCounts), true);
    public static readonly StyledProperty<bool> ShowsBadgeProperty = AvaloniaProperty.Register<ChangedFileRow, bool>(nameof(ShowsBadge), true);
    public static readonly StyledProperty<bool> IsAddedProperty = AvaloniaProperty.Register<ChangedFileRow, bool>(nameof(IsAdded));
    public static readonly StyledProperty<bool> IsDeletedProperty = AvaloniaProperty.Register<ChangedFileRow, bool>(nameof(IsDeleted));
    public static readonly StyledProperty<bool> IsModifiedProperty = AvaloniaProperty.Register<ChangedFileRow, bool>(nameof(IsModified));
    public static readonly StyledProperty<bool> IsSearchMatchProperty = AvaloniaProperty.Register<ChangedFileRow, bool>(nameof(IsSearchMatch));
    public static readonly StyledProperty<Thickness> IndentProperty = AvaloniaProperty.Register<ChangedFileRow, Thickness>(nameof(Indent));

    public string? Marker { get => GetValue(MarkerProperty); set => SetValue(MarkerProperty, value); }
    public string? Glyph { get => GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? AddedText { get => GetValue(AddedTextProperty); set => SetValue(AddedTextProperty, value); }
    public string? RemovedText { get => GetValue(RemovedTextProperty); set => SetValue(RemovedTextProperty, value); }
    public bool ShowsCounts { get => GetValue(ShowsCountsProperty); set => SetValue(ShowsCountsProperty, value); }
    public bool ShowsBadge { get => GetValue(ShowsBadgeProperty); set => SetValue(ShowsBadgeProperty, value); }
    public bool IsAdded { get => GetValue(IsAddedProperty); set => SetValue(IsAddedProperty, value); }
    public bool IsDeleted { get => GetValue(IsDeletedProperty); set => SetValue(IsDeletedProperty, value); }
    public bool IsModified { get => GetValue(IsModifiedProperty); set => SetValue(IsModifiedProperty, value); }
    public bool IsSearchMatch { get => GetValue(IsSearchMatchProperty); set => SetValue(IsSearchMatchProperty, value); }
    public Thickness Indent { get => GetValue(IndentProperty); set => SetValue(IndentProperty, value); }

    /// <summary>The width each count column keeps whether or not it has a number in it.</summary>
    private const double CountWidth = 38;

    public ChangedFileRow() {
        var marker = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Width = 14 };
        marker.Bind(TextBlock.TextProperty, this.GetObservable(MarkerProperty));
        marker.Bind(ForegroundProperty, this.GetResourceObservable("GitKaySecondaryTextBrush").ToBinding());

        var glyph = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        glyph.Bind(TextBlock.TextProperty, this.GetObservable(GlyphProperty));
        var badge = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, Child = glyph };
        badge.Classes.Add("fileChange");
        badge.Bind(IsVisibleProperty, this.GetObservable(ShowsBadgeProperty));
        Bind(badge, "added", IsAddedProperty);
        Bind(badge, "deleted", IsDeletedProperty);
        Bind(badge, "modified", IsModifiedProperty);

        var name = new TextBlock {
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        name.Bind(TextBlock.TextProperty, this.GetObservable(LabelProperty));
        name.Bind(ForegroundProperty, this.GetResourceObservable("GitKayTextBrush").ToBinding());
        Bind(name, "pathMatch", IsSearchMatchProperty);

        var added = Count(AddedTextProperty, "GitKayAddedAccentBrush");
        var removed = Count(RemovedTextProperty, "GitKayRemovedAccentBrush");

        var trailing = new FileRowLayout { Spacing = 7 };
        trailing.Children.Add(name);
        trailing.Children.Add(added);
        trailing.Children.Add(removed);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 6 };
        Grid.SetColumn(badge, 1);
        Grid.SetColumn(trailing, 2);
        grid.Children.Add(marker);
        grid.Children.Add(badge);
        grid.Children.Add(trailing);
        grid.Bind(MarginProperty, this.GetObservable(IndentProperty));

        Template = new FuncControlTemplate((_, _) => grid);
    }

    private TextBlock Count(StyledProperty<string?> text, string brush) {
        var block = new TextBlock {
            FontSize = 11, Width = CountWidth, TextAlignment = TextAlignment.Right,
            FontFamily = FontStacks.Mono, VerticalAlignment = VerticalAlignment.Center,
        };
        block.Bind(TextBlock.TextProperty, this.GetObservable(text));
        block.Bind(ForegroundProperty, this.GetResourceObservable(brush).ToBinding());
        block.Bind(IsVisibleProperty, this.GetObservable(ShowsCountsProperty));
        return block;
    }

    /// <summary>Adds or removes a class as a flag changes, which is how the badge takes its colour.</summary>
    private void Bind(StyledElement target, string name, StyledProperty<bool> flag) =>
        this.GetObservable(flag).Subscribe(new AnonymousObserver<bool>(on => {
            if (on) { if (!target.Classes.Contains(name)) target.Classes.Add(name); }
            else target.Classes.Remove(name);
        }));

    private sealed class AnonymousObserver<T>(System.Action<T> next) : System.IObserver<T> {
        public void OnCompleted() { }
        public void OnError(System.Exception error) { }
        public void OnNext(T value) => next(value);
    }
}
