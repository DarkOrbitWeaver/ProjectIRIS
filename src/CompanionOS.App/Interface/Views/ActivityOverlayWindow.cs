using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CompanionOS.App.Interface.Controls;
using CompanionOS.App.Interface.Theme;
using CompanionOS.Core.Modules;

namespace CompanionOS.App.Interface.Views;

/// <summary>
/// A secondary, non-activating progress card. The call pill stays tiny and quiet;
/// this card appears only for a meaningful active turn and can be hidden independently.
/// </summary>
internal sealed class ActivityOverlayWindow : Window
{
    private const double CardWidth = 326;
    private const double CardHeight = 104;

    /// <summary>Transparent breathing room around the card so the drop shadow paints
    /// inside the window instead of getting clipped into a hard rim at the edges.</summary>
    private const double ShadowGutter = 32;

    private readonly TextBlock _module;
    private readonly Border _moduleIcon;
    private readonly TextBlock _step;
    private readonly TextBlock _detail;
    private readonly Border _progressTrack;
    private readonly Border _progressFill;
    private readonly Button _hide;
    private readonly Button _open;
    private readonly Button _cancel;
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
        Width = CardWidth + (ShadowGutter * 2);
        Height = CardHeight + (ShadowGutter * 2);

        _module = Label(Palette.FontCaption, Palette.TextFaintBrush, FontWeight.SemiBold);
        _module.LetterSpacing = 0.9;
        _moduleIcon = ModuleIconGlyph.Create("body", 15, Palette.AccentSoftBrush);
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
        _cancel = IconButton(IconGlyph.Stop, "Stop background task", () => { });
        _cancel.IsVisible = false;

        StackPanel moduleHeading = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _moduleIcon, _module }
        };
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        header.Children.Add(moduleHeading);
        Grid.SetColumn(_open, 1);
        header.Children.Add(_open);
        Grid.SetColumn(_cancel, 2);
        header.Children.Add(_cancel);
        Grid.SetColumn(_hide, 3);
        header.Children.Add(_hide);

        StackPanel body = new()
        {
            Spacing = 5,
            Children = { header, _step, _detail, _progressTrack }
        };
        Content = new Border
        {
            Margin = new Thickness(ShadowGutter),
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
        _cancel.IsVisible = false;
        ModuleIconGlyph.Update(_moduleIcon, model.ActivityIconKey, 15, Palette.AccentSoftBrush);
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

    public bool Bind(BackgroundTaskSnapshot task, IBackgroundTaskAccess tasks)
    {
        if (!string.Equals(_boundTurnId, task.Id, StringComparison.Ordinal))
        {
            _boundTurnId = task.Id;
            _hiddenTurnId = string.Empty;
        }

        bool live = task.State is BackgroundTaskState.Queued or BackgroundTaskState.Running or BackgroundTaskState.CancelRequested;
        _module.Text = $"{task.ModuleName.ToUpperInvariant()} · {(live ? "IN BACKGROUND" : task.State.ToString().ToUpperInvariant())}";
        ModuleIconGlyph.Update(_moduleIcon, task.IconKey, 15, Palette.AccentSoftBrush);
        _step.Text = task.CurrentStep;
        _detail.Text = task.Result ?? $"{task.ToolName} · {task.Id}";
        _cancel.IsVisible = live;
        _cancel.Click -= OnCancelBackgroundTask;
        _cancel.Click += OnCancelBackgroundTask;
        _boundTasks = tasks;
        _progressTrack.IsVisible = task.Progress is { } progress && task.Total is { } total && total > 0 &&
                                   double.IsFinite(progress) && double.IsFinite(total);
        if (_progressTrack.IsVisible)
        {
            RenderProgress(task.Progress!.Value / task.Total!.Value);
        }

        return _hiddenTurnId != _boundTurnId;
    }

    private IBackgroundTaskAccess? _boundTasks;

    private void OnCancelBackgroundTask(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (_boundTasks?.Cancel(_boundTurnId, out _) == true)
        {
            _cancel.IsEnabled = false;
        }
    }

    public void PlaceNear(PixelPoint pillPosition, int pillWidth, int pillHeight, PixelRect workArea,
        bool growsLeft)
    {
        double scale = RenderScaling > 0 ? RenderScaling : 1.0;
        int width = (int)Math.Ceiling(CardWidth * scale);
        int gutter = (int)Math.Ceiling(ShadowGutter * scale);
        int windowWidth = width + (gutter * 2);
        int windowHeight = (int)Math.Ceiling(CardHeight * scale) + (gutter * 2);
        int gap = (int)Math.Ceiling(10 * scale); // window edge to pill-edge breathing room

        // Coordinates are the card's horizontally, the window's vertically: the card
        // keeps the pill's shared edge, the window rectangle extends one gutter outward
        // so the drop shadow paints without touching the pill itself.
        int x = growsLeft ? pillPosition.X + pillWidth - width : pillPosition.X;
        int y = pillPosition.Y - windowHeight - gap;
        if (y < workArea.Y)
        {
            y = pillPosition.Y + pillHeight + gap;
        }

        Position = new PixelPoint(
            Math.Clamp(x - gutter, workArea.X, Math.Max(workArea.X, workArea.Right - windowWidth)),
            Math.Clamp(y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - windowHeight)));
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
