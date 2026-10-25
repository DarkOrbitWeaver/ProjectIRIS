using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CompanionOS.App.Interface;
using CompanionOS.App.Interface.Controls;
using CompanionOS.App.Interface.Theme;
using CompanionOS.Core.Brain;
using CompanionOS.Core.Events;
using CompanionOS.Core.Memory;

namespace CompanionOS.App.Interface.Views;

/// <summary>
/// Her memory, transcribed 1:1 from the Memory board:
///
///   graph     the whole picture, pannable and zoomable by wheel and drag
///   search    the board's 480 × 44 pill at its exact spot, filtering as you type
///   legend    "core N · episodic N · semantic N · procedural N" with the board's
///             colours and real counts, sitting where the board puts it
///   rail      a 360 px detail column on the right: layer dot, 20 px title, 15/22 body,
///             "created X · last touched Y", then real Pin and Forget actions
///
/// The board has no toolbar, no footer and no strength buttons — none exist here either.
/// </summary>
public sealed class MemoryView : UserControl
{
    private readonly CompanionRuntime _runtime;
    private readonly MemoryGraphCanvas _canvas = new();
    private readonly TextBox _search = new();
    private readonly StackPanel _legend = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 16
    };
    private readonly ScrollViewer _railScroller;
    private readonly StackPanel _rail = new() { Spacing = 16 };
    private string? _selectedNodeId;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    public MemoryView(CompanionRuntime runtime)
    {
        _runtime = runtime;

        _search.TextChanged += (_, _) => ApplySearch();
        _canvas.NodeSelected += OnNodeSelected;

        _railScroller = new ScrollViewer { Content = _rail };
        ShowEmptyRail();

        Content = BuildLayout();
        RefreshGraph();
    }

    /// <summary>Redraws if the screen is on display and enough time passed to matter.</summary>
    public void RefreshIfVisible()
    {
        if (!IsEffectivelyVisible ||
            DateTimeOffset.Now - _lastRefresh < TimeSpan.FromSeconds(3))
        {
            return;
        }

        long refreshStarted = Stopwatch.GetTimestamp();
        RefreshGraph();
        double costMs = (Stopwatch.GetTimestamp() - refreshStarted) * 1_000.0 / Stopwatch.Frequency;
        if (costMs > 5.0)
        {
            SystemWatch.Trace("ui", "ui.expensive", $"memory.refresh cost={costMs:0.#}ms visible={IsEffectivelyVisible}");
        }
    }

    private void RefreshGraph()
    {
        _lastRefresh = DateTimeOffset.Now;

        List<MemoryNode> nodes = _runtime.Graph.SnapshotNodes().ToList();
        IReadOnlyList<MemorySynapse> synapses = _runtime.Graph.SnapshotSynapses();

        _canvas.SetGraph(nodes, synapses);
        _canvas.SelectedNodeId = _selectedNodeId;
        RenderLegend(nodes);

        if (_selectedNodeId is null)
        {
            ShowEmptyRail();
        }
        else
        {
            ShowDetail(_selectedNodeId);
        }
    }

    private void ApplySearch()
    {
        string text = _search.Text ?? string.Empty;

        IReadOnlyList<MemoryNode> nodes = string.IsNullOrWhiteSpace(text)
            ? _runtime.Graph.SnapshotNodes()
            : _runtime.Graph.SearchNodes(text, 60).Select(pair => pair.Node).ToList();

        _canvas.SetGraph(nodes, _runtime.Graph.SnapshotSynapses());
        RenderLegend(nodes);
        if (_selectedNodeId is not null && nodes.All(node => node.Id != _selectedNodeId))
        {
            _selectedNodeId = null;
            _canvas.SelectedNodeId = null;
        }

        if (_selectedNodeId is not null)
        {
            _canvas.SelectedNodeId = _selectedNodeId;
        }
        else
        {
            ShowEmptyRail();
        }
    }

    private Control BuildLayout()
    {
        // The overlays sit exactly where the board draws them: search pill at 137/16,
        // legend at 24/76.
        Border searchPill = BuildSearchPill();
        searchPill.HorizontalAlignment = HorizontalAlignment.Left;
        searchPill.VerticalAlignment = VerticalAlignment.Top;
        searchPill.Margin = new Thickness(137, 16, 0, 0);

        _legend.HorizontalAlignment = HorizontalAlignment.Left;
        _legend.VerticalAlignment = VerticalAlignment.Top;
        _legend.Margin = new Thickness(24, 76, 0, 0);

        Panel graphSide = new();
        graphSide.Children.Add(_canvas);
        graphSide.Children.Add(searchPill);
        graphSide.Children.Add(_legend);

        Border rail = new()
        {
            Background = Palette.ElevatedBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(24),
            Child = _railScroller
        };

        Grid layout = new() { ColumnDefinitions = new ColumnDefinitions("*,360") };
        Grid.SetColumn(graphSide, 0);
        Grid.SetColumn(rail, 1);
        layout.Children.Add(graphSide);
        layout.Children.Add(rail);
        return layout;
    }

    private Border BuildSearchPill()
    {
        _search.Watermark = "Search what she remembers";
        _search.Background = Brushes.Transparent;
        _search.BorderThickness = new Thickness(0);
        _search.FontFamily = Palette.UiFont;
        _search.FontSize = Palette.FontEmphasis;
        _search.LineHeight = 22;
        _search.Foreground = Palette.TextPrimaryBrush;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.MinHeight = 0;
        _search.Padding = new Thickness(0);

        return new Border
        {
            Width = 480,
            Height = 44,
            Background = Palette.SurfaceBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Palette.RadiusCard),
            Padding = new Thickness(14, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    IconGlyph.Make(IconGlyph.Search, 20, Palette.TextSecondaryBrush),
                    _search
                }
            }
        };
    }

    /// <summary>The board's legend, with real counts per layer.</summary>
    private void RenderLegend(IReadOnlyList<MemoryNode> nodes)
    {
        _legend.Children.Clear();
        foreach (MemoryLayer layer in new[]
                 {
                     MemoryLayer.Core, MemoryLayer.Episodic,
                     MemoryLayer.Semantic, MemoryLayer.Procedural
                 })
        {
            int count = nodes.Count(node => node.Layer == layer);
            _legend.Children.Add(LegendEntry(LayerLabel(layer), LayerColor(layer), count));
        }
    }

    private static Control LegendEntry(string label, Color color, int count)
    {
        Border dot = new()
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(color)
        };
        TextBlock text = Ui.Text($"{label} {count}", Palette.FontBody, Palette.TextSecondaryBrush,
            FontWeight.Medium, wrap: false);
        text.LineHeight = 18;
        text.VerticalAlignment = VerticalAlignment.Center;

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { dot, text }
        };
    }

    private void OnNodeSelected(string? nodeId)
    {
        _selectedNodeId = nodeId;
        if (nodeId is null)
        {
            ShowEmptyRail();
            return;
        }

        ShowDetail(nodeId);
    }

    /// <summary>The detail rail as the board builds it, fed only with real memory data.</summary>
    private void ShowDetail(string nodeId)
    {
        MemoryNode? node = _runtime.Graph.FindById(nodeId);
        if (node is null)
        {
            return;
        }

        Color layerColor = LayerColor(node.Layer);

        // Header: layer dot, uppercase layer name, and the close glyph at the right.
        Border dot = new()
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(layerColor)
        };
        TextBlock layerLabel = Ui.Text(LayerLabel(node.Layer).ToUpperInvariant(),
            Palette.FontCaption, Palette.TextSecondaryBrush, FontWeight.Medium, wrap: false);
        layerLabel.LineHeight = 16;
        layerLabel.LetterSpacing = 0.88;
        layerLabel.VerticalAlignment = VerticalAlignment.Center;

        Button close = new()
        {
            Content = IconGlyph.Make(IconGlyph.Close, 20, Palette.TextSecondaryBrush),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(close, "Close detail");
        close.Click += (_, _) =>
        {
            _selectedNodeId = null;
            _canvas.SelectedNodeId = null;
            ShowEmptyRail();
        };

        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        dot.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(dot, 0);
        Grid.SetColumn(layerLabel, 1);
        layerLabel.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(close, 3);
        header.Children.Add(dot);
        header.Children.Add(layerLabel);
        header.Children.Add(close);

        TextBlock title = Ui.Text(node.Title, Palette.FontTitle, Palette.TextPrimaryBrush,
            FontWeight.SemiBold);
        title.LineHeight = 26;

        _rail.Children.Clear();
        _rail.Children.Add(header);

        if (!string.IsNullOrWhiteSpace(node.Content))
        {
            TextBlock body = Ui.Text(node.Content, Palette.FontEmphasis, Palette.TextPrimaryBrush);
            body.LineHeight = 22;
            _rail.Children.Add(body);
        }

        // The spacer the board puts between the body and the timeline line.
        _rail.Children.Add(new Panel { MinHeight = 16 });

        TextBlock timeline = Ui.Text(
            $"created {Relative(node.CreatedAt)} · last touched {Relative(node.LastUsedAt ?? node.UpdatedAt)}",
            Palette.FontCaption, Palette.TextFaintBrush, wrap: true);
        timeline.LineHeight = 16;
        _rail.Children.Add(timeline);

        // A memory without provenance is an assertion Iris cannot justify. Keep the
        // explanation compact, but always show where it came from, how strong it is,
        // and whether it was actually recalled before the user decides to trust it.
        TextBlock evidence = Ui.Text(
            $"source: {(string.IsNullOrWhiteSpace(node.Provenance) ? "unknown" : node.Provenance)} " +
            $"· confidence {node.Strength:P0} · recalled {node.UseCount} time(s)",
            Palette.FontCaption, Palette.TextSecondaryBrush, wrap: true);
        evidence.LineHeight = 16;
        evidence.Margin = new Thickness(0, 6, 0, 0);
        _rail.Children.Add(evidence);

        _rail.Children.Add(new Border
        {
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 12, 0, 0),
            Child = BuildActions(node)
        });
    }

    private Control BuildActions(MemoryNode node)
    {
        Button pin = BuildRailButton(IconGlyph.Pin, node.Pinned ? "Unpin" : "Pin",
            node.Pinned ? Palette.IrisBrush : Palette.TextSecondaryBrush,
            node.Pinned ? "Let her fade this when it goes unused" : "Pin her core — it never fades and is never merged away");
        pin.Click += (_, _) => _ = TogglePinAsync(node);

        Button forget = BuildRailButton(IconGlyph.Trash, "Forget", Palette.DangerBrush,
            "Remove this memory for good");
        forget.Click += (_, _) => _ = ForgetNodeAsync(node);

        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(pin);
        actions.Children.Add(forget);
        return actions;
    }

    private static Button BuildRailButton(string glyph, string label, IBrush brush, string tooltip)
    {
        StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(IconGlyph.Make(glyph, 16, brush));
        TextBlock text = Ui.Text(label, Palette.FontBody, brush, FontWeight.Medium, wrap: false);
        text.LineHeight = 18;
        content.Children.Add(text);

        Button button = new()
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 0),
            MinWidth = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    /// <summary>An empty rail that still tells the truth about why it is empty.</summary>
    private void ShowEmptyRail()
    {
        TextBlock nothing = Ui.Text("NOTHING SELECTED", Palette.FontCaption,
            Palette.TextSecondaryBrush, FontWeight.Medium, wrap: false);
        nothing.LineHeight = 16;
        nothing.LetterSpacing = 0.88;

        bool hasSearch = !string.IsNullOrWhiteSpace(_search.Text);
        TextBlock hint = _runtime.Graph.SnapshotNodes().Count == 0
            ? Ui.Text("Her memory is empty. Tell her something worth keeping.",
                Palette.FontEmphasis, Palette.TextSecondaryBrush)
            : hasSearch
                ? Ui.Text("Nothing matches that search.", Palette.FontEmphasis, Palette.TextSecondaryBrush)
            : Ui.Text("Click a node to read what she remembers.",
                Palette.FontEmphasis, Palette.TextSecondaryBrush);
        hint.LineHeight = 22;

        _rail.Children.Clear();
        _rail.Children.Add(nothing);
        _rail.Children.Add(hint);
    }

    private async Task TogglePinAsync(MemoryNode node)
    {
        bool pinned = !node.Pinned;
        await _runtime.Graph.SetPinnedAsync(node.Id, pinned);
        _runtime.Events.PublishNotice(pinned
            ? $"Pinned \"{node.Title}\" — it will not fade."
            : $"Unpinned \"{node.Title}\".", NoticeSeverity.Success);
        ShowDetail(node.Id);
    }

    private async Task ForgetNodeAsync(MemoryNode node)
    {
        await _runtime.Graph.ForgetNodeAsync(node.Id);
        _runtime.Events.PublishNotice($"Forgot \"{node.Title}\".", NoticeSeverity.Success);
        _selectedNodeId = null;
        _canvas.SelectedNodeId = null;
        RefreshGraph();
    }

    private static string LayerLabel(MemoryLayer layer) => layer switch
    {
        MemoryLayer.Core => "core",
        MemoryLayer.Episodic => "episodic",
        MemoryLayer.Semantic => "semantic",
        _ => "procedural"
    };

    private static Color LayerColor(MemoryLayer layer) => layer switch
    {
        // Exactly the layers legend from the Memory board:
        // core #7C5CFF, episodic #22D3EE, semantic #34D399, procedural #F59E0B.
        MemoryLayer.Core => Palette.Iris,
        MemoryLayer.Episodic => Palette.Mic,
        MemoryLayer.Semantic => Palette.Success,
        _ => Palette.Warning
    };

    /// <summary>Human time, the board's way: "3 days ago", "today".</summary>
    private static string Relative(DateTimeOffset timestamp)
    {
        TimeSpan span = DateTimeOffset.Now - timestamp;

        if (span < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)span.TotalMinutes)} minutes ago";
        }

        DateTimeOffset local = timestamp.ToLocalTime();
        DateTimeOffset today = DateTimeOffset.Now.Date;
        if (local.Date == today)
        {
            return "today";
        }

        if (local.Date == today.AddDays(-1))
        {
            return "yesterday";
        }

        if (span < TimeSpan.FromDays(60))
        {
            return $"{Math.Max(2, (int)Math.Round(span.TotalDays))} days ago";
        }

        return local.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }
}
