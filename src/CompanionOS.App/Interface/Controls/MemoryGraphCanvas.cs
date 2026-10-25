using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CompanionOS.App.Interface.Theme;
using CompanionOS.Core.Memory;

namespace CompanionOS.App.Interface.Controls;

/// <summary>
/// Her memory, drawn.
///
/// One custom control renders the whole graph instead of creating a UI element per node,
/// which is the only way this stays smooth with tens of thousands of memories.
///
/// Readability rules:
///   * colour is the layer (core, episodic, semantic, procedural);
///   * size is the strength of the memory;
///   * red dashed links are the inhibitory ones, the "never do this" connections;
///   * zoomed far out you see clusters, zoomed in you see titles.
///
/// The layout is deterministic — concentric layer rings with a short spring relaxation —
/// so the picture does not jump around between sessions and you can learn where things are.
/// </summary>
public sealed class MemoryGraphCanvas : Control
{
    private readonly List<GraphNode> _nodes = new();
    private readonly List<GraphEdge> _edges = new();

    private double _scale = 1.0;
    private Point _pan;
    private Point? _dragOrigin;
    private Point _dragStartPan;
    private string? _selectedNodeId;

    public MemoryGraphCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>Raised when the user clicks a node (or null to clear the selection).</summary>
    public event Action<string?>? NodeSelected;

    /// <summary>The node currently highlighted in the inspector.</summary>
    public string? SelectedNodeId
    {
        get => _selectedNodeId;
        set
        {
            _selectedNodeId = value;
            InvalidateVisual();
        }
    }

    /// <summary>Number of nodes currently drawn, shown in the footer.</summary>
    public int NodeCount => _nodes.Count;

    /// <summary>Replaces the picture with a fresh snapshot and lays it out.</summary>
    public void SetGraph(IReadOnlyList<MemoryNode> nodes, IReadOnlyList<MemorySynapse> synapses)
    {
        Dictionary<string, int> indexById = new(StringComparer.Ordinal);

        _nodes.Clear();
        _edges.Clear();

        foreach (MemoryNode node in nodes.Take(6000))
        {
            indexById[node.Id] = _nodes.Count;
            _nodes.Add(new GraphNode
            {
                Id = node.Id,
                Title = node.Title,
                Layer = node.Layer,
                Strength = node.Strength,
                IsRecent = node.IsRecent
            });
        }

        foreach (MemorySynapse synapse in synapses)
        {
            if (!indexById.TryGetValue(synapse.FromNodeId, out int fromIndex) ||
                !indexById.TryGetValue(synapse.ToNodeId, out int toIndex))
            {
                continue;
            }

            _edges.Add(new GraphEdge
            {
                FromIndex = fromIndex,
                ToIndex = toIndex,
                Polarity = synapse.Polarity,
                Strength = synapse.Strength
            });
        }

        LayoutNodes();
        InvalidateVisual();
    }

    /// <summary>Moves the camera; used by the "fit" button and by the replay animation.</summary>
    public void ResetView()
    {
        _scale = 1.0;
        _pan = default;
        InvalidateVisual();
    }

    public void ZoomBy(double factor)
    {
        _scale = Math.Clamp(_scale * factor, 0.15, 6.0);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width < 10 || height < 10)
        {
            return;
        }

        Point center = new(width / 2, height / 2);

        // Subtle vignette-free background grid: gives depth without stealing attention.
        IBrush gridBrush = new SolidColorBrush(Palette.Stroke, 0.35);
        for (double x = 0; x < width; x += 64)
        {
            context.DrawLine(new Pen(gridBrush, 0.5), new Point(x, 0), new Point(x, height));
        }

        for (double y = 0; y < height; y += 64)
        {
            context.DrawLine(new Pen(gridBrush, 0.5), new Point(0, y), new Point(width, y));
        }

        if (_nodes.Count == 0)
        {
            FormattedText empty = new("Nothing remembered yet. Teach her something.",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(Palette.UiFont), Palette.FontBody, Palette.TextFaintBrush);
            context.DrawText(empty, new Point(center.X - empty.Width / 2, center.Y - empty.Height / 2));
            return;
        }

