using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
namespace CompanionOS.App.Interface.Controls;

/// <summary>
/// The interface's icon set — the same 24-unit stroke glyphs the design boards use,
/// one geometry per glyph, rendered on the same 24-unit artboard. Nothing is lorem-ipsumed: every
/// glyph here comes from the delivered HTML.
///
/// Full icon paths in the board markup are stored verbatim; the 20 px rail icons and
/// 16 px title-bar glyphs are the same shapes at different sizes because the boards size
/// one geometry family.
/// </summary>
public static class IconGlyph
{
    // Verbatim geometry from the boards (24-unit viewBox, 1.75 stroke, round caps).
    public const string Chat = "M7.9 20A9 9 0 1 0 4 16.1L2 22Z";
    public const string Memory = "M16 2h6v6h-6Z M2 16h6v6H2Z M10 2h6v6h-6Z " +
        "M5 16v-3a1 1 0 0 1 1-1h12a1 1 0 0 1 1 1v3 M12 12V8";
    public const string Body = "M19.4 7.85c-.05.32.06.65.29.88l1.57 1.57c.47.47.7 1.09.7 1.7s-.23 1.23-.7 1.7" +
        "l-1.61 1.61a.98.98 0 0 1-.84.28c-.47-.07-.8-.48-.97-.93a2.5 2.5 0 1 0-3.21 3.21c.45.17.86.5.93.97a.98.98 " +
        "0 0 1-.28.84l-1.61 1.61a2.4 2.4 0 0 1-1.7.7 2.4 2.4 0 0 1-1.7-.7l-1.57-1.57a1.03 1.03 0 0 0-.88-.29c-.49." +
        "07-.84.5-1.02.97a2.5 2.5 0 1 1-3.24-3.24c.46-.18.89-.53.97-1.02a1.03 1.03 0 0 0-.29-.88l-1.57-1.57A2.4 2.4 " +
        "0 0 1 2 12c0-.62.24-1.23.7-1.7l1.53-1.53c.24-.24.58-.35.92-.3.51.08.88.53 1.07 1.01a2.5 2.5 0 1 0 3.26-3.26" +
        "c-.48-.2-.93-.56-1.01-1.07-.05-.34.06-.68.3-.92l1.53-1.53A2.4 2.4 0 0 1 12 2c.62 0 1.23.24 1.7.7l1.57 1.57c." +
        "23.23.56.34.88.29.49-.07.84-.5 1.02-.97a2.5 2.5 0 1 1 3.24 3.24c-.46.18-.89.53-.97 1.02Z";
    public const string Persona = "M20 21a8 8 0 0 0-16 0 M12 13a5 5 0 1 0 0-10 5 5 0 0 0 0 10Z";
    public const string Settings = "M21 4h-7M10 4H3M21 12h-9M8 12H3M21 20h-5M12 20H3M14 2v4M8 10v4M16 18v4";
    public const string Sleep = "M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9Z";
    public const string OverlayFrame = "M3 4h18v16H3Z M12 12h7v5h-7Z";
    public const string Minimize = "M5 12h14";
    public const string Close = "M18 6 6 18M6 6l12 12";
    public const string Eye =
        "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Z";
    public const string Microphone = "M9 5a3 3 0 0 1 6 0v6a3 3 0 0 1-6 0Z M5 11a7 7 0 0 0 14 0 M12 18v4";
    public const string Send = "M12 19V5M5 12l7-7 7 7";
    public const string PlayPrevious = "M19 20 9 12l10-8ZM5 19V5";
    public const string Play = "M6 4l14 8-14 8Z";
    public const string PlayNext = "M5 4l10 8-10 8ZM19 5v14";
    public const string Pause = "M10 4H7v16h3ZM17 4h-3v16h3Z";
    public const string Stop = "M6 6h12v12H6Z";

    // Persona board: the rename pencil beside her name.
    public const string Rename = "M12 20h9M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z";

