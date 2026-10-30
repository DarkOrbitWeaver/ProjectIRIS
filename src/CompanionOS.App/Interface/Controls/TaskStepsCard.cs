using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CompanionOS.App.Interface.Theme;

namespace CompanionOS.App.Interface.Controls;

/// <summary>
/// One quiet card under her reply that lists what she is doing as short rows:
///
///   ✓  Scan Downloads                      ▣
///   ✓  Sort 48 files into folders          ▣
///   ◌  Open mail and attach                ⌕
///   ○  Send to Sam                         ➤
///
/// Left: state glyph (done / spinning / waiting / failed / denied / answered).
/// Middle: plain words from the tool's presentation ("Resumed your music").
/// Right: the module's icon instead of a word. A tiny shield appears before it when
/// the step needed your OK; hover it for how (button / voice) and when.
/// Failures add one muted red line; nothing else ever adds a second line.
/// </summary>
public sealed class TaskStepsCard : Border, IDisposable
{
    private readonly ChatEntry _entry;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly Dictionary<ToolStep, StepRow> _rowByStep = new();
    private bool _disposed;

    public TaskStepsCard(ChatEntry entry, bool compact = false)
    {
        _entry = entry;
        Background = new SolidColorBrush(Palette.Elevated, 0.72);
        BorderBrush = new SolidColorBrush(Palette.Stroke, 0.9);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(Palette.RadiusCard);
        Padding = compact ? new Thickness(8, 6) : new Thickness(10, 8, 10, 8);
        Margin = compact ? new Thickness(0) : new Thickness(36, 2, 48, 6);
        MaxWidth = compact ? 294 : 440;
        HorizontalAlignment = HorizontalAlignment.Left;
        Child = _rows;

        foreach (ToolStep step in entry.Steps)
        {
            AddRow(step);
        }

        entry.Steps.CollectionChanged += OnStepsChanged;
    }

    private void OnStepsChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (eventArgs.Action == NotifyCollectionChangedAction.Reset)
            {
                _rows.Children.Clear();
                foreach (StepRow row in _rowByStep.Values)
                {
                    row.Dispose();
                }
                _rowByStep.Clear();
                foreach (ToolStep step in _entry.Steps)
                {
                    AddRow(step);
                }
                return;
            }

            if (eventArgs.NewItems is not null)
            {
                foreach (ToolStep step in eventArgs.NewItems.OfType<ToolStep>())
                {
                    AddRow(step);
                }
            }

            if (eventArgs.OldItems is not null)
            {
                foreach (ToolStep step in eventArgs.OldItems.OfType<ToolStep>())
                {
                    if (_rowByStep.Remove(step, out StepRow? row))
                    {
                        _rows.Children.Remove(row);
                        row.Dispose();
                    }
                }
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _entry.Steps.CollectionChanged -= OnStepsChanged;
        _rows.Children.Clear();
        foreach (StepRow row in _rowByStep.Values)
        {
            row.Dispose();
        }
        _rowByStep.Clear();
    }

    private void AddRow(ToolStep step)
    {
        if (_rowByStep.ContainsKey(step))
        {
            return;
        }
        StepRow row = new(step);
        _rowByStep[step] = row;
        _rows.Children.Add(row);
    }

    /// <summary>One line of the card. Owns its own spinner timer while running.</summary>
    private sealed class StepRow : Grid, IDisposable
    {
        private readonly ToolStep _step;
        private readonly Panel _glyphHost = new() { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _label;
        private readonly TextBlock _failure;
        private readonly Border _shield;
        private readonly Border _moduleIcon;
        private readonly RotateTransform _spin = new();
        private readonly PropertyChangedEventHandler _stepChangedHandler;
        private DispatcherTimer? _timer;
        private bool _disposed;

        public StepRow(ToolStep step)
        {
            _step = step;
            ColumnDefinitions = new ColumnDefinitions("16,*,Auto,Auto");
            ColumnSpacing = 10;
            RowDefinitions = new RowDefinitions("Auto,Auto");
            MinHeight = 26;

            _label = new TextBlock
            {
                FontFamily = Palette.UiFont,
                FontSize = Palette.FontBody,
                LineHeight = 18,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap
            };
            _failure = new TextBlock
            {
                FontFamily = Palette.UiFont,
                FontSize = Palette.FontCaption,
                LineHeight = 15,
                Foreground = new SolidColorBrush(Palette.Danger, 0.85),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 4),
                IsVisible = false
            };
            _shield = new Border
            {
                Width = 14,
                Height = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Child = IconGlyph.Make(IconGlyph.Shield, 13, Palette.TextFaintBrush),
                IsVisible = false
            };
            _moduleIcon = ModuleIconGlyph.Create(step.IconKey, 14, Palette.TextFaintBrush);
            _moduleIcon.VerticalAlignment = VerticalAlignment.Center;
            _moduleIcon.Opacity = 0.8;
            ToolTip.SetTip(_moduleIcon, step.ModuleName);

            Children.Add(_glyphHost);
            SetColumn(_label, 1);
            Children.Add(_label);
            SetColumn(_shield, 2);
            Children.Add(_shield);
            SetColumn(_moduleIcon, 3);
            Children.Add(_moduleIcon);
            SetColumn(_failure, 1);
            SetRow(_failure, 1);
            SetColumnSpan(_failure, 3);
            Children.Add(_failure);

            Refresh();
            _stepChangedHandler = (_, _) => Dispatcher.UIThread.Post(Refresh);
            step.PropertyChanged += _stepChangedHandler;
        }