        Point ToScreen(GraphNode node) => new(
            center.X + (node.X * _scale) + _pan.X,
            center.Y + (node.Y * _scale) + _pan.Y);

        // 1. Edges first so nodes always sit on top.
        foreach (GraphEdge edge in _edges)
        {
            Point from = ToScreen(_nodes[edge.FromIndex]);
            Point to = ToScreen(_nodes[edge.ToIndex]);

            Color edgeColor = edge.Polarity switch
            {
                SynapsePolarity.Inhibitory => Palette.Danger,
                SynapsePolarity.Affective => Palette.Warning,
                SynapsePolarity.Inward => Palette.Cyan,
                _ => Palette.TextFaint
            };

            double opacity = 0.12 + edge.Strength * 0.35;
            IPen edgePen = new Pen(new SolidColorBrush(edgeColor, opacity), 1);

            if (_scale < 0.45)
            {
                continue;
            }

            if (edge.Polarity == SynapsePolarity.Inhibitory)
            {
                DrawDashedLine(context, edgePen, from, to);
            }
            else
            {
                context.DrawLine(edgePen, from, to);
            }
        }

        // 2. Nodes.
        foreach (GraphNode node in _nodes)
        {
            Point position = ToScreen(node);
            if (position.X < -80 || position.Y < -80 || position.X > width + 80 || position.Y > height + 80)
            {
                continue;
            }

            Color layerColor = ColorForLayer(node.Layer);
            double radius = (2.5 + node.Strength * 6) * Math.Clamp(_scale, 0.4, 1.6);

            if (node.IsRecent)
            {
                context.DrawEllipse(new SolidColorBrush(layerColor, 0.20), null, position,
                    radius * 2.6, radius * 2.6);
            }

            IBrush nodeBrush = new SolidColorBrush(layerColor, 0.35 + node.Strength * 0.65);
            context.DrawEllipse(nodeBrush, null, position, radius, radius);

            if (node.Id == _selectedNodeId)
            {
                context.DrawEllipse(null, new Pen(Palette.TextPrimaryBrush, 1.6), position,
                    radius + 4, radius + 4);
            }

            // Labels appear only when there is room for them, which is what keeps the
            // far view readable as clusters rather than a wall of text.
            if (_scale >= 0.75 && node.Strength > 0.35)
            {
                FormattedText label = new(node.Title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(Palette.UiFont), 12, new SolidColorBrush(Palette.TextPrimary, 0.88));
                context.DrawText(label, new Point(position.X + radius + 4, position.Y - label.Height / 2));
            }
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs eventArgs)
    {
        base.OnPointerWheelChanged(eventArgs);
        ZoomBy(eventArgs.Delta.Y > 0 ? 1.12 : 0.89);
        eventArgs.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs eventArgs)
    {
        base.OnPointerPressed(eventArgs);

        Point position = eventArgs.GetPosition(this);
        GraphNode? hit = HitTest(position);

        if (hit is not null)
        {
            SelectedNodeId = hit.Id;
            NodeSelected?.Invoke(hit.Id);
            return;
        }

        _dragOrigin = position;
        _dragStartPan = _pan;
        NodeSelected?.Invoke(null);
        SelectedNodeId = null;
    }

    protected override void OnPointerMoved(PointerEventArgs eventArgs)
    {
        base.OnPointerMoved(eventArgs);

        if (_dragOrigin is null)
        {
            return;
        }

        Point position = eventArgs.GetPosition(this);
        _pan = new Point(
            _dragStartPan.X + (position.X - _dragOrigin.Value.X),
            _dragStartPan.Y + (position.Y - _dragOrigin.Value.Y));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs eventArgs)
    {
        base.OnPointerReleased(eventArgs);
        _dragOrigin = null;
    }

    private GraphNode? HitTest(Point position)
    {
        Point center = new(Bounds.Width / 2, Bounds.Height / 2);

        foreach (GraphNode node in _nodes)
        {
            Point nodePoint = new(
                center.X + (node.X * _scale) + _pan.X,
                center.Y + (node.Y * _scale) + _pan.Y);

            double radius = (2.5 + node.Strength * 6) * Math.Clamp(_scale, 0.4, 1.6) + 4;
            double deltaX = nodePoint.X - position.X;
            double deltaY = nodePoint.Y - position.Y;

            if ((deltaX * deltaX) + (deltaY * deltaY) <= radius * radius)
            {
                return node;
            }
        }

        return null;
    }