    // Memory board: the search bar.
    public const string Search = "M11 3a8 8 0 1 0 0 16a8 8 0 0 0 0-16Z M21 21l-4.35-4.35";

    // Boards' "More options": three round dots, drawn with zero-length strokes.
    public const string MoreOptions = "M5 12h.01M12 12h.01M19 12h.01";

    // Boards' chevrons: open lists point down, closed lists point right.
    public const string ChevronDown = "m6 9 6 6 6-6";
    public const string ChevronRight = "m9 18 6-6-6-6";

    // Memory board: the detail rail's actions.
    public const string Pin =
        "M12 17v5M9 10.8a2 2 0 0 1-1.1 1.8l-1.8.9A2 2 0 0 0 5 15.2V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1" +
        "v-.8a2 2 0 0 0-1.1-1.8l-1.8-.9a2 2 0 0 1-1.1-1.8V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 " +
        "0 0 1 1 1Z";
    public const string Trash =
        "M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2";

    // Body board: the shield marks tools that ask before they run.
    public const string Shield =
        "M20 13c0 5-3.5 7.5-7.7 8.97a1 1 0 0 1-.6 0C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 " +
        "4.5-1.2 6.2-2.7a1.17 1.17 0 0 1 1.6 0C14.5 3.8 17 5 19 5a1 1 0 0 1 1 1Z";

    // States board, notices: a round check for good news, a round alert for bad.
    // The check carries an ABSOLUTE move: a relative "m8 ..." after the circle's Z is
    // computed from the circle start point, flings the tick outside the path box and
    // letterboxes the whole icon — the "broken, off-centre circle" on notice toasts.
    private const string Circle24 = "M21 12a9 9 0 1 1-18 0a9 9 0 0 1 18 0Z";
    public const string CheckCircle = Circle24 + " M9.2 12.2l2.5 2.5 3.6-3.8";
    public const string Check16 = "M5 12.5l4.5 4.5L19 7";
    public const string AlertCircle = Circle24 + " M12 8v4M12 16h.01";
    public const string InfoCircle = Circle24 + " M12 11v5 M12 8h.01";

    // Task feedback cards: what the floating cards show while a module narrates work.
    public const string Download =
        "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M7 10l5 5 5-5 M12 15V3";
    public const string Package =
        "M21 8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4" +
        "a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16Z M3.3 7l8.7 5 8.7-5 M12 22V12";
    public const string PlayCircle = Circle24 + " M10 8.5l6 3.5-6 3.5Z";

    // Body board: module identity icons.
    public const string Monitor = "M2 3h20v14H2Z M8 21h8M12 17v4";
    public const string VoiceWave = "M5 13v6M9 9v14M13 5v14M17 9v14M21 13v6";
    public const string MemoryTools = Memory;
    public const string Clock14 =
        "M12 2a10 10 0 1 0 0 20a10 10 0 0 0 0-20Z M12 6v6l4 2";

    /// <summary>A stroke icon at the board's stroke weight (1.75 in a 24-unit box).</summary>
    public static Control Make(string geometry, double size, IBrush stroke)
    {
        // A dash-only glyph has no height, and Stretch=Uniform turns a zero-height
        // path into a sub-pixel speck — the minimize mark that showed as a stray dot.
        // It is drawn filled instead of stroked.
        if (geometry == Minimize)
        {
            return MakeDash(size, stroke);
        }

        Avalonia.Controls.Shapes.Path path = new()
        {
            Data = StreamGeometry.Parse(geometry),
            Width = 24,
            Height = 24,
            Stretch = Stretch.None,
            Stroke = stroke,
            StrokeThickness = 1.75,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            // Balance the two asymmetric shapes within the common 24-unit canvas.
            RenderTransform = geometry switch
            {
                Microphone => new TranslateTransform(0, -1.5),
                Play => new TranslateTransform(0.8, 0),
                _ => null
            }
        };

        // Keep the original 24-unit artboard. Stretching each path to its own tight
        // bounds made glyphs with different geometry bounds (especially the mic and
        // transport controls) appear off-centre and inconsistently sized.
        return new Viewbox
        {
            Width = size,
            Height = size,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
            Child = path
        };
    }

