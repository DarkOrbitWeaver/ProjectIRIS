using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CompanionOS.App.Interface.Controls;
using CompanionOS.App.Interface.Theme;
using CompanionOS.Core.Brain;
using CompanionOS.Core.Events;
using CompanionOS.Core.Modules;
using CompanionOS.Core.Plugins;
using System.IO;

namespace CompanionOS.App.Interface.Views;

/// <summary>
/// What I can do, transcribed from the Body board:
///
///   header   the title at 28 px, the real "N modules · M tools · all local" caption,
///            and the shield legend that explains what asks before it runs
///   cards    one per real module in a two-column grid: icon, name, status dot,
///            "Active"/"Off", a real 40 × 24 switch, the purpose line, and an
///            expandable tool grid
///   tools    each tool gets an eye (runs on reflex) or a shield (asks first), its
///            name in the board's uppercase mono caption, and its real description
///
/// The switch is real: it writes the module enabled state that the module host's
/// tool routing already obeys. A module that is always available keeps its switch
/// but explains on hover that it never turns off — no pretend toggle.
/// </summary>
public sealed class ModulesView : UserControl
{
    private readonly CompanionRuntime _runtime;
    // Two real columns with a 16 px gutter. The old fixed 532 px cards were sized as if
    // the 64 px rail did not exist, so in the shell they could never fit two abreast and
    // the whole page silently became a single long column.
    private readonly Grid _cardGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
    private readonly TextBlock _countLine;
    private readonly Button _moreOptions;
    private readonly ContentControl _pluginsHost = new();
    private readonly List<ModuleCardControl> _cards = new();
    private bool _toolsExpanded;

    public ModulesView(CompanionRuntime runtime)
    {
        _runtime = runtime;

        _moreOptions = new Button
        {
            Content = IconGlyph.MakeDots(20, Palette.TextSecondaryBrush),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2.4),
            MinWidth = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Top
        };
        ToolTip.SetTip(_moreOptions, "Expand or collapse every tool list");
        _moreOptions.Click += (_, _) =>
        {
            _toolsExpanded = !_toolsExpanded;
            foreach (ModuleCardControl card in _cards)
            {
                card.SetToolsExpanded(_toolsExpanded);
            }
        };

        _countLine = Ui.Text(string.Empty, Palette.FontCaption, Palette.TextSecondaryBrush,
            FontWeight.Medium, wrap: false);
        _countLine.LineHeight = 16;
        _countLine.LetterSpacing = 0.88;

