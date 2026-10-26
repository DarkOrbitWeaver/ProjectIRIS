using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CompanionOS.App.Interface.Controls;
using CompanionOS.App.Interface.Theme;
using CompanionOS.Core.Events;
using CompanionOS.Core.Modules;

namespace CompanionOS.App.Interface.Views;

/// <summary>
/// Shell-owned floating UI for a module's validated overlay surface. It appears only
/// while that module is doing work, never claims progress the module did not report,
/// and stays independently dismissible for the current task.
/// </summary>
internal sealed class ModuleSurfaceOverlayWindow : Window
{
    private const double CardWidth = 368;
    private const double CardHeight = 410;
    private static readonly TimeSpan CompletedHold = TimeSpan.FromMilliseconds(1800);

    private readonly ModuleSurface _surface;
    private readonly TextBlock _status;
    private readonly Dictionary<string, Control> _elementControls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Control> _elementHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _elementLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Border _progressTrack;
    private readonly Border _progressFill;
    private DispatcherTimer? _completedTimer;
    private string _boundTurnId = string.Empty;
    private string _hiddenTurnId = string.Empty;
    private double _progressFraction;

    public string ModuleId => _surface.ModuleId;
    public string SurfaceId => _surface.Id;
    public bool IsCompletionHold => _completedTimer?.IsEnabled == true;
    public bool IsHiddenFor(string turnId) => _hiddenTurnId == turnId;

