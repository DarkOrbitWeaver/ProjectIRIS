using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CompanionOS.App.Interface.Theme;

namespace CompanionOS.App.Interface.Views;

/// <summary>Dismissible recovery prompt for a previously connected Telegram account.</summary>
public sealed class TelegramSetupWindow : Window
{
    public TelegramSetupWindow(Action openModules, Action openSetupPage)
    {
        Title = "Connect Telegram to Iris";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.BackgroundBrush;

        TextBlock title = Ui.Text("Reconnect your Telegram account", Palette.FontPage,
            Palette.TextPrimaryBrush, FontWeight.SemiBold);
        title.TextWrapping = TextWrapping.Wrap;
        TextBlock details = Ui.Text(
            "A Telegram account connected to Iris before, but its saved session is no longer available. Reconnect from Modules, or close this window and continue without Telegram.",
            Palette.FontBody, Palette.TextSecondaryBrush);
        details.LineHeight = 22;
        details.TextWrapping = TextWrapping.Wrap;

        Button modules = Ui.GhostButton("Open Modules", () =>
        {
            Close();
            openModules();
        });
        Button connect = Ui.GhostButton("Open Telegram setup", openSetupPage);
        Button close = Ui.GhostButton("Close", Close);
        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { modules, connect, close }
        };

        Content = new Border
        {
            Background = Palette.ElevatedBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Palette.RadiusPanel),
            Padding = new Thickness(24),
            Child = new StackPanel { Spacing = 16, Children = { title, details, actions } }
        };
    }
}
