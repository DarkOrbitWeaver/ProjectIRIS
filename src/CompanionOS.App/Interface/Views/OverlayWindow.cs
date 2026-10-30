using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CompanionOS.App.Interface.Controls;
using CompanionOS.App.Interface.Theme;
using CompanionOS.App.Services;
using CompanionOS.Core.Events;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Mind;
using CompanionOS.Core.Modules;
using CompanionOS.Core.Modules.BuiltIn;

namespace CompanionOS.App.Interface.Views;

/// <summary>
/// Her compact form — the floating pill, exactly as the Overlay and States boards draw
/// it: 44 px tall, fully rounded, one 22 px orb, a six-pixel state dot, and three icon
/// buttons — Open, Sleep (moon), Close. No words on the pill: "color, motion and a small
/// cue, nothing else". While she speaks, the equalizer takes the buttons' place; while
/// she games, the pill is just her amber orb at 40 % opacity.
///
/// It is a separate window on purpose — closing the main window, or a game taking the
/// screen, must never remove her from your desk. It is driven by the same ShellModel as
/// the main window, so orb state and levels are identical in both places.
///
/// Hover is read from the whole window, not from the orb cell. A 5 px transparent inset
/// leaves room for the called-state contour. Hover reveals the controls; clicking the
/// purple orb opens the main app for chat. Leaving collapses after a short grace period.
/// </summary>
public sealed class OverlayWindow : Window
{
    // The board's pill arithmetic, in DIPs. The border remains 44 px tall; transparent
    // space around it gives the attention halo room without clipping the pill's contents.
    private const double PillHeight = 44;
    private const double HomeWidth = CompactWidth + (AttentionAuraInset * 2);
    private const double HomeHeight = PillHeight + (AttentionAuraInset * 2);
    private const double OrbSize = 22;
    private const double DotSize = 6;
    private const double Gap = 10;
    private const double PadLeft = 16;
    private const double PadRight = 8;
    private const double ButtonSize = 32;
    private const double EqualizerBarWidth = 3;
    private const double EqualizerGap = 3;
    private const double OrbitOnlyPadding = 15;
    private const double Hairline = 1;
    private const double AttentionAuraInset = 5;

    private const double CompactWidth = (Hairline * 2) + PadLeft + OrbSize + Gap + DotSize + PadRight;
    private const double ButtonsWidth = CompactWidth + Gap + (ButtonSize * 3);
    private const double EqualizerWidth = (Hairline * 2) + PadLeft + OrbSize + Gap + DotSize + Gap +
                                           (EqualizerBarWidth * 3) + (EqualizerGap * 2) + PadRight;
    private const double OrbOnlyWidth = (Hairline * 2) + (OrbitOnlyPadding * 2) + OrbSize;

    private static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(380);
    private static readonly TimeSpan ResizeDuration = TimeSpan.FromMilliseconds(260);

    /// <summary>Few pixels between her docked edge and the screen edge.</summary>
    private const double DockMargin = 8;

    /// <summary>Dropping her this close to a screen edge snaps her docked there.</summary>
    private const double DockSnap = 40;

    /// <summary>A remembered position counts as docked only within this of a screen edge;
    /// anything further out is a stale coordinate from another desktop and is ignored.</summary>
    private const double DockTolerance = 72;

    private readonly ShellModel _model;
    private readonly StatusOrb _orb = new() { Width = OrbSize, Height = OrbSize };
    private readonly Rectangle[] _equalizerBars;
    private readonly Border _dot;
    private readonly StackPanel _buttonsHost;
    private readonly StackPanel _equalizerHost;
    private readonly Border _pill;
    private readonly Grid _pillSurface;
    private readonly AttentionContour _attentionContour = new() { IsHitTestVisible = false };
    private readonly DispatcherTimer _syncTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _resizeTimer;
    private readonly DispatcherTimer _backgroundCompletionTimer;
    private readonly MusicOverlayWindow _musicOverlay;
    private readonly TimerOverlayWindow _timerOverlay;
    private readonly PermissionOverlayWindow _permissionOverlay;
    private readonly ActivityOverlayWindow _activityOverlay;
    private readonly ModuleTaskFeedbackOverlayWindow _taskFeedbackOverlay;
    private readonly Dictionary<string, ModuleSurfaceOverlayWindow> _moduleSurfaceOverlays =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Window, long> _overlayShowOrder = new();
    private long _overlaySequence;
    private readonly Dictionary<string, BackgroundTaskSnapshot> _backgroundTasks =
        new(StringComparer.OrdinalIgnoreCase);
    private NowPlayingModel? _nowPlaying;
    private ActiveTimerModel? _activeTimer;
    private IDisposable? _moduleRegistrySubscription;
    private IDisposable? _conversationSubscription;
    private IDisposable? _moduleProgressSubscription;
    private IDisposable? _backgroundTaskSubscription;
    private IDisposable? _moduleSurfaceStateSubscription;
    private IDisposable? _taskFeedbackSubscription;
    private BackgroundTaskSnapshot? _backgroundTask;
    private string _activeModuleId = string.Empty;
    private string _activeModuleTurnId = string.Empty;
    private PillShape _shape = PillShape.Compact;
    private bool _pointerInside;
    private bool _dragging;
    private bool _growsLeft;
    private bool _hasPosition;
    private DateTimeOffset _attentionUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _resizeStartedAt;
    private double _resizeFromWidth;
    private double _resizeFromHeight;
    private double _resizeToWidth;
    private double _resizeToHeight;
    private PixelPoint _resizeFromPosition;
    private PixelPoint _resizeToPosition;

    /// <summary>The pill's four shapes; each one fixes the window's width.</summary>
    private enum PillShape
    {
        Compact,   // at rest: just her orb and her state dot — the board's quiet look
        Buttons,   // hovered: orb, dot, three icon buttons — Open, Sleep (moon), Close
        Equalizer, // speaking: orb, dot, three dancing bars
        OrbOnly    // gaming: just her orb, the whole pill at 40 %
    }

