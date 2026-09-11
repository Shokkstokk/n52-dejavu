using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DejaVu.App.Controls;

/// <summary>
/// A saturation/value square, a hue spectrum, and the numbers.
///
/// <para><b>Hue is held separately from the color.</b> Black and white have no hue - every
/// hue produces the same black - so a picker that recovered hue from the current color would
/// snap the spectrum back to red the moment the user dragged into a corner, and dragging out
/// again would come back the wrong color. The hue the user chose is therefore remembered on
/// its own and only overwritten when a color arrives from outside with a hue worth having.</para>
///
/// <para><b>Every change is reported, including the ones mid-drag.</b> The caller repaints the
/// window from these, which is the entire point: a color is judged by what it does to the
/// window behind it, not by a 34-pixel preview.</para>
/// </summary>
public partial class ColorPicker : UserControl
{
    /// <summary>Somewhere to start. Two rows: saturated hues, then the neutrals.</summary>
    private static readonly string[] Basic =
    [
        "#E05252", "#FF8200", "#FFC53D", "#8BD450", "#3DD68C", "#2FBFA8",
        "#4CC9F0", "#4C8DFF", "#6E7BFF", "#9000AC", "#C13FE3", "#FF5BC8",
        "#FFFFFF", "#C7CBD1", "#9AA0A6", "#6B7178", "#3A3F46", "#1B1D21",
    ];

    public event Action<Color>? Changed;

    private double _hue;
    private double _saturation;
    private double _value;

    /// <summary>True while the control is writing to its own boxes, so their change events
    /// do not read half-typed text back as a color.</summary>
    private bool _updating;

    public ColorPicker()
    {
        InitializeComponent();
        BuildBasic();
        Color = Colors.Black;
    }

    private Color _color = Colors.Black;

    /// <summary>
    /// The color shown. Setting it does not raise <see cref="Changed"/> - that is for the
    /// user's edits, and a caller that has just set the value does not need telling.
    /// </summary>
    public Color Color
    {
        get => _color;
        set
        {
            _color = value;

            var (hue, saturation, brightness) = Palette.ToHsv(value);

            // Only take the hue when there is one to take. A gray has no hue, and adopting the
            // zero it reports would throw away whatever the user had the spectrum set to.
            if (saturation > 0.001 && brightness > 0.001) _hue = hue;

            _saturation = saturation;
            _value = brightness;

            Refresh();
        }
    }

    private void BuildBasic()
    {
        foreach (var hex in Basic)
        {
            if (Palette.Parse(hex) is not { } color) continue;

            var swatch = new Border
            {
                Width = 26,
                Height = 22,
                Margin = new Thickness(0, 0, 5, 5),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(color),
                Cursor = Cursors.Hand,
                ToolTip = hex,
            };

            // Bound rather than copied, so these keep up with a theme change. FindResource
            // would freeze whatever was current when the control was built.
            swatch.SetResourceReference(BorderBrushProperty, "Line");

            swatch.MouseLeftButtonUp += (_, _) => Commit(color);
            PanelBasic.Children.Add(swatch);
        }
    }

    /// <summary>Brings every part of the control into line with the current color.</summary>
    private void Refresh()
    {
        _updating = true;

        FieldHue.Fill = new SolidColorBrush(Palette.FromHsv(_hue, 1, 1));
        Preview.Background = new SolidColorBrush(_color);

        Canvas.SetLeft(FieldCursor, _saturation * Field.Width - FieldCursor.Width / 2);
        Canvas.SetTop(FieldCursor, (1 - _value) * Field.Height - FieldCursor.Height / 2);
        Canvas.SetTop(SpectrumCursor, _hue / 360 * Spectrum.Height - SpectrumCursor.Height / 2);

        TxtHex.Text = Palette.ToHex(_color);
        TxtRed.Text = _color.R.ToString();
        TxtGreen.Text = _color.G.ToString();
        TxtBlue.Text = _color.B.ToString();

        _updating = false;
    }

