using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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

    private readonly ShellModel _model;
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
        Width = CardWidth;
        Height = CardHeight;

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
        Border card = new()
        {
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
        Content = card;
    }

    public void Bind(NowPlayingModel? nowPlaying)
    {
        _nowPlaying = nowPlaying;
        Render();
    }

    /// <summary>Anchors the card just above the pill when possible, otherwise below it.
    /// The card aligns with the screen edge the pill grows away from, so neither surface
    /// jumps inward on a multi-monitor desktop.</summary>
    public void PlaceNear(PixelPoint pillPosition, int pillWidth, int pillHeight, PixelRect workArea,
        bool growsLeft)
    {
        double scale = RenderScaling > 0 ? RenderScaling : 1.0;
        int width = (int)Math.Ceiling(CardWidth * scale);
        int height = (int)Math.Ceiling(CardHeight * scale);
        int gap = (int)Math.Ceiling(10 * scale);
        int x = growsLeft ? pillPosition.X + pillWidth - width : pillPosition.X;
        int y = pillPosition.Y - height - gap;
        if (y < workArea.Y)
        {
            y = pillPosition.Y + pillHeight + gap;
        }

        Position = new PixelPoint(
            Math.Clamp(x, workArea.X, Math.Max(workArea.X, workArea.Right - width)),
            Math.Clamp(y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - height)));
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