    public OverlayWindow(ShellModel model, Action showMainWindow, Func<Task> quitAsync)
    {
        _model = model;
        _musicOverlay = new MusicOverlayWindow(model);
        _timerOverlay = new TimerOverlayWindow(model);
        _permissionOverlay = new PermissionOverlayWindow(model);
        _activityOverlay = new ActivityOverlayWindow(showMainWindow);
        _taskFeedbackOverlay = new ModuleTaskFeedbackOverlayWindow(model);
        _backgroundCompletionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1800), DispatcherPriority.Background,
            (sender, _) =>
            {
                (sender as DispatcherTimer)?.Stop();
                _backgroundTask = null;
                SyncActivityOverlay();
            });
        _backgroundCompletionTimer.Stop();

        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = HomeWidth;
        Height = HomeHeight;

        _dot = new Border
        {
            Width = DotSize,
            Height = DotSize,
            CornerRadius = new CornerRadius(DotSize / 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Palette.SpeechBrush
        };

        StackPanel identity = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Gap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Button openChatButton = new()
        {
            Width = OrbSize,
            Height = OrbSize,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(OrbSize / 2),
            Content = _orb,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(openChatButton, "Open Iris chat");
        openChatButton.Click += (_, _) => showMainWindow();
        identity.Children.Add(openChatButton);
        identity.Children.Add(_dot);

        StackPanel buttonsHost = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (Button button in (Button[])[
                    IconButton(IconGlyph.OverlayFrame, "Open", showMainWindow),
                    IconButton(IconGlyph.Sleep, "Sleep", () => _ = _model.ToggleSleepAsync()),
                    IconButton(IconGlyph.Close, "Close", () => _ = quitAsync())
                ])
        {
            buttonsHost.Children.Add(button);
        }
        _buttonsHost = buttonsHost;

        _equalizerBars = BuildEqualizer();
        StackPanel equalizerHost = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = EqualizerGap,
            VerticalAlignment = VerticalAlignment.Center,
            Height = 14
        };
        foreach (Rectangle bar in _equalizerBars)
        {
            equalizerHost.Children.Add(bar);
        }
        _equalizerHost = equalizerHost;

        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Gap,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(identity);
        row.Children.Add(buttonsHost);
        row.Children.Add(equalizerHost);

        _pill = new Border
        {
            Width = FixedWidth(PillShape.Compact),
            Height = PillHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Palette.Elevated, 0.92),
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(Hairline),
            CornerRadius = new CornerRadius(PillHeight / 2),
            Padding = new Thickness(PadLeft, 0, PadRight, 0),
            ClipToBounds = true,
            Child = row
        };

        _pillSurface = new Grid
        {
            Width = WindowWidth(PillShape.Compact),
            Height = PillHeight + (AttentionAuraInset * 2),
            ClipToBounds = false
        };
        _pillSurface.Children.Add(_pill);
        _pillSurface.Children.Add(_attentionContour);

        Content = _pillSurface;

        // Apply the resting shape before the first paint. Previously the construction
        // width was the compact one while both button and equalizer hosts still had their
        // default Visible state, so the controls were painted outside the pill until the
        // first hover happened to run ApplyShape.
        ApplyShape(PillShape.Compact, initial: true);

        _collapseTimer = new DispatcherTimer(CollapseDelay, DispatcherPriority.Normal, (sender, _) =>
        {
            (sender as DispatcherTimer)?.Stop();
            if (!_pointerInside && !_dragging)
            {
                RefreshShapeFromState();
            }
        });
        _collapseTimer.Stop();

        _resizeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            (_, _) => AdvanceWindowResize());
        _resizeTimer.Stop();

        // One hover surface: the window. Entering anywhere expands; leaving starts a short
        // grace period, cancelled by re-entry. Moving across a child button never counts
        // as leaving, because child transitions stay inside the window's bounds.
        PointerEntered += (_, _) =>
        {
            _pointerInside = true;
            _collapseTimer.Stop();
            RefreshShapeFromState();
        };
        PointerExited += (_, _) =>
        {
            _pointerInside = false;
            if (_dragging)
            {
                return;
            }

            _collapseTimer.Stop();
            _collapseTimer.Start();
        };

        // Dragging belongs to the pill body, never to one of its buttons. BeginMoveDrag
        // runs its own modal loop and does not reliably deliver PointerReleased to the
        // control, so the position is saved the moment the loop returns.
        _pill.PointerPressed += (_, eventArgs) =>
        {
            if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
                IsInsideButton(eventArgs.Source))
            {
                return;
            }

            _collapseTimer.Stop();
            _dragging = true;
            try
            {
                BeginMoveDrag(eventArgs);
            }
            catch (InvalidOperationException)
            {
                // The platform refused the drag (the press was already released); the
                // pill simply stays where it is.
            }
            finally
            {
                _dragging = false;
                SaveDraggedPosition();
            }
        };

        _syncTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
            (_, _) => Sync());
        _syncTimer.Start();
        Closed += (_, _) =>
        {
            _syncTimer.Stop();
            _collapseTimer.Stop();
            _resizeTimer.Stop();
            _backgroundCompletionTimer.Stop();
            _moduleRegistrySubscription?.Dispose();
            _conversationSubscription?.Dispose();
            _moduleProgressSubscription?.Dispose();
            _backgroundTaskSubscription?.Dispose();
            _moduleSurfaceStateSubscription?.Dispose();
            _taskFeedbackSubscription?.Dispose();
            if (_nowPlaying is not null)
            {
                _nowPlaying.PropertyChanged -= OnNowPlayingChanged;
            }
            if (_activeTimer is not null)
            {
                _activeTimer.PropertyChanged -= OnActiveTimerChanged;
            }
            _musicOverlay.Close();
            _timerOverlay.Close();
            _permissionOverlay.Close();
            _activityOverlay.Close();
            _taskFeedbackOverlay.Close();
            foreach (ModuleSurfaceOverlayWindow window in _moduleSurfaceOverlays.Values)
            {
                window.Close();
            }
            _moduleSurfaceOverlays.Clear();
        };

        Opened += (_, _) => ApplyPosition();

        // The Position dropdown writes the corner she wakes up in; dragging still wins
        // because a dragged position is stored separately and takes priority.
        _model.Runtime.Events.Subscribe<SettingsChangedEvent>(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                if (IsVisible && !_dragging && !_pointerInside)
                {
                    ApplyPosition();
                }
            }));
        _moduleRegistrySubscription = _model.Runtime.Events.Subscribe<ModuleRegistryChangedEvent>(_ =>
            Dispatcher.UIThread.Post(RebindModuleSurfaces));
        _conversationSubscription = _model.Runtime.Events.Subscribe<ConversationEvent>(payload =>
            Dispatcher.UIThread.Post(() =>
            {
                HandleModuleConversation(payload);
            }));
        _moduleProgressSubscription = _model.Runtime.Events.Subscribe<ModuleProgressEvent>(payload =>
            Dispatcher.UIThread.Post(() => HandleModuleProgress(payload)));
        _backgroundTaskSubscription = _model.Runtime.Events.Subscribe<BackgroundTaskChangedEvent>(payload =>
            Dispatcher.UIThread.Post(() => HandleBackgroundTask(payload.Task)));
        _moduleSurfaceStateSubscription = _model.Runtime.Events.Subscribe<ModuleSurfaceStateChangedEvent>(payload =>
            Dispatcher.UIThread.Post(() => HandleModuleSurfaceState(payload)));
        _taskFeedbackSubscription = _model.Runtime.Events.Subscribe<ModuleTaskFeedbackEvent>(payload =>
            Dispatcher.UIThread.Post(() => _taskFeedbackOverlay.Apply(payload)));
        RebindModuleSurfaces();
    }

    /// <summary>
    /// Brings the normally quiet pill into its short call state. It stays non-activating and
    /// therefore never steals keyboard focus from a game, video or editor.
    /// </summary>
    public void ShowAttention(TimeSpan holdFor)
    {
        _attentionUntil = DateTimeOffset.Now.Add(holdFor);
        RefreshShapeFromState();
    }

    /// <summary>One of the pill's three icon buttons: 32 px, quiet, brightens on hover.</summary>
    private static Button IconButton(string glyph, string label, Action onClick)
    {
        // The overlay's three glyphs are circles and strokes, never the filled dash,
        // so the hover recolour can keep the Path they come back as.
        Control icon = IconGlyph.Make(glyph, 20, Palette.TextSecondaryBrush);

        Button button = new()
        {
            Width = ButtonSize,
            MinWidth = ButtonSize,
            Height = ButtonSize,
            MinHeight = ButtonSize,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = icon,
            Focusable = false,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(button, label);
        button.Click += (_, _) => onClick();

        // These stay quiet and just lift the glyph on hover, so the pill never looks like
        // a toolbar that wandered in from another app.
        button.PointerEntered += (_, _) =>
        {
            button.Background = new SolidColorBrush(Color.FromArgb(70, 38, 42, 54));
            if (icon is Avalonia.Controls.Shapes.Path path)
            {
                path.Stroke = Palette.TextPrimaryBrush;
            }
        };
        button.PointerExited += (_, _) =>
        {
            button.Background = Brushes.Transparent;
            if (icon is Avalonia.Controls.Shapes.Path path)
            {
                path.Stroke = Palette.TextSecondaryBrush;
            }
        };
        return button;
    }

    private static Rectangle[] BuildEqualizer()
    {
        // The states board: three bars, 3 px wide, 6/12/8 tall at rest, 2 px radius.
        double[] heights = [6, 12, 8];
        Rectangle[] bars = new Rectangle[heights.Length];
        for (int index = 0; index < bars.Length; index++)
        {
            bars[index] = new Rectangle
            {
                Width = EqualizerBarWidth,
                Height = heights[index],
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = Palette.SpeechBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        return bars;
    }

    /// <summary>The fixed width a shape needs; the pill morphs without reshuffling its contents.</summary>
    private static double FixedWidth(PillShape shape) => shape switch
    {
        // 16 left padding + orb 22 + gap 10 + dot 6 + 8 right padding + 2 hairline = 64.
        PillShape.Compact => CompactWidth,
        // The compact pill plus a gap and three 32 px buttons = 170.
        PillShape.Buttons => ButtonsWidth,
        // Same morphology, with the 15 px equalizer standing in for the buttons = 89.
        PillShape.Equalizer => EqualizerWidth,
        // Just her orb inside symmetric 15 px breathing room = 54.
        PillShape.OrbOnly => OrbOnlyWidth,
        _ => ButtonsWidth
    };

    private static double WindowWidth(PillShape shape) => FixedWidth(shape) + (AttentionAuraInset * 2);

    /// <summary>
    /// Where she lives: a pixel spot remembered from dragging wins — but only while it
    /// hugs a screen edge, so a coordinate left over from an older desktop can never park
    /// her mid-screen. Otherwise the configured corner of the work area applies, a few
    /// pixels off the edges. Positions are pixels, widths are DIPs, so every comparison
    /// converts through the window's scaling.
    /// </summary>
    private void ApplyPosition()
    {
        PixelRect? workArea = WorkArea();
        if (workArea is not { } work)
        {
            return;
        }

        int pixelWidth = PixelWidth();
        int pixelHeight = PixelHeight();
        int margin = (int)Math.Ceiling(DockMargin * Scaling);
        string remembered = _model.Runtime.Settings.Current.Interface.OverlayPositionPx;

        if (remembered.Split(',') is { Length: 2 } parts &&
            int.TryParse(parts[0], out int rememberedX) &&
            int.TryParse(parts[1], out int rememberedY) &&
            HugsHorizontalEdge(new PixelPoint(rememberedX, rememberedY), work, pixelWidth))
        {
            SetPositionClamped(new PixelPoint(rememberedX, rememberedY), work, pixelWidth, pixelHeight);
        }
        else
        {
            string position = _model.Runtime.Settings.Current.Interface.OverlayPosition;
            PixelPoint corner = position switch
            {
                "top left" => new PixelPoint(work.X + margin, work.Y + margin),
                "top right" => new PixelPoint(work.Right - pixelWidth - margin, work.Y + margin),
                "bottom left" => new PixelPoint(work.X + margin, work.Bottom - pixelHeight - margin),
                _ => new PixelPoint(work.Right - pixelWidth - margin,
                    work.Bottom - pixelHeight - margin) // bottom right, her desk default
            };

            SetPositionClamped(corner, work, pixelWidth, pixelHeight);
        }

        _hasPosition = true;
        // Grow away from the nearest horizontal screen edge: a pill on the right side
        // unfolds to the left, a pill on the left unfolds to the right. This is what keeps
        // the hovered pill under the pointer instead of moving it.
        _growsLeft = Position.X + (pixelWidth / 2.0) > work.X + (work.Width / 2.0);
    }

    /// <summary>True while her window sits within the dock tolerance of the screen's
    /// left or right edge — the only positions worth remembering as "where she lives".</summary>
    private bool HugsHorizontalEdge(PixelPoint point, PixelRect work, int pixelWidth)
    {
        double tolerance = DockTolerance * Scaling;
        return point.X - work.X <= tolerance ||
            work.Right - (point.X + pixelWidth) <= tolerance;
    }

    private void SetPositionClamped(PixelPoint point, PixelRect work, int pixelWidth, int pixelHeight)
    {
        Position = new PixelPoint(
            Math.Clamp(point.X, work.X, Math.Max(work.X, work.Right - pixelWidth)),
            Math.Clamp(point.Y, work.Y, Math.Max(work.Y, work.Bottom - pixelHeight)));
    }

    private PixelRect? WorkArea()
    {
        Screen? screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        return screen?.WorkingArea;
    }

    private double Scaling => RenderScaling > 0 ? RenderScaling : 1.0;

    private int PixelWidth() => Math.Max(1, (int)Math.Ceiling((Width > 0 ? Width : CompactWidth) * Scaling));

    private int PixelHeight() => Math.Max(1, (int)Math.Ceiling(Height * Scaling));

    private void Sync()
    {
        MindState state = _model.State;
        _orb.State = state;
        _orb.MicrophoneLevel = _model.MicrophoneLevel;
        _orb.SpeechLevel = _model.SpeechLevel;
        _dot.Background = new SolidColorBrush(StatusOrb.ColorForState(state));

        // The equalizer is honest: still bars mean no sound, dancing bars mean sound.
        double level = _model.IsSpeaking ? Math.Max(_model.SpeechLevel, 0.15) : 0;
        double now = Environment.TickCount64 / 1000.0;
        for (int index = 0; index < _equalizerBars.Length; index++)
        {
            double phase = now + (index * 0.7);
            double dance = 0.5 + 0.5 * Math.Sin(level * 6 + phase * 3) * Math.Sin(level * 11 + phase * 5);
            double height = level <= 0.03 ? 3 : 3 + Math.Clamp(dance, 0, 1) * 11 * level;
            _equalizerBars[index].Height = Math.Clamp(height, 3, 14);
        }

        RefreshShapeFromState();

        // Gaming dims the pill to 40 % as designed — but only when the user asked for it.
        bool dim = _model.Runtime.Settings.Current.Interface.DimWhileGaming &&
                   state == MindState.Gaming;
        _pill.Opacity = dim ? 0.4 : 1.0;
        _attentionContour.Update(
            DateTimeOffset.Now < _attentionUntil,
            state,
            _model.MicrophoneLevel,
            _model.SpeechLevel,
            _model.Runtime.Settings.Current.Interface.ReduceMotion);

        SyncMusicOverlay();
        SyncTimerOverlay();
        SyncPermissionOverlay();
        SyncModuleSurfaceOverlays();
        SyncActivityOverlay();
        SyncTaskFeedbackOverlay();
        ArrangeVisibleOverlays();
        TimeSpan desired = state == MindState.Gaming
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromMilliseconds(100);
        if (_syncTimer.Interval != desired)
        {
            _syncTimer.Interval = desired;
        }
    }

    /// <summary>Cards share a single top-right stack beneath the pill.
    /// A newly shown card gets the newest sequence and takes the first slot, shifting
    /// earlier cards down in their existing order.</summary>
    private void ArrangeVisibleOverlays()
    {
        if (WorkArea() is not { } work)
        {
            return;
        }

        List<Window> cards =
        [
            _permissionOverlay,
            _taskFeedbackOverlay,
            _activityOverlay,
            _timerOverlay,
            _musicOverlay,
            .. _moduleSurfaceOverlays.Values
        ];
        foreach (Window hidden in cards.Where(window => !window.IsVisible))
        {
            _overlayShowOrder.Remove(hidden);
        }

        foreach (Window visible in cards.Where(window => window.IsVisible))
        {
            if (!_overlayShowOrder.ContainsKey(visible))
            {
                _overlayShowOrder[visible] = ++_overlaySequence;
            }
        }

        double scale = Scaling;
        int margin = (int)Math.Ceiling(DockMargin * scale);
        int xGap = (int)Math.Ceiling(8 * scale);
        int y = work.Y + PixelHeight() + (int)Math.Ceiling(8 * scale);
        foreach (Window card in cards.Where(window => window.IsVisible)
                     .OrderByDescending(window => _overlayShowOrder[window]))
        {
            int width = Math.Max(1, (int)Math.Ceiling(card.Width * scale));
            int height = Math.Max(1, (int)Math.Ceiling(card.Height * scale));
            int x = work.Right - width - margin;
            y = Math.Min(y, work.Bottom - height - margin);
            card.Position = new PixelPoint(
                Math.Clamp(x, work.X + margin, Math.Max(work.X + margin, work.Right - width - margin)),
                Math.Clamp(y, work.Y + margin, Math.Max(work.Y + margin, work.Bottom - height - margin)));
            y += height + xGap;
        }
    }

    /// <summary>Keeps contextual controls separate from Iris' quiet default pill. The
    /// player appears only while music is active and can be dismissed without stopping
    /// playback; Iris can perform the same show/hide action through music tools.</summary>
    private void SyncMusicOverlay()
    {
        NowPlayingModel? nowPlaying = _nowPlaying;
        bool hasMusic = nowPlaying is not null && nowPlaying.Status is "Playing" or "Paused";
        bool shouldShow = IsVisible && hasMusic &&
            _model.Runtime.Settings.Current.Interface.ShowMusicOverlay &&
            !_model.HasPendingPermission &&
            _model.State != MindState.Gaming;
        if (!shouldShow)
        {
            _musicOverlay.Hide();
            return;
        }

        _musicOverlay.Bind(nowPlaying);
        if (WorkArea() is { } work)
        {
            _musicOverlay.PlaceNear(Position, PixelWidth(), PixelHeight(), work, _growsLeft);
        }

        if (!_musicOverlay.IsVisible)
        {
            _musicOverlay.Show();
        }
    }

    private void SyncTimerOverlay()
    {
        ActiveTimerModel? timer = _activeTimer;
        bool shouldShow = IsVisible && timer?.IsActive == true &&
            _model.Runtime.Settings.Current.Interface.ShowTimerOverlay &&
            !_model.HasPendingPermission &&
            _model.State != MindState.Gaming;
        if (!shouldShow)
        {
            _timerOverlay.Hide();
            return;
        }

        _timerOverlay.Bind(timer);
        if (WorkArea() is { } work)
        {
            _timerOverlay.PlaceNear(Position, PixelWidth(), PixelHeight(), work, _growsLeft);
        }

        if (!_timerOverlay.IsVisible)
        {
            _timerOverlay.Show();
        }
    }

    private void SyncPermissionOverlay()
    {
        bool shouldShow = IsVisible && _model.HasPendingPermission && _model.State != MindState.Gaming;
        if (!shouldShow)
        {
            _permissionOverlay.Hide();
            return;
        }

        _permissionOverlay.Bind(_model.PendingPermission);
        if (WorkArea() is { } work)
        {
            _permissionOverlay.PlaceNear(Position, PixelWidth(), PixelHeight(), work, _growsLeft);
        }
        if (!_permissionOverlay.IsVisible)
        {
            _permissionOverlay.Show();
        }
    }

    private void SyncActivityOverlay()
    {
        bool hasMeaningfulWork = _backgroundTask is not null || (_model.HasActiveWork &&
                                 !string.IsNullOrWhiteSpace(_model.ActivityTurnId) &&
                                 DateTimeOffset.Now - _model.ActivityStartedAt >= TimeSpan.FromMilliseconds(650));
        bool moduleOverlayVisible = _moduleSurfaceOverlays.Values.Any(window => window.IsVisible);
        bool canShow = IsVisible && hasMeaningfulWork && !_model.HasPendingPermission &&
                       _model.State != MindState.Gaming && !moduleOverlayVisible &&
                       (_backgroundTask is not null
                           ? _activityOverlay.Bind(_backgroundTask, _model.Runtime.Modules.BackgroundTasks)
                           : _activityOverlay.Bind(_model));
        if (!canShow)
        {
            _activityOverlay.Hide();
            return;
        }

        if (WorkArea() is { } work)
        {
            _activityOverlay.PlaceNear(Position, PixelWidth(), PixelHeight(), work, _growsLeft);
        }

        if (!_activityOverlay.IsVisible)
        {
            _activityOverlay.Show();
        }
    }

    /// <summary>Keeps the module task-card stack alive while a module narrates its own
    /// work. It rests right below the music card when that one is on screen, so the two
    /// never stack over each other; otherwise it spawns top-right like the others.</summary>
    private void SyncTaskFeedbackOverlay()
    {
        bool canShow = IsVisible && _taskFeedbackOverlay.HasLiveCards &&
                       !_model.HasPendingPermission && _model.State != MindState.Gaming;
        if (!canShow)
        {
            _taskFeedbackOverlay.Hide();
            return;
        }

        PixelPoint? belowAnchor = null;
        if (_musicOverlay.IsVisible)
        {
            belowAnchor = new PixelPoint(_musicOverlay.Position.X,
                _musicOverlay.Position.Y + (int)Math.Ceiling(_musicOverlay.Height * Scaling) + 12);
        }

        if (WorkArea() is { } work)
        {
            _taskFeedbackOverlay.PlaceNear(Position, PixelWidth(), PixelHeight(), work, _growsLeft, belowAnchor);
        }

        if (!_taskFeedbackOverlay.IsVisible)
        {
            _taskFeedbackOverlay.Show();
        }
    }

    private void HandleBackgroundTask(BackgroundTaskSnapshot task)
    {
        _backgroundTasks[task.Id] = task;
        foreach (string stale in _backgroundTasks.Values
                     .Where(candidate => candidate.State is not (BackgroundTaskState.Queued or BackgroundTaskState.Running or BackgroundTaskState.CancelRequested))
                     .OrderByDescending(candidate => candidate.UpdatedAt)
                     .Skip(32)
                     .Select(candidate => candidate.Id)
                     .ToList())
        {
            _backgroundTasks.Remove(stale);
        }

        _backgroundTask = task;
        bool live = task.State is BackgroundTaskState.Queued or BackgroundTaskState.Running or BackgroundTaskState.CancelRequested;
        bool foregroundOwnsModule = string.Equals(task.ModuleId, _activeModuleId, StringComparison.OrdinalIgnoreCase) &&
                                    !string.IsNullOrEmpty(_activeModuleTurnId);
        foreach (ModuleSurfaceOverlayWindow moduleSurface in foregroundOwnsModule
                     ? Array.Empty<ModuleSurfaceOverlayWindow>()
                     : FindModuleSurfaceOverlays(task.ModuleId))
        {
            if (live)
            {
                moduleSurface.BindProgress(task.Id, task.CurrentStep, task.Progress, task.Total);
            }
            else
            {
                string summary = string.IsNullOrWhiteSpace(task.Result) ? task.CurrentStep : task.Result;
                moduleSurface.BindCompletion(task.Id, summary);
            }
        }
        if (live)
        {
            _backgroundCompletionTimer.Stop();
        }
        else
        {
            _backgroundCompletionTimer.Start();
        }
        SyncModuleSurfaceOverlays();
        SyncActivityOverlay();
    }

    private void HandleModuleSurfaceState(ModuleSurfaceStateChangedEvent payload)
    {
        foreach (ModuleSurfaceOverlayWindow window in _moduleSurfaceOverlays.Values)
        {
            window.ApplyState(payload);
        }
        SyncModuleSurfaceOverlays();
    }

    /// <summary>Looks up the module surface only when the registry changes. Progress is
    /// delivered through NowPlayingModel notifications, rather than repeatedly walking
    /// every module on the overlay's animation timer.</summary>
    private void RebindModuleSurfaces()
    {
        NowPlayingModel? discovered = null;
        ActiveTimerModel? timer = null;
        Dictionary<string, ModuleSurface> overlaySurfaces = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (ModuleSurface surface in _model.Runtime.Modules.GetSurfaces())
            {
                if (surface.Slot == ShellSlot.Overlay)
                {
                    overlaySurfaces[surface.Id] = surface;
                }
                if (surface.Model is NowPlayingModel candidate)
                {
                    discovered = candidate;
                }
                if (surface.Model is ActiveTimerModel candidateTimer)
                {
                    timer = candidateTimer;
                }
            }
        }
        catch
        {
            // The overlay can be constructed before the module host has registered
            // surfaces. The registry-changed event will bind it once that is true.
            return;
        }

        if (!ReferenceEquals(_nowPlaying, discovered))
        {
            if (_nowPlaying is not null)
            {
                _nowPlaying.PropertyChanged -= OnNowPlayingChanged;
            }
            _nowPlaying = discovered;
            if (_nowPlaying is not null)
            {
                _nowPlaying.PropertyChanged += OnNowPlayingChanged;
            }
        }

        if (!ReferenceEquals(_activeTimer, timer))
        {
            if (_activeTimer is not null)
            {
                _activeTimer.PropertyChanged -= OnActiveTimerChanged;
            }
            _activeTimer = timer;
            if (_activeTimer is not null)
            {
                _activeTimer.PropertyChanged += OnActiveTimerChanged;
            }
        }

        foreach (string surfaceId in _moduleSurfaceOverlays.Keys
                     .Where(surfaceId => !overlaySurfaces.ContainsKey(surfaceId)).ToList())
        {
            ModuleSurfaceOverlayWindow stale = _moduleSurfaceOverlays[surfaceId];
            stale.Close();
            _moduleSurfaceOverlays.Remove(surfaceId);
        }
        foreach (ModuleSurface surface in overlaySurfaces.Values)
        {
            if (!_moduleSurfaceOverlays.ContainsKey(surface.Id))
            {
                _moduleSurfaceOverlays[surface.Id] = new ModuleSurfaceOverlayWindow(_model, surface);
            }
        }

        SyncMusicOverlay();
        SyncTimerOverlay();
        SyncModuleSurfaceOverlays();
    }

    private void HandleModuleConversation(ConversationEvent payload)
    {
        if (payload.Phase is ConversationPhase.TurnCancelled or ConversationPhase.TurnFailed)
        {
            if (!string.Equals(payload.TurnId, _activeModuleTurnId, StringComparison.Ordinal))
            {
                return;
            }

            foreach (ModuleSurfaceOverlayWindow window in FindModuleSurfaceOverlays(_activeModuleId))
            {
                window.Hide();
            }
            _activeModuleId = string.Empty;
            _activeModuleTurnId = string.Empty;
            SyncModuleSurfaceOverlays();
            SyncActivityOverlay();
            return;
        }

        if (payload.Phase == ConversationPhase.ToolStarted)
        {
            ModuleSurfaceOverlayWindow[] windows = FindModuleSurfaceOverlays(payload.ModuleId).ToArray();
            if (windows.Length == 0)
            {
                return;
            }
            _activeModuleId = payload.ModuleId;
            _activeModuleTurnId = payload.TurnId;
            foreach (ModuleSurfaceOverlayWindow window in windows)
            {
                window.BindActivity(payload.TurnId, payload.Text);
            }
            SyncModuleSurfaceOverlays();
            SyncActivityOverlay();
            return;
        }

        if (payload.Phase != ConversationPhase.ToolFinished ||
            !string.Equals(payload.ModuleId, _activeModuleId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(payload.TurnId, _activeModuleTurnId, StringComparison.Ordinal))
        {
            return;
        }

        ModuleSurfaceOverlayWindow[] active = FindModuleSurfaceOverlays(_activeModuleId).ToArray();
        _activeModuleId = string.Empty;
        _activeModuleTurnId = string.Empty;
        if (active.Length > 0 && !(payload.Detail?.Contains("did not allow", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            string completion = string.IsNullOrWhiteSpace(payload.Detail) ? "Done" : payload.Detail;
            foreach (ModuleSurfaceOverlayWindow window in active)
            {
                window.BindCompletion(payload.TurnId, completion);
            }
        }
        else
        {
            foreach (ModuleSurfaceOverlayWindow window in active)
            {
                window.Hide();
            }
        }
        SyncModuleSurfaceOverlays();
        SyncActivityOverlay();
    }

    private void HandleModuleProgress(ModuleProgressEvent payload)
    {
        if (!string.Equals(payload.ModuleId, _activeModuleId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(payload.TurnId, _activeModuleTurnId, StringComparison.Ordinal))
        {
            return;
        }

        foreach (ModuleSurfaceOverlayWindow active in FindModuleSurfaceOverlays(payload.ModuleId))
        {
            active.BindProgress(payload.TurnId, payload.Message, payload.Progress, payload.Total);
        }
        SyncModuleSurfaceOverlays();
    }

    private void SyncModuleSurfaceOverlays()
    {
        int stackIndex = 0;
        foreach (ModuleSurfaceOverlayWindow window in _moduleSurfaceOverlays.Values)
        {
            bool foregroundTurn = string.Equals(window.ModuleId, _activeModuleId, StringComparison.OrdinalIgnoreCase) &&
                                  !string.IsNullOrEmpty(_activeModuleTurnId);
            BackgroundTaskSnapshot? backgroundTask = _backgroundTasks.Values
                .Where(task => string.Equals(task.ModuleId, window.ModuleId, StringComparison.OrdinalIgnoreCase) &&
                               task.State is BackgroundTaskState.Queued or BackgroundTaskState.Running or BackgroundTaskState.CancelRequested)
                .OrderByDescending(task => task.UpdatedAt)
                .FirstOrDefault();
            if (!foregroundTurn && backgroundTask is not null)
            {
                window.BindProgress(backgroundTask.Id, backgroundTask.CurrentStep,
                    backgroundTask.Progress, backgroundTask.Total);
            }

            string activeWorkId = foregroundTurn ? _activeModuleTurnId : backgroundTask?.Id ?? string.Empty;
            bool activeTurn = activeWorkId.Length > 0;
            bool completed = window.IsCompletionHold;
            bool mayShow = IsVisible && !_model.HasPendingPermission && _model.State != MindState.Gaming;
            if (activeTurn && mayShow && !window.IsHiddenFor(activeWorkId))
            {
                ShowModuleSurfaceOverlay(window, stackIndex++);
            }
            else if (completed && mayShow && !window.IsHiddenFor(window.BoundWorkId))
            {
                ShowModuleSurfaceOverlay(window, stackIndex++);
            }
            else
            {
                window.Hide();
            }
        }
    }

    private void ShowModuleSurfaceOverlay(ModuleSurfaceOverlayWindow window, int stackIndex)
    {
        if (WorkArea() is { } work)
        {
            window.PlaceNear(Position, PixelWidth(), PixelHeight(), work, _growsLeft, stackIndex);
        }
        if (!window.IsVisible)
        {
            window.Show();
        }
    }

    private IEnumerable<ModuleSurfaceOverlayWindow> FindModuleSurfaceOverlays(string moduleId) =>
        _moduleSurfaceOverlays.Values.Where(window =>
            string.Equals(window.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase));

    private void OnNowPlayingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        Dispatcher.UIThread.Post(SyncMusicOverlay);
    }

    private void OnActiveTimerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        Dispatcher.UIThread.Post(SyncTimerOverlay);
    }

    /// <summary>
    /// Shape of the moment: gaming collapses to the dimmed orb; hovering unfolds the
    /// three controls; speaking shows the equalizer; otherwise she is the quiet pill.
    /// Hover outranks speech so the controls stay usable mid-sentence.
    /// </summary>
    private void RefreshShapeFromState()
    {
        PillShape shape;
        if (_model.State == MindState.Gaming)
        {
            shape = PillShape.OrbOnly;
        }
        else if (_pointerInside || _dragging || DateTimeOffset.Now < _attentionUntil)
        {
            shape = PillShape.Buttons;
        }
        else if (_model.IsSpeaking)
        {
            shape = PillShape.Equalizer;
        }
        else
        {
            shape = PillShape.Compact;
        }

        ApplyShape(shape);
    }

    /// <summary>Remembers where the user put her, so a restart does not move her
    /// desk-mate. A drop near a screen edge docks her there with the standard small
    /// margin first, so "a few pixels off the edge" is the natural resting state.</summary>
    private void SaveDraggedPosition()
    {
        try
        {
            if (WorkArea() is { } work)
            {
                int margin = (int)Math.Ceiling(DockMargin * Scaling);
                int snap = (int)Math.Ceiling(DockSnap * Scaling);
                int pixelWidth = PixelWidth();
                int fromLeft = Position.X - work.X;
                int fromRight = work.Right - (Position.X + pixelWidth);
                if (fromRight >= 0 && fromRight <= snap)
                {
                    Position = new PixelPoint(work.Right - pixelWidth - margin, Position.Y);
                }
                else if (fromLeft >= 0 && fromLeft <= snap)
                {
                    Position = new PixelPoint(work.X + margin, Position.Y);
                }

                _growsLeft = Position.X + (PixelWidth() / 2.0) > work.X + (work.Width / 2.0);
            }

            _model.Runtime.Settings.Current.Interface.OverlayPositionPx =
                $"{Position.X},{Position.Y}";
            _model.Runtime.Settings.Save();
            _model.Runtime.Events.Publish(new SettingsChangedEvent());
        }
        catch (Exception exception)
        {
            SystemWatch.Trace("ui", "overlay drag position save failed", exception.Message);
        }
    }

    /// <summary>
    /// Updates the pill and animates the whole card from its docked edge, keeping the
    /// pill anchored while the chat surface unfolds beside it.
    /// </summary>
    private void ApplyShape(PillShape shape, bool initial = false)
    {
        double targetWidth = WindowWidth(shape);
        double targetHeight = HomeHeight;
        if (!initial && _shape == shape &&
            Math.Abs(_resizeToWidth - targetWidth) < 0.1 &&
            Math.Abs(_resizeToHeight - targetHeight) < 0.1)
        {
            return;
        }

        _shape = shape;

        bool orbOnly = shape == PillShape.OrbOnly;
        bool speaking = shape == PillShape.Equalizer;
        // Compact is intentionally quiet. The controls are revealed only while the
        // pointer is inside the pill; showing this host for every non-speaking shape
        // overflows the compact width on first paint.
        _buttonsHost.IsVisible = shape == PillShape.Buttons;
        _equalizerHost.IsVisible = speaking;
        _dot.IsVisible = !orbOnly;
        _pill.Padding = orbOnly
            ? new Thickness(OrbitOnlyPadding, 0, OrbitOnlyPadding, 0)
            : new Thickness(PadLeft, 0, PadRight, 0);

        _pillSurface.Width = WindowWidth(shape);
        _pill.Width = FixedWidth(shape);

        if (initial || !_hasPosition || !IsVisible)
        {
            Width = targetWidth;
            Height = targetHeight;
            _resizeToWidth = targetWidth;
            _resizeToHeight = targetHeight;
            return;
        }

        AnimateWindowBounds(targetWidth, targetHeight);
    }

    private void AnimateWindowBounds(double targetWidth, double targetHeight)
    {
        if (WorkArea() is not { } work)
        {
            Width = targetWidth;
            Height = targetHeight;
            return;
        }

        _resizeTimer.Stop();
        _resizeFromWidth = Width;
        _resizeFromHeight = Height;
        _resizeToWidth = targetWidth;
        _resizeToHeight = targetHeight;
        _resizeFromPosition = Position;
        int widthDelta = (int)Math.Round((targetWidth - Width) * Scaling);
        int x = _growsLeft ? Position.X - widthDelta : Position.X;
        int y = Position.Y;
        int targetPixelWidth = Math.Max(1, (int)Math.Ceiling(targetWidth * Scaling));
        int targetPixelHeight = Math.Max(1, (int)Math.Ceiling(targetHeight * Scaling));
        _resizeToPosition = new PixelPoint(
            Math.Clamp(x, work.X, Math.Max(work.X, work.Right - targetPixelWidth)),
            Math.Clamp(y, work.Y, Math.Max(work.Y, work.Bottom - targetPixelHeight)));
        _resizeStartedAt = DateTimeOffset.UtcNow;
        _resizeTimer.Start();
    }

    private void AdvanceWindowResize()
    {
        double progress = Math.Clamp(
            (DateTimeOffset.UtcNow - _resizeStartedAt).TotalMilliseconds / ResizeDuration.TotalMilliseconds,
            0,
            1);
        double eased = 1 - Math.Pow(1 - progress, 3);
        Width = _resizeFromWidth + ((_resizeToWidth - _resizeFromWidth) * eased);
        Height = _resizeFromHeight + ((_resizeToHeight - _resizeFromHeight) * eased);
        Position = new PixelPoint(
            (int)Math.Round(_resizeFromPosition.X + ((_resizeToPosition.X - _resizeFromPosition.X) * eased)),
            (int)Math.Round(_resizeFromPosition.Y + ((_resizeToPosition.Y - _resizeFromPosition.Y) * eased)));
        if (progress >= 1)
        {
            Width = _resizeToWidth;
            Height = _resizeToHeight;
            Position = _resizeToPosition;
            _resizeTimer.Stop();
        }
    }

    private static bool IsInsideButton(object? source)
    {
        Visual? visual = source as Visual;
        while (visual is not null)
        {
            if (visual is Button)
            {
                return true;
            }

            visual = visual.GetVisualParent();
        }

        return false;
    }

    /// <summary>
    /// A restrained, state-colored current around the pill while Iris has been called.
    /// Its line breathes with the live mic/speech envelope and uses only a few dozen
    /// points at the existing 10 Hz overlay cadence, so the effect stays cheap.
    /// </summary>
    private sealed class AttentionContour : Control
    {
        private bool _active;
        private MindState _state;
        private double _microphoneLevel;
        private double _speechLevel;
        private bool _reduceMotion;

        public void Update(bool active, MindState state, double microphoneLevel, double speechLevel,
            bool reduceMotion)
        {
            bool needsFrame = active || _active;
            _active = active;
            _state = state;
            _microphoneLevel = Math.Clamp(microphoneLevel, 0, 1);
            _speechLevel = Math.Clamp(speechLevel, 0, 1);
            _reduceMotion = reduceMotion;
            if (needsFrame)
            {
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (!_active || Bounds.Width <= AttentionAuraInset * 2 ||
                Bounds.Height <= AttentionAuraInset * 2)
            {
                return;
            }

            Color color = StatusOrb.ColorForState(_state);
            double level = _state == MindState.Listening ? _microphoneLevel
                : _state == MindState.Speaking ? _speechLevel : 0;
            double seconds = _reduceMotion ? 0 : Environment.TickCount64 / 1000d;
            double amplitude = _reduceMotion ? 0 : 0.55 + level * 0.9;
            StreamGeometry contour = BuildContour(Bounds.Width, Bounds.Height, seconds, amplitude);

            // The wider low-alpha stroke is a soft, non-blurred halo approximation;
            // the crisp hairline keeps the state readable at small overlay sizes.
            context.DrawGeometry(null,
                new Pen(new SolidColorBrush(color, 0.13 + level * 0.05), 7), contour);
            context.DrawGeometry(null,
                new Pen(new SolidColorBrush(color, 0.42 + level * 0.24), 1.15), contour);
        }

        private static StreamGeometry BuildContour(double width, double height, double seconds, double amplitude)
        {
            double left = AttentionAuraInset;
            double right = width - AttentionAuraInset;
            double top = AttentionAuraInset;
            double bottom = height - AttentionAuraInset;
            double radius = Math.Max(1, (bottom - top) / 2);
            double straight = Math.Max(0, right - left - (2 * radius));
            double arc = Math.PI * radius;
            double perimeter = Math.Max(1, (2 * straight) + (2 * arc));
            const int samples = 88;

            Point PointAt(double distance, out Vector normal)
            {
                distance %= perimeter;
                if (distance < straight)
                {
                    normal = new Vector(0, -1);
                    return new Point(left + radius + distance, top);
                }

                distance -= straight;
                if (distance < arc)
                {
                    double angle = -Math.PI / 2 + distance / radius;
                    normal = new Vector(Math.Cos(angle), Math.Sin(angle));
                    return new Point(right - radius + (normal.X * radius), top + radius + (normal.Y * radius));
                }

                distance -= arc;
                if (distance < straight)
                {
                    normal = new Vector(0, 1);
                    return new Point(right - radius - distance, bottom);
                }

                distance -= straight;
                double leftAngle = Math.PI / 2 + distance / radius;
                normal = new Vector(Math.Cos(leftAngle), Math.Sin(leftAngle));
                return new Point(left + radius + (normal.X * radius), top + radius + (normal.Y * radius));
            }

            StreamGeometry geometry = new();
            using StreamGeometryContext builder = geometry.Open();
            for (int index = 0; index < samples; index++)
            {
                double distance = perimeter * index / samples;
                Point point = PointAt(distance, out Vector normal);
                double wave = Math.Sin(distance * 0.075 - seconds * 3.4) * amplitude +
                              Math.Sin(distance * 0.14 + seconds * 2.1) * amplitude * 0.28;
                point += normal * wave;
                if (index == 0)
                {
                    builder.BeginFigure(point, isFilled: false);
                }
                else
                {
                    builder.LineTo(point);
                }
            }

            builder.EndFigure(isClosed: true);
            return geometry;
        }
    }
}
