using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using CompanionOS.App.Interface.Controls;
using CompanionOS.App.Interface.Theme;
using CompanionOS.Core.Events;
using CompanionOS.Core.Modules.BuiltIn;

namespace CompanionOS.App.Interface.Views;

/// <summary>
/// The contextual half of Iris' desk presence. The quiet pill remains the default;
/// this card exists only while there is something worth controlling. It deliberately
/// contains transport rather than artwork: a made-up album cover is decoration, while
/// title, elapsed time and controls are the truth the user needs at a glance.
/// </summary>
internal sealed class MusicOverlayWindow : Window
{
    private const double CardWidth = 292;
    private const double CardHeight = 164;

    /// <summary>Transparent breathing room around the card. The drop shadow (blur 24,
    /// offset 8) must be able to paint inside the window; a window hugging the card
    /// exactly clips it into the hard dark rim and corner blocks the screenshots show.</summary>
    private const double ShadowGutter = 32;

    /// <summary>Few pixels of margin between the docked card edge and the screen edge.</summary>
    private const double DockMargin = 8;

    /// <summary>A remembered position counts as docked only within this of the right edge.</summary>
    private const double RightDockTolerance = 72;

    /// <summary>Dropping the card this close to the right edge snaps it docked.</summary>
    private const double RightDockSnap = 40;

    private readonly ShellModel _model;
    private readonly Border _card;
    private readonly TextBlock _title;
    private readonly TextBlock _detail;
    private readonly Border _progress;
    private readonly Border _progressFill;
    private readonly Button _playPause;
    private NowPlayingModel? _nowPlaying;

