using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CompanionOS.App.Interface.Theme;

namespace CompanionOS.App.Interface.Controls;

/// <summary>
/// The 40 × 24 switch the boards draw: fully rounded track, an 18 px circular knob that
/// rides 3 px from its edge — iris violet with a white knob when on, #262A36 with a grey
/// knob when off. Rendering this once here means every screen shows the identical switch
/// instead of a home-made copy per card.
/// </summary>
public sealed class BoardSwitch : Button
{
    private readonly Border _knob;
    private bool _isOn;

    public BoardSwitch()
    {
        Width = 40;
        Height = 24;
        CornerRadius = new CornerRadius(Palette.RadiusPill);
        Padding = new Thickness(0);
        BorderThickness = new Thickness(0);
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;

        _knob = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9)
        };

        Content = new Panel { Children = { _knob } };
        IsOn = false;
    }

    /// <summary>True paints the track iris and parks the white knob on the right.</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            _isOn = value;
            Background = _isOn ? Palette.IrisBrush : Palette.StrokeBrush;
            _knob.Background = _isOn ? Brushes.White : Palette.TextSecondaryBrush;
            _knob.HorizontalAlignment = _isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _knob.Margin = new Thickness(3);
        }
    }

    /// <summary>Flips the switch and raises Changed for real state owners.</summary>
    protected override void OnClick()
    {
        base.OnClick();
        IsOn = !IsOn;
        Changed?.Invoke(IsOn);
    }

    /// <summary>Raised with the new on/off state after every real flip.</summary>
    public event Action<bool>? Changed;
}
