using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CompanionOS.App.Interface.Theme;
using CompanionOS.Core.Modules;

namespace CompanionOS.App.Interface.Views;

/// <summary>High-priority contextual permission surface. It never steals keyboard
/// focus from a game, but it gives a pending guarded action a clear visible answer.</summary>
internal sealed class PermissionOverlayWindow : Window
{
    private const double CardWidth = 356;
    private const double CardHeight = 190;

    private readonly ShellModel _model;
    private readonly TextBlock _question;
    private readonly TextBlock _caption;
    private readonly Button _alwaysButton;

    public PermissionOverlayWindow(ShellModel model)
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

        _question = new TextBlock
        {
            FontFamily = Palette.UiFont,
            FontSize = Palette.FontBody,
            LineHeight = 20,
            Foreground = Palette.TextPrimaryBrush,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 64
        };
        _alwaysButton = Ui.PrimaryButton("Always", () => _model.AnswerPermission(PermissionDecision.AllowAlways));
        _caption = Ui.Caption(string.Empty);
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children =
            {
                Ui.GhostButton("Deny", () => _model.AnswerPermission(PermissionDecision.Deny)),
                Ui.GhostButton("Once", () => _model.AnswerPermission(PermissionDecision.AllowOnce)),
                _alwaysButton
            }
        };
        TextBlock caption = Ui.Text("NEEDS YOUR APPROVAL", Palette.FontCaption, Palette.WarningBrush,
            FontWeight.Medium, wrap: false);
        caption.LetterSpacing = 0.88;

        Content = new Border
        {
            Background = new SolidColorBrush(Palette.Elevated, 0.98),
            BorderBrush = Palette.WarningBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Palette.RadiusPanel),
            Padding = new Thickness(16, 14),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Color = Color.FromArgb(125, 0, 0, 0), Blur = 26, OffsetY = 8
            }),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    caption,
                    _question,
                    _caption,
                    buttons
                }
            }
        };
    }

    public void Bind(PermissionRequest? request)
    {
        _question.Text = request?.Question ?? string.Empty;
        _alwaysButton.IsVisible = request?.CanAlwaysAllow ?? false;
        _caption.Text = request?.CanAlwaysAllow == true
            ? "Expires in 15 seconds. Always remembers the shown capability for this module."
            : "This action requires your approval each time. Expires in 15 seconds.";
    }

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
}
