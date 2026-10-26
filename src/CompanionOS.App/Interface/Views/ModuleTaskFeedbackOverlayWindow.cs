using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using CompanionOS.App.Interface.Controls;
using CompanionOS.App.Interface.Theme;
using CompanionOS.Core.Events;
using CompanionOS.Core.Modules;

namespace CompanionOS.App.Interface.Views;

/// <summary>
/// The floating stack of module feedback cards — her agentic-work narration on screen:
/// "downloading song …" with a spinner, then a green check and a quiet fade once the
/// step is real. One window, many cards, exactly like the notices board in the design
/// reference. Every card is event-authored by a module, and every event also lands in
/// IrisEvent's receipts, so a faded card is never a lost truth.
/// </summary>
internal sealed class ModuleTaskFeedbackOverlayWindow : Window
{
    private const double CardWidth = 308;
    private const double TaskCardHeight = 88;
    private const double NoteCardHeight = 50;
    private const int CardSpacing = 10;

    /// <summary>Transparent breathing room around the stack. The drop shadow (blur 24,
    /// offset 8) must be able to paint inside the window; a window hugging the cards
    /// exactly clips it into a hard dark rim.</summary>
    private const double ShadowGutter = 32;

    /// <summary>Few pixels of margin between the docked stack edge and the screen edge.</summary>
    private const double DockMargin = 8;

    /// <summary>Most cards worth showing at once; the full story stays in her log.</summary>
    private const int MaximumCards = 6;

    private readonly ShellModel _model;
    private readonly StackPanel _stack;
    private readonly List<FeedbackCard> _cards = [];
    private readonly DispatcherTimer _spinnerTimer;

    public ModuleTaskFeedbackOverlayWindow(ShellModel model)
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
        Height = ShadowGutter * 2;

