using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CompanionOS.App.Interface.Theme;
using CompanionOS.Core.Prompts;

namespace CompanionOS.App.Interface.Controls;

/// <summary>
/// One card for every question Iris asks: approvals, short choices and numbered lists. The
/// desktop overlay and the in-app dialog both host this same control, so a question looks
/// and behaves the same wherever it appears.
///
///   Confirm    Deny, Allow once, and optionally "This session" / "Always"
///   Choice     two to four buttons, each with its number so "two" works by voice
///   Numbered   a list with number badges, five at a time, with More / Back
///
/// A thin bar at the bottom runs down with the time left, so a waiting question is never a
/// mystery. The card never takes the keyboard from a game: it only reacts to clicks.
/// </summary>
internal sealed class PromptCardControl : Border
{
    private readonly Action<Guid, string> _answer;
    private readonly Action _dismiss;
    private readonly StackPanel _body = new() { Spacing = 10 };
    private readonly ProgressBar _countdown = new()
    {
        Minimum = 0,
        Maximum = 1,
        Value = 1,
        Height = 3,
        MinHeight = 3,
        Background = Palette.StrokeBrush
    };

    private IrisPrompt? _prompt;
    private int _shownRevision = -1;
    private Guid _shownId = Guid.Empty;

    public PromptCardControl(Action<Guid, string> answer, Action dismiss)
    {
        _answer = answer;
        _dismiss = dismiss;

        Background = new SolidColorBrush(Palette.Elevated, 0.98);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(Palette.RadiusPanel);
        Padding = new Thickness(16, 14, 16, 12);
        BoxShadow = new BoxShadows(new BoxShadow { Color = Color.FromArgb(125, 0, 0, 0), Blur = 26, OffsetY = 8 });
        Child = new StackPanel { Spacing = 10, Children = { _body, _countdown } };
    }

    /// <summary>Height the card needs, so the overlay window can be sized before it is shown.</summary>
    public static double EstimateHeight(IrisPrompt prompt)
    {
        double height = 26 + 22 + 10 + 46 + 10 + 22 + 10 + 3;
        if (!string.IsNullOrWhiteSpace(prompt.Detail))
        {
            height += 18;
        }
        if (prompt.CanRememberChoice)
        {
            height += 28;
        }

        return height + prompt.Kind switch
        {
            PromptKind.Confirm => prompt.Options.Count > 3 ? 92 : 46,
            PromptKind.Choice => prompt.Options.Count > 2 ? 92 : 46,
            _ => (prompt.VisibleOptions.Count * 50) + 36
        };
    }

    public static double ResultHeight => 84;

    /// <summary>Shows a question. Rebuilds only when the question or its visible state changed.</summary>
    public void Bind(IrisPrompt prompt)
    {
        _prompt = prompt;
        if (_shownId != prompt.Id || _shownRevision != prompt.Revision)
        {
            _shownId = prompt.Id;
            _shownRevision = prompt.Revision;
            Rebuild(prompt);
        }

        Tick();
    }

    /// <summary>Updates the countdown bar. Cheap enough to call on every sync tick.</summary>
    public void Tick()
    {
        if (_prompt is null)
        {
            return;
        }

        double total = Math.Max(1, _prompt.Timeout.TotalSeconds);
        double left = Math.Max(0, (_prompt.ExpiresAt - DateTimeOffset.Now).TotalSeconds);
        _countdown.Value = Math.Clamp(left / total, 0, 1);
    }

    /// <summary>A short "done" card shown just after an answer, so the user sees it landed.</summary>
    public void BindResult(string headline, string detail, bool positive)
    {
        _prompt = null;
        _shownId = Guid.Empty;
        _shownRevision = -1;
        _body.Children.Clear();
        BorderBrush = positive ? Palette.SuccessBrush : Palette.StrokeStrongBrush;
        _countdown.Value = 0;

        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(IconGlyph.Make(positive ? IconGlyph.CheckCircle : IconGlyph.AlertCircle, 20,
            positive ? Palette.SuccessBrush : Palette.TextSecondaryBrush));
        StackPanel copy = new() { Spacing = 2 };
        copy.Children.Add(Ui.Text(headline, Palette.FontEmphasis, Palette.TextPrimaryBrush, FontWeight.SemiBold, wrap: false));
        if (!string.IsNullOrWhiteSpace(detail))
        {
            TextBlock line = Ui.Text(detail, Palette.FontCaption, Palette.TextSecondaryBrush, FontWeight.Medium, wrap: false);
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            line.MaxWidth = 280;
            copy.Children.Add(line);
        }

        row.Children.Add(copy);
        _body.Children.Add(row);
    }

