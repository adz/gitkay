using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GitKay.UI;

/// <summary>Compact GitHub-style change-size indicator: five blocks split by added/removed ratio.</summary>
public sealed class DiffStatBar : Control {
    private static readonly IBrush AddedFallback = new SolidColorBrush(Color.FromRgb(63, 185, 80)).ToImmutable();
    private static readonly IBrush RemovedFallback = new SolidColorBrush(Color.FromRgb(248, 81, 73)).ToImmutable();
    private static readonly IBrush NeutralFallback = new SolidColorBrush(Color.FromRgb(48, 54, 61)).ToImmutable();

    public static readonly StyledProperty<int> AddedProperty = AvaloniaProperty.Register<DiffStatBar, int>(nameof(Added));
    public static readonly StyledProperty<int> RemovedProperty = AvaloniaProperty.Register<DiffStatBar, int>(nameof(Removed));
    public static readonly StyledProperty<bool> IsKnownProperty = AvaloniaProperty.Register<DiffStatBar, bool>(nameof(IsKnown));
    public static readonly StyledProperty<double> BlockSizeProperty = AvaloniaProperty.Register<DiffStatBar, double>(nameof(BlockSize), 5);

    static DiffStatBar() => AffectsRender<DiffStatBar>(AddedProperty, RemovedProperty, IsKnownProperty, BlockSizeProperty);

    public int Added { get => GetValue(AddedProperty); set => SetValue(AddedProperty, value); }
    public int Removed { get => GetValue(RemovedProperty); set => SetValue(RemovedProperty, value); }
    public bool IsKnown { get => GetValue(IsKnownProperty); set => SetValue(IsKnownProperty, value); }
    public double BlockSize { get => GetValue(BlockSizeProperty); set => SetValue(BlockSizeProperty, value); }

    private double Spacing => System.Math.Max(1, BlockSize / 4);

    protected override Size MeasureOverride(Size availableSize) =>
        new(5 * BlockSize + 4 * Spacing, BlockSize);

    /// <summary>Returns (green, red) block counts out of five, scaled like GitHub for small changes.</summary>
    public static (int Green, int Red) Blocks(int added, int removed) {
        var total = added + removed;
        if (total == 0) return (0, 0);
        var filled = System.Math.Min(5, total);
        var green = (int)System.Math.Round(filled * (double)added / total);
        if (added > 0 && green == 0) green = 1;
        var red = filled - green;
        if (removed > 0 && red == 0) { red = 1; green = filled - 1; }
        return (System.Math.Max(0, green), System.Math.Max(0, red));
    }

    public override void Render(DrawingContext context) {
        var added = Brush("GitKayAddedAccentBrush", AddedFallback);
        var removed = Brush("GitKayRemovedAccentBrush", RemovedFallback);
        var neutral = Brush("GitKayDiffStatNeutralBrush", NeutralFallback);
        var (green, red) = IsKnown ? Blocks(Added, Removed) : (0, 0);
        var y = (Bounds.Height - BlockSize) / 2;
        for (var i = 0; i < 5; i++) {
            var brush = i < green ? added : i < green + red ? removed : neutral;
            context.DrawRectangle(brush, null, new Rect(i * (BlockSize + Spacing), y, BlockSize, BlockSize), 1.5, 1.5);
        }
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
}
