using Avalonia;
using Avalonia.Media;

namespace CompanionOS.App.Interface.Theme;

/// <summary>
/// The visual DNA of CompanionOS, in one file.
///
/// Every colour, radius, spacing step and font in the interface comes from here, taken
/// from the delivered design boards ("Iris — Desktop Companion Design"): near-black
/// surfaces #12131A/#1A1C24/#161821, iris violet identity, one-pixel strokes instead of
/// shadows, an Inter type scale, and small uppercase captions with wide tracking.
/// Changing her entire look means editing this file and nothing else.
/// </summary>
public static class Palette
{
    // Surfaces — the exact values from the design boards.
    public static readonly Color Background = Color.Parse("#12131A");
    public static readonly Color Surface = Color.Parse("#161821");
    public static readonly Color Elevated = Color.Parse("#1A1C24");
    public static readonly Color Stroke = Color.Parse("#262A36");
    public static readonly Color StrokeStrong = Color.Parse("#3F4656");

    // Identity — her iris violet, and the lighter lambent edge the orb gradient uses.
    public static readonly Color Iris = Color.Parse("#7C5CFF");
    public static readonly Color IrisEdge = Color.Parse("#A38DFF");
    public static readonly Color AccentSoft = Color.Parse("#8E70FF");

    // Signal
    public static readonly Color Mic = Color.Parse("#22D3EE");
    public static readonly Color Speech = Color.Parse("#A78BFA");

    public static readonly Color SpeechEdge = Color.Parse("#C4B5FD");

    /// <summary>The boards' accent words and links (#8E70FF): state captions, Details.</summary>
    public static readonly Color AccentText = Color.Parse("#8E70FF");
    public static readonly Color Success = Color.Parse("#34D399");
    public static readonly Color Warning = Color.Parse("#F59E0B");
    public static readonly Color Danger = Color.Parse("#FB7185");

    // The seven orb states, from the States board.
    public static readonly Color StateIdle = Iris;          // breathes, 4 s
    public static readonly Color StateListening = Mic;      // follows your voice
    public static readonly Color StateThinking = Iris;      // 270° sweep
    public static readonly Color StateSpeaking = Speech;    // pulses with speech
    public static readonly Color StateAsleep = StrokeStrong; // still, faint ring
    public static readonly Color StateGaming = Warning;      // pulse every 8 s
    public static readonly Color StateError = Danger;        // two shakes, hold

    // Text
    public static readonly Color TextPrimary = Color.Parse("#E7E9F2");
    public static readonly Color TextSecondary = Color.Parse("#9AA1B5");
    public static readonly Color TextFaint = Color.Parse("#5D6478");

    // Brushes (created once; brushes are immutable enough for our use)
    public static readonly IBrush BackgroundBrush = new SolidColorBrush(Background);
    public static readonly IBrush SurfaceBrush = new SolidColorBrush(Surface);
    public static readonly IBrush ElevatedBrush = new SolidColorBrush(Elevated);
    public static readonly IBrush StrokeBrush = new SolidColorBrush(Stroke);
    public static readonly IBrush StrokeStrongBrush = new SolidColorBrush(StrokeStrong);
    public static readonly IBrush TextPrimaryBrush = new SolidColorBrush(TextPrimary);
    public static readonly IBrush TextSecondaryBrush = new SolidColorBrush(TextSecondary);
    public static readonly IBrush TextFaintBrush = new SolidColorBrush(TextFaint);
    public static readonly IBrush IrisBrush = new SolidColorBrush(Iris);
    public static readonly IBrush IrisEdgeBrush = new SolidColorBrush(IrisEdge);
    public static readonly IBrush AccentSoftBrush = new SolidColorBrush(AccentSoft);
    public static readonly IBrush MicBrush = new SolidColorBrush(Mic);
    public static readonly IBrush SpeechBrush = new SolidColorBrush(Speech);
    public static readonly IBrush SuccessBrush = new SolidColorBrush(Success);
    public static readonly IBrush WarningBrush = new SolidColorBrush(Warning);
    public static readonly IBrush DangerBrush = new SolidColorBrush(Danger);

    // Older names kept as aliases — the listening colour is the mic/ear cyan.
    public static readonly Color Cyan = Mic;
    public static readonly IBrush CyanBrush = MicBrush;

    /// <summary>Her signature gradient: the orb's radial identity, edge to core.</summary>
    public static readonly IBrush IdentityGradient = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(IrisEdge, 0.0),
            new GradientStop(Iris, 0.72)
        }
    };

    /// <summary>Soft accent wash used behind focused elements — accent at partial alpha.</summary>
    public static IBrush AccentWash(double opacity) => new SolidColorBrush(Iris, opacity);

    /// <summary>The active rail item: accent tint at 14 % with accent-soft text.</summary>
    public static readonly IBrush RailActiveBrush = new SolidColorBrush(Iris, 0.14);

    // Typography — Inter first, matching the boards.
    public static readonly FontFamily UiFont = new("Inter, Segoe UI Variable Display, Segoe UI, sans-serif");
    public static readonly FontFamily MonoFont = new("Cascadia Code, Consolas, monospace");

    // Type scale from the boards: 11 / 13 / 15 / 20 / 28.
    public const double FontCaption = 11;
    public const double FontBody = 13;

    /// <summary>Notice toasts speak at 15/22 per the States board.</summary>
    public const double FontNotice = 15;
    public const double FontEmphasis = 15;
    public const double FontSection = 16;
    public const double FontTitle = 20;
    public const double FontPage = 28;

    // Spacing (strict 4 px grid)
    public const double SpaceXs = 4;
    public const double SpaceSm = 8;
    public const double SpaceMd = 12;
    public const double SpaceLg = 16;
    public const double SpaceXl = 24;
    public const double SpaceXxl = 32;

    // Radii — the boards use pill chips and quiet 12–16 px corners.
    public const double RadiusSmall = 6;
    public const double RadiusControl = 10;
    public const double RadiusCard = 12;
    public const double RadiusPanel = 16;
    public const double RadiusPill = 999;

    // Layout constants from the boards: title bar 40, status strip 32, rail 64, dock 280.
    public const double TitleBarHeight = 40;
    public const double StatusStripHeight = 32;
    public const double RailWidth = 64;
    public const double RailExpandedWidth = 232;
    public const double DockWidth = 280;

    /// <summary>1 px hairline pen, the only border style used in the product.</summary>
    public static readonly IPen HairlinePen = new Pen(StrokeBrush, 1);

    /// <summary>Accent pen for the focused or active element.</summary>
    public static IPen AccentPen(double thickness = 1.2) => new Pen(IrisBrush, thickness);
}
