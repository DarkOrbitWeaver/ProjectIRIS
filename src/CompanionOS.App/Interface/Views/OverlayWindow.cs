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
using CompanionOS.Core.Events;
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
/// leaves room for the called-state contour, and the window resizes around a fixed edge.
/// That combination is what makes the reveal usable:
/// expanding never slides the pill out from under the pointer, and leaving it collapses
/// after a short grace period instead of flickering. The window is shown without
/// activation so opening the overlay never steals focus from a game.
/// </summary>
public sealed class OverlayWindow : Window
{
    // The board's pill arithmetic, in DIPs. The border remains 44 px tall; transparent
    // space around it gives the attention halo room without clipping the pill's contents.
    private const double PillHeight = 44;
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

    private static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(280);

    private readonly ShellModel _model;
    private readonly StatusOrb _orb = new() { Width = OrbSize, Height = OrbSize };
    private readonly Rectangle[] _equalizerBars;
    private readonly Border _dot;
    private readonly StackPanel _buttonsHost;
    private readonly StackPanel _equalizerHost;
    private readonly Border _pill;
    private readonly AttentionContour _attentionContour = new() { IsHitTestVisible = false };
    private readonly DispatcherTimer _syncTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly MusicOverlayWindow _musicOverlay;
    private readonly TimerOverlayWindow _timerOverlay;
    private readonly PermissionOverlayWindow _permissionOverlay;
    private NowPlayingModel? _nowPlaying;
    private ActiveTimerModel? _activeTimer;
    private IDisposable? _moduleRegistrySubscription;
    private PillShape _shape = PillShape.Compact;
    private bool _pointerInside;
    private bool _dragging;
    private bool _growsLeft;
    private bool _hasPosition;
    private DateTimeOffset _attentionUntil = DateTimeOffset.MinValue;

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

        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Height = PillHeight + (AttentionAuraInset * 2);

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
        identity.Children.Add(_orb);
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

        Grid overlaySurface = new() { ClipToBounds = false };
        overlaySurface.Children.Add(_pill);
        overlaySurface.Children.Add(_attentionContour);
        Content = overlaySurface;
        Width = WindowWidth(PillShape.Compact);

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
            _moduleRegistrySubscription?.Dispose();
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
    /// Where she lives: a pixel corner remembered from dragging wins; otherwise the
    /// configured corner of the work area, 24 px off the edges. Positions are pixels,
    /// widths are DIPs, so every comparison converts through the window's scaling.
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
        string remembered = _model.Runtime.Settings.Current.Interface.OverlayPositionPx;

        if (!string.IsNullOrWhiteSpace(remembered) &&
            remembered.Split(',') is { Length: 2 } parts &&
            int.TryParse(parts[0], out int rememberedX) &&
            int.TryParse(parts[1], out int rememberedY))
        {
            SetPositionClamped(new PixelPoint(rememberedX, rememberedY), work, pixelWidth, pixelHeight);
        }
        else
        {
            string position = _model.Runtime.Settings.Current.Interface.OverlayPosition;
            PixelPoint corner = position switch
            {
                "top left" => new PixelPoint(work.X + 24, work.Y + 24),
                "top right" => new PixelPoint(work.Right - pixelWidth - 24, work.Y + 24),
                "bottom left" => new PixelPoint(work.X + 24, work.Bottom - pixelHeight - 24),
                _ => new PixelPoint(work.Right - pixelWidth - 24,
                    work.Bottom - pixelHeight - 24) // bottom right, her desk default
            };

            SetPositionClamped(corner, work, pixelWidth, pixelHeight);
        }

        _hasPosition = true;
        // Grow away from the nearest horizontal screen edge: a pill on the right side
        // unfolds to the left, a pill on the left unfolds to the right. This is what keeps
        // the hovered pill under the pointer instead of moving it.
        _growsLeft = Position.X + (pixelWidth / 2.0) > work.X + (work.Width / 2.0);
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
        TimeSpan desired = state == MindState.Gaming
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromMilliseconds(100);
        if (_syncTimer.Interval != desired)
        {
            _syncTimer.Interval = desired;
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

    /// <summary>Looks up the module surface only when the registry changes. Progress is
    /// delivered through NowPlayingModel notifications, rather than repeatedly walking
    /// every module on the overlay's animation timer.</summary>
    private void RebindModuleSurfaces()
    {
        NowPlayingModel? discovered = null;
        ActiveTimerModel? timer = null;
        try
        {
            foreach (ModuleSurface surface in _model.Runtime.Modules.GetSurfaces())
            {
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

        SyncMusicOverlay();
        SyncTimerOverlay();
    }

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

    /// <summary>Remembers where the user put her, so a restart does not move her desk-mate.</summary>
    private void SaveDraggedPosition()
    {
        try
        {
            _model.Runtime.Settings.Current.Interface.OverlayPositionPx =
                $"{Position.X},{Position.Y}";
            _model.Runtime.Settings.Save();

            if (WorkArea() is { } work)
            {
                _growsLeft = Position.X + (PixelWidth() / 2.0) > work.X + (work.Width / 2.0);
            }

            _model.Runtime.Events.Publish(new SettingsChangedEvent());
        }
        catch (Exception exception)
        {
            SystemWatch.Trace("ui", "overlay drag position save failed", exception.Message);
        }
    }

    /// <summary>
    /// Swaps the pill's contents for a new shape and resizes the window around the edge
    /// the pill is anchored to, so the screen position of that edge never moves.
    /// </summary>
    private void ApplyShape(PillShape shape, bool initial = false)
    {
        if (!initial && _shape == shape)
        {
            return;
        }

        double oldWidth = Width;
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

        Width = WindowWidth(shape);
        _pill.Width = FixedWidth(shape);

        if (initial || !_hasPosition || !IsVisible || Math.Abs(oldWidth - Width) < 0.1)
        {
            return;
        }

        // Window width is DIPs; screen coordinates are physical pixels.
        int delta = (int)Math.Round((Width - oldWidth) * Scaling);
        if (delta == 0)
        {
            return;
        }

        if (WorkArea() is not { } work)
        {
            return;
        }

        int x = _growsLeft ? Position.X - delta : Position.X;
        x = Math.Clamp(x, work.X, Math.Max(work.X, work.Right - PixelWidth()));
        Position = new PixelPoint(x, Position.Y);
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