    /// <summary>Rebuilds the color from the three components and reports it.</summary>
    private void Emit()
    {
        _color = Palette.FromHsv(_hue, _saturation, _value);
        Refresh();
        Changed?.Invoke(_color);
    }

    private void Commit(Color color)
    {
        Color = color;
        Changed?.Invoke(color);
    }

    // =====================================================================
    //  Dragging
    // =====================================================================
    //
    //  Captured on press and tracked until release, so a drag that leaves the square keeps
    //  working - clamped at the edges rather than stopping dead, which is what every other
    //  picker does and what the hand expects.

    private void FieldDown(object sender, MouseButtonEventArgs e)
    {
        Field.CaptureMouse();
        TrackField(e.GetPosition(Field));
    }

    private void FieldMove(object sender, MouseEventArgs e)
    {
        if (Field.IsMouseCaptured) TrackField(e.GetPosition(Field));
    }

    private void FieldUp(object sender, MouseButtonEventArgs e) => Field.ReleaseMouseCapture();

    private void TrackField(Point point)
    {
        _saturation = Math.Clamp(point.X / Field.Width, 0, 1);
        _value = Math.Clamp(1 - point.Y / Field.Height, 0, 1);
        Emit();
    }

    private void SpectrumDown(object sender, MouseButtonEventArgs e)
    {
        Spectrum.CaptureMouse();
        TrackSpectrum(e.GetPosition(Spectrum));
    }

    private void SpectrumMove(object sender, MouseEventArgs e)
    {
        if (Spectrum.IsMouseCaptured) TrackSpectrum(e.GetPosition(Spectrum));
    }

    private void SpectrumUp(object sender, MouseButtonEventArgs e) => Spectrum.ReleaseMouseCapture();

    private void TrackSpectrum(Point point)
    {
        _hue = Math.Clamp(point.Y / Spectrum.Height, 0, 1) * 360;
        Emit();
    }

    // =====================================================================
    //  Typing
    // =====================================================================

    private void HexTyped(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        // Silently ignored until it is a whole color. Half of a hex value is not an error to
        // report, it is a value being typed - and rejecting it would fight the user's cursor.
        if (Palette.Parse(TxtHex.Text) is not { } color) return;

        var caret = TxtHex.CaretIndex;
        Color = color;
        TxtHex.CaretIndex = Math.Min(caret, TxtHex.Text.Length);

        Changed?.Invoke(color);
    }

    private void ChannelTyped(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        if (!byte.TryParse(TxtRed.Text, out var r)
            || !byte.TryParse(TxtGreen.Text, out var g)
            || !byte.TryParse(TxtBlue.Text, out var b)) return;

        // The box being typed in must not be rewritten underneath the caret: "5" on its way to
        // "50" would come back as "5" with the cursor moved, and the second keystroke would
        // land in the wrong place. Everything else is refreshed.
        var typing = sender as TextBox;

        _color = Color.FromRgb(r, g, b);

        var (hue, saturation, value) = Palette.ToHsv(_color);
        if (saturation > 0.001 && value > 0.001) _hue = hue;
        _saturation = saturation;
        _value = value;

        _updating = true;
        FieldHue.Fill = new SolidColorBrush(Palette.FromHsv(_hue, 1, 1));
        Preview.Background = new SolidColorBrush(_color);
        Canvas.SetLeft(FieldCursor, _saturation * Field.Width - FieldCursor.Width / 2);
        Canvas.SetTop(FieldCursor, (1 - _value) * Field.Height - FieldCursor.Height / 2);
        Canvas.SetTop(SpectrumCursor, _hue / 360 * Spectrum.Height - SpectrumCursor.Height / 2);
        TxtHex.Text = Palette.ToHex(_color);

        if (!ReferenceEquals(typing, TxtRed)) TxtRed.Text = r.ToString();
        if (!ReferenceEquals(typing, TxtGreen)) TxtGreen.Text = g.ToString();
        if (!ReferenceEquals(typing, TxtBlue)) TxtBlue.Text = b.ToString();
        _updating = false;

        Changed?.Invoke(_color);
    }
}
