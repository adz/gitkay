using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GitKay.UI;

public class GraphRowControl : Control
{
    public static readonly DirectProperty<GraphRowControl, IEnumerable<SegmentProjection>?> SegmentsProperty =
        AvaloniaProperty.RegisterDirect<GraphRowControl, IEnumerable<SegmentProjection>?>(
            nameof(Segments),
            o => o.Segments,
            (o, v) => o.Segments = v);

    private IEnumerable<SegmentProjection>? _segments;
    public IEnumerable<SegmentProjection>? Segments
    {
        get => _segments;
        set
        {
            SetAndRaise(SegmentsProperty, ref _segments, value);
            InvalidateVisual();
        }
    }

    public static readonly StyledProperty<int> CommitLaneProperty =
        AvaloniaProperty.Register<GraphRowControl, int>(nameof(CommitLane));

    public int CommitLane
    {
        get => GetValue(CommitLaneProperty);
        set => SetValue(CommitLaneProperty, value);
    }

    private static readonly IBrush[] LaneBrushes = new IBrush[]
    {
        Brushes.Red, Brushes.Green, Brushes.Blue, Brushes.Orange, Brushes.Purple,
        Brushes.Cyan, Brushes.Magenta, Brushes.Yellow, Brushes.LightGreen, Brushes.LightBlue
    };

    public override void Render(DrawingContext context)
    {
        if (Segments == null) return;

        double laneWidth = 15;
        double rowHeight = Bounds.Height;
        double halfHeight = rowHeight / 2;
        double dotRadius = 4;

        foreach (var segment in Segments)
        {
            var brush = LaneBrushes[segment.Color % LaneBrushes.Length];
            var pen = new Pen(brush, 2);

            double x1 = (segment.Lane + 1) * laneWidth;
            double x2 = (segment.TargetLane + 1) * laneWidth;

            // Draw line from current row to next row
            // We draw from (x1, halfHeight) to (x2, rowHeight) if it's a commit?
            // Actually, segments describe the path from THIS row to NEXT row.
            // So we draw from (x1, halfHeight) to (x2, rowHeight + halfHeight)? No, row-based.
            
            // Layout: 
            // Previous Row: (..., halfHeight)
            // This Row: (x1, 0) -> (x1, halfHeight) [Entry]
            // This Row: (x1, halfHeight) -> (x2, rowHeight) [Exit to next]
            
            // Entry line (from previous row's exit)
            // This is handled by the fact that we draw the EXIT in the previous row.
            // Wait, virtualized list means rows are independent.
            // Let's draw:
            // 1. Line from (x1, 0) to (x1, halfHeight) -- This is the continuation from previous row's exit.
            // 2. Line from (x1, halfHeight) to (x2, rowHeight) -- This is the exit to next row.

            context.DrawLine(pen, new Point(x1, 0), new Point(x1, halfHeight));
            context.DrawLine(pen, new Point(x1, halfHeight), new Point(x2, rowHeight));
        }

        // Draw the commit dot
        double cx = (CommitLane + 1) * laneWidth;
        context.DrawEllipse(LaneBrushes[CommitLane % LaneBrushes.Length], null, new Rect(cx - dotRadius, halfHeight - dotRadius, dotRadius * 2, dotRadius * 2));
    }
}