    private void Rebuild(IrisPrompt prompt)
    {
        _body.Children.Clear();
        bool sensitive = prompt.Risk == PromptRisk.Sensitive;
        IBrush accent = sensitive ? Palette.WarningBrush : Palette.AccentSoftBrush;
        BorderBrush = sensitive ? Palette.WarningBrush : Palette.StrokeStrongBrush;
        _countdown.Foreground = sensitive ? Palette.WarningBrush : Palette.IrisBrush;

        // Header: icon, small caps title, who is asking, dismiss.
        Control icon = ModuleIconGlyph.Create(prompt.IconKey, 16, accent);
        icon.VerticalAlignment = VerticalAlignment.Center;
        TextBlock title = Ui.Text(prompt.Title.ToUpperInvariant(), Palette.FontCaption, accent,
            FontWeight.SemiBold, wrap: false);
        title.LetterSpacing = 0.88;
        title.VerticalAlignment = VerticalAlignment.Center;
        TextBlock owner = Ui.Text(prompt.OwnerName, Palette.FontCaption, Palette.TextFaintBrush,
            FontWeight.Medium, wrap: false);
        owner.VerticalAlignment = VerticalAlignment.Center;
        owner.Margin = new Thickness(0, 0, 8, 0);

        Button close = new()
        {
            Content = IconGlyph.Make(IconGlyph.Close, 12, Palette.TextSecondaryBrush),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            MinWidth = 24,
            Height = 24,
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(close, prompt.Kind == PromptKind.Confirm ? "Deny" : "Dismiss");
        close.Click += (_, _) => _dismiss();

        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(owner, 2);
        Grid.SetColumn(close, 3);
        header.Children.Add(icon);
        header.Children.Add(title);
        header.Children.Add(owner);
        header.Children.Add(close);
        _body.Children.Add(header);

        // The question, in plain words.
        TextBlock question = Ui.Text(prompt.Question, Palette.FontEmphasis, Palette.TextPrimaryBrush, FontWeight.Medium);
        question.LineHeight = 22;
        question.MaxHeight = 66;
        question.TextTrimming = TextTrimming.CharacterEllipsis;
        _body.Children.Add(question);

        if (!string.IsNullOrWhiteSpace(prompt.Detail))
        {
            _body.Children.Add(Ui.Text(prompt.Detail, Palette.FontCaption, Palette.TextFaintBrush,
                FontWeight.Medium, wrap: false));
        }

        switch (prompt.Kind)
        {
            case PromptKind.Confirm:
                _body.Children.Add(BuildButtonRow(prompt, withNumbers: false));
                break;
            case PromptKind.Choice:
                _body.Children.Add(BuildButtonRow(prompt, withNumbers: true));
                break;
            default:
                _body.Children.Add(BuildNumberedList(prompt));
                break;
        }

        if (prompt.CanRememberChoice)
        {
            CheckBox remember = new()
            {
                Content = Ui.Text("Remember this choice for the same question", Palette.FontCaption,
                    Palette.TextSecondaryBrush, FontWeight.Medium, wrap: false),
                IsChecked = prompt.RememberChoice
            };
            remember.IsCheckedChanged += (_, _) => prompt.SetRememberChoice(remember.IsChecked == true);
            _body.Children.Add(remember);
        }

        // The voice hint: what to say right now, or why the last try did not count.
        bool hasProblem = !string.IsNullOrWhiteSpace(prompt.Hint);
        StackPanel hint = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        hint.Children.Add(IconGlyph.Make(IconGlyph.Microphone, 12,
            hasProblem ? Palette.WarningBrush : Palette.TextFaintBrush));
        TextBlock hintText = Ui.Text(hasProblem ? prompt.Hint : PromptSpeechMatcher.DefaultHint(prompt),
            Palette.FontCaption, hasProblem ? Palette.WarningBrush : Palette.TextFaintBrush, FontWeight.Medium, wrap: false);
        hintText.TextTrimming = TextTrimming.CharacterEllipsis;
        hintText.MaxWidth = 300;
        hint.Children.Add(hintText);
        _body.Children.Add(hint);
    }

    private Control BuildButtonRow(IrisPrompt prompt, bool withNumbers)
    {
        WrapPanel row = new() { Orientation = Orientation.Horizontal };
        for (int index = 0; index < prompt.Options.Count; index++)
        {
            PromptOption option = prompt.Options[index];
            Button button = MakeButton(option, withNumbers ? (index + 1).ToString() : null);
            button.Margin = new Thickness(0, 0, 8, 8);
            button.Click += (_, _) => _answer(prompt.Id, option.Id);
            row.Children.Add(button);
        }

        return row;
    }

    private Control BuildNumberedList(IrisPrompt prompt)
    {
        StackPanel list = new() { Spacing = 6 };
        IReadOnlyList<PromptOption> page = prompt.VisibleOptions;
        for (int index = 0; index < page.Count; index++)
        {
            PromptOption option = page[index];
            Button row = MakeRow(option, (prompt.FirstVisibleNumber + index).ToString());
            row.Click += (_, _) => _answer(prompt.Id, option.Id);
            list.Children.Add(row);
        }

        if (prompt.PageCount > 1)
        {
            int first = prompt.FirstVisibleNumber;
            TextBlock range = Ui.Text($"{first}\u2013{first + page.Count - 1} of {prompt.Options.Count}",
                Palette.FontCaption, Palette.TextSecondaryBrush, FontWeight.Medium, wrap: false);
            range.VerticalAlignment = VerticalAlignment.Center;

            Button back = Ui.GhostButton("Back", () => prompt.Page(-1));
            back.IsEnabled = prompt.PageIndex > 0;
            Button more = Ui.GhostButton("More", () => prompt.Page(1));
            more.IsEnabled = prompt.PageIndex < prompt.PageCount - 1;

            StackPanel pager = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { back, more, range }
            };
            list.Children.Add(pager);
        }

        return list;
    }