        _stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = CardSpacing,
            Margin = new Thickness(ShadowGutter),
            VerticalAlignment = VerticalAlignment.Top
        };
        Content = _stack;

        _spinnerTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background,
            (_, _) => RotateSpinners());
        _spinnerTimer.Start();
    }

    /// <summary>True while at least one card still deserves screen time.</summary>
    public bool HasLiveCards => _cards.Count > 0;

    /// <summary>Feeds one module-authored event card into the stack. Runs on the UI thread.</summary>
    public void Apply(ModuleTaskFeedbackEvent payload)
    {
        FeedbackCard? card = FindCard(payload.TaskId);
        switch (payload.Kind)
        {
            case ModuleFeedbackKind.TaskStarted:
                card = CreateTaskCard(payload);
                break;

            case ModuleFeedbackKind.Note:
                CreateNoteCard(payload);
                break;

            case ModuleFeedbackKind.TaskRunning when card is not null:
                card.SetStory(payload.Title, payload.Detail);
                card.SetProgress(payload.Progress, payload.Total);
                break;

            case ModuleFeedbackKind.TaskCompleted when card is not null:
                card.Finish(IconGlyph.CheckCircle, Palette.SuccessBrush, payload.Summary);
                HoldThenFade(card, payload.HoldMilliseconds);
                break;

            case ModuleFeedbackKind.TaskFailed when card is not null:
                card.Finish(IconGlyph.AlertCircle, Palette.DangerBrush, payload.Summary);
                HoldThenFade(card, payload.HoldMilliseconds);
                break;

            case ModuleFeedbackKind.TaskCancelled when card is not null:
                card.Finish(IconGlyph.Clock14, Palette.TextSecondaryBrush, payload.Summary);
                HoldThenFade(card, payload.HoldMilliseconds);
                break;
        }
    }

    /// <summary>Anchors the stack to the screen edge the pill shares — max right with a
    /// few pixels of margin, top-right about a fifth down from the top, or right below
    /// the music card whenever it is on screen so the two never stack over each other.</summary>
    public void PlaceNear(PixelPoint pillPosition, int pillWidth, int pillHeight, PixelRect workArea,
        bool growsLeft, PixelPoint? belowAnchor)
    {
        double scale = RenderScaling > 0 ? RenderScaling : 1.0;
        int gutters = (int)Math.Ceiling(ShadowGutter * scale);
        int windowWidth = (int)Math.Ceiling((CardWidth + (ShadowGutter * 2)) * scale);
        int windowHeight = (int)Math.Ceiling((ComputeStackHeight() + (ShadowGutter * 2)) * scale);

        int x = growsLeft
            ? pillPosition.X + pillWidth - (int)Math.Ceiling(CardWidth * scale) - gutters
            : pillPosition.X - gutters;

        int y = belowAnchor is { } anchor
            ? anchor.Y
            : workArea.Y + (int)(workArea.Height * 0.20);

        Position = new PixelPoint(
            Math.Clamp(x, workArea.X, Math.Max(workArea.X, workArea.Right - windowWidth)),
            Math.Clamp(y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - windowHeight)));
    }

    private FeedbackCard? FindCard(string taskId) =>
        _cards.FirstOrDefault(candidate => candidate.TaskId == taskId);

    private FeedbackCard CreateTaskCard(ModuleTaskFeedbackEvent payload)
    {
        FeedbackCard card = new(TaskCardHeight, _model.Runtime.Settings.Current.Interface.ReduceMotion);
        card.TaskId = payload.TaskId;
        card.BeginRunning(payload.IconKey, payload.Title, payload.Detail);
        card.SetProgress(payload.Progress, payload.Total);

        _cards.Add(card);
        _stack.Children.Add(card);

        // The oldest card beyond the cap retires at once — the receipts keep its story.
        while (_cards.Count > MaximumCards)
        {
            FeedbackCard oldest = _cards[0];
            _cards.RemoveAt(0);
            _stack.Children.Remove(oldest);
        }

        UpdateHeight();
        return card;
    }

    private void CreateNoteCard(ModuleTaskFeedbackEvent payload)
    {
        FeedbackCard card = new(NoteCardHeight, _model.Runtime.Settings.Current.Interface.ReduceMotion);
        card.TaskId = payload.TaskId;

        IBrush tone = payload.NoteKind switch
        {
            ModuleNoteKind.Success => Palette.SuccessBrush,
            ModuleNoteKind.Warning => Palette.WarningBrush,
            ModuleNoteKind.Error => Palette.DangerBrush,
            _ => Palette.TextPrimaryBrush
        };
        card.ShowNote(payload.NoteKind switch
        {
            ModuleNoteKind.Success => IconGlyph.CheckCircle,
            ModuleNoteKind.Warning => IconGlyph.AlertCircle,
            ModuleNoteKind.Error => IconGlyph.AlertCircle,
            _ => IconGlyph.InfoCircle
        }, tone, payload.Title);

        _cards.Add(card);
        _stack.Children.Add(card);

        while (_cards.Count > MaximumCards)
        {
            FeedbackCard oldest = _cards[0];
            _cards.RemoveAt(0);
            _stack.Children.Remove(oldest);
        }

        UpdateHeight();
        HoldThenFade(card, payload.HoldMilliseconds);
    }

    /// <summary>One-shot deferred action on the UI thread. This Avalonia surface has no
    /// RunOnce helper, so a shared explicit timer keeps the hold/fade sequencing safe.</summary>
    private static void RunOnce(TimeSpan delay, Action action)
    {
        DispatcherTimer? timer = null;
        timer = new DispatcherTimer(delay, DispatcherPriority.Background, (_, _) =>
        {
            timer!.Stop();
            action();
        });
        timer.Start();
    }

    private void HoldThenFade(FeedbackCard card, double holdMilliseconds)
    {
        if (card.FadeScheduled)
        {
            return;
        }

        card.FadeScheduled = true;
        double hold = double.IsFinite(holdMilliseconds) && holdMilliseconds >= 0
            ? Math.Min(holdMilliseconds, 60_000)
            : 6_000;
        RunOnce(TimeSpan.FromMilliseconds(hold + 120), () => BeginFade(card));
    }

    private void BeginFade(FeedbackCard card)
    {
        if (card.Fading)
        {
            return;
        }

        card.Fading = true;
        if (_model.Runtime.Settings.Current.Interface.ReduceMotion)
        {
            RemoveCard(card);
            return;
        }

        // Transitions is nullable on StyledElement; the card normally carries a fresh
        // collection, but a card built by a reduced-motion path may not have one yet.
        card.Transitions ??= new Transitions();
        card.Transitions.Add(new DoubleTransition
        {
            Property = Visual.OpacityProperty,
            Duration = TimeSpan.FromMilliseconds(320),
            Easing = new SineEaseOut()
        });
        card.Opacity = 0;
        RunOnce(TimeSpan.FromMilliseconds(360), () => RemoveCard(card));
    }

    private void RemoveCard(FeedbackCard card)
    {
        if (!_cards.Remove(card))
        {
            return;
        }

        card.Transitions?.Clear();
        _stack.Children.Remove(card);
        UpdateHeight();
    }

    private void UpdateHeight()
    {
        Height = ShadowGutter * 2 + ComputeStackHeight();
    }

    private double ComputeStackHeight()
    {
        if (_cards.Count == 0)
        {
            return 0;
        }

        return _cards.Sum(candidate => candidate.CardHeight) + (CardSpacing * (_cards.Count - 1));
    }

    private void RotateSpinners()
    {
        foreach (FeedbackCard card in _cards)
        {
            card.RotateSpinner();
        }
    }

    /// <summary>One card visual: icon slot, title, detail line, thin progress bar.</summary>
    private sealed class FeedbackCard : Border
    {
        private readonly TextBlock _title;
        private readonly TextBlock _detail;
        private readonly Border _iconSlot;
        private readonly Border _barTrack;
        private readonly Border _barFill;
        private readonly bool _reduceMotion;
        private Shape? _spinner;
        private double _trackWidth;
        private double _progressFraction = double.NaN;

        public FeedbackCard(double cardHeight, bool reduceMotion)
        {
            _reduceMotion = reduceMotion;
            CardHeight = cardHeight;
            Width = CardWidth;
            Height = cardHeight;
            Background = new SolidColorBrush(Palette.Elevated, 0.96);
            BorderBrush = Palette.StrokeBrush;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(Palette.RadiusPanel);
            Padding = new Thickness(14, 12);
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Color = Color.FromArgb(115, 0, 0, 0), Blur = 24, OffsetY = 8
            });

            _title = new TextBlock
            {
                FontFamily = Palette.UiFont,
                FontSize = Palette.FontEmphasis,
                FontWeight = FontWeight.SemiBold,
                LineHeight = 20,
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

            _barFill = new Border
            {
                Height = 3,
                Background = Palette.AccentSoftBrush,
                CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _barTrack = new Border
            {
                Height = 3,
                Background = Palette.StrokeBrush,
                CornerRadius = new CornerRadius(1.5),
                Child = _barFill,
                IsVisible = false
            };
            _barTrack.SizeChanged += (_, eventArgs) =>
            {
                _trackWidth = eventArgs.NewSize.Width;
                RefreshBar();
            };

            _iconSlot = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(9),
                Background = Palette.SurfaceBrush,
                BorderBrush = Palette.StrokeBrush,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Top
            };

            // The fade-out transition is attached when the card's hold runs out.
            Transitions = [];

            StackPanel column = new() { Spacing = 4 };
            column.Children.Add(_title);
            column.Children.Add(_detail);
            column.Children.Add(_barTrack);

            StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 11 };
            row.Children.Add(_iconSlot);
            row.Children.Add(column);
            Child = row;
        }

        public string TaskId = string.Empty;
        public double CardHeight { get; }
        public bool FadeScheduled;
        public bool Fading;

        /// <summary>The card's story begins: module icon, headline, first step, spinner.</summary>
        public void BeginRunning(string? iconKey, string title, string? detail)
        {
            _iconSlot.Child = ModuleIconGlyph.Create(iconKey, 30, Palette.TextPrimaryBrush);
            SetStory(title, detail ?? string.Empty);
            if (double.IsNaN(_progressFraction))
            {
                _spinner = BuildSpinner();
                if (!_reduceMotion && _spinner is not null)
                {
                    _iconSlot.Child = _spinner;
                }
                // ReduceMotion keeps the module icon and the story does the turning.
            }
        }

        public void ShowNote(string glyph, IBrush tone, string text)
        {
            _iconSlot.Child = IconGlyph.Make(glyph, 18, tone);
            _title.Text = text;
            _detail.IsVisible = false;
        }

        public void SetStory(string title, string detail)
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                _title.Text = title;
            }

            _detail.Text = detail;
            _detail.IsVisible = detail.Length > 0;
        }

        public void SetProgress(double? progress, double? total)
        {
            if (progress is null || total is not { } knownTotal || knownTotal <= 0)
            {
                return;
            }

            _progressFraction = Math.Clamp(progress.Value / knownTotal, 0, 1);
            _barTrack.IsVisible = true;
            RefreshBar();
        }

        /// <summary>The end state: the spinner leaves, the honest mark arrives, the
        /// summary line replaces the last step, and the bar makes way for the verdict.</summary>
        public void Finish(string glyph, IBrush tone, string? summary)
        {
            _spinner = null;
            _iconSlot.Child = IconGlyph.Make(glyph, 18, tone);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                _detail.Text = summary;
                _detail.Foreground = tone;
                _detail.IsVisible = true;
            }

            _barTrack.IsVisible = false;
        }

        public void RotateSpinner()
        {
            if (_spinner?.RenderTransform is RotateTransform rotation)
            {
                rotation.Angle = (rotation.Angle + 30) % 360;
            }
        }

        private static Shape? BuildSpinner()
        {
            Avalonia.Controls.Shapes.Path spinner;
            try
            {
                spinner = new Avalonia.Controls.Shapes.Path
                {
                    Data = StreamGeometry.Parse("M12 3a9 9 0 0 1 9 9"),
                    Width = 18,
                    Height = 18,
                    Stretch = Stretch.None,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Stroke = Palette.AccentSoftBrush,
                    StrokeThickness = 2.2,
                    StrokeLineCap = PenLineCap.Round
                };
                spinner.RenderTransform = new RotateTransform(0);
                return spinner;
            }
            catch
            {
                return null;
            }
        }

        private void RefreshBar()
        {
            if (double.IsNaN(_progressFraction))
            {
                return;
            }

            _barFill.Width = Math.Max(0, _trackWidth * _progressFraction);
        }
    }
}
