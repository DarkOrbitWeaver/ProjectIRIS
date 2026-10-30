using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CompanionOS.App.Interface.Controls;
using CompanionOS.Core.Prompts;

namespace CompanionOS.App.Interface.Views;

/// <summary>
/// The desktop face of every question: approvals, choices and numbered lists, shown beside
/// Iris's pill so you can answer without opening the full app. It replaces the old
/// permission-only overlay. It never takes keyboard focus from a game; you answer by clicking,
/// speaking, or typing in the main window. After an answer it briefly shows a "done" card so
/// you can see that it landed.
/// </summary>
internal sealed class PromptOverlayWindow : Window
{
    private const double CardWidth = 372;

    /// <summary>Transparent breathing room so the drop shadow paints inside the window.</summary>
    private const double ShadowGutter = 32;

    private readonly PromptCardControl _card;
    private double _cardHeight = 200;

    public PromptOverlayWindow(ShellModel model)
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
        Height = _cardHeight + (ShadowGutter * 2);

        _card = new PromptCardControl(
            (promptId, optionId) => model.AnswerPrompt(promptId, optionId),
            model.DismissPrompt);
        Content = new Border { Margin = new Thickness(ShadowGutter), Child = _card };
    }

    public void Bind(IrisPrompt prompt)
    {
        _card.Bind(prompt);
        Resize(PromptCardControl.EstimateHeight(prompt));
    }

    public void BindResult(PromptNotice notice)
    {
        _card.BindResult(notice.Headline, notice.Detail, notice.Positive);
        Resize(PromptCardControl.ResultHeight);
    }

    private void Resize(double cardHeight)
    {
        if (Math.Abs(cardHeight - _cardHeight) < 1)
        {
            return;
        }

        _cardHeight = cardHeight;
        Height = _cardHeight + (ShadowGutter * 2);
    }

    public void PlaceNear(PixelPoint pillPosition, int pillWidth, int pillHeight, PixelRect workArea,
        bool growsLeft)
    {
        double scale = RenderScaling > 0 ? RenderScaling : 1.0;
        int width = (int)Math.Ceiling(CardWidth * scale);
        int gutter = (int)Math.Ceiling(ShadowGutter * scale);
        int windowWidth = width + (gutter * 2);
        int windowHeight = (int)Math.Ceiling(_cardHeight * scale) + (gutter * 2);
        int gap = (int)Math.Ceiling(10 * scale); // window edge to pill-edge breathing room

        // The card keeps the pill's shared edge; the window extends one gutter outward so
        // the drop shadow paints without touching the pill.
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
}
