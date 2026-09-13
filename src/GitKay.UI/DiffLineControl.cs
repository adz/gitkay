using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;

namespace GitKay.UI;

public sealed class DiffLineControl : Control {
    private const int MaxHighlightedLineLength = 240;
    private static readonly Typeface CodeTypeface = new(FontStacks.Mono);
    private const int HighlightPromotionsPerFrame = 8;
    private static readonly SemaphoreSlim HighlightWorkers = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
    private static readonly ConcurrentQueue<HighlightCompletion> HighlightCompletions = new();
    private static int _completionDrainScheduled;
    private static long _highlightingSuspendedUntil;

    public static void NotifyScrolling() =>
        Interlocked.Exchange(ref _highlightingSuspendedUntil, Stopwatch.GetTimestamp() + Stopwatch.Frequency / 25);

    private static bool IsScrolling => Stopwatch.GetTimestamp() < Interlocked.Read(ref _highlightingSuspendedUntil);

    public void CancelPendingHighlighting() {
        foreach (var pending in _pendingHighlights.Values)
            pending.Cancel();
        _pendingHighlights.Clear();
    }
    private readonly Dictionary<string, CancellationTokenSource> _pendingHighlights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<HighlightToken>> _preparedHighlights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FormattedText> _plainLayouts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FormattedText> _colouredLayouts = new(StringComparer.Ordinal);
    private DiffLineProjection? _highlightedRow;
    public static readonly StyledProperty<IDiffRowProjection> RowProperty =
        AvaloniaProperty.Register<DiffLineControl, IDiffRowProjection>(nameof(Row));

    public static readonly StyledProperty<string> ModeProperty =
        AvaloniaProperty.Register<DiffLineControl, string>(nameof(Mode), "diff");

