using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CompanionOS.App.Interface.Controls;
using CompanionOS.App.Interface.Theme;
using CompanionOS.Core.Brain;
using CompanionOS.Core.Events;
using CompanionOS.Core.Modules.BuiltIn;

namespace CompanionOS.App.Interface;

/// <summary>
/// The chat screen's right rail, transcribed card for card from the Chat board:
///
///   MusicRailPanel    title, status, a 2 px progress line and the real transport
///   SensesRailRow     the eye row: whatever she is actually looking at right now
///   ResourcesRailCard VRAM, RAM and context meters, moved by real telemetry
///
/// Nothing here invents state. The music panel speaks through her own module tools;
/// the senses row reads the perception engine; the meters follow the telemetry event.
/// </summary>

/// <summary>
/// The music card, board spec: #161821, radius 12, padding 16, gap 8 — a 15 px title,
/// a 13 px secondary line, a 2 px progress line and three centred transport icons.
///
/// <paramref name="invokeTool"/> receives the real module tool name, so pressing the
/// transport is exactly the same as asking her to play something with her voice.
/// </summary>
public sealed class MusicRailPanel : Border
{
    private readonly Action<string> _invokeTool;
    private readonly Border _fill;
    private readonly TextBlock _title;
    private readonly TextBlock _secondary;
    private NowPlayingModel? _nowPlaying;
    private Button _play = null!;
    private readonly System.ComponentModel.PropertyChangedEventHandler _onModelChanged;
    private bool _isSubscribed;
    private bool _isAttached;

    public MusicRailPanel(NowPlayingModel? nowPlaying, Action<string> invokeTool)
    {
        _invokeTool = invokeTool;
        _nowPlaying = nowPlaying;
        // Her player runs on its own thread; every model change lands on the UI
        // thread before a control is touched. The handler exists unconditionally so
        // a later Rebind to a model can subscribe without ever having had one.
        _onModelChanged = (_, _) => Dispatcher.UIThread.Post(ReadingFromModel);

        Background = Palette.SurfaceBrush;
        CornerRadius = new CornerRadius(Palette.RadiusCard);
        Padding = new Thickness(16);

        _title = new TextBlock
        {
            FontFamily = Palette.UiFont,
            FontSize = 15,
            LineHeight = 22,
            FontWeight = FontWeight.Medium,
            Foreground = Palette.TextPrimaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };

        _secondary = new TextBlock
        {
            FontFamily = Palette.UiFont,
            FontSize = Palette.FontBody,
            LineHeight = 18,
            FontWeight = FontWeight.Medium,
            Foreground = Palette.TextSecondaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };

        // The board's progress line: a 2 px #262A36 track across the card, filled from
        // the left in #9AA1B5. The fill width tracks the control's own width, so the
        // percentage stays honest at any size.
        _fill = new Border
        {
            Height = 2,
            CornerRadius = new CornerRadius(1),
            Background = Palette.TextSecondaryBrush,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        Border track = new()
        {
            Height = 2,
            CornerRadius = new CornerRadius(1),
            Background = Palette.StrokeBrush,
            Margin = new Thickness(0, 4, 0, 0),
            Child = _fill
        };
        track.SizeChanged += (_, _) => UpdateProgress();

        BlinkTransportRow();

        StackPanel stack = new() { Spacing = 8 };
        stack.Children.Add(_title);
        stack.Children.Add(_secondary);
        stack.Children.Add(track);
        stack.Children.Add(TransportRow());
        Child = stack;

        // The panel lives as long as the chat screen and is detached whenever the user
        // visits Memory or Body. Subscribing in the constructor and unsubscribing on
        // detach therefore killed the music card permanently after one screen switch;
        // the subscription follows the panel's attachment instead.
        AttachedToVisualTree += (_, _) =>
        {
            _isAttached = true;
            SubscribeToModel();
            ReadingFromModel();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _isAttached = false;
            UnsubscribeFromModel();
        };

        ReadingFromModel();
        UpdateProgress();
    }

    /// <summary>The live model this panel is bound to, or null while she is unbound.</summary>
    public NowPlayingModel? NowPlaying => _nowPlaying;

    /// <summary>
    /// Rebinds the panel to a new player model and re-asserts the visuals from it.
    ///
    /// The previous signature threw its argument away and only refreshed from the
    /// model held since construction - so a caller handing in the FIRST discovered
    /// model after a null start was pinned to "Loading player..." forever, with the
    /// subscription silently pointed at nothing (2026-10-27 audit).
    /// </summary>
    public void Rebind(NowPlayingModel? nowPlaying)
    {
        if (ReferenceEquals(_nowPlaying, nowPlaying))
        {
            ReadingFromModel();
            return;
        }

        UnsubscribeFromModel();
        _nowPlaying = nowPlaying;
        SubscribeToModel();
        ReadingFromModel();
    }

    private void SubscribeToModel()
    {
        if (_isAttached && !_isSubscribed && _nowPlaying is not null)
        {
            _nowPlaying.PropertyChanged += _onModelChanged;
            _isSubscribed = true;
        }
    }

    private void UnsubscribeFromModel()
    {
        // Invariant: _isSubscribed implies _nowPlaying is the model holding the
        // subscription - Rebind unsubscribes BEFORE swapping the field in.
        if (_isSubscribed && _nowPlaying is not null)
        {
            _nowPlaying.PropertyChanged -= _onModelChanged;
        }

        _isSubscribed = false;
    }

    private Control TransportRow()
    {
        Button previous = TransportButton(IconGlyph.PlayPrevious, Palette.TextSecondaryBrush, "Previous track",
            () => _invokeTool("previous_track"));
        Button next = TransportButton(IconGlyph.PlayNext, Palette.TextSecondaryBrush, "Next track",
            () => _invokeTool("next_track"));

        // The board centers three icons with a 20 px breathing gap between them.
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 20,
            Margin = new Thickness(0, 4, 0, 0),
            Children = { previous, _play, next }
        };
    }