        Content = BuildLayout();
        Refresh();
    }

    /// <summary>Rebuilds every card from the live registry. Called on entry and on changes.</summary>
    public void Refresh()
    {
        _cards.Clear();
        _cardGrid.Children.Clear();
        _cardGrid.RowDefinitions.Clear();

        IReadOnlyList<IAssistantModule> modules = _runtime.Modules.Modules;
        int toolCount = modules.Sum(module => module.Tools.Count);

        // The board's status line, with real numbers.
        _countLine.Text = $"{modules.Count} modules · {toolCount} tools · all local"
            .ToUpperInvariant();

        for (int index = 0; index < modules.Count; index++)
        {
            IAssistantModule module = modules[index];
            int row = index / 2;
            int column = index % 2 == 0 ? 0 : 2;
            while (_cardGrid.RowDefinitions.Count <= row)
            {
                _cardGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            ModuleCardControl? card = null;
            card = new ModuleCardControl(
                module,
                _runtime.Modules.IsEnabled(module.Id),
                _toolsExpanded,
                () =>
                {
                    if (card is null)
                    {
                        return;
                    }

                    bool current = _runtime.Modules.IsEnabled(module.Id);
                    _runtime.Modules.SetEnabled(module.Id, !current);
                    // The whole grid re-renders: counts, status and every card stay true.
                    Refresh();
                });
            _cards.Add(card);
            Grid.SetRow(card.Control, row);
            Grid.SetColumn(card.Control, column);
            card.Control.Margin = new Thickness(0, 0, 0, 16);
            _cardGrid.Children.Add(card.Control);
        }

        if (modules.Count == 0)
        {
            Control empty = Ui.Text("No modules are installed.",
                Palette.FontBody, Palette.TextSecondaryBrush);
            Grid.SetColumnSpan(empty, 3);
            _cardGrid.Children.Add(empty);
        }

        _pluginsHost.Content = BuildPluginsSection();
    }

    private Control BuildLayout()
    {
        TextBlock title = Ui.Text("What I can do", Palette.FontPage, Palette.TextPrimaryBrush,
            FontWeight.SemiBold, wrap: false);
        title.LineHeight = 34;

        StackPanel shieldNote = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        shieldNote.Children.Add(Ui.Text("Anything marked", Palette.FontCaption,
            Palette.TextSecondaryBrush, FontWeight.Medium, wrap: false));
        shieldNote.Children.Add(IconGlyph.Make(IconGlyph.Shield, 12, Palette.WarningBrush));
        shieldNote.Children.Add(Ui.Text("asks before it runs.", Palette.FontCaption,
            Palette.TextSecondaryBrush, FontWeight.Medium, wrap: false));

        StackPanel heading = new() { Spacing = 4 };
        heading.Children.Add(title);
        heading.Children.Add(_countLine);
        heading.Children.Add(shieldNote);

        Grid headerRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(heading, 0);
        Grid.SetColumn(_moreOptions, 1);
        headerRow.Children.Add(heading);
        headerRow.Children.Add(_moreOptions);

        // The board scroll: 32 top/bottom, 48 sides, 24 between header and grid.
        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(48, 32),
                Spacing = 24,
                Children = { headerRow, _cardGrid, _pluginsHost }
            }
        };
    }

    /// <summary>
    /// The plugin section: exactly what her folder loaded, and nothing else. A rejected
    /// manifest is named with its reason — the folder is the truth, and the screen must
    /// never dress up absence as presence.
    /// </summary>
    private Control BuildPluginsSection()
    {
        TextBlock caption = Ui.Text("Plugins · from her folder", Palette.FontCaption,
            Palette.TextSecondaryBrush, FontWeight.SemiBold, wrap: false);
        caption.LineHeight = 16;
        caption.LetterSpacing = 0.88;
        caption.Margin = new Thickness(0, 16, 0, 0);

        Grid cards = new() { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
        StackPanel section = new() { Spacing = 12, Children = { caption, cards } };
        bool anyChild = false;
        int pluginIndex = 0;

        foreach (PluginManifest plugin in _runtime.Plugins.Loaded)
        {
            anyChild = true;
            int row = pluginIndex / 2;
            int column = pluginIndex % 2 == 0 ? 0 : 2;
            pluginIndex++;
            while (cards.RowDefinitions.Count <= row)
            {
                cards.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            string transportWord = plugin.Transport.ToUpperInvariant();

            TextBlock name = Ui.Text($"{plugin.Name} · v{plugin.Version}", Palette.FontEmphasis,
                Palette.TextPrimaryBrush, FontWeight.SemiBold, wrap: false);
            name.LineHeight = 22;

            TextBlock transport = Ui.Text(transportWord, Palette.FontCaption,
                Palette.AccentSoftBrush, FontWeight.SemiBold, wrap: false);
            transport.LineHeight = 16;
            transport.LetterSpacing = 0.88;
            transport.VerticalAlignment = VerticalAlignment.Center;

            TextBlock toolsLine = Ui.Text($"{plugin.Tools.Count} tools", Palette.FontCaption,
                Palette.TextSecondaryBrush, FontWeight.Medium, wrap: false);
            toolsLine.LineHeight = 16;
            toolsLine.LetterSpacing = 0.88;
            toolsLine.VerticalAlignment = VerticalAlignment.Center;

            StackPanel headerRow = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { name, transport, toolsLine }
            };
            if (plugin.AsksPermission.Count > 0)
            {
                headerRow.Children.Add(IconGlyph.Make(IconGlyph.Shield, 12, Palette.WarningBrush));
            }
            string moduleId = $"plugin:{plugin.Id}";
            bool activeThisSession = _runtime.Modules.IsEnabled(moduleId);
            string pluginState = plugin.Disabled
                ? "DISABLED"
                : activeThisSession ? "ACTIVE" : "NEXT LAUNCH";
            TextBlock stateTag = Ui.Text(pluginState, Palette.FontCaption,
                activeThisSession ? Palette.SuccessBrush : Palette.TextSecondaryBrush,
                FontWeight.SemiBold, wrap: false);
            stateTag.LineHeight = 16;
            stateTag.LetterSpacing = 0.88;
            stateTag.VerticalAlignment = VerticalAlignment.Center;
            headerRow.Children.Add(stateTag);

            Button launchToggle = new()
            {
                Content = Ui.Text(plugin.Disabled ? "Enable at next launch"
                    : activeThisSession ? "Disable now and next launch" : "Cancel next launch enable",
                    Palette.FontCaption, Palette.AccentSoftBrush, FontWeight.Medium, wrap: false),
                Background = Brushes.Transparent,
                BorderBrush = Palette.StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 5),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            ToolTip.SetTip(launchToggle, plugin.Disabled
                ? "Load this external plugin after Iris restarts"
                : activeThisSession
                    ? "Stop routing to this plugin now and keep it disabled after restart"
                    : "Cancel the pending enable so this plugin remains off after restart");
            launchToggle.Click += (_, _) => SetPluginLaunchEnabled(plugin, plugin.Disabled);

            StackPanel body = new() { Spacing = 6 };
            if (!string.IsNullOrWhiteSpace(plugin.Description))
            {
                TextBlock description = Ui.Text(plugin.Description, Palette.FontBody,
                    Palette.TextSecondaryBrush);
                description.LineHeight = 20;
                body.Children.Add(description);
            }

            foreach (PluginTool tool in plugin.Tools)
            {
                StackPanel toolRow = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
                toolRow.Children.Add(Ui.Text(tool.Name, Palette.FontCaption, Palette.TextPrimaryBrush,
                    FontWeight.Medium, wrap: false));
                if (!string.IsNullOrWhiteSpace(tool.Permission))
                {
                    toolRow.Children.Add(IconGlyph.Make(IconGlyph.Shield, 11, Palette.WarningBrush));
                }
                if (tool.AlwaysConfirm)
                {
                    toolRow.Children.Add(Ui.Text("CONFIRM EACH TIME", Palette.FontCaption,
                        Palette.WarningBrush, FontWeight.Medium, wrap: false));
                }
                body.Children.Add(toolRow);
            }

            Border card = new()
            {
                Margin = new Thickness(0, 0, 0, 16),
                Background = Palette.ElevatedBrush,
                BorderBrush = Palette.StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Palette.RadiusPanel),
                Padding = new Thickness(20),
                Child = new StackPanel { Spacing = 10, Children = { headerRow, body, launchToggle } }
            };
            Grid.SetRow(card, row);
            Grid.SetColumn(card, column);
            cards.Children.Add(card);
        }

        foreach ((string folder, string reason) in _runtime.Plugins.Rejected)
        {
            anyChild = true;
            TextBlock rejected = Ui.Text(
                $"{Path.GetFileName(folder)} — rejected: {reason}", Palette.FontBody,
                Palette.WarningBrush);
            rejected.LineHeight = 20;
            section.Children.Add(rejected);
        }

        if (!anyChild)
        {
            TextBlock empty = Ui.Text(
                $"No plugins in her folder yet — drop one in {_runtime.Plugins.Root} " +
                "and she picks it up on the next start.", Palette.FontBody,
                Palette.TextSecondaryBrush);
            empty.LineHeight = 20;
            empty.TextWrapping = TextWrapping.Wrap;
            section.Children.Add(empty);
        }

        return section;
    }

    private void SetPluginLaunchEnabled(PluginManifest plugin, bool enable)
    {
        string moduleId = $"plugin:{plugin.Id}";
        bool activeThisSession = _runtime.Modules.IsEnabled(moduleId);
        if (_runtime.Plugins.SetLaunchEnabled(plugin.Id, enable, out string message))
        {
            // Persist both parts of the state. Disabling an active plugin removes it
            // from tool routing immediately; enabling a folder plugin remains a
            // next-start action because its process/MCP lifecycle is owned by startup.
            _runtime.Modules.SetEnabled(moduleId, enable);
            if (!enable && activeThisSession)
            {
                message = "Disabled now and at next launch.";
            }
            else if (enable)
            {
                message = "Will load at next launch.";
            }

            _runtime.Events.PublishNotice($"{plugin.Name}: {message}", NoticeSeverity.Success);
            _pluginsHost.Content = BuildPluginsSection();
        }
        else
        {
            _runtime.Events.PublishNotice($"{plugin.Name}: {message}", NoticeSeverity.Danger);
        }
    }
}

