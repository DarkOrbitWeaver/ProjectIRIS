using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CompanionOS.App.Interface.Controls;
using CompanionOS.App.Interface.Theme;
using CompanionOS.Core.Events;
using CompanionOS.Core.Mind;

namespace CompanionOS.App.Interface.Views;

/// <summary>
/// The conversation, transcribed from the Chat board:
///
///   transcript   bottom-anchored, 24 px padding, 16 px gaps; her turns carry a 24 px
///                avatar dot and a #1A1C24 bubble with the 4/16/16/16 radius; yours are
///                accent-tinted and right-aligned; kernel facts render as small eye-lines
///   composer     56 px pill: 36 px mic ring (2 px cyan), "Say something", 36 px send
///                disc, and the 2 px cyan voice meter across its bottom
///   right rail    280 px card stack: music with real transport, her last sense, meters
///
/// Every control here is backed by real state: the masks and meters move only because
/// events moved them.
/// </summary>
public sealed class ChatView : UserControl
{
    private readonly ShellModel _model;
    private readonly ItemsControl _messages = new();
    private readonly ScrollViewer _scrollViewer = new();
    private readonly TextBox _composer;
    private readonly Border _voiceMeter;
    private readonly Panel _railHost = new();
    private MusicRailPanel? _musicPanel;
    private readonly SensesRailRow _sensesRow;
    private readonly ResourcesRailCard _resourcesCard;
    private readonly DispatcherTimer _railTimer;
    private readonly DispatcherTimer _voiceTimer;
    private readonly StatusOrb _emptyOrb = new() { Width = 96, Height = 96 };
    private readonly Control _emptyState;
    private Button _micButton = null!;
    private Button _sendButton = null!;
    private MeterKind _meterKind = MeterKind.Idle;

    /// <summary>Which voice the composer's 2 px underline is currently reporting.</summary>
    private enum MeterKind
    {
        Idle,
        Listening,
        Speaking
    }