    private static Button MakeButton(PromptOption option, string? number)
    {
        (IBrush background, IBrush border, IBrush foreground) = option.Role switch
        {
            PromptOptionRole.Approve => (Palette.IrisBrush, Palette.IrisBrush, (IBrush)Brushes.White),
            PromptOptionRole.Primary => (Brushes.Transparent, Palette.IrisBrush, Palette.AccentSoftBrush),
            PromptOptionRole.Deny => (Brushes.Transparent, Palette.StrokeBrush, Palette.TextSecondaryBrush),
            _ => (Brushes.Transparent, Palette.StrokeBrush, Palette.TextPrimaryBrush)
        };

        StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (number is not null)
        {
            content.Children.Add(NumberBadge(number, 18));
        }

        content.Children.Add(Ui.Text(option.Label, Palette.FontBody, foreground, FontWeight.Medium, wrap: false));
        return new Button
        {
            Content = content,
            Background = background,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 8)
        };
    }

    private static Button MakeRow(PromptOption option, string number)
    {
        TextBlock label = Ui.Text(option.Label, Palette.FontBody, Palette.TextPrimaryBrush, FontWeight.Medium, wrap: false);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        StackPanel copy = new() { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(label);
        if (!string.IsNullOrWhiteSpace(option.Detail))
        {
            TextBlock detail = Ui.Text(option.Detail, Palette.FontCaption, Palette.TextFaintBrush, FontWeight.Medium, wrap: false);
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            copy.Children.Add(detail);
        }

        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        Control badge = NumberBadge(number, 24);
        Grid.SetColumn(badge, 0);
        Grid.SetColumn(copy, 1);
        grid.Children.Add(badge);
        grid.Children.Add(copy);

        return new Button
        {
            Content = grid,
            Background = Brushes.Transparent,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 7),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private static Control NumberBadge(string number, double size) => new Border
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = Palette.AccentWash(0.18),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = number,
            FontFamily = Palette.UiFont,
            FontSize = size > 20 ? 12 : 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.AccentSoftBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };
}