/// <summary>One module card: its switch, purpose and expandable tool grid, all real data.</summary>
internal sealed class ModuleCardControl
{
    private static readonly IReadOnlyDictionary<string, string> GlyphById =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["core"] = IconGlyph.Eye,        // Core Senses: memory, eyes, sleep, self
            ["music"] = IconGlyph.Play,      // Music: playback and transport
            ["system"] = IconGlyph.Settings, // System Control: timers, notes, launches
        };

    private readonly IAssistantModule _module;
    private readonly Action _onToggle;
    private readonly Border _dot;
    private readonly TextBlock _statusText;
    private readonly Button _switch;
    private readonly Border _switchKnob;
    private readonly Control _glyph;
    private readonly StackPanel _toggleRow;
    private readonly ContentControl _toolsIconHost;
    private readonly TextBlock _toolsLabel;
    private readonly Control _expandedHost;
    private readonly Grid _toolsGrid;

    private bool _isEnabled;
    private bool _toolsExpanded;

    /// <param name="onToggle">Invoked instead of touching module state directly, so the
    /// collection that owns this card can decide what a switch press means.</param>
    public ModuleCardControl(IAssistantModule module, bool isEnabled, bool toolsExpanded,
        Action onToggle)
    {
        _module = module;
        _isEnabled = isEnabled;
        _toolsExpanded = toolsExpanded;
        _onToggle = onToggle;

        _dot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };

        _statusText = Ui.Text(_isEnabled ? "Active" : "Off", Palette.FontBody,
            Palette.TextSecondaryBrush, FontWeight.Medium, wrap: false);
        _statusText.LineHeight = 18;
        _statusText.VerticalAlignment = VerticalAlignment.Center;

        (_switch, _switchKnob) = BuildSwitch();
        _switch.VerticalAlignment = VerticalAlignment.Center;

        _glyph = IconGlyph.Make(
            GlyphById.TryGetValue(module.Id, out string? geometry) ? geometry : IconGlyph.Monitor,
            24, Palette.TextPrimaryBrush);
        _glyph.VerticalAlignment = VerticalAlignment.Center;

        TextBlock title = Ui.Text(module.DisplayName, Palette.FontTitle, Palette.TextPrimaryBrush,
            FontWeight.SemiBold, wrap: false);
        title.LineHeight = 26;
        title.VerticalAlignment = VerticalAlignment.Center;

        // Identity + name on the left; dot, word and switch pinned to the right edge.
        StackPanel identity = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
        identity.Children.Add(_glyph);
        identity.Children.Add(title);

        Grid header = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"),
            Children = { identity, _dot, _statusText, _switch }
        };
        Grid.SetColumn(identity, 0);
        Grid.SetColumn(_dot, 2);
        Grid.SetColumn(_statusText, 3);
        Grid.SetColumn(_switch, 4);
        _statusText.Margin = new Thickness(8, 0, 12, 0);

        TextBlock purpose = Ui.Text(module.Purpose, Palette.FontEmphasis, Palette.TextSecondaryBrush);
        purpose.LineHeight = 22;

        // The board's "6 tools ▾" affordance: accent text, chevron direction = state.
        _toolsIconHost = new ContentControl { Content = MakeChevron(_toolsExpanded) };
        _toolsLabel = Ui.Text($"{module.Tools.Count} tools", Palette.FontBody,
            Palette.AccentSoftBrush, FontWeight.Medium, wrap: false);
        _toolsLabel.LineHeight = 18;
        _toggleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        _toggleRow.Children.Add(_toolsIconHost);
        _toggleRow.Children.Add(_toolsLabel);
        Button toolsToggle = new()
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        toolsToggle.Content = _toggleRow;
        toolsToggle.Click += (_, _) => SetToolsExpanded(!_toolsExpanded);

        // The expanded area: hairline, then the two-column tool rows. The middle
        // 24 px column is the gutter between tool cells, matching the cards grid.
        _toolsGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,*") };
        StackPanel expanded = new()
        {
            Spacing = 12,
            Children = { Divider(), _toolsGrid }
        };

        StackPanel shell = new() { Spacing = 12 };
        shell.Children.Add(header);
        shell.Children.Add(purpose);
        shell.Children.Add(toolsToggle);
        shell.Children.Add(expanded);

        Border card = new()
        {
            Margin = new Thickness(0, 0, 0, 16),
            Background = Palette.ElevatedBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Palette.RadiusPanel),
            Padding = new Thickness(20),
            Child = shell
        };

        Control = card;
        _expandedHost = expanded;
        // State must be APPLIED at construction, not assumed: the panel defaults to
        // visible, so a "collapsed" chevron shipped with an expanded grid and every
        // first tap was swallowed re-syncing reality to the label - the "press twice"
        // the user felt. One call makes the initial state true.
        SetToolsExpanded(_toolsExpanded);

        Render();
        BuildToolRows();
    }

    public Control Control { get; }

    private static Control Divider() => new Border
    {
        Height = 1,
        Background = Palette.StrokeBrush
    };

    private static Avalonia.Controls.Shapes.Path MakeChevron(bool expanded) =>
        (Avalonia.Controls.Shapes.Path)IconGlyph.Make(
            expanded ? IconGlyph.ChevronDown : IconGlyph.ChevronRight, 12, Palette.AccentSoftBrush);

    private (Button button, Border knob) BuildSwitch()
    {
        Border knob = new()
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9)
        };

        Panel track = new();
        track.Children.Add(knob);

        Button button = new()
        {
            Width = 40,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Content = track
        };
        button.Click += (_, _) => _onToggle();
        return (button, knob);
    }

    /// <summary>Repaints dot, word, track, knob and glyph from the current state.</summary>
    private void Render()
    {
        // Board: on = #7C5CFF track, white knob on the right; off = #262A36 track,
        // grey knob on the left.
        _switch.Background = _isEnabled ? Palette.IrisBrush : Palette.StrokeBrush;
        _switchKnob.Background = _isEnabled ? Brushes.White : Palette.TextSecondaryBrush;
        _switchKnob.HorizontalAlignment = _isEnabled ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;
        _switchKnob.Margin = new Thickness(3);

        _switch.IsEnabled = true;
        // Fluent's disabled style paints the track grey and would hide the on state,
        // so the switch stays clickable and the tooltip carries the caveat instead.
        ToolTip.SetTip(_switch, _module.AlwaysAvailable
            ? "Her senses never sleep while this is on — switching it off still stands down its tools"
            : "Switch this module on or off");

        _dot.Background = _isEnabled ? Palette.SuccessBrush : Palette.TextFaintBrush;
        _statusText.Text = _isEnabled ? "Active" : "Off";
        if (_glyph is Avalonia.Controls.Shapes.Path glyphPath)
        {
            glyphPath.Stroke = _isEnabled ? Palette.TextPrimaryBrush : Palette.TextSecondaryBrush;
        }
    }

    private void BuildToolRows()
    {
        _toolsGrid.Children.Clear();
        _toolsGrid.RowDefinitions.Clear();

        IReadOnlyList<ModuleTool> tools = _module.Tools;
        for (int index = 0; index < tools.Count; index++)
        {
            ModuleTool tool = tools[index];
            bool guarded = tool.Tier is ToolTier.Scoped or ToolTier.SystemCritical;

            // The name is the board's uppercase mono caption. It is trimmed to its
            // column because a NoWrap mono name drives its own width and pushes the
            // second column's text around — exactly the spill this row structure
            // used to have.
            TextBlock name = new()
            {
                Text = tool.Name.ToUpperInvariant(),
                FontFamily = Palette.MonoFont,
                FontSize = Palette.FontCaption,
                LineHeight = 16,
                FontWeight = FontWeight.Medium,
                Foreground = Palette.TextPrimaryBrush,
                LetterSpacing = 0.88,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            TextBlock description = Ui.Text(tool.Description, Palette.FontBody,
                Palette.TextSecondaryBrush);
            description.LineHeight = 18;

            // Each cell is its own grid so the text column gets a hard width and the
            // description wraps inside it instead of painting over the next cell.
            Grid cellText = new() { RowDefinitions = new RowDefinitions("Auto,Auto") };
            Grid.SetRow(name, 0);
            Grid.SetRow(description, 1);
            cellText.Children.Add(name);
            cellText.Children.Add(description);

            Grid cell = new()
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Margin = new Thickness(0, 0, 0, 12)
            };
            Control glyph = IconGlyph.Make(guarded ? IconGlyph.Shield : IconGlyph.Eye, 12,
                guarded ? Palette.WarningBrush : Palette.TextSecondaryBrush);
            glyph.VerticalAlignment = VerticalAlignment.Top;
            glyph.Margin = new Thickness(0, 2, 8, 0);
            Grid.SetColumn(glyph, 0);
            Grid.SetColumn(cellText, 1);
            cell.Children.Add(glyph);
            cell.Children.Add(cellText);

            int row = index / 2;
            while (_toolsGrid.RowDefinitions.Count <= row)
            {
                _toolsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, index % 2 == 0 ? 0 : 2);
            _toolsGrid.Children.Add(cell);
        }
    }

    /// <summary>Opens or closes the tool grid, and turns the chevron to match.</summary>
    public void SetToolsExpanded(bool expanded)
    {
        _toolsExpanded = expanded;
        _expandedHost.IsVisible = expanded;
        _toolsIconHost.Content = MakeChevron(expanded);
    }
}