        private void Refresh()
        {
            if (_disposed)
            {
                return;
            }

            _label.Text = _step.Label;
            ToolTip.SetTip(_label, string.IsNullOrWhiteSpace(_step.Detail) ? null : _step.Detail);

            bool quiet = _step.Status is StepStatus.Waiting or StepStatus.Denied or StepStatus.Stopped;
            _label.Foreground = quiet ? Palette.TextFaintBrush : Palette.TextPrimaryBrush;
            _label.TextDecorations = _step.Status == StepStatus.Denied ? TextDecorations.Strikethrough : null;

            _failure.IsVisible = _step.Status == StepStatus.Failed && !string.IsNullOrWhiteSpace(_step.Detail);
            _failure.Text = _step.Detail;

            _shield.IsVisible = !string.IsNullOrWhiteSpace(_step.PermissionNote);
            if (_shield.IsVisible)
            {
                // Shield = you allowed it. Small "i" = you answered its question ("You chose …").
                bool isAnswer = _step.PermissionNote.StartsWith("You chose", StringComparison.Ordinal);
                ToolTip.SetTip(_shield, _step.PermissionNote);
                _shield.Child = IconGlyph.Make(isAnswer ? IconGlyph.InfoCircle : IconGlyph.Shield, 13,
                    _step.Status == StepStatus.Denied ? Palette.TextFaintBrush : Palette.AccentSoftBrush);
            }

            _glyphHost.Children.Clear();
            StopSpin();
            switch (_step.Status)
            {
                case StepStatus.Running:
                    _glyphHost.Children.Add(MakeSpinner());
                    StartSpin();
                    break;
                case StepStatus.Done:
                    _glyphHost.Children.Add(IconGlyph.Make(IconGlyph.Check16, 16, Palette.SuccessBrush));
                    break;
                case StepStatus.Answered:
                    _glyphHost.Children.Add(IconGlyph.Make(IconGlyph.Check16, 16, Palette.AccentSoftBrush));
                    break;
                case StepStatus.Failed:
                    _glyphHost.Children.Add(IconGlyph.Make(IconGlyph.AlertCircle, 16, Palette.DangerBrush));
                    break;
                case StepStatus.Denied:
                case StepStatus.Stopped:
                    _glyphHost.Children.Add(IconGlyph.Make(IconGlyph.Close, 12, Palette.TextFaintBrush));
                    break;
                default:
                    _glyphHost.Children.Add(new Ellipse
                    {
                        Width = 12,
                        Height = 12,
                        Stroke = Palette.StrokeStrongBrush,
                        StrokeThickness = 1.5,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    break;
            }
        }

        private Control MakeSpinner()
        {
            // A 270° arc that turns: the same language as the orb's thinking sweep.
            Arc arc = new()
            {
                Width = 13,
                Height = 13,
                StartAngle = 0,
                SweepAngle = 270,
                Stroke = Palette.AccentSoftBrush,
                StrokeThickness = 1.8,
                StrokeLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = RelativePoint.Center,
                RenderTransform = _spin
            };
            return arc;
        }

        private void StartSpin()
        {
            _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
            {
                _spin.Angle = (_spin.Angle + 9) % 360;
            });
            _timer.Start();
        }

        private void StopSpin()
        {
            _timer?.Stop();
        }

        public void Dispose()
        {
            _disposed = true;
            _step.PropertyChanged -= _stepChangedHandler;
            StopSpin();
        }
    }
}