    private static Color ColorForLayer(MemoryLayer layer) => layer switch
    {
        // The Memory board's layers legend — the same mapping MemoryView uses for its
        // legend, so a dot never disagrees with the text beside it:
        // core #7C5CFF, episodic #22D3EE, semantic #34D399, procedural #F59E0B.
        MemoryLayer.Core => Palette.Iris,
        MemoryLayer.Episodic => Palette.Mic,
        MemoryLayer.Semantic => Palette.Success,
        _ => Palette.Warning
    };

    private static void DrawDashedLine(DrawingContext context, IPen pen, Point from, Point to)
    {
        double length = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
        if (length < 1)
        {
            return;
        }

        double segmentLength = 6;
        int segmentCount = (int)(length / (segmentLength * 2));

        for (int segment = 0; segment < segmentCount; segment++)
        {
            double startFraction = (segment * 2 * segmentLength) / length;
            double endFraction = Math.Min(1, ((segment * 2 + 1) * segmentLength) / length);

            Point start = new(from.X + (to.X - from.X) * startFraction, from.Y + (to.Y - from.Y) * startFraction);
            Point end = new(from.X + (to.X - from.X) * endFraction, from.Y + (to.Y - from.Y) * endFraction);
            context.DrawLine(pen, start, end);
        }
    }

    /// <summary>
    /// Deterministic layout: one ring per layer, ordered by a stable hash of the node id,
    /// then a few spring passes so connected memories sit near each other.
    /// </summary>
    private void LayoutNodes()
    {
        Dictionary<MemoryLayer, List<GraphNode>> byLayer = _nodes
            .GroupBy(node => node.Layer)
            .ToDictionary(group => group.Key, group => group.ToList());

        double baseRadius = 120;
        int ringIndex = 0;

        foreach (KeyValuePair<MemoryLayer, List<GraphNode>> layerGroup in byLayer.OrderBy(pair => pair.Key))
        {
            double radius = baseRadius * (1 + ringIndex * 0.9);
            List<GraphNode> ringNodes = layerGroup.Value;

            for (int index = 0; index < ringNodes.Count; index++)
            {
                double angle = (Math.PI * 2 * index / Math.Max(1, ringNodes.Count)) +
                               StableAngleOffset(ringNodes[index].Id);
                ringNodes[index].X = Math.Cos(angle) * radius;
                ringNodes[index].Y = Math.Sin(angle) * radius;
            }

            ringIndex++;
        }

        // Short spring relaxation over the links only: cheap even with many edges.
        for (int iteration = 0; iteration < 24; iteration++)
        {
            foreach (GraphEdge edge in _edges)
            {
                GraphNode from = _nodes[edge.FromIndex];
                GraphNode to = _nodes[edge.ToIndex];

                double deltaX = to.X - from.X;
                double deltaY = to.Y - from.Y;
                double distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
                if (distance < 0.001)
                {
                    continue;
                }

                double desiredDistance = 70;
                double force = (distance - desiredDistance) * 0.02 * edge.Strength;
                double moveX = deltaX / distance * force;
                double moveY = deltaY / distance * force;

                from.X += moveX;
                from.Y += moveY;
                to.X -= moveX;
                to.Y -= moveY;
            }
        }
    }

    private static double StableAngleOffset(string id)
    {
        // A small stable offset per node stops rings from looking mechanically perfect.
        int hash = 0;
        foreach (char character in id)
        {
            hash = (hash * 31) + character;
        }

        return (Math.Abs(hash) % 100) / 100.0 * 0.35;
    }

    private sealed class GraphNode
    {
        public string Id { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public MemoryLayer Layer { get; init; }
        public double Strength { get; init; }
        public bool IsRecent { get; init; }
        public double X { get; set; }
        public double Y { get; set; }
    }

    private sealed class GraphEdge
    {
        public int FromIndex { get; init; }
        public int ToIndex { get; init; }
        public SynapsePolarity Polarity { get; init; }
        public double Strength { get; init; }
    }
}
