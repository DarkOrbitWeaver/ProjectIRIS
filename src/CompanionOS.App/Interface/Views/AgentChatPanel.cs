using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CompanionOS.App.Interface.Controls;
using CompanionOS.App.Interface.Theme;

namespace CompanionOS.App.Interface.Views;

internal sealed class AgentChatPanel : UserControl
{
    private readonly ShellModel _model;
    private readonly Action _activateWindow;
    private readonly TextBox _composer;
    private readonly Button _send;
    private readonly TextBlock _activity;
    private readonly TextBlock _detail;
    private readonly Border _activityDot;
    private readonly TextBlock _emptyState;
    private readonly ItemsControl _messages;
    private readonly ScrollViewer _transcript;

    public AgentChatPanel(ShellModel model, Action activateWindow)
    {
        _model = model;
        _activateWindow = activateWindow;
        _composer = new TextBox
        {
            Watermark = "Message Iris",
            AcceptsReturn = false,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = Palette.UiFont,
            FontSize = Palette.FontBody,
            Foreground = Palette.TextPrimaryBrush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _send = new Button
        {
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            Background = Palette.IrisBrush,
            BorderThickness = new Thickness(0),
            Content = IconGlyph.Make(IconGlyph.Send, 17, Brushes.White),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        _send.Click += (_, _) => SendOrStop();
        _composer.AddHandler(InputElement.KeyDownEvent, OnComposerKeyDown,
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        _composer.PointerPressed += (_, _) =>
        {
            _activateWindow();
            Dispatcher.UIThread.Post(() => _composer.Focus());
        };

        _activityDot = new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(4),
            Background = Palette.SuccessBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        _activity = Ui.Text("Ready", Palette.FontBody, Palette.TextPrimaryBrush, FontWeight.SemiBold);
        _detail = Ui.Text("Ask Iris anything", Palette.FontCaption, Palette.TextSecondaryBrush);
        _messages = new ItemsControl
        {
            ItemsSource = model.Messages,
            ItemTemplate = new FuncDataTemplate<ChatEntry>((entry, _) => BuildEntry(entry),
                supportsRecycling: false)
        };
        _emptyState = Ui.Text("Your conversation will appear here.\nTool activity and results show inline.",
            Palette.FontCaption, Palette.TextFaintBrush);
        _emptyState.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyState.VerticalAlignment = VerticalAlignment.Center;
        _emptyState.TextAlignment = TextAlignment.Center;
        _emptyState.Margin = new Thickness(18);
        _emptyState.IsVisible = model.Messages.Count == 0;
        Grid transcriptContent = new();
        transcriptContent.Children.Add(_messages);
        transcriptContent.Children.Add(_emptyState);
        _transcript = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = transcriptContent
        };

        StackPanel activityCopy = new() { Spacing = 2 };
        activityCopy.Children.Add(_activity);
        activityCopy.Children.Add(_detail);
        StackPanel activityHeader = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            Children = { _activityDot, activityCopy }
        };
        Border activityCard = new()
        {
            Background = Palette.ElevatedBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Palette.RadiusControl),
            Padding = new Thickness(12, 9),
            Child = activityHeader
        };

        Grid composerRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Border composerSurface = new()
        {
            Background = Palette.ElevatedBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Palette.RadiusControl),
            Padding = new Thickness(8, 0),
            Child = _composer
        };
        Grid.SetColumn(composerSurface, 0);
        composerRow.Children.Add(composerSurface);
        Grid.SetColumn(_send, 1);
        _send.Margin = new Thickness(8, 0, 0, 0);
        composerRow.Children.Add(_send);

        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        activityCard.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetRow(activityCard, 0);
        layout.Children.Add(activityCard);
        Grid.SetRow(_transcript, 1);
        layout.Children.Add(_transcript);
        composerRow.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(composerRow, 2);
        layout.Children.Add(composerRow);

        Content = layout;
        ClipToBounds = true;

