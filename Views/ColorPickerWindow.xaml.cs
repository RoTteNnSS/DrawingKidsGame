using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ColorKids.Views;

/// <summary>
/// Color picker complet : spectre SV, slider teinte, slider alpha, sliders RGB, hex, aperçu.
/// </summary>
public partial class ColorPickerWindow : Window
{
    // ── État ──────────────────────────────────────────────────────────────────
    private double _hue   = 0;    // 0–360
    private double _sat   = 1;    // 0–1
    private double _val   = 1;    // 0–1
    private byte   _alpha = 255;
    private bool   _updating;

    /// <summary>Couleur sélectionnée par l'utilisateur après OK.</summary>
    public Color SelectedColor { get; private set; } = Colors.Red;

    // ── Brushes mutables (non-frozen) créées en code-behind ──────────────────
    private readonly LinearGradientBrush _svHueBrush;
    private readonly LinearGradientBrush _alphaBrush;
    private readonly SolidColorBrush     _previewBrush = new();

    public ColorPickerWindow(Color initial)
    {
        // Créer les brushes AVANT InitializeComponent pour éviter NullReferenceException
        // si les sliders déclenchent ValueChanged pendant le chargement XAML
        _svHueBrush = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Colors.White, 0),
            new GradientStop(Colors.Red,   1),
        }, new Point(0,0), new Point(1,0));

        _alphaBrush = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(0,0,0,0), 0),
            new GradientStop(Colors.Red, 1),
        }, new Point(0,0), new Point(1,0));

        InitializeComponent();

        SvCanvas.Background = _svHueBrush;
        AlphaTrackBorder.Background = _alphaBrush;

        Loaded += (_, _) =>
        {
            PreviewBorder.Background = _previewBrush;
            SetFromColor(initial);
        };
    }

    // ── Initialisation depuis une couleur ─────────────────────────────────────

    private void SetFromColor(Color c)
    {
        _alpha = c.A;
        RgbToHsv(c.R, c.G, c.B, out _hue, out _sat, out _val);
        RefreshAll();
    }

    // ── Spectre SV ────────────────────────────────────────────────────────────

    private void OnSvMouseDown(object sender, MouseButtonEventArgs e)
    {
        ((UIElement)sender).CaptureMouse();
        UpdateSvFromMouse(e.GetPosition(SvCanvas));
    }

    private void OnSvMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        UpdateSvFromMouse(e.GetPosition(SvCanvas));
    }

    private void OnSvMouseUp(object sender, MouseButtonEventArgs e)
    {
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void UpdateSvFromMouse(Point p)
    {
        double w = SvCanvas.ActualWidth;
        double h = SvCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        _sat = Math.Clamp(p.X / w, 0, 1);
        _val = Math.Clamp(1.0 - p.Y / h, 0, 1);
        RefreshAll();
    }

    // ── Sliders ───────────────────────────────────────────────────────────────

    private void OnHueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || _svHueBrush is null) return;
        _hue = e.NewValue;
        RefreshAll();
    }

    private void OnAlphaChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || _alphaBrush is null) return;
        _alpha = (byte)Math.Clamp((int)e.NewValue, 0, 255);
        RefreshAll();
    }

    private void OnRgbChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || _svHueBrush is null || RSlider is null || GSlider is null || BSlider is null) return;
        byte r = (byte)Math.Clamp((int)RSlider.Value, 0, 255);
        byte g = (byte)Math.Clamp((int)GSlider.Value, 0, 255);
        byte b = (byte)Math.Clamp((int)BSlider.Value, 0, 255);
        RgbToHsv(r, g, b, out _hue, out _sat, out _val);
        SyncRgbBoxes(r, g, b);
        RefreshHueAndSvCursor();
        RefreshHueStop();
        RefreshPreview();
    }

    // ── TextBox hex / RGB ─────────────────────────────────────────────────────

    private void OnRgbBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not System.Windows.Controls.TextBox tb) return;
        if (!byte.TryParse(tb.Text, out byte v)) { RefreshAll(); return; }
        byte r = (byte)RSlider.Value, g = (byte)GSlider.Value, b = (byte)BSlider.Value;
        switch (tb.Tag as string)
        {
            case "R": r = v; break;
            case "G": g = v; break;
            case "B": b = v; break;
        }
        RgbToHsv(r, g, b, out _hue, out _sat, out _val);
        RefreshAll();
    }

    private void OnRgbBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnRgbBoxLostFocus(sender, e);
            e.Handled = true;
        }
    }

    private void OnHexBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var hex = HexBox.Text.TrimStart('#');
        if (hex.Length == 6 && TryParseHex(hex, out byte r, out byte g, out byte b))
        {
            RgbToHsv(r, g, b, out _hue, out _sat, out _val);
            RefreshAll();
        }
        else
        {
            RefreshAll();
        }
    }

    private void OnHexBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnHexBoxLostFocus(sender, e);
            e.Handled = true;
        }
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    private void RefreshAll()
    {
        if (_svHueBrush is null || _alphaBrush is null || SvCanvas is null || RSlider is null) return;
        _updating = true;
        try
        {
            HsvToRgb(_hue, _sat, _val, out byte r, out byte g, out byte b);
            var pureHue = HsvToColor(_hue, 1, 1);

            // Spectre SV — couleur du coin (hue pur)
            _svHueBrush.GradientStops[1].Color = pureHue;

            // Curseur SV
            double svW = SvCanvas.ActualWidth;
            double svH = SvCanvas.ActualHeight;
            System.Windows.Controls.Canvas.SetLeft(SvCursor, _sat * svW - 7);
            System.Windows.Controls.Canvas.SetTop(SvCursor,  (1 - _val) * svH - 7);

            // Alpha stop
            _alphaBrush.GradientStops[1].Color = Color.FromRgb(r, g, b);

            // Sliders
            HueSlider.Value   = _hue;
            AlphaSlider.Value = _alpha;
            RSlider.Value = r; GSlider.Value = g; BSlider.Value = b;

            // TextBoxes
            SyncRgbBoxes(r, g, b);
            HexBox.Text = $"{r:X2}{g:X2}{b:X2}";

            // Preview
            var final = Color.FromArgb(_alpha, r, g, b);
            _previewBrush.Color = final;
            SelectedColor = final;
        }
        finally
        {
            _updating = false;
        }
    }

    private void RefreshHueStop()
    {
        _svHueBrush.GradientStops[1].Color = HsvToColor(_hue, 1, 1);
    }

    private void RefreshHueAndSvCursor()
    {
        HueSlider.Value = _hue;
        double svW = SvCanvas.ActualWidth;
        double svH = SvCanvas.ActualHeight;
        System.Windows.Controls.Canvas.SetLeft(SvCursor, _sat * svW - 7);
        System.Windows.Controls.Canvas.SetTop(SvCursor,  (1 - _val) * svH - 7);
    }

    private void RefreshPreview()
    {
        HsvToRgb(_hue, _sat, _val, out byte r, out byte g, out byte b);
        var final = Color.FromArgb(_alpha, r, g, b);
        _previewBrush.Color = final;
        SelectedColor = final;
    }

    private void SyncRgbBoxes(byte r, byte g, byte b)
    {
        RBox.Text = r.ToString();
        GBox.Text = g.ToString();
        BBox.Text = b.ToString();
    }

    // ── Boutons ───────────────────────────────────────────────────────────────

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    // ── Conversions HSV ↔ RGB ─────────────────────────────────────────────────

    private static void RgbToHsv(byte r, byte g, byte b,
        out double h, out double s, out double v)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf));
        double min = Math.Min(rf, Math.Min(gf, bf));
        double delta = max - min;

        v = max;
        s = max == 0 ? 0 : delta / max;

        if (delta == 0) { h = 0; return; }
        if (max == rf)      h = 60 * (((gf - bf) / delta) % 6);
        else if (max == gf) h = 60 * ((bf - rf) / delta + 2);
        else                h = 60 * ((rf - gf) / delta + 4);
        if (h < 0) h += 360;
    }

    private static void HsvToRgb(double h, double s, double v,
        out byte r, out byte g, out byte b)
    {
        var c = HsvToColor(h, s, v);
        r = c.R; g = c.G; b = c.B;
    }

    private static Color HsvToColor(double h, double s, double v)
    {
        double c  = v * s;
        double x  = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m  = v - c;
        double r1, g1, b1;

        if      (h < 60)  { r1 = c; g1 = x; b1 = 0; }
        else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
        else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
        else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
        else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
        else              { r1 = c; g1 = 0; b1 = x; }

        return Color.FromRgb(
            (byte)Math.Clamp((r1 + m) * 255, 0, 255),
            (byte)Math.Clamp((g1 + m) * 255, 0, 255),
            (byte)Math.Clamp((b1 + m) * 255, 0, 255));
    }

    private static bool TryParseHex(string hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (hex.Length != 6) return false;
        if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint v))
            return false;
        r = (byte)(v >> 16);
        g = (byte)(v >> 8);
        b = (byte)v;
        return true;
    }
}
