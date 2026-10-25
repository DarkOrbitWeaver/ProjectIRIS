using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CompanionOS.App.Interface.Theme;
using CompanionOS.Core.Mind;

namespace CompanionOS.App.Interface.Controls;

/// <summary>
/// Her face. One orb, seven states.
///
/// Color, motion and a small cue — nothing else, exactly as the States board specifies:
/// Idle breathes on a 4-second cycle, Listening follows your voice, Thinking runs a
/// 270-degree sweep, Speaking pulses with her speech, Asleep is still with a faint ring,
/// Gaming pulses once every 8 seconds, Error shakes twice and holds.
///
/// Rendering is hand-drawn on one timer so it stays at full frame rate for free and can
/// never flood the UI thread with layout work. The orb never animates for decoration,
/// only because something actually happened.
///
/// The look is the board's: one radial — lightened core at 35 % x, 30 % y easing to the
/// state colour at 72 % of the disc — filling the whole orb, one soft glow behind it, no
/// extra rings. Thinking is the board's conic sweep: a light 150-degree wedge riding
/// around the inside of the disc.
/// </summary>
public sealed class StatusOrb : Control
{
    private readonly DispatcherTimer _animationTimer;
    private MindState _state = MindState.Idle;
    private double _microphoneLevel;
    private double _speechLevel;
    private double _seconds;      // seconds since the state began, drives the cycle lengths
    private double _errorShake;   // remaining shake energy, only Error spends it
    private double _attentionFlash;

    public StatusOrb()
    {
        _animationTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background,
            (_, _) => AdvanceFrame());
        _animationTimer.Stop();

