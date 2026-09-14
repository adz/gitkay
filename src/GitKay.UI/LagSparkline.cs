using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GitKay.UI;

/// <summary>Bars of UI-thread wait per watchdog ping: green when responsive, amber when slow, red when stalled.</summary>
public sealed class LagSparkline : Control {
    public static readonly StyledProperty<double[]?> ValuesProperty =
        AvaloniaProperty.Register<LagSparkline, double[]?>(nameof(Values));

    private static readonly IBrush Good = new SolidColorBrush(Color.FromRgb(63, 185, 80)).ToImmutable();
    private static readonly IBrush Slow = new SolidColorBrush(Color.FromRgb(210, 153, 34)).ToImmutable();
    private static readonly IBrush Stalled = new SolidColorBrush(Color.FromRgb(248, 81, 73)).ToImmutable();

    static LagSparkline() => AffectsRender<LagSparkline>(ValuesProperty);

    public double[]? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    public override void Render(DrawingContext context) {
        var values = Values;
        if (values == null || values.Length == 0 || Bounds.Width <= 0) return;
        const int slots = 60;
        var width = Bounds.Width / slots;
        // Log scale: 1ms..5s fills the height, so both small jitter and long stalls stay readable.
        double Height(double ms) => Bounds.Height * Math.Clamp(Math.Log10(Math.Max(1, ms)) / Math.Log10(5000), 0.06, 1);
        for (var i = 0; i < values.Length; i++) {
            var ms = values[i];
            var height = Height(ms);
            var x = (slots - values.Length + i) * width;
            context.FillRectangle(ms > 1000 ? Stalled : ms > 100 ? Slow : Good, new Rect(x + 0.5, Bounds.Height - height, Math.Max(1, width - 1), height));
        }
    }
}
