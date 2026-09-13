using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GitKay.UI;

if (args.FirstOrDefault() == "ui") {
    // The real application styles and Skia text shaping, so layout and rendering costs are representative.
    AppBuilder.Configure<GitKay.UI.App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .SetupWithoutStarting();
    UiInteractions.Run(args.Skip(1).ToArray());
    return;
}

AppBuilder.Configure<BenchmarkApp>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
    .SetupWithoutStarting();

if (args.FirstOrDefault() == "hotpaths") {
    HotPaths.Run(args.Skip(1).ToArray());
    return;
}

var source = args.FirstOrDefault() ?? "/home/adam/projects/Axial/main";
var lines = Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
    .Where(path => path.Contains("/src/") && Path.GetExtension(path) is ".fs" or ".cs" or ".md")
    .SelectMany(path => File.ReadLines(path))
    .Where(line => line.Length > 0)
    .Take(100_000)
    .ToArray();
if (lines.Length == 0) throw new InvalidOperationException($"No source lines found under {source}");

const int visibleLines = 60;
const int frames = 400;
var bitmap = new RenderTargetBitmap(new PixelSize(1400, visibleLines * 18), new Vector(96, 96));
var typeface = new Typeface("Cascadia Code,Consolas,Monospace");

Run("token-per-layout", 0);
Run("styled-line-layout", 1);
Run("plain-line-layout", 2);

void Run(string name, int strategy) {
    for (var warmup = 0; warmup < 20; warmup++) RenderFrame(warmup * 11, strategy);
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var before = GC.GetAllocatedBytesForCurrentThread();
    var samples = new double[frames];
    for (var frame = 0; frame < frames; frame++) {
        var started = Stopwatch.GetTimestamp();
        RenderFrame((frame * 17) % Math.Max(1, lines.Length - visibleLines), strategy);
        samples[frame] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Array.Sort(samples);
    Console.WriteLine($"{name,-20} lines={lines.Length,6} p50={Percentile(.50),7:F2}ms p95={Percentile(.95),7:F2}ms p99={Percentile(.99),7:F2}ms max={samples[^1],7:F2}ms alloc/frame={allocated / frames,10:N0}B");
    double Percentile(double p) => samples[Math.Min(samples.Length - 1, (int)Math.Ceiling(samples.Length * p) - 1)];
}

void RenderFrame(int first, int strategy) {
    using var context = bitmap.CreateDrawingContext();
    context.FillRectangle(Brushes.Black, new Rect(0, 0, 1400, visibleLines * 18));
    for (var row = 0; row < visibleLines; row++) {
        var text = lines[(first + row) % lines.Length];
        var x = 0d;
        if (strategy == 0) {
            foreach (var token in SyntaxHighlighting.Tokenize(text)) {
                var layout = new FormattedText(token.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 12, Brushes.White);
                context.DrawText(layout, new Point(x, row * 18));
                x += layout.Width;
            }
        }
        else {
            var layout = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 12, Brushes.White);
            if (strategy == 1) {
                var offset = 0;
                foreach (var token in SyntaxHighlighting.Tokenize(text)) {
                    if (token.Kind != HighlightKind.Plain)
                        layout.SetForegroundBrush(Brushes.CornflowerBlue, offset, token.Text.Length);
                    offset += token.Text.Length;
                }
            }
            context.DrawText(layout, new Point(0, row * 18));
        }
    }
}

sealed class BenchmarkApp : Application { }
