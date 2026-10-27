using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CompanionOS.App.Interface.Theme;

namespace CompanionOS.App.Interface.Views;

/// <summary>Dismissible first-run prompt for connecting a personal Telegram account.</summary>
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

        TextBlock title = Ui.Text("Connect your Telegram account", Palette.FontPage,
            Palette.TextPrimaryBrush, FontWeight.SemiBold);
        title.TextWrapping = TextWrapping.Wrap;
        TextBlock details = Ui.Text(
            "Telegram Personal is not connected yet. Set up the module and add your Telegram API credentials in Modules. You can close this window and finish later.",
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