    private void BlinkTransportRow()
    {
        _play = TransportButton(IconGlyph.Play, Palette.TextPrimaryBrush, "Play or pause", OnTransportPressed,
            primary: true);
    }

    private static Button TransportButton(string geometry, IBrush stroke, string label, Action onClick,
        bool primary = false)
    {
        Button button = new()
        {
            Content = IconGlyph.Make(geometry, 20, stroke),
            Background = primary ? Palette.ElevatedBrush : Brushes.Transparent,
            BorderBrush = primary ? Palette.StrokeBrush : Brushes.Transparent,
            BorderThickness = primary ? new Thickness(1) : new Thickness(0),
            Padding = new Thickness(0),
            Width = primary ? 36 : 28,
            Height = primary ? 36 : 28,
            CornerRadius = new CornerRadius(primary ? 18 : 14),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            UseLayoutRounding = true
        };
        ToolTip.SetTip(button, label);
        button.Click += (_, _) => onClick();
        return button;
    }

    private void OnTransportPressed()
    {
        // The truth of the moment decides which real tool runs: playing → pause her
        // music, paused → resume it, stopped → start it.
        switch (NowPlaying?.Status)
        {
            case "Playing":
                _invokeTool("pause_music");
                break;
            case "Paused":
                _invokeTool("resume_music");
                break;
            default:
                _invokeTool("play_music");
                break;
        }
    }