    public ChatView(ShellModel model)
    {
        _model = model;
        _composer = new TextBox
        {
            Watermark = "Say something",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Palette.UiFont,
            FontSize = Palette.FontEmphasis,
            FontWeight = FontWeight.Normal,
            LineHeight = 22,
            Foreground = Palette.TextPrimaryBrush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _voiceMeter = new Border
        {
            Height = 2,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Palette.Mic, 0.8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(16, 0)
        };

        // Her rail cards; the music panel binds to the real module surface as soon as the
        // registry publishes it.
        _sensesRow = new SensesRailRow(model.Runtime);
        _resourcesCard = new ResourcesRailCard(model.Runtime);
        _musicPanel = new MusicRailPanel(null, InvokeMusicTool);
        _railHost.Children.Add(BuildRailCard(_musicPanel, _sensesRow, _resourcesCard));
        _emptyState = BuildEmptyState();

        _messages.ItemsSource = _model.Messages;
        // Recycling stays off: each bubble subscribes to its own entry's live streaming
        // text, so a recycled template would speak another entry's words mid-flight.
        _messages.ItemTemplate = new FuncDataTemplate<ChatEntry>(
            (entry, _) => BuildBubble(entry), supportsRecycling: false);

        _model.Messages.CollectionChanged += OnMessagesChanged;
        _composer.KeyDown += OnComposerKeyDown;
        model.Runtime.Events.Subscribe<ModuleRegistryChangedEvent>(_ => Dispatcher.UIThread.Post(RebindMusic));

        Content = BuildLayout();
        RebindMusic();
        RefreshEmptyState();

        // The composer's ring and underline follow real state, not just the last click.
        _model.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName is nameof(ShellModel.IsListening)
                or nameof(ShellModel.IsSpeaking)
                or nameof(ShellModel.IsTurnInProgress)
                or nameof(ShellModel.State))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateVoiceChrome();
                    UpdateTimerCadence();
                });
            }
        };

        // First paint must never be a blank row: read what she is looking at right now,
        // then keep it fresh.
        _sensesRow.Refresh();
        _resourcesCard.Refresh();

        // Progress bars, senses and RAM meters are cheap to refresh from live state.
        _railTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) =>
            {
                _sensesRow.Refresh();
                _resourcesCard.Refresh();
                UpdateVoiceChrome();
            });
        _railTimer.Start();

        // The underline is a live meter, not a decoration: a small timer keeps its
        // opacity honest without making every audio event redraw the whole transcript.
        _voiceTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Background,
            (_, _) => UpdateVoiceMeter());
        _voiceTimer.Start();

        // The timers stop on detach (screen switch) and must come back on reattach —
        // a chat that was left and returned still keeps her senses, meters and underline
        // live, and the transcript stays anchored at its newest line.
        DetachedFromVisualTree += (_, _) =>
        {
            _railTimer.Stop();
            _voiceTimer.Stop();
        };
        AttachedToVisualTree += (_, _) =>
        {
            _railTimer.Start();
            _voiceTimer.Start();
            UpdateTimerCadence();
            Dispatcher.UIThread.Post(() => _scrollViewer.ScrollToEnd(), DispatcherPriority.Background);
        };

        UpdateVoiceChrome();
        UpdateVoiceMeter();
        UpdateTimerCadence();
    }

    /// <summary>When a game is in front, this hidden-or-background conversation surface
    /// has no reason to repaint meters at 15 fps. Keep the information truthful while
    /// reducing its UI wake-ups to a quiet status cadence.</summary>
    private void UpdateTimerCadence()
    {
        bool gaming = _model.State == MindState.Gaming;
        _railTimer.Interval = gaming ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(500);
        _voiceTimer.Interval = gaming ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromMilliseconds(66);
    }

    /// <summary>Runs one of her real music tools, so the transport is folded into her.</summary>
    private void InvokeMusicTool(string tool)
    {
        _ = _model.Runtime.Modules.ExecuteAsync(tool, System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone(),
            Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
    }

    private void RebindMusic()
    {
        try
        {
            foreach (Core.Modules.ModuleSurface surface in _model.Runtime.Modules.GetSurfaces())
            {
                if (surface.Model is Core.Modules.BuiltIn.NowPlayingModel nowPlaying)
                {
                    if (!ReferenceEquals(_musicPanel?.NowPlaying, nowPlaying))
                    {
                        _musicPanel = new MusicRailPanel(nowPlaying, InvokeMusicTool);
                        _railHost.Children[0] = BuildRailCard(_musicPanel, _sensesRow, _resourcesCard);
                    }

                    return;
                }
            }
        }
        catch
        {
            // The registry is not open yet (called before modules register); the card
            // stays in its resting state and rebinds on the registry-changed event.
        }
    }

    private static Border BuildRailCard(Control music, Control senses, Control resources)
    {
        StackPanel stack = new() { Spacing = 12 };
        stack.Children.Add(music);
        stack.Children.Add(senses);
        stack.Children.Add(resources);

        return new Border
        {
            Background = Palette.ElevatedBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16),
            Child = stack
        };
    }

    private Control BuildLayout()
    {
        // Transcript column.
        Grid transcript = new() { RowDefinitions = new RowDefinitions("*,Auto") };

        // Bottom anchor: an empty star row above the messages pushes them to the board's
        // flex-end position; it collapses once the transcript outgrows the window.
        // This is the field the auto-scroll uses — the old local ScrollViewer meant every
        // ScrollToEnd() ran on a control that was never on screen, so new replies could
        // land below the fold.
        _scrollViewer.Margin = new Thickness(24, 24, 24, 0);
        _scrollViewer.Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Children =
            {
                StarSpacer(),
                AlignBottom(_messages)
            }
        };
        _scrollViewer.HorizontalScrollBarVisibility =
            Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;

        // The board's empty state: one 96 px orb and one line, nothing else. It floats
        // above the (empty) transcript and disappears with the first message.
        Grid feed = new();
        feed.Children.Add(_scrollViewer);
        feed.Children.Add(_emptyState);
        Grid.SetRow(feed, 0);
        transcript.Children.Add(feed);

        Border composer = BuildComposer();
        Grid.SetRow(composer, 1);
        transcript.Children.Add(composer);

        // Right rail column: the 280 px board card.
        Border rail = new()
        {
            Width = 280,
            Margin = new Thickness(0, 16, 16, 16),
            VerticalAlignment = VerticalAlignment.Top,
            Child = _railHost
        };
        _railHost.Children[_railHost.Children.Count - 1].VerticalAlignment = VerticalAlignment.Top;

        Grid layout = new() { ColumnDefinitions = new ColumnDefinitions("*,280") };
        Grid.SetColumn(transcript, 0);
        Grid.SetColumn(rail, 1);
        layout.Children.Add(transcript);
        layout.Children.Add(rail);

        return layout;
    }

    private static Control StarSpacer() => new Border();

    private static Control AlignBottom(Control content)
    {
        content.VerticalAlignment = VerticalAlignment.Bottom;
        return content;
    }

    private Border BuildComposer()
    {
        _micButton = new Button
        {
            Content = IconGlyph.Make(IconGlyph.Microphone, 18, Palette.MicBrush),
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            BorderBrush = Palette.MicBrush,
            BorderThickness = new Thickness(2),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(_micButton, "Open or close her ears (Ctrl+M)");
        _micButton.Click += (_, _) =>
        {
            _model.ToggleMicrophone();
            UpdateVoiceChrome();
        };

        _sendButton = new Button
        {
            Content = IconGlyph.Make(IconGlyph.Send, 18, Brushes.White),
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(0),
            Background = Palette.IrisBrush,
            Padding = new Thickness(0)
        };
        ToolTip.SetTip(_sendButton, "Send");
        _sendButton.Click += (_, _) =>
        {
            if (_model.IsTurnInProgress)
            {
                _model.Interrupt();
            }
            else
            {
                SendCurrentText();
            }
        };

        Panel content = new();
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(_micButton, 0);
        _micButton.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(_composer, 1);
        Grid.SetColumn(_sendButton, 2);
        _sendButton.Margin = new Thickness(12, 0, 0, 0);
        row.Children.Add(_micButton);
        row.Children.Add(_composer);
        row.Children.Add(_sendButton);
        content.Children.Add(row);
        content.Children.Add(_voiceMeter);

        return new Border
        {
            Background = Palette.SurfaceBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Height = 56,
            Margin = new Thickness(24, 0, 24, 24),
            Padding = new Thickness(10, 0),
            Child = content
        };
    }

    /// <summary>The board's welcome state: one orb and one line. Never a wall of tips.</summary>
    private Control BuildEmptyState()
    {
        TextBlock line = Ui.Text("Say something, or press Ctrl+M to talk.",
            Palette.FontEmphasis, Palette.TextSecondaryBrush, wrap: false);
        line.TextAlignment = TextAlignment.Center;

        return new StackPanel
        {
            Spacing = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 24),
            Children = { _emptyOrb, line }
        };
    }

    /// <summary>The mic ring is cyan while her ears are open, a quiet slate while closed.</summary>
    private void UpdateVoiceChrome()
    {
        if (_micButton.Content is not Avalonia.Controls.Shapes.Path icon)
        {
            return;
        }

        bool open = _model.Runtime.Voice.IsListening;
        _micButton.BorderBrush = open ? Palette.MicBrush : Palette.StrokeStrongBrush;
        icon.Stroke = open ? Palette.MicBrush : Palette.TextSecondaryBrush;
        ToolTip.SetTip(_micButton, open
            ? "Close her ears (Ctrl+M)"
            : "Open her ears (Ctrl+M)");

        _sendButton.Content = IconGlyph.Make(
            _model.IsTurnInProgress ? IconGlyph.Stop : IconGlyph.Send,
            18,
            Brushes.White);
        ToolTip.SetTip(_sendButton, _model.IsTurnInProgress ? "Stop" : "Send");
    }

    /// <summary>
    /// The composer's 2 px underline: cyan for your voice, warm violet for hers, faint
    /// when the room is quiet. Its opacity follows the real level.
    /// </summary>
    private void UpdateVoiceMeter()
    {
        bool speaking = _model.IsSpeaking;
        bool listening = !speaking && (_model.Runtime.Voice.IsListening || _model.MicrophoneLevel > 0.02);
        MeterKind kind = speaking ? MeterKind.Speaking
            : listening ? MeterKind.Listening
            : MeterKind.Idle;

        if (kind != _meterKind)
        {
            _meterKind = kind;
            _voiceMeter.Background = kind == MeterKind.Speaking
                ? Palette.SpeechBrush
                : Palette.MicBrush;
        }

        double level = Math.Clamp(speaking ? _model.SpeechLevel : _model.MicrophoneLevel, 0, 1);
        _voiceMeter.Opacity = kind == MeterKind.Idle ? 0.18 : 0.45 + (level * 0.55);
    }

    /// <summary>Honest empty state: shown only when there is truly nothing to read.</summary>
    private void RefreshEmptyState()
    {
        _emptyOrb.State = _model.State;
        _emptyState.IsVisible = _model.Messages.Count == 0;
    }

    private Control BuildBubble(ChatEntry entry)
    {
        switch (entry.Role)
        {
            case ChatRole.User:
                return StackOf(BuildUserBubble(entry), BuildKernelLine(entry));

            case ChatRole.Assistant:
                return BuildHerRow(entry);

            case ChatRole.Tool:
                return BuildToolCapsule(entry);

            default:
                return new TextBlock
                {
                    Text = entry.Text,
                    FontFamily = Palette.UiFont,
                    FontSize = Palette.FontCaption,
                    LineHeight = 16,
                    Foreground = Palette.TextFaintBrush,
                    Margin = new Thickness(36, 0, 48, 0),
                    TextWrapping = TextWrapping.Wrap
                };
        }
    }

    private static StackPanel StackOf(params Control?[] cells)
    {
        StackPanel stack = new();
        foreach (Control? cell in cells)
        {
            if (cell is not null)
            {
                stack.Children.Add(cell);
            }
        }

        return stack;
    }

    private Control BuildHerRow(ChatEntry entry)
    {
        StackPanel stack = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
        stack.Children.Add(AvatarDot());

        StackPanel bubbleContent = new();
        TextBlock text = Ui.Text(
            entry.IsStreaming && string.IsNullOrEmpty(entry.Text) ? "…" : entry.Text,
            Palette.FontEmphasis,
            Palette.TextPrimaryBrush);
        text.LineHeight = 22;
        bubbleContent.Children.Add(text);
        entry.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            text.Text = entry.IsStreaming ? entry.Text + " ▍" : entry.Text;
            if (!entry.IsStreaming && string.IsNullOrEmpty(entry.Text))
            {
                text.Text = "…";
            }
        });

        if (entry.RecalledTitles.Count > 0 && _model.Runtime.Settings.Current.Interface.ShowRecallChips)
        {
            WrapPanel chips = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            foreach (string title in entry.RecalledTitles.Take(4))
            {
                chips.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Palette.Iris, 0.14),
                    CornerRadius = new CornerRadius(Palette.RadiusPill),
                    Padding = new Thickness(10, 3),
                    Margin = new Thickness(0, 0, 6, 0),
                    Child = new TextBlock
                    {
                        Text = title,
                        FontSize = Palette.FontCaption,
                        FontWeight = FontWeight.Medium,
                        Foreground = Palette.AccentSoftBrush
                    }
                });
            }

            bubbleContent.Children.Add(chips);
        }

        Border bubble = new()
        {
            // Board: max-width 480, #1A1C24, 1 px stroke, radius 4 16 16 16, padding 12×16.
            Background = Palette.ElevatedBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4, 16, 16, 16),
            Padding = new Thickness(16, 12),
            MaxWidth = 480,
            Child = bubbleContent
        };

        stack.Children.Add(bubble);
        return stack;
    }

    private Control BuildUserBubble(ChatEntry entry)
    {
        TextBlock text = Ui.Text(entry.Text, Palette.FontEmphasis, Palette.TextPrimaryBrush);
        text.LineHeight = 22;
        entry.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(() => text.Text = entry.Text);

        return new Border
        {
            // Board: your words sit accent-tinted at 14 %, even 16 px, aligned to the end.
            Background = new SolidColorBrush(Palette.Iris, 0.14),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16, 12),
            MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = text
        };
    }

    private Control? BuildKernelLine(ChatEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Detail))
        {
            return null;
        }

        StackPanel line = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(36, 0, 0, 0)
        };
        line.Children.Add(IconGlyph.Make(IconGlyph.Eye, 12, Palette.TextFaintBrush));
        line.Children.Add(new TextBlock
        {
            Text = entry.Detail,
            FontSize = Palette.FontCaption,
            FontWeight = FontWeight.Medium,
            LineHeight = 16,
            Foreground = Palette.TextFaintBrush,
            VerticalAlignment = VerticalAlignment.Center
        });
        return line;
    }

    private static Border AvatarDot() => new()
    {
        Width = 24,
        Height = 24,
        CornerRadius = new CornerRadius(12),
        // Board's muted wisteria dot — hers, smaller and calmer than the title orb.
        Background = new RadialGradientBrush
        {
            Center = new RelativePoint(0.35, 0.30, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.35, 0.30, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.65, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.65, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#9280DB"), 0),
                new GradientStop(Color.Parse("#634ACC"), 0.72)
            }
        }
    };

    private static Control BuildToolCapsule(ChatEntry entry)
    {
        StackPanel stack = new() { Spacing = 2, Margin = new Thickness(36, 0, 48, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = entry.Text,
            FontSize = Palette.FontCaption,
            FontWeight = FontWeight.Medium,
            LineHeight = 16,
            Foreground = Palette.TextSecondaryBrush,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(entry.Detail))
        {
            stack.Children.Add(new TextBlock
            {
                Text = entry.Detail,
                FontSize = Palette.FontCaption,
                LineHeight = 16,
                Foreground = Palette.TextFaintBrush,
                TextWrapping = TextWrapping.Wrap
            });
        }

        return stack;
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter && !eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            SendCurrentText();
            eventArgs.Handled = true;
        }
    }

    private void SendCurrentText()
    {
        string text = _composer.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        _composer.Text = string.Empty;
        _ = _model.SendAsync(text);
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        // The empty state is part of the transcript's truth: it disappears the moment she
        // or you actually say something.
        Dispatcher.UIThread.Post(() =>
        {
            RefreshEmptyState();
            _scrollViewer.ScrollToEnd();
        }, DispatcherPriority.Background);
    }
}
