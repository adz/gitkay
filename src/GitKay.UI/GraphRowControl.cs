using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace GitKay.UI;

public class GraphRowControl : Control
{
    public static readonly DirectProperty<GraphRowControl, IEnumerable<SegmentProjection>?> SegmentsProperty =
        AvaloniaProperty.RegisterDirect<GraphRowControl, IEnumerable<SegmentProjection>?>(
            nameof(Segments),
            o => o.Segments,
            (o, v) => o.Segments = v);

    private IEnumerable<SegmentProjection>? _segments;
    private INotifyCollectionChanged? _segmentsCollection;
    private SegmentProjection[] _cachedSegments = Array.Empty<SegmentProjection>();
    public IEnumerable<SegmentProjection>? Segments
    {
        get => _segments;
        set
        {
            if (ReferenceEquals(_segments, value))
            {
                if (_segmentsCollection is null && value is not null)
                {
                    AttachSegmentsCollection(value);
                    UpdateCachedSegments();
                    InvalidateVisual();
                }

                return;
            }

            DetachSegmentsCollection();
            SetAndRaise(SegmentsProperty, ref _segments, value);
            AttachSegmentsCollection(value);
            UpdateCachedSegments();
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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (_segments is not null && _segmentsCollection is null)
        {
            AttachSegmentsCollection(_segments);
            UpdateCachedSegments();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachSegmentsCollection();
        base.OnDetachedFromVisualTree(e);
    }

    private void AttachSegmentsCollection(IEnumerable<SegmentProjection>? segments)
    {
        if (segments is INotifyCollectionChanged collection)
        {
            _segmentsCollection = collection;
            _segmentsCollection.CollectionChanged += OnSegmentsCollectionChanged;
        }
        else
        {
            _segmentsCollection = null;
        }
    }

    private void DetachSegmentsCollection()
    {
        if (_segmentsCollection is not null)
        {
            _segmentsCollection.CollectionChanged -= OnSegmentsCollectionChanged;
            _segmentsCollection = null;
        }
    }

    private void OnSegmentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateCachedSegments();
        InvalidateVisual();
    }

    private void UpdateCachedSegments()
    {
        if (_segments is SegmentProjection[] array)
        {
            _cachedSegments = array;
            return;
        }

        if (_segments is ICollection<SegmentProjection> collection)
        {
            if (collection.Count == 0)
            {
                _cachedSegments = Array.Empty<SegmentProjection>();
                return;
            }

            var snapshot = new SegmentProjection[collection.Count];
            collection.CopyTo(snapshot, 0);
            _cachedSegments = snapshot;
            return;
        }

        _cachedSegments = _segments?.ToArray() ?? Array.Empty<SegmentProjection>();
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

        var segments = _cachedSegments;
        SegmentProjection? firstCommitSegment = null;

        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].IsCommit)
            {
                firstCommitSegment = segments[i];
                break;
            }
        }

        var commitBrush =
            firstCommitSegment != null
                ? LaneBrushes[firstCommitSegment.Color % LaneBrushes.Length]
                : LaneBrushes[CommitLane % LaneBrushes.Length];

        for (int i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];

            if (segment.IsCommit)
            {
                continue;
            }

            var brush = LaneBrushes[segment.Color % LaneBrushes.Length];
            var pen = new Pen(brush, 2);
            double x = (segment.Lane + 1) * laneWidth;
            context.DrawLine(pen, new Point(x, 0), new Point(x, rowHeight));
        }

        if (firstCommitSegment != null)
        {
            var commitLaneX = (firstCommitSegment.Lane + 1) * laneWidth;
            var pen = new Pen(commitBrush, 2);

            context.DrawLine(pen, new Point(commitLaneX, 0), new Point(commitLaneX, halfHeight));

            for (int i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];

                if (!segment.IsCommit)
                {
                    continue;
                }

                double targetX = (segment.TargetLane + 1) * laneWidth;
                context.DrawLine(pen, new Point(commitLaneX, halfHeight), new Point(targetX, rowHeight));
            }
        }

        // Draw the commit dot
        double cx = (CommitLane + 1) * laneWidth;
        context.DrawEllipse(commitBrush, null, new Rect(cx - dotRadius, halfHeight - dotRadius, dotRadius * 2, dotRadius * 2));
    }
}
