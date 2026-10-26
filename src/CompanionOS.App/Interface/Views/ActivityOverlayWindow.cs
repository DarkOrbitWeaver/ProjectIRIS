using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using CompanionOS.App.Interface.Controls;
using CompanionOS.App.Interface.Theme;

namespace CompanionOS.App.Interface.Views;

/// <summary>
/// A secondary, non-activating progress card. The call pill stays tiny and quiet;
/// this card appears only for a meaningful active turn and can be hidden independently.
/// </summary>
internal sealed class ActivityOverlayWindow : Window
{
    private const double CardWidth = 326;
    private const double CardHeight = 104;

    private readonly TextBlock _module;
    private readonly Path _moduleIcon;
    private readonly TextBlock _step;
    private readonly TextBlock _detail;
    private readonly Border _progressTrack;
    private readonly Border _progressFill;
    private readonly Button _hide;
    private readonly Button _open;
    private string _boundTurnId = string.Empty;
    private string _hiddenTurnId = string.Empty;

    public ActivityOverlayWindow(Action openMainWindow)
    {
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

        _module = Label(Palette.FontCaption, Palette.TextFaintBrush, FontWeight.SemiBold);
        _module.LetterSpacing = 0.9;
        _moduleIcon = (Path)IconGlyph.Make(ModuleIconGlyph.Resolve("body"), 15, Palette.AccentSoftBrush);
        _step = Label(Palette.FontEmphasis, Palette.TextPrimaryBrush, FontWeight.SemiBold);
        _detail = Label(Palette.FontBody, Palette.TextSecondaryBrush, FontWeight.Medium);

        _progressFill = new Border
        {
            Height = 2,
            Background = Palette.AccentSoftBrush,
            CornerRadius = new CornerRadius(1),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _progressTrack = new Border
        {
            Height = 2,
            Background = Palette.StrokeBrush,
            CornerRadius = new CornerRadius(1),
            Child = _progressFill,
            IsVisible = false
        };
        _progressTrack.SizeChanged += (_, _) => RenderProgress();

        _hide = IconButton(IconGlyph.Minimize, "Hide activity card", () =>
        {
            _hiddenTurnId = _boundTurnId;
            Hide();
        });
        _open = IconButton(IconGlyph.OverlayFrame, "Open Iris", openMainWindow);

        StackPanel moduleHeading = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _moduleIcon, _module }
        };
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        header.Children.Add(moduleHeading);
        Grid.SetColumn(_open, 1);
        header.Children.Add(_open);
        Grid.SetColumn(_hide, 2);
        header.Children.Add(_hide);

        StackPanel body = new()
        {
            Spacing = 5,
            Children = { header, _step, _detail, _progressTrack }
        };
        Content = new Border
        {
            Background = new SolidColorBrush(Palette.Elevated, 0.97),
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Palette.RadiusPanel),
            Padding = new Thickness(16, 12),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Color = Color.FromArgb(115, 0, 0, 0), Blur = 24, OffsetY = 8
            }),
            Child = body
        };
    }

    public bool Bind(ShellModel model)
    {
        if (!string.Equals(_boundTurnId, model.ActivityTurnId, StringComparison.Ordinal))
        {
            _boundTurnId = model.ActivityTurnId;
            _hiddenTurnId = string.Empty;
        }

        _module.Text = $"{model.ActivityModule.ToUpperInvariant()} · IN PROGRESS";
        _moduleIcon.Data = Avalonia.Media.StreamGeometry.Parse(ModuleIconGlyph.Resolve(model.ActivityIconKey));
        _step.Text = model.ActivityMessage;
        _detail.Text = model.ActivityDetail;
        bool hasProgress = model.ActivityProgress is { } progress &&
                           model.ActivityTotal is { } total && total > 0 &&
                           double.IsFinite(progress) && double.IsFinite(total);
        _progressTrack.IsVisible = hasProgress;
        if (hasProgress)
        {
            RenderProgress(model.ActivityProgress!.Value / model.ActivityTotal!.Value);
        }

        return _hiddenTurnId != _boundTurnId;
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

    private void RenderProgress(double? fraction = null)
    {
        double value = fraction ?? 0;
        _progressFill.Width = _progressTrack.Bounds.Width * Math.Clamp(value, 0, 1);
    }

    private static TextBlock Label(double size, IBrush foreground, FontWeight weight) => new()
    {
        FontFamily = Palette.UiFont,
        FontSize = size,
        FontWeight = weight,
        Foreground = foreground,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap
    };

    private static Button IconButton(string glyph, string label, Action action)
    {
        Button button = new()
        {
            Width = 28,
            Height = 26,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = IconGlyph.Make(glyph, 16, Palette.TextSecondaryBrush),
            Focusable = false
        };
        ToolTip.SetTip(button, label);
        button.Click += (_, _) => action();
        return button;
    }
}