    public MusicOverlayWindow(ShellModel model)
    {
        _model = model;
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = CardWidth + (ShadowGutter * 2);
        Height = CardHeight + (ShadowGutter * 2);

        _title = new TextBlock
        {
            FontFamily = Palette.UiFont,
            FontSize = Palette.FontEmphasis,
            FontWeight = FontWeight.SemiBold,
            LineHeight = 22,
            Foreground = Palette.TextPrimaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        _detail = new TextBlock
        {
            FontFamily = Palette.UiFont,
            FontSize = Palette.FontBody,
            FontWeight = FontWeight.Medium,
            LineHeight = 18,
            Foreground = Palette.TextSecondaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };

        _progressFill = new Border
        {
            Height = 2,
            Background = Palette.AccentSoftBrush,
            CornerRadius = new CornerRadius(1),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _progress = new Border
        {
            Height = 2,
            Background = Palette.StrokeBrush,
            CornerRadius = new CornerRadius(1),
            Child = _progressFill
        };
        _progress.SizeChanged += (_, _) => Render();

        _playPause = TransportButton(IconGlyph.Play, "Play or pause", TogglePlayPause,
            Palette.TextPrimaryBrush);
        Button minimize = TransportButton(IconGlyph.Minimize, "Hide music controls", HideForNow,
            Palette.TextSecondaryBrush);

        Grid heading = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        TextBlock label = Ui.Text("NOW PLAYING", Palette.FontCaption, Palette.TextFaintBrush,
            FontWeight.Medium, wrap: false);
        label.LetterSpacing = 0.88;
        label.LineHeight = 16;
        heading.Children.Add(label);
        Grid.SetColumn(minimize, 1);
        heading.Children.Add(minimize);

        StackPanel transport = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 18,
            Children =
            {
                TransportButton(IconGlyph.PlayPrevious, "Previous track", () => Invoke("previous_track"),
                    Palette.TextSecondaryBrush),
                _playPause,
                TransportButton(IconGlyph.Stop, "Stop music", () => Invoke("stop_music"), Palette.TextSecondaryBrush),
                TransportButton(IconGlyph.PlayNext, "Next track", () => Invoke("next_track"),
                    Palette.TextSecondaryBrush)
            }
        };

        StackPanel content = new()
        {
            Spacing = 8,
            Children = { heading, _title, _detail, _progress, transport }
        };
        _card = new Border
        {
            // Gutter margin: the card floats inside the window so the box shadow keeps
            // painting freely and the transparent corners stay truly transparent.
            Margin = new Thickness(ShadowGutter),
            Width = CardWidth,
            Height = CardHeight,
            Background = new SolidColorBrush(Palette.Elevated, 0.96),
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Palette.RadiusPanel),
            Padding = new Thickness(16, 14),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Color = Color.FromArgb(115, 0, 0, 0), Blur = 24, OffsetY = 8
            }),
            Child = content
        };
        Content = _card;

        // Dragging belongs to the card body, never to one of its transport buttons.
        _card.PointerPressed += (_, eventArgs) =>
        {
            if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
                IsInsideButton(eventArgs.Source))
            {
                return;
            }

            try
            {
                BeginMoveDrag(eventArgs);
            }
            catch (InvalidOperationException)
            {
                // The platform refused the drag (the press was already released); the
                // card simply stays where it is.
            }
            finally
            {
                SaveDraggedPosition();
            }
        };
    }

    public void Bind(NowPlayingModel? nowPlaying)
    {
        _nowPlaying = nowPlaying;
        Render();
    }

    /// <summary>
    /// Anchors the card to the screen edge the pill shares, max right with a few pixels
    /// of margin, and spawns it about a fifth of the work area down from the top — above
    /// her pill, which lives in the bottom corner. Where the card was dragged and let
    /// docked again, that remembered spot wins. The card keeps tracking the pill's edge
    /// horizontally so the two never drift across the desktop.
    /// </summary>
    public void PlaceNear(PixelPoint pillPosition, int pillWidth, int pillHeight, PixelRect workArea,
        bool growsLeft)
    {
        double scale = RenderScaling > 0 ? RenderScaling : 1.0;
        int cardWidth = (int)Math.Ceiling(CardWidth * scale);
        int cardHeight = (int)Math.Ceiling(CardHeight * scale);
        int gutter = (int)Math.Ceiling(ShadowGutter * scale);
        int margin = (int)Math.Ceiling(DockMargin * scale);
        int tolerance = (int)Math.Ceiling(RightDockTolerance * scale);
        int windowWidth = cardWidth + (gutter * 2);
        int windowHeight = cardHeight + (gutter * 2);

        // A remembered docked spot wins. Mid-screen leftovers (e.g. after a resolution
        // change) are ignored, so the card always wakes up docked.
        string remembered = _model.Runtime.Settings.Current.Interface.MusicOverlayPositionPx;
        if (remembered.Split(',') is { Length: 2 } parts &&
            int.TryParse(parts[0], out int rememberedX) &&
            int.TryParse(parts[1], out int rememberedY) &&
            Math.Abs((rememberedX + windowWidth) - (workArea.Right - margin)) <= tolerance)
        {
            Position = new PixelPoint(
                Math.Clamp(rememberedX, workArea.X, Math.Max(workArea.X, workArea.Right - windowWidth)),
                Math.Clamp(rememberedY, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - windowHeight)));
            return;
        }

        // Default: max right, mirroring the pill's edge, top-right ~20 % from the top.
        int x = growsLeft
            ? pillPosition.X + pillWidth - cardWidth - gutter
            : pillPosition.X - gutter;
        int y = workArea.Y + (int)(workArea.Height * 0.20);

        int cardY = y + gutter;                      // where the card visual would start
        int pillTop = pillPosition.Y;
        int pillBottom = pillPosition.Y + pillHeight;
        bool overlapsPill = cardY < pillBottom && cardY + cardHeight > pillTop;
        if (overlapsPill)
        {
            // The pill was dragged up; slip the card just above it, window top edge
            // sitting the usual gap over the pill's top.
            y = pillPosition.Y - windowHeight - (int)Math.Ceiling(10 * scale);
        }

        Position = new PixelPoint(
            Math.Clamp(x, workArea.X, Math.Max(workArea.X, workArea.Right - windowWidth)),
            Math.Clamp(y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - windowHeight)));
    }

    private void TogglePlayPause()
    {
        Invoke(_nowPlaying?.Status == "Playing" ? "pause_music" : "resume_music");
    }

    private void HideForNow()
    {
        _model.Runtime.Settings.Current.Interface.ShowMusicOverlay = false;
        _model.Runtime.Settings.Save();
        _model.Runtime.Events.Publish(new SettingsChangedEvent());
        Hide();
    }

    private void Invoke(string tool)
    {
        using System.Text.Json.JsonDocument arguments = System.Text.Json.JsonDocument.Parse("{}");
        _ = _model.Runtime.Modules.ExecuteAsync(tool, arguments.RootElement.Clone(),
            Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
    }

    /// <summary>Remembers a dragged spot, but only after snapping it back to the right
    /// edge when the drop lands near it, so "docked" stays the natural resting state.</summary>
    private void SaveDraggedPosition()
    {
        try
        {
            double scale = RenderScaling > 0 ? RenderScaling : 1.0;
            if (WorkArea() is { } work)
            {
                int windowWidth = (int)Math.Ceiling((CardWidth + ShadowGutter * 2) * scale);
                int margin = (int)Math.Ceiling(DockMargin * scale);
                int snap = (int)Math.Ceiling(RightDockSnap * scale);
                int dockedX = work.Right - windowWidth - margin;
                if (dockedX - Position.X <= snap)
                {
                    Position = new PixelPoint(Math.Max(work.X, dockedX), Position.Y);
                }
            }

            _model.Runtime.Settings.Current.Interface.MusicOverlayPositionPx =
                $"{Position.X},{Position.Y}";
            _model.Runtime.Settings.Save();
            _model.Runtime.Events.Publish(new SettingsChangedEvent());
        }
        catch
        {
            // Losing a remembered card spot must never crash the player.
        }
    }

    private PixelRect? WorkArea()
    {
        Screen? screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        return screen?.WorkingArea;
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

            visual = visual.Parent as Visual;
        }

        return false;
    }

    private void Render()
    {
        bool active = _nowPlaying is not null && _nowPlaying.Status is "Playing" or "Paused";
        _title.Text = active ? _nowPlaying!.Title : "Nothing playing";
        _detail.Text = active ? $"{_nowPlaying!.Status} · {_nowPlaying.PositionText}" : "Ask Iris to play something";
        _playPause.Content = IconGlyph.Make(_nowPlaying?.Status == "Playing" ? IconGlyph.Pause : IconGlyph.Play,
            20, Palette.TextPrimaryBrush);
        _progressFill.Width = _progress.Bounds.Width * Math.Clamp(_nowPlaying?.Progress ?? 0, 0, 1);
    }

    private static Button TransportButton(string glyph, string label, Action action, IBrush brush)
    {
        Button button = new()
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = IconGlyph.Make(glyph, 20, brush),
            Focusable = false
        };
        ToolTip.SetTip(button, label);
        button.Click += (_, _) => action();
        return button;
    }
}
