using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Layout;
using Avalonia.Media;
using CompanionOS.App.Interface;
using CompanionOS.App.Interface.Theme;

namespace CompanionOS.App.Interface.Controls;

/// <summary>
/// The dropdown the boards draw, one-to-one: 36 px tall, #161821 fill, one-pixel
/// #262A36 stroke, 10 px radius, 13/18 medium text on the left with a 16 px chevron on
/// the right. The popup list speaks the same language and closes the moment a choice is
/// made. It exists instead of the stock ComboBox because the boards draw their own chrome
/// and ours does not re-invent it per screen.
/// </summary>
public sealed class BoardSelect : Panel
{
    private readonly Button _button;
    private readonly TextBlock _label;
    private readonly StackPanel _itemList;
    private readonly Popup _popup;
    private readonly List<(string Label, string Value)> _items = new();
    private string _selectedValue = string.Empty;
    private string _selectedLabel = string.Empty;

    /// <summary>Raised only when the picked value actually changed.</summary>
    public event Action<string>? Changed;

    public BoardSelect()
    {
        _label = new TextBlock
        {
            FontFamily = Palette.UiFont,
            FontSize = Palette.FontBody,
            LineHeight = 18,
            FontWeight = FontWeight.Medium,
            Foreground = Palette.TextPrimaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        _itemList = new StackPanel();

        _popup = new Popup
        {
            Placement = PlacementMode.Bottom,
            PlacementAnchor = PopupAnchor.TopLeft,
            IsLightDismissEnabled = true,
            Child = new Border
            {
                Background = Palette.ElevatedBrush,
                BorderBrush = Palette.StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Palette.RadiusControl),
                Padding = new Thickness(4),
                Child = new ScrollViewer
                {
                    MaxHeight = 320,
                    Content = _itemList
                }
            }
        };

        _button = new Button
        {
            Background = Palette.SurfaceBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Palette.RadiusControl),
            Width = 220,
            Height = 36,
            Padding = new Thickness(12, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        ToolTip.SetTip(_button, "Choose from the list");
        _button.Click += (_, _) => _popup.IsOpen = !_popup.IsOpen;

        // The chevron is 16 px, per the board's control spec.
        Grid face = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _label.Margin = new Thickness(0, 0, 12, 0);
        Control chevron = IconGlyph.Make(IconGlyph.ChevronDown, 16, Palette.TextSecondaryBrush);
        Grid.SetColumn(_label, 0);
        Grid.SetColumn(chevron, 1);
        face.Children.Add(_label);
        face.Children.Add(chevron);
        _button.Content = face;

        _popup.PlacementTarget = _button;

        Children.Add(_button);
        Children.Add(_popup);
    }

    /// <summary>The picked value; unchanged until the user picks something else.</summary>
    public string SelectedValue => _selectedValue;

    /// <summary>
    /// The popup is a child purely so it can open — it must never contribute to this
    /// control's measured size, or a closed dropdown injects a phantom 220 × 320 block
    /// into every row that hosts it. Only the button is measured and arranged.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        _button.Measure(new Size(double.MaxValue, availableSize.Height));
        return _button.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = _button.DesiredSize.Width;
        double height = _button.DesiredSize.Height;
        double x = HorizontalAlignment switch
        {
            Avalonia.Layout.HorizontalAlignment.Right => finalSize.Width - width,
            Avalonia.Layout.HorizontalAlignment.Center => (finalSize.Width - width) / 2,
            _ => 0
        };
        double y = VerticalAlignment switch
        {
            Avalonia.Layout.VerticalAlignment.Bottom => finalSize.Height - height,
            Avalonia.Layout.VerticalAlignment.Center => (finalSize.Height - height) / 2,
            _ => 0
        };
        _button.Arrange(new Rect(x, y, width, height));
        return finalSize;
    }

    /// <summary>Replaces the item list and the visible selection.</summary>
    public void SetItems(IReadOnlyList<(string Label, string Value)> items, string selectedValue)
    {
        _items.Clear();
        _items.AddRange(items);
        _itemList.Children.Clear();

        foreach ((string label, string value) in _items)
        {
            bool isSelected = string.Equals(value, selectedValue, StringComparison.Ordinal);
            Button item = new()
            {
                Background = isSelected ? new SolidColorBrush(Palette.Iris, 0.14) : Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8),
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            TextBlock text = Ui.Text(label, Palette.FontBody,
                isSelected ? Palette.TextPrimaryBrush : Palette.TextSecondaryBrush,
                FontWeight.Medium, wrap: false);
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            item.Content = text;
            item.Click += (_, _) =>
            {
                _popup.IsOpen = false;
                Pick(label, value);
            };
            _itemList.Children.Add(item);
        }

        ApplySelection(selectedValue);
    }

    private void ApplySelection(string value)
    {
        _selectedValue = value;
        _selectedLabel = _items.FirstOrDefault(item =>
            string.Equals(item.Value, value, StringComparison.Ordinal)).Label ?? value;
        _label.Text = _selectedLabel;
    }

    private void Pick(string label, string value)
    {
        if (string.Equals(value, _selectedValue, StringComparison.Ordinal))
        {
            ApplySelection(value);
            return;
        }

        ApplySelection(value);
        Changed?.Invoke(value);
    }
}