    public IDiffRowProjection Row {
        get => GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public string Mode {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    static DiffLineControl() {
        AffectsRender<DiffLineControl>(RowProperty, ModeProperty);
        AffectsMeasure<DiffLineControl>(RowProperty, ModeProperty);
    }

    protected override Size MeasureOverride(Size availableSize) {
        return new Size(0, 18); // Fixed height for speed
    }

    public override void Render(DrawingContext context) {
        if (Row is not DiffLineProjection line) {
            return;
        }

        var bounds = Bounds;
        if (line.RowBackground != Brushes.Transparent) {
            context.DrawRectangle(line.RowBackground, null, bounds);
        }

        if (line.BorderBrush != Brushes.Transparent) {
            context.DrawRectangle(null, new Pen(line.BorderBrush, 1), bounds.Deflate(0.5));
        }

        var typeface = CodeTypeface;
        var fontSize = 12.0;

        switch (Mode) {
            case "side-by-side":
                RenderSideBySide(context, line, typeface, fontSize);
                break;
            case "new":
                RenderNew(context, line, typeface, fontSize);
                break;
            case "old":
                RenderOld(context, line, typeface, fontSize);
                break;
            default:
                RenderUnified(context, line, typeface, fontSize);
                break;
        }
    }

    private void RenderUnified(DrawingContext context, DiffLineProjection line, Typeface typeface, double fontSize) {
        var x = 0.0;
        DrawText(context, line.OldLineNoText, line.LineNumberForeground, ref x, 40, typeface, fontSize, TextAlignment.Right);
        DrawText(context, line.NewLineNoText, line.LineNumberForeground, ref x, 40, typeface, fontSize, TextAlignment.Right);
        DrawText(context, line.Prefix, line.PrefixForeground, ref x, 16, typeface, fontSize, TextAlignment.Center, FontWeight.Bold);

        var contentX = x;
        RenderTokens(context, line.Content, line.Foreground, contentX, typeface, fontSize);

        if (line.IsSearchMatch) {
            RenderSearchHighlight(context, line.Content, line.MatchPrefix, line.MatchText, contentX, typeface, fontSize);
        }
    }

    private void RenderSideBySide(DrawingContext context, DiffLineProjection line, Typeface typeface, double fontSize) {
        var mid = Bounds.Width / 2.0;

        // Old cell
        if (line.OldCellBackground != Brushes.Transparent) {
            context.DrawRectangle(line.OldCellBackground, null, new Rect(0, 0, mid - 8, Bounds.Height));
        }

        var x = 0.0;
        DrawText(context, line.OldLineNoText, line.LineNumberForeground, ref x, 40, typeface, fontSize, TextAlignment.Right);
        using (context.PushClip(new Rect(40, 0, Math.Max(0, mid - 48), Bounds.Height))) {
            RenderTokens(context, line.OldContent, line.Foreground, x, typeface, fontSize);
        }

        // Prefix
        var px = mid - 8;
        DrawText(context, line.Prefix, line.PrefixForeground, ref px, 16, typeface, fontSize, TextAlignment.Center, FontWeight.Bold);

        // New cell
        if (line.NewCellBackground != Brushes.Transparent) {
            context.DrawRectangle(line.NewCellBackground, null, new Rect(mid + 8, 0, Bounds.Width - (mid + 8), Bounds.Height));
        }

        var nx = mid + 8;
        DrawText(context, line.NewLineNoText, line.LineNumberForeground, ref nx, 40, typeface, fontSize, TextAlignment.Right);
        using (context.PushClip(new Rect(mid + 48, 0, Math.Max(0, Bounds.Width - mid - 48), Bounds.Height))) {
            RenderTokens(context, line.NewContent, line.Foreground, nx, typeface, fontSize);
        }
    }

    private void RenderNew(DrawingContext context, DiffLineProjection line, Typeface typeface, double fontSize) {
        var x = 0.0;
        DrawText(context, line.Prefix, line.PrefixForeground, ref x, 16, typeface, fontSize, TextAlignment.Center, FontWeight.Bold);
        DrawText(context, line.NewLineNoText, line.LineNumberForeground, ref x, 40, typeface, fontSize, TextAlignment.Right);
        RenderTokens(context, line.NewContent, line.Foreground, x, typeface, fontSize);
    }

    private void RenderOld(DrawingContext context, DiffLineProjection line, Typeface typeface, double fontSize) {
        var x = 0.0;
        DrawText(context, line.Prefix, line.PrefixForeground, ref x, 16, typeface, fontSize, TextAlignment.Center, FontWeight.Bold);
        DrawText(context, line.OldLineNoText, line.LineNumberForeground, ref x, 40, typeface, fontSize, TextAlignment.Right);
        RenderTokens(context, line.OldContent, line.Foreground, x, typeface, fontSize);
    }

    private void DrawText(DrawingContext context, string text, IBrush foreground, ref double x, double width, Typeface typeface, double fontSize, TextAlignment alignment, FontWeight fontWeight = FontWeight.Normal) {
        if (string.IsNullOrEmpty(text)) {
            x += width;
            return;
        }

        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(typeface.FontFamily, typeface.Style, fontWeight), fontSize, foreground) {
            MaxTextWidth = width,
            TextAlignment = alignment
        };

        context.DrawText(ft, new Point(x, (Bounds.Height - ft.Height) / 2));
        x += width;
    }

    private void RenderTokens(DrawingContext context, string text, IBrush baseForeground, double x, Typeface typeface, double fontSize) {
        if (string.IsNullOrEmpty(text)) return;

        var tokens = IsScrolling ? null : GetPreparedTokens(text);
        var useColour = tokens != null;
        var layouts = useColour ? _colouredLayouts : _plainLayouts;
        if (!layouts.TryGetValue(text, out var formatted)) {
            formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, baseForeground);
            if (useColour) {
                var offset = 0;
                foreach (var token in tokens!) {
                    var brush = token.Kind switch {
                        HighlightKind.Keyword => SyntaxHighlighting.GetKeywordBrush(),
                        HighlightKind.String => SyntaxHighlighting.GetStringBrush(),
                        HighlightKind.Number => SyntaxHighlighting.GetNumberBrush(),
                        HighlightKind.Comment => SyntaxHighlighting.GetCommentBrush(),
                        HighlightKind.TypeName => SyntaxHighlighting.GetTypeBrush(),
                        _ => baseForeground,
                    };
                    if (token.Kind != HighlightKind.Plain)
                        formatted.SetForegroundBrush(brush, offset, token.Text.Length);
                    offset += token.Text.Length;
                }
            }
            layouts[text] = formatted;
        }

        context.DrawText(formatted, new Point(x, (Bounds.Height - formatted.Height) / 2));
    }