    private void ReadingFromModel()
    {
        if (NowPlaying is null)
        {
            // A player is an affordance, not only a receipt for active playback. Hiding
            // it made the feature look as if it had disappeared while its module was
            // initialising or when playback stopped.
            IsVisible = true;
            _title.Text = "Music library";
            _secondary.Text = "Loading player…";
            _play.Content = IconGlyph.Make(IconGlyph.Play, 20, Palette.TextPrimaryBrush);
            UpdateProgress();
            return;
        }

        IsVisible = true;
        bool resting = NowPlaying.Status == "Stopped" ||
            string.IsNullOrWhiteSpace(NowPlaying.Title) ||
            string.Equals(NowPlaying.Title, "Nothing playing", StringComparison.Ordinal);
        _title.Text = resting ? "Music library" : NowPlaying.Title;
        if (!resting)
        {
            _secondary.Text = NowPlaying.Status;
        }
        else
        {
            bool showScanStatus = NowPlaying.IsScanning ||
                NowPlaying.LibraryStatus.StartsWith("Partial", StringComparison.OrdinalIgnoreCase) ||
                NowPlaying.LibraryStatus.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
                NowPlaying.LibraryStatus.Contains("skipped", StringComparison.OrdinalIgnoreCase) ||
                NowPlaying.LibraryStatus.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
                NowPlaying.LibraryStatus.Contains("failed", StringComparison.OrdinalIgnoreCase);
            _secondary.Text = showScanStatus
                ? NowPlaying.LibraryStatus
                : NowPlaying.TrackCount == 0 ? "Ask Iris to add a music folder" : $"{NowPlaying.TrackCount} tracks ready";
        }

        // Board truth: the play glyph in text-white while stopped or paused; when she is
        // actually playing, the same button offers pause.
        _play.Content = IconGlyph.Make(NowPlaying.Status == "Playing" ? IconGlyph.Pause : IconGlyph.Play,
            20, Palette.TextPrimaryBrush);
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        double trackWidth = _fill.Parent is Border track ? track.Bounds.Width : 0;
        double progress = Math.Clamp(NowPlaying?.Progress ?? 0, 0, 1);

        // Without a bound model the line is empty — an honest zero, not a sampled fake.
        _fill.Width = trackWidth * progress;
    }
}

/// <summary>
/// The senses row, board spec: #161821, radius 12, padding 16 — one 16 px eye and one
/// 13 px line that never wraps. It answers one question honestly: what she is looking
/// at right now — the real foreground application and window, or nothing at all.
/// </summary>
public sealed class SensesRailRow : Border
{
    private readonly CompanionRuntime _runtime;
    private TextBlock _text = null!;

    public SensesRailRow(CompanionRuntime runtime)
    {
        _runtime = runtime;

        _text = new TextBlock
        {
            FontFamily = Palette.UiFont,
            FontSize = Palette.FontBody,
            LineHeight = 18,
            FontWeight = FontWeight.Medium,
            Foreground = Palette.TextPrimaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        // A grid rather than a stack: the text gets a bounded star column, so its
        // ellipsis trimming actually engages instead of painting past the card edge.
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Control eye = IconGlyph.Make(IconGlyph.Eye, 16, Palette.TextSecondaryBrush);
        eye.VerticalAlignment = VerticalAlignment.Center;
        eye.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(eye, 0);
        Grid.SetColumn(_text, 1);
        row.Children.Add(eye);
        row.Children.Add(_text);

        Background = Palette.SurfaceBrush;
        CornerRadius = new CornerRadius(Palette.RadiusCard);
        Padding = new Thickness(16);
        Child = row;
    }

    /// <summary>Re-reads the perception snapshot. Called on a slow timer, not per frame.</summary>
    public void Refresh()
    {
        var snapshot = _runtime.Perception.Current;
        string application = snapshot.ActiveApplicationName?.Trim() ?? string.Empty;
        string window = snapshot.ActiveWindowTitle?.Trim() ?? string.Empty;

        if (application.Length == 0 && window.Length == 0)
        {
            // Honest absence: nothing is in focus, so the row says nothing is in focus.
            _text.Text = snapshot.IsUserIdle ? "Idle — nothing in focus" : "Nothing in focus";
            return;
        }

        window = string.Join(' ', window.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(6));
        _text.Text = window.Length > 0 ? $"{application} — {window}" : application;
    }
}

/// <summary>
/// The resources card, board spec: #161821, radius 12, padding 16, gap 14 — three
/// meters, each a spaced label/value pair over a 4 px bar. The labels are the board's
/// small uppercase captions; the values come only from the telemetry event, and a
/// machine without the GPU probe shows a dash, never a made-up number.
/// </summary>
public sealed class ResourcesRailCard : Border
{
    private readonly Meter _vram = new("GPU VRAM", "Total GPU allocation across processes; this is not Iris-only.");
    private readonly Meter _ram = new("RAM");
    private readonly Meter _context = new("CONTEXT");

    private double _usedVramMegabytes;
    private double _totalVramMegabytes;
    private double _processRamMegabytes;
    private double _contextFillPercent;

    private readonly CompanionRuntime _runtime;

