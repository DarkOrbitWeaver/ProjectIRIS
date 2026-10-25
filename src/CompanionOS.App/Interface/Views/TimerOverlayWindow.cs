using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CompanionOS.App.Interface.Controls;
using CompanionOS.App.Interface.Theme;
using CompanionOS.Core.Events;
using CompanionOS.Core.Modules.BuiltIn;

namespace CompanionOS.App.Interface.Views;

/// <summary>A compact, focus-safe countdown surface. It is a control surface, not a
/// notification: the timer keeps its own state and this window merely mirrors it.</summary>
internal sealed class TimerOverlayWindow : Window
{
    private const double CardWidth = 248;
    private const double CardHeight = 144;

    private readonly ShellModel _model;
    private readonly TextBlock _label;
    private readonly TextBlock _remaining;
    private ActiveTimerModel? _timer;

    public TimerOverlayWindow(ShellModel model)
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

        _label = new TextBlock
        {
            FontFamily = Palette.UiFont,
            FontSize = Palette.FontEmphasis,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.TextPrimaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        _remaining = new TextBlock
        {
            FontFamily = Palette.UiFont,
            FontSize = Palette.FontTitle,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.AccentSoftBrush,
            TextWrapping = TextWrapping.NoWrap
        };

        Button minimize = ButtonFor(IconGlyph.Minimize, "Hide timer controls", HideForNow,
            Palette.TextSecondaryBrush);
        Grid heading = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        TextBlock caption = Ui.Text("TIMER", Palette.FontCaption, Palette.TextFaintBrush,
            FontWeight.Medium, wrap: false);
        caption.LetterSpacing = 0.88;
        heading.Children.Add(caption);
        Grid.SetColumn(minimize, 1);
        heading.Children.Add(minimize);

        StackPanel transport = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Children =
            {
                Ui.GhostButton("+5 min", () => Invoke("snooze_timer", "{\"minutes\":5}")),
                Ui.GhostButton("Cancel", () => Invoke("cancel_timers", "{}"))
            }
        };
        StackPanel stack = new() { Spacing = 8, Children = { heading, _label, _remaining, transport } };
        Content = new Border
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
            Child = stack
        };
    }

    public void Bind(ActiveTimerModel? timer)
    {
        _timer = timer;
        Render();
    }

    /// <summary>Timers live below the pill. Music occupies the available space above it,
    /// so two contextual cards never overlap.</summary>
    public void PlaceNear(PixelPoint pillPosition, int pillWidth, int pillHeight, PixelRect workArea,
        bool growsLeft)
    {
        double scale = RenderScaling > 0 ? RenderScaling : 1.0;
        int width = (int)Math.Ceiling(CardWidth * scale);
        int height = (int)Math.Ceiling(CardHeight * scale);
        int gap = (int)Math.Ceiling(10 * scale);
        int x = growsLeft ? pillPosition.X + pillWidth - width : pillPosition.X;
        int y = pillPosition.Y + pillHeight + gap;
        if (y + height > workArea.Bottom)
        {
            y = pillPosition.Y - height - gap;
        }

        Position = new PixelPoint(
            Math.Clamp(x, workArea.X, Math.Max(workArea.X, workArea.Right - width)),
            Math.Clamp(y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - height)));
    }

    private void HideForNow()
    {
        _model.Runtime.Settings.Current.Interface.ShowTimerOverlay = false;
        _model.Runtime.Settings.Save();
        _model.Runtime.Events.Publish(new SettingsChangedEvent());
        Hide();
    }

    private void Invoke(string tool, string json)
    {
        using System.Text.Json.JsonDocument arguments = System.Text.Json.JsonDocument.Parse(json);
        _ = _model.Runtime.Modules.ExecuteAsync(tool, arguments.RootElement.Clone(),
            Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
    }

    private void Render()
    {
        bool active = _timer?.IsActive == true;
        _label.Text = active ? _timer!.Label : "No timer running";
        _remaining.Text = active ? _timer!.RemainingText : string.Empty;
    }

    private static Button ButtonFor(string glyph, string label, Action action, IBrush brush)
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