    /// <summary>Gets the stroke path from a rendered icon, if it has one.</summary>
    public static Avalonia.Controls.Shapes.Path? GetPath(Control glyph) => glyph switch
    {
        Avalonia.Controls.Shapes.Path path => path,
        Viewbox { Child: Avalonia.Controls.Shapes.Path path } => path,
        _ => null
    };

    /// <summary>
    /// The window minimize dash, drawn as a filled rounded bar the way the boards
    /// stroke it: 14 units wide, 1.75 tall, centred in the 20 px box.
    /// </summary>
    public static Avalonia.Controls.Shapes.Rectangle MakeDash(double size, IBrush brush)
    {
        return new Avalonia.Controls.Shapes.Rectangle
        {
            Width = size * 14.0 / 24.0,
            Height = Math.Max(size * 1.75 / 24.0, 1.5),
            RadiusX = 1,
            RadiusY = 1,
            Fill = brush,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
    }

    /// <summary>
    /// The "more options" three-dots mark, drawn as filled circles. The usual h.01
    /// stroke trick collapses under Stretch=Uniform into a sub-pixel hairline — that
    /// was the stray dot stuck at the top-right of whole pages.
    /// </summary>
    public static Control MakeDots(double size, IBrush brush)
    {
        Canvas canvas = new() { Width = size, Height = size };
        double radius = size * 0.085;
        foreach (double fraction in (double[])[5.0 / 24, 12.0 / 24, 19.0 / 24])
        {
            Avalonia.Controls.Shapes.Ellipse dot = new()
            {
                Width = radius * 2,
                Height = radius * 2,
                Fill = brush
            };
            Canvas.SetLeft(dot, fraction * size - radius);
            Canvas.SetTop(dot, size / 2 - radius);
            canvas.Children.Add(dot);
        }

        return canvas;
    }

    /// <summary>The orb's radial identity, exactly as the boards render it.</summary>
    public static Border Orb(double size, Color outer, Color core, double glowOpacity)
    {
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = OrbBrush(outer, core),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Color = Color.FromArgb((byte)(glowOpacity * 255), core.R, core.G, core.B),
                Blur = 20,
                Spread = 0,
                OffsetX = 0,
                OffsetY = 0
            })
        };
    }

    /// <summary>
    /// The orb's brush: a radial at 35 % x, 30 % y that eases from a lightened edge to
    /// the core colour at 72 % of the radius — the boards' gradient spec.
    /// </summary>
    public static IBrush OrbBrush(Color core) => OrbBrush(Lighten(core, 0.4), core);

    private static IBrush OrbBrush(Color outer, Color core)
    {
        return new RadialGradientBrush
        {
            Center = new RelativePoint(0.35, 0.30, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.35, 0.30, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.72, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.72, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(outer, 0),
                new GradientStop(core, 0.72)
            }
        };
    }

    /// <summary>Rebuilds an orb's background and glow for a new state colour.</summary>
    public static void RecolorOrb(Border orb, Color core, double glowOpacity)
    {
        orb.Background = OrbBrush(core);
        orb.BoxShadow = new BoxShadows(new BoxShadow
        {
            Color = Color.FromArgb((byte)(glowOpacity * 255), core.R, core.G, core.B),
            Blur = 20,
            Spread = 0,
            OffsetX = 0,
            OffsetY = 0
        });
    }

    private static Color Lighten(Color color, double amount) => Color.FromRgb(
        (byte)Math.Clamp(color.R + (255 - color.R) * amount, 0, 255),
        (byte)Math.Clamp(color.G + (255 - color.G) * amount, 0, 255),
        (byte)Math.Clamp(color.B + (255 - color.B) * amount, 0, 255));
}