    public ResourcesRailCard(CompanionRuntime runtime)
    {
        _runtime = runtime;

        // Telemetry is published by the runtime on its own cadence; every number here
        // arrived as an event, and only as an event.
        runtime.Events.Subscribe<TelemetryEvent>(payload => Dispatcher.UIThread.Post(() =>
        {
            _usedVramMegabytes = payload.UsedVramMegabytes;
            _totalVramMegabytes = payload.TotalVramMegabytes;
            _processRamMegabytes = payload.ProcessRamMegabytes;
            _contextFillPercent = payload.ContextFillPercent;
            ReadingFromTelemetry();
        }));

        StackPanel stack = new() { Spacing = 14, Children = { _vram.Control, _ram.Control, _context.Control } };

        Background = Palette.SurfaceBrush;
        CornerRadius = new CornerRadius(Palette.RadiusCard);
        Padding = new Thickness(16);
        Child = stack;
    }

    /// <summary>Re-asserts the meters. Called on a slow timer so a fresh bar width also fills.</summary>
    public void Refresh()
    {
        ReadingFromTelemetry();
    }

    private double PercentFor(string key)
    {
        return key switch
        {
            "VRAM" => _totalVramMegabytes > 0 ? _usedVramMegabytes / _totalVramMegabytes : 0,
            "RAM" => PercentRam(),
            _ => Math.Clamp(_contextFillPercent / 100.0, 0, 1)
        };
    }

    private double PercentRam()
    {
        // The bar's denominator is the machine's real total memory from perception —
        // without it the number is left at an honest zero rather than invented.
        double machineTotal = _runtime.Perception.Current.RamTotalMegabytes;
        return machineTotal > 0 ? _processRamMegabytes / machineTotal : 0;
    }

    private void ReadingFromTelemetry()
    {
        // VRAM: the board's "5.5 / 8 GB" — one decimal used, whole-number total. Without
        // the GPU probe the value is a dash and the bar stays empty.
        _vram.Value.Text = _totalVramMegabytes > 0
            ? $"{_usedVramMegabytes / 1024.0:F1} / {_totalVramMegabytes / 1024.0:F0} GB"
            : "—";
        _vram.Refresh(PercentFor("VRAM"));

        _ram.Value.Text = $"{_processRamMegabytes:F0} MB";
        _ram.Refresh(PercentFor("RAM"));

        _context.Value.Text = $"{_contextFillPercent:F0}%";
        _context.Refresh(PercentFor("CONTEXT"));
    }

    /// <summary>One meter: its board row of label and value over a 4 px two-tone bar.</summary>
    private sealed class Meter
    {
        private readonly Border _fill;
        private readonly Border _bar;

        public Meter(string labelText, string? tooltip = null)
        {
            Label = new TextBlock
            {
                Text = labelText,
                FontFamily = Palette.UiFont,
                FontSize = Palette.FontCaption,
                LineHeight = 16,
                FontWeight = FontWeight.Medium,
                Foreground = Palette.TextSecondaryBrush,
                // The board: .08em tracking (0.88 px) on these small captions; the text
                // itself is uppercase already.
                LetterSpacing = 0.88
            };
            Value = new TextBlock
            {
                FontFamily = Palette.UiFont,
                FontSize = Palette.FontCaption,
                LineHeight = 16,
                FontWeight = FontWeight.Medium,
                Foreground = Palette.TextPrimaryBrush,
                TextAlignment = TextAlignment.Right
            };

            if (!string.IsNullOrWhiteSpace(tooltip))
            {
                ToolTip.SetTip(Label, tooltip);
                ToolTip.SetTip(Value, tooltip);
            }

            _fill = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = Palette.TextSecondaryBrush,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _bar = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = Palette.StrokeBrush,
                Child = _fill
            };

            // The row's value sits at the far edge, exactly as the board aligns it.
            Control meterRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children = { Label, Value }
            };
            Grid.SetColumn(Value, 1);

            Control = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 6,
                Children = { meterRow, _bar }
            };
        }

        public Control Control { get; }
        public TextBlock Label { get; }
        public TextBlock Value { get; }

        public void Refresh(double percent)
        {
            double trackWidth = _bar.Bounds.Width;
            _fill.Width = trackWidth * Math.Clamp(percent, 0, 1);
        }
    }
}