        model.Messages.CollectionChanged += OnMessagesChanged;
        model.PropertyChanged += OnModelPropertyChanged;
        UpdateActivity();
        UpdateSendButton();
        DetachedFromVisualTree += (_, _) => model.Messages.CollectionChanged -= OnMessagesChanged;
    }

    private Control BuildEntry(ChatEntry entry)
    {
        TextBlock text = Ui.Text(string.Empty, Palette.FontCaption, Palette.TextPrimaryBrush);
        text.LineHeight = 17;
        text.TextWrapping = TextWrapping.NoWrap;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        TextBlock? toolStatus = null;
        TextBlock? toolDetail = null;

        void Refresh()
        {
            text.Text = entry.IsStreaming ? entry.Text + " ▍" : entry.Text;
            if (string.IsNullOrEmpty(text.Text) && entry.IsStreaming)
            {
                text.Text = "Thinking…";
            }

            if (toolStatus is not null)
            {
                string status = string.IsNullOrWhiteSpace(entry.ToolStatus)
                    ? entry.IsToolRunning ? "Running" : "Tool result"
                    : entry.ToolStatus;
                toolStatus.Text = status;
                toolStatus.Foreground = entry.IsToolRunning
                    ? Palette.AccentSoftBrush
                    : status.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                      status.Contains("fail", StringComparison.OrdinalIgnoreCase)
                        ? Palette.DangerBrush
                        : status.Contains("cancel", StringComparison.OrdinalIgnoreCase)
                            ? Palette.TextSecondaryBrush
                            : Palette.SuccessBrush;
            }

            if (toolDetail is not null)
            {
                toolDetail.Text = entry.Detail;
            }
        }

        Refresh();
        entry.PropertyChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(Refresh);
            Dispatcher.UIThread.Post(_transcript.ScrollToEnd, DispatcherPriority.Background);
        };

        if (entry.Role == ChatRole.Tool)
        {
            StackPanel toolCopy = new() { Spacing = 3 };
            text.FontWeight = FontWeight.SemiBold;
            Grid toolHeading = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            toolHeading.Children.Add(text);
            toolStatus = Ui.Text(string.Empty, Palette.FontCaption, Palette.TextFaintBrush, FontWeight.Medium);
            Grid.SetColumn(toolStatus, 1);
            toolHeading.Children.Add(toolStatus);
            toolCopy.Children.Add(toolHeading);
            if (!string.IsNullOrWhiteSpace(entry.Detail))
            {
                toolDetail = Ui.Text(entry.Detail, Palette.FontCaption, Palette.TextSecondaryBrush, wrap: false);
                toolDetail.TextTrimming = TextTrimming.CharacterEllipsis;
                toolCopy.Children.Add(toolDetail);
            }

            Grid toolRow = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
            toolRow.Children.Add(ModuleIconGlyph.Create(entry.IconKey, 16, Palette.AccentSoftBrush));
            Grid.SetColumn(toolCopy, 1);
            toolRow.Children.Add(toolCopy);
            Border toolCard = new()
            {
                Background = Palette.ElevatedBrush,
                BorderBrush = Palette.StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Palette.RadiusControl),
                Padding = new Thickness(8, 6),
                Child = toolRow
            };
            StackPanel toolEntry = new() { Margin = new Thickness(0, 1) };
            toolEntry.Children.Add(toolCard);
            Refresh();
            return toolEntry;
        }

        if (entry.Role == ChatRole.User)
        {
            return new Border
            {
                MaxWidth = 280,
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = new SolidColorBrush(Palette.Iris, 0.18),
                CornerRadius = new CornerRadius(12, 12, 3, 12),
                Padding = new Thickness(10, 8),
                Margin = new Thickness(28, 3, 0, 3),
                Child = text
            };
        }

        if (entry.Role == ChatRole.System)
        {
            text.Foreground = Palette.TextSecondaryBrush;
            return new Border
            {
                Padding = new Thickness(8, 5),
                Margin = new Thickness(0, 2),
                Child = text
            };
        }

        return new Border
        {
            MaxWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Palette.ElevatedBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3, 12, 12, 12),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 3, 22, 3),
            Child = text
        };
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() =>
        {
            _emptyState.IsVisible = _model.Messages.Count == 0;
            _transcript.ScrollToEnd();
        }, DispatcherPriority.Background);

    private void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(ShellModel.ActivityMessage)
            or nameof(ShellModel.ActivityModule)
            or nameof(ShellModel.ActivityDetail)
            or nameof(ShellModel.HasActiveWork)
            or nameof(ShellModel.IsTurnInProgress)
            or nameof(ShellModel.IsRuntimeReady))
        {
            Dispatcher.UIThread.Post(UpdateActivity);
            Dispatcher.UIThread.Post(UpdateSendButton);
        }
    }

    private void UpdateActivity()
    {
        bool active = _model.HasActiveWork;
        string title = active
            ? string.IsNullOrWhiteSpace(_model.ActivityMessage) ? "Working" : _model.ActivityMessage
            : "Ready";
        string detail = active
            ? string.Join(" · ", new[] { _model.ActivityModule, _model.ActivityDetail }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            : _model.StateText;

        _activity.Text = title;
        _detail.Text = detail;
        _activityDot.Background = active ? Palette.AccentSoftBrush : Palette.SuccessBrush;
    }

    private void UpdateSendButton()
    {
        bool canSend = _model.IsRuntimeReady;
        _send.IsEnabled = canSend || _model.IsTurnInProgress;
        _send.Content = IconGlyph.Make(
            _model.IsTurnInProgress ? IconGlyph.Stop : IconGlyph.Send,
            17,
            Brushes.White);
        ToolTip.SetTip(_send, _model.IsTurnInProgress ? "Stop Iris" : "Send message");
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter && !eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            eventArgs.Handled = true;
            SendOrStop();
        }
    }

    private void SendOrStop()
    {
        if (_model.IsTurnInProgress)
        {
            _model.Interrupt();
            return;
        }

        string text = _composer.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        _composer.Text = string.Empty;
        _ = _model.SendAsync(text);
    }
}