        Width = 64;
        Height = 64;
    }

    /// <summary>What she is doing. Set from the event bus on the UI thread.</summary>
    public MindState State
    {
        get => _state;
        set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            _seconds = 0;
            _errorShake = value == MindState.Error ? 1.0 : 0;
            if (value == MindState.Listening)
            {
                _attentionFlash = 1.0;
            }

            InvalidateVisual();
        }
    }

    /// <summary>Microphone loudness, 0 to 1, drives the listening ring.</summary>
    public double MicrophoneLevel
    {
        get => _microphoneLevel;
        set => _microphoneLevel = Math.Clamp(value, 0, 1);
    }

    /// <summary>Her own voice loudness, 0 to 1, drives the speaking pulse.</summary>
    public double SpeechLevel
    {
        get => _speechLevel;
        set => _speechLevel = Math.Clamp(value, 0, 1);
    }

    /// <summary>When true she breathes and pulses far less.</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>Shows the "needs approval" question mark.</summary>
    public bool ShowPermissionGlyph { get; set; }

    /// <summary>Returns the colour that represents a mind state.</summary>
    public static Color ColorForState(MindState state) => state switch
    {
        MindState.Listening => Palette.StateListening,
        MindState.Thinking => Palette.StateThinking,
        MindState.Acting => Palette.StateThinking,
        MindState.Speaking => Palette.StateSpeaking,
        MindState.PermissionWait => Palette.StateGaming,
        MindState.Sleeping => Palette.StateAsleep,
        MindState.Gaming => Palette.StateGaming,
        MindState.Error => Palette.StateError,
        MindState.Booting => Palette.TextFaint,
        _ => Palette.StateIdle
    };

    /// <summary>Returns the small uppercase state word the title bar shows.</summary>
    public static string WordForState(MindState state, bool isRunning) => state switch
    {
        MindState.Listening => "Listening",
        MindState.Thinking or MindState.Acting => "Thinking",
        MindState.Speaking => "Speaking",
        MindState.Sleeping => "Asleep",
        MindState.Gaming => "Gaming",
        MindState.Error => "Error",
        MindState.Booting => "Booting",
        _ => isRunning ? "Ready" : "Idle"
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        double side = Math.Min(Width, Height);
        side = double.IsNaN(side) || side <= 0 ? 64 : side;
        return new Size(side, side);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        base.OnAttachedToVisualTree(eventArgs);
        _animationTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        base.OnDetachedFromVisualTree(eventArgs);
        _animationTimer.Stop();
    }

    public override void Render(DrawingContext context)
    {
        double side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 4)
        {
            return;
        }

        Point center = new(Bounds.Width / 2, Bounds.Height / 2);
        Color stateColor = ColorForState(_state);
        double discRadius = side * 0.47;

        // The shake: Error spends two quick shakes then holds still. Nothing else shakes.
        double shake;
        if (_errorShake > 0)
        {
            shake = Math.Sin(_errorShake * Math.PI * 6) * _errorShake * discRadius * 0.35;
        }
        else
        {
            shake = 0;
        }

        center = new Point(center.X + shake, center.Y);

        // Breathing — Idle and Gaming pulse on their own slow cycles, Speaking follows
        // her actual voice, Listening follows yours. Asleep does not move at all.
        double pulse = 1.0;
        switch (_state)
        {
            case MindState.Idle when !ReduceMotion:
                pulse = 1.0 + Math.Sin(_seconds * Math.PI / 2.0) * 0.04; // one breath, 4 s
                break;
            case MindState.Speaking:
                double voice = Math.Max(_speechLevel, ReduceMotion ? 0 : 0.15);
                pulse = 1.0 + voice * 0.10;
                break;
            case MindState.Gaming when !ReduceMotion:
                // A single soft pulse every 8 seconds, so she stays noticed but quiet.
                double period = 8.0;
                double position = _seconds % period / period;
                double envelope = Math.Sin(position * Math.PI);
                pulse = 1.0 + envelope * 0.06;
                break;
            case MindState.Sleeping:
            case MindState.Booting:
                pulse = 0.90;
                break;
        }

        double drawnRadius = discRadius * pulse;
        double opacity = _state switch
        {
            MindState.Sleeping => 0.85,
            MindState.Booting => 0.55,
            _ => 1.0
        };

        // The glow: one soft halo behind the disc, the state colour at low alpha — the
        // boards' blurred box-shadow rendered as paint.
        if (_state != MindState.Sleeping)
        {
            context.DrawEllipse(new SolidColorBrush(stateColor, 0.16 * opacity), null, center,
                drawnRadius * 1.24, drawnRadius * 1.24);
        }

        // The core disc: the boards' radial — lightened edge at 35 % x, 30 % y easing to
        // the state colour at 72 % of the disc.
        IBrush disc = new RadialGradientBrush
        {
            Center = new RelativePoint(0.35, 0.30, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.35, 0.30, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(WithAlpha(Lighten(stateColor, 0.42), opacity), 0.0),
                new GradientStop(WithAlpha(stateColor, opacity), 0.72),
                new GradientStop(WithAlpha(Darken(stateColor, 0.06), opacity), 1.0)
            }
        };
        context.DrawEllipse(disc, null, center, drawnRadius, drawnRadius);

        // The 270-degree sweep, Thinking and Booting only: the board's conic gradient as
        // a light wedge riding around inside the disc.
        if (_state is MindState.Thinking or MindState.Acting or MindState.Booting)
        {
            double sweepOpacity = _state == MindState.Booting ? 0.30 : 0.55;
            IPen? wedgePen = null;
            context.DrawGeometry(new SolidColorBrush(Palette.SpeechEdge, sweepOpacity), wedgePen,
                Wedge(center, drawnRadius * 0.62, _seconds * 2.2 + 2.1, 2.7));
        }

        // Asleep: still, with the faint white ring the board spreads six pixels out.
        if (_state == MindState.Sleeping)
        {
            context.DrawEllipse(null,
                new Pen(new SolidColorBrush(Color.FromArgb(20, 231, 233, 242)), 6),
                center, drawnRadius + 3, drawnRadius + 3);
        }

        // Listening: a ring of short bars that follows your voice.
        if (_state == MindState.Listening)
        {
            DrawVoiceBars(context, center, drawnRadius + 2, stateColor, _microphoneLevel);
        }

        if (_attentionFlash > 0)
        {
            double flashRadius = drawnRadius * 2.2 * (1.2 - _attentionFlash);
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Palette.Mic, _attentionFlash * 0.7), 1.5),
                center, flashRadius, flashRadius);
        }

        if (ShowPermissionGlyph || _state == MindState.PermissionWait)
        {
            FormattedText glyph = new("?", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface(Palette.UiFont, FontStyle.Normal, FontWeight.SemiBold),
                drawnRadius * 1.1, new SolidColorBrush(Palette.Background));
            context.DrawText(glyph, new Point(center.X - glyph.Width / 2, center.Y - glyph.Height / 2));
        }
    }

    /// <summary>A pie wedge: center, edge line, arc, closed. The sweep's visible part.</summary>
    private static Geometry Wedge(Point center, double radius, double from, double to)
    {
        Point edge(double angle) => new(
            center.X + Math.Cos(angle) * radius,
            center.Y + Math.Sin(angle) * radius);

        Point start = edge(from);
        Point end = edge(to);
        const double radiusValue = 1000;
        bool clockwise = to > from;

        string data =
            $"M {center.X:0.##},{center.Y:0.##} L {start.X:0.##},{start.Y:0.##} " +
            $"A {radiusValue},{radiusValue} 0 0 {(clockwise ? 1 : 0)} {end.X:0.##},{end.Y:0.##} Z";
        return StreamGeometry.Parse(data);
    }

    private void AdvanceFrame()
    {
        if (!IsEffectivelyVisible)
        {
            return;
        }

        _seconds += 0.033;
        if (_seconds > 3600)
        {
            _seconds = 0;
        }

        if (_state == MindState.Error)
        {
            // Two shakes (about 0.5 s), then hold.
            _errorShake = Math.Max(0, _errorShake - 0.066);
        }
        else if (_errorShake > 0)
        {
            _errorShake = 0;
        }

        if (_attentionFlash > 0)
        {
            _attentionFlash = Math.Max(0, _attentionFlash - 0.06);
        }

        // Microphone level decays on its own so the ring settles instead of sticking.
        _microphoneLevel *= 0.88;
        _speechLevel *= 0.90;

        InvalidateVisual();
    }

    private void DrawVoiceBars(DrawingContext context, Point center, double radius, Color color, double level)
    {
        const int barCount = 32;
        double amplitude = Math.Max(level, 0.06);
        IPen barPen = new Pen(new SolidColorBrush(color, 0.75), 1.4);

        for (int barIndex = 0; barIndex < barCount; barIndex++)
        {
            double angle = barIndex * Math.PI * 2 / barCount;
            double wave = Math.Abs(Math.Sin(barIndex * 0.9));
            double length = 2 + amplitude * 14 * wave;

            Point inner = new(
                center.X + Math.Cos(angle) * radius,
                center.Y + Math.Sin(angle) * radius);
            Point outer = new(
                center.X + Math.Cos(angle) * (radius + length),
                center.Y + Math.Sin(angle) * (radius + length));

            context.DrawLine(barPen, inner, outer);
        }
    }

    private static Color WithAlpha(Color color, double opacity)
    {
        byte alpha = (byte)Math.Clamp(opacity * 255, 0, 255);
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static Color Lighten(Color color, double amount)
    {
        return Blend(color, Colors.White, amount);
    }

    private static Color Darken(Color color, double amount)
    {
        return Blend(color, Colors.Black, amount);
    }

    private static Color Blend(Color color, Color with, double amount)
    {
        double mix(double a, double b) => a + (b - a) * Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            color.A,
            (byte)Math.Clamp(mix(color.R, with.R), 0, 255),
            (byte)Math.Clamp(mix(color.G, with.G), 0, 255),
            (byte)Math.Clamp(mix(color.B, with.B), 0, 255));
    }
}
