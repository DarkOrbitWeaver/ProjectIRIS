using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CompanionOS.App.Interface.Theme;

namespace CompanionOS.App.Interface;

/// <summary>
/// Small builders for the interface, so screens read as layout instead of property soup.
///
/// They also enforce the design rules in one place: every surface uses a one-pixel stroke,
/// every button has the same radius and padding, every label uses the type scale. A screen
/// that uses these cannot drift away from the design.
/// </summary>
public static class Ui
{
    /// <summary>A text label with the house style.</summary>
    public static TextBlock Text(string text, double fontSize = Palette.FontBody, IBrush? brush = null,
        FontWeight weight = FontWeight.Normal, bool wrap = true)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = Palette.UiFont,
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = brush ?? Palette.TextPrimaryBrush,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
        };
    }

    /// <summary>A muted secondary label.</summary>
    public static TextBlock Caption(string text) =>
        Text(text, Palette.FontCaption, Palette.TextSecondaryBrush);

    /// <summary>A flat panel with a hairline border.</summary>
    public static Border Card(Control child, double padding = Palette.SpaceLg,
        double radius = Palette.RadiusCard, IBrush? background = null, IBrush? border = null)
    {
        return new Border
        {
            Background = background ?? Palette.SurfaceBrush,
            BorderBrush = border ?? Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius),
            Padding = new Thickness(padding),
            Child = child
        };
    }

    /// <summary>The primary call to action.</summary>
    public static Button PrimaryButton(string label, Action onClick, bool wide = false)
    {
        Button button = new()
        {
            Content = Text(label, Palette.FontBody, Brushes.White, FontWeight.SemiBold, wrap: false),
            Background = Palette.IrisBrush,
            Foreground = Brushes.White,
            Padding = new Thickness(Palette.SpaceLg, Palette.SpaceSm),
            CornerRadius = new CornerRadius(Palette.RadiusControl),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };

        if (wide)
        {
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A quiet button for secondary actions.</summary>
    public static Button GhostButton(string label, Action onClick)
    {
        Button button = new()
        {
            Content = Text(label, Palette.FontBody, Palette.TextSecondaryBrush, wrap: false),
            Background = Brushes.Transparent,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(Palette.SpaceMd, Palette.SpaceSm),
            CornerRadius = new CornerRadius(Palette.RadiusControl),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };

        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A tiny status pill, e.g. "Active" or "Tier 3" — accent tint, no border.</summary>
    public static Border Chip(string text, Color color, bool filled = false)
    {
        return new Border
        {
            Background = filled ? new SolidColorBrush(color, 0.14) : Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(Palette.RadiusPill),
            Padding = new Thickness(10, 3),
            Child = Text(text, Palette.FontCaption, new SolidColorBrush(color), FontWeight.Medium, wrap: false)
        };
    }

    /// <summary>A titled block used throughout the settings screen.</summary>
    public static Control Section(string title, Control content, string? description = null)
    {
        StackPanel header = new() { Spacing = 2 };
        header.Children.Add(Text(title, Palette.FontSection, Palette.TextPrimaryBrush, FontWeight.SemiBold));
        if (!string.IsNullOrWhiteSpace(description))
        {
            header.Children.Add(Caption(description));
        }

        StackPanel body = new() { Spacing = Palette.SpaceMd };
        body.Children.Add(header);
        body.Children.Add(content);

        return Card(body, Palette.SpaceXl, Palette.RadiusPanel);
    }

    /// <summary>A label on the left and a control on the right.</summary>
    public static Control SettingRow(string label, Control control, string? hint = null)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        StackPanel textStack = new() { Spacing = 2, Margin = new Thickness(0, 0, Palette.SpaceLg, 0) };
        textStack.Children.Add(Text(label, Palette.FontBody));
        if (!string.IsNullOrWhiteSpace(hint))
        {
            textStack.Children.Add(Caption(hint));
        }

        Grid.SetColumn(textStack, 0);
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;

        row.Children.Add(textStack);
        row.Children.Add(control);
        return row;
    }

    /// <summary>A horizontal stack with the house gap.</summary>
    public static StackPanel Row(double spacing = Palette.SpaceSm, params Control[] children)
    {
        StackPanel panel = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = spacing,
            VerticalAlignment = VerticalAlignment.Center
        };

        foreach (Control child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    /// <summary>A vertical stack with the house gap.</summary>
    public static StackPanel Column(double spacing = Palette.SpaceMd, params Control[] children)
    {
        StackPanel panel = new() { Orientation = Orientation.Vertical, Spacing = spacing };

        foreach (Control child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    /// <summary>A one-pixel horizontal divider.</summary>
    public static Border Divider() => new()
    {
        Height = 1,
        Background = Palette.StrokeBrush,
        Margin = new Thickness(0, Palette.SpaceSm)
    };

    /// <summary>A styled text box for chat and forms.</summary>
    public static TextBox Input(string watermark, bool multiLine = false)
    {
        return new TextBox
        {
            Watermark = watermark,
            AcceptsReturn = multiLine,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Palette.UiFont,
            FontSize = Palette.FontBody,
            Background = Palette.ElevatedBrush,
            Foreground = Palette.TextPrimaryBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Palette.RadiusControl),
            Padding = new Thickness(Palette.SpaceMd, Palette.SpaceSm),
            MinHeight = multiLine ? 44 : 44
        };
    }

    /// <summary>
    /// The composer's microphone button: a 36 px circle ringed in her listening colour,
    /// per the Chat board.
    /// </summary>
    public static Button RoundIcon(string label, Color ring, Action onClick, double size = 36)
    {
        TextBlock glyph = Ui.Text(label, size * 0.35, new SolidColorBrush(ring),
            FontWeight.SemiBold, wrap: false);

        Button button = new()
        {
            Content = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                BorderBrush = new SolidColorBrush(ring, 0.55),
                BorderThickness = new Thickness(1),
                Child = glyph
            },
            Background = Brushes.Transparent,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };

        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>The 2 px voice underline: green while she speaks, cyan while you do.</summary>
    public static Border VoiceMeter() => new()
    {
        Height = 2,
        CornerRadius = new CornerRadius(1),
        Background = Palette.StrokeBrush,
        Margin = new Thickness(0, Palette.SpaceSm, 0, 0)
    };
}