    private IReadOnlyList<HighlightToken>? GetPreparedTokens(string text) {
        if (text.Length > MaxHighlightedLineLength)
            return null;

        if (!ReferenceEquals(_highlightedRow, Row)) {
            CancelPendingHighlighting();
            _preparedHighlights.Clear();
            _plainLayouts.Clear();
            _colouredLayouts.Clear();
            _highlightedRow = Row as DiffLineProjection;
        }

        if (_preparedHighlights.TryGetValue(text, out var prepared))
            return prepared;
        if (_pendingHighlights.ContainsKey(text))
            return null;

        var cancellation = new CancellationTokenSource();
        _pendingHighlights[text] = cancellation;
        _ = PrepareTokensAsync(text, _highlightedRow, cancellation);
        return null;
    }

    private async Task PrepareTokensAsync(string text, DiffLineProjection? row, CancellationTokenSource cancellation) {
        try {
            await HighlightWorkers.WaitAsync(cancellation.Token).ConfigureAwait(false);
            IReadOnlyList<HighlightToken> tokens;
            try {
                tokens = await Task.Run(() => SyntaxHighlighting.Tokenize(text), cancellation.Token).ConfigureAwait(false);
            }
            finally {
                HighlightWorkers.Release();
            }

            HighlightCompletions.Enqueue(new HighlightCompletion(this, row, text, tokens, cancellation));
            ScheduleCompletionDrain();
        }
        catch (OperationCanceledException) {
        }
    }

    private static void ScheduleCompletionDrain() {
        if (Interlocked.Exchange(ref _completionDrainScheduled, 1) == 0)
            Dispatcher.UIThread.Post(DrainHighlightCompletions, DispatcherPriority.Background);
    }

    private static void DrainHighlightCompletions() {
        var promoted = 0;
        while (promoted < HighlightPromotionsPerFrame && HighlightCompletions.TryDequeue(out var completion)) {
            completion.Control.ApplyHighlightCompletion(completion);
            promoted++;
        }

        Interlocked.Exchange(ref _completionDrainScheduled, 0);
        if (!HighlightCompletions.IsEmpty)
            ScheduleCompletionDrain();
    }

    private void ApplyHighlightCompletion(HighlightCompletion completion) {
        if (completion.Cancellation.IsCancellationRequested || !ReferenceEquals(_highlightedRow, completion.Row))
            return;

        _pendingHighlights.Remove(completion.Text);
        _preparedHighlights[completion.Text] = completion.Tokens;
        _colouredLayouts.Remove(completion.Text);
        InvalidateVisual();
    }

    private sealed record HighlightCompletion(
        DiffLineControl Control,
        DiffLineProjection? Row,
        string Text,
        IReadOnlyList<HighlightToken> Tokens,
        CancellationTokenSource Cancellation);

    private void RenderSearchHighlight(DrawingContext context, string text, string prefix, string match, double x, Typeface typeface, double fontSize) {
        if (string.IsNullOrEmpty(match)) return;

        var prefixFt = new FormattedText(prefix, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.Transparent);
        var matchX = x + prefixFt.Width;

        var matchFt = new FormattedText(match, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(typeface.FontFamily, typeface.Style, FontWeight.Bold), fontSize, DiffSearchPresentation.MatchForeground);
        var y = (Bounds.Height - matchFt.Height) / 2;

        // Draw underline
        context.DrawLine(new Pen(DiffSearchPresentation.MatchForeground, 1), new Point(matchX, y + matchFt.Height), new Point(matchX + matchFt.Width, y + matchFt.Height));
        context.DrawText(matchFt, new Point(matchX, y));
    }
}
