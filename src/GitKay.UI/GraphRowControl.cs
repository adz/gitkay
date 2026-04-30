using System.Collections.Generic;
using System.Linq;
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
        double laneWidth = 15;
        double rowHeight = Bounds.Height;
        double halfHeight = rowHeight / 2;
        double dotRadius = 4;

        var segments = Segments?.ToArray() ?? System.Array.Empty<SegmentProjection>();
        var commitSegments = segments.Where(segment => segment.IsCommit).ToArray();
        var commitBrush =
            commitSegments.Length > 0
                ? LaneBrushes[commitSegments[0].Color % LaneBrushes.Length]
                : LaneBrushes[CommitLane % LaneBrushes.Length];

        foreach (var segment in segments.Where(segment => !segment.IsCommit).GroupBy(segment => segment.Lane))
        {
            var firstSegment = segment.First();
            var brush = LaneBrushes[firstSegment.Color % LaneBrushes.Length];
            var pen = new Pen(brush, 2);

            double x = (segment.Key + 1) * laneWidth;
            context.DrawLine(pen, new Point(x, 0), new Point(x, rowHeight));
        }

        if (commitSegments.Length > 0)
        {
            var commitLaneX = (commitSegments[0].Lane + 1) * laneWidth;
            var pen = new Pen(commitBrush, 2);

            context.DrawLine(pen, new Point(commitLaneX, 0), new Point(commitLaneX, halfHeight));

            foreach (var segment in commitSegments)
            {
                double targetX = (segment.TargetLane + 1) * laneWidth;
                context.DrawLine(pen, new Point(commitLaneX, halfHeight), new Point(targetX, rowHeight));
            }
        }

        // Draw the commit dot
        double cx = (CommitLane + 1) * laneWidth;
        context.DrawEllipse(commitBrush, null, new Rect(cx - dotRadius, halfHeight - dotRadius, dotRadius * 2, dotRadius * 2));
    }
}