    public ModuleSurfaceOverlayWindow(ShellModel model, ModuleSurface surface)
    {
        _surface = surface;
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

        _status = new TextBlock
        {
            FontFamily = Palette.UiFont,
            FontSize = Palette.FontBody,
            Foreground = Palette.TextSecondaryBrush,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 48
        };
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

        Button hide = new()
        {
            Content = IconGlyph.Make(IconGlyph.Minimize, 15, Palette.TextSecondaryBrush),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(8)
        };
        ToolTip.SetTip(hide, "Hide this module overlay until its next task");
        hide.Click += (_, _) =>
        {
            _hiddenTurnId = _boundTurnId;
            _completedTimer?.Stop();
            Hide();
        };

        StackPanel heading = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                IconGlyph.Make(ModuleIconGlyph.Resolve(surface.IconKey), 20, Palette.AccentSoftBrush),
                Ui.Text(surface.Title, Palette.FontEmphasis, Palette.TextPrimaryBrush,
                    FontWeight.SemiBold, wrap: false)
            }
        };
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(heading);
        Grid.SetColumn(hide, 1);
        header.Children.Add(hide);

        Dictionary<string, Func<string>> inputs = new(StringComparer.OrdinalIgnoreCase);
        StackPanel controls = new() { Spacing = 10 };
        foreach (ModuleSurfaceElement element in surface.Elements)
        {
            switch (element.Type)
            {
                case ModuleSurfaceElementType.Text:
                    TextBlock text = Ui.Text(element.Label, Palette.FontBody, Palette.TextSecondaryBrush, wrap: true);
                    _elementControls[element.Id] = text;
                    _elementHosts[element.Id] = text;
                    controls.Children.Add(text);
                    break;

                case ModuleSurfaceElementType.Input:
                    TextBox input = new()
                    {
                        Watermark = element.Placeholder,
                        MaxLength = 4096,
                        FontFamily = Palette.UiFont,
                        FontSize = Palette.FontBody,
                        Foreground = Palette.TextPrimaryBrush,
                        Background = Palette.SurfaceBrush,
                        BorderBrush = Palette.StrokeBrush,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(Palette.RadiusControl),
                        Padding = new Thickness(10, 8)
                    };
                    inputs[element.Name] = () => input.Text ?? string.Empty;
                    StackPanel field = new() { Spacing = 5 };
                    TextBlock inputLabel = Ui.Caption(element.Label);
                    _elementLabels[element.Id] = inputLabel;
                    _elementControls[element.Id] = input;
                    _elementHosts[element.Id] = field;
                    field.Children.Add(inputLabel);
                    field.Children.Add(input);
                    controls.Children.Add(field);
                    break;

                case ModuleSurfaceElementType.Select:
                    ComboBox select = new()
                    {
                        FontFamily = Palette.UiFont,
                        FontSize = Palette.FontBody,
                        Foreground = Palette.TextPrimaryBrush,
                        Background = Palette.SurfaceBrush,
                        BorderBrush = Palette.StrokeBrush,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(Palette.RadiusControl),
                        Padding = new Thickness(8, 5)
                    };
                    foreach (ModuleSurfaceOption option in element.Options)
                    {
                        select.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
                    }
                    if (select.Items.Count > 0)
                    {
                        select.SelectedIndex = 0;
                    }
                    inputs[element.Name] = () => (select.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
                    StackPanel choiceField = new() { Spacing = 5 };
                    TextBlock selectLabel = Ui.Caption(element.Label);
                    _elementLabels[element.Id] = selectLabel;
                    _elementControls[element.Id] = select;
                    _elementHosts[element.Id] = choiceField;
                    choiceField.Children.Add(selectLabel);
                    choiceField.Children.Add(select);
                    controls.Children.Add(choiceField);
                    break;

                case ModuleSurfaceElementType.Button:
                    Button action = Ui.PrimaryButton(element.Label, () =>
                    {
                        Dictionary<string, string> values = inputs.ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value(),
                            StringComparer.OrdinalIgnoreCase);
                        _ = model.ExecuteModuleSurfaceActionAsync(surface, element, values);
                    }, wide: true);
                    _elementControls[element.Id] = action;
                    _elementHosts[element.Id] = action;
                    controls.Children.Add(action);
                    break;
            }
        }

        ScrollViewer scroll = new()
        {
            MaxHeight = 282,
            Content = controls
        };
        StackPanel content = new()
        {
            Spacing = 10,
            Children = { header, _status, _progressTrack, scroll }
        };
        Content = new Border
        {
            Background = new SolidColorBrush(Palette.Elevated, 0.98),
            BorderBrush = Palette.AccentSoftBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Palette.RadiusPanel),
            Padding = new Thickness(16),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Color = Color.FromArgb(115, 0, 0, 0),
                Blur = 24,
                OffsetY = 7
            }),
            Child = content
        };

        _completedTimer = new DispatcherTimer(CompletedHold, DispatcherPriority.Background, (_, _) =>
        {
            _completedTimer?.Stop();
            Hide();
        });
        _completedTimer?.Stop();
    }

    public bool BindActivity(string turnId, string message)
    {
        _completedTimer?.Stop();
        if (!string.Equals(_boundTurnId, turnId, StringComparison.Ordinal))
        {
            _boundTurnId = turnId;
            _progressTrack.IsVisible = false;
            _progressFraction = 0;
        }
        _status.Text = ClipDisplayText(message);
        return _hiddenTurnId != turnId;
    }

    public void ApplyState(ModuleSurfaceStateChangedEvent update)
    {
        if (!string.Equals(update.ModuleId, ModuleId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(update.SurfaceId, SurfaceId, StringComparison.OrdinalIgnoreCase) ||
            !_elementControls.TryGetValue(update.ElementId, out Control? control))
        {
            return;
        }

        if (update.Label is { } label)
        {
            if (_elementLabels.TryGetValue(update.ElementId, out TextBlock? fieldLabel))
            {
                fieldLabel.Text = label;
            }
            else if (control is TextBlock text)
            {
                text.Text = label;
            }
            else if (control is Button button && button.Content is TextBlock buttonLabel)
            {
                buttonLabel.Text = label;
            }
        }

        if (update.Options is { } options && control is ComboBox dynamicSelect)
        {
            string? selectedValue = update.Value ?? (dynamicSelect.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            dynamicSelect.Items.Clear();
            foreach (ModuleSurfaceOption option in options)
            {
                dynamicSelect.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
            }
            dynamicSelect.SelectedItem = dynamicSelect.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), selectedValue, StringComparison.Ordinal));
            if (dynamicSelect.SelectedItem is null && dynamicSelect.Items.Count > 0)
            {
                dynamicSelect.SelectedIndex = 0;
            }
        }
        else if (update.Value is { } value)
        {
            switch (control)
            {
                case TextBlock text:
                    text.Text = value;
                    break;
                case TextBox input:
                    input.Text = value;
                    break;
                case ComboBox select:
                    select.SelectedItem = select.Items
                        .OfType<ComboBoxItem>()
                        .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), value, StringComparison.Ordinal));
                    break;
            }
        }

        if (update.IsEnabled is { } isEnabled)
        {
            control.IsEnabled = isEnabled;
        }
        if (update.IsVisible is { } isVisible && _elementHosts.TryGetValue(update.ElementId, out Control? host))
        {
            host.IsVisible = isVisible;
        }
    }

    public bool BindProgress(string turnId, string message, double? progress, double? total)
    {
        bool shouldShow = BindActivity(turnId, message);
        bool valid = progress is { } amount && total is { } maximum &&
                     double.IsFinite(amount) && double.IsFinite(maximum) && maximum > 0 && amount >= 0;
        _progressTrack.IsVisible = valid;
        if (valid)
        {
            _progressFraction = Math.Clamp(progress!.Value / total!.Value, 0, 1);
            RenderProgress();
        }
        return shouldShow;
    }

    public bool BindCompletion(string turnId, string message)
    {
        bool shouldShow = BindActivity(turnId, message);
        if (shouldShow)
        {
            _completedTimer?.Stop();
            _completedTimer?.Start();
        }
        return shouldShow;
    }

    public void RenderProgress()
    {
        if (_progressTrack.IsVisible && _progressTrack.Bounds.Width > 0)
        {
            _progressFill.Width = _progressTrack.Bounds.Width * _progressFraction;
        }
    }

    public void PlaceNear(PixelPoint pillPosition, int pillWidth, int pillHeight,
        PixelRect workArea, bool growsLeft, int stackIndex = 0)
    {
        double scale = RenderScaling > 0 ? RenderScaling : 1.0;
        int width = (int)Math.Ceiling(CardWidth * scale);
        int height = (int)Math.Ceiling(CardHeight * scale);
        int gap = (int)Math.Ceiling(10 * scale);
        int baseX = growsLeft ? pillPosition.X + pillWidth - width : pillPosition.X;
        int baseY = pillPosition.Y - height - gap;
        bool abovePill = baseY >= workArea.Y;
        if (!abovePill)
        {
            baseY = pillPosition.Y + pillHeight + gap;
        }

        int direction = growsLeft ? -1 : 1;
        int availableInDirection = direction > 0
            ? workArea.Right - baseX
            : baseX + width - workArea.X;
        int columns = Math.Max(1, (availableInDirection + gap) / (width + gap));
        int column = stackIndex % columns;
        int row = stackIndex / columns;
        int x = baseX + direction * column * (width + gap);
        int y = row == 0
            ? baseY
            : abovePill
                ? pillPosition.Y + pillHeight + gap + (row - 1) * (height + gap)
                : baseY - row * (height + gap);

        if (x < workArea.X || x + width > workArea.Right)
        {
            x = baseX - direction * column * (width + gap);
        }

        Position = new PixelPoint(
            Math.Clamp(x, workArea.X, Math.Max(workArea.X, workArea.Right - width)),
            Math.Clamp(y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - height)));
    }

    private static string ClipDisplayText(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "Working" : message.Length <= 240 ? message : message[..240];
}
