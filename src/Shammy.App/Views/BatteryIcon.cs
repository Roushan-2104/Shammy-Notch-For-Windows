using System.Windows;
using System.Windows.Media;

namespace Shammy.Views;

/// <summary>An iOS-style battery glyph: green while charging, red when low.</summary>
internal sealed class BatteryIcon : FrameworkElement
{
    static readonly Brush Outline = Ui.Tokens.Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
    static readonly Brush Green = Ui.Tokens.Frozen(Color.FromRgb(0x30, 0xD1, 0x58));
    static readonly Brush Red = Ui.Tokens.Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));
    static readonly Brush White = Ui.Tokens.Frozen(Colors.White);

    int _percent = 100;
    bool _charging;

    public BatteryIcon()
    {
        Width = 25;
        Height = 12;
    }

    public void Set(int percent, bool charging)
    {
        _percent = Math.Clamp(percent, 0, 100);
        _charging = charging;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = Width - 2.5, h = Height;
        var pen = new Pen(Outline, 1.1);
        dc.DrawRoundedRectangle(null, pen, new Rect(0.55, 0.55, w - 1.1, h - 1.1), 3.2, 3.2);
        dc.DrawRoundedRectangle(Outline, null, new Rect(w + 0.6, h / 2 - 2, 1.8, 4), 0.9, 0.9);

        var fill = _charging ? Green : Shammy.Core.Cards.BatteryLevels.IsLow(_percent) ? Red : White;
        double inner = (w - 4) * _percent / 100.0;
        if (inner > 0.5) dc.DrawRoundedRectangle(fill, null, new Rect(2, 2, inner, h - 4), 1.6, 1.6);
    }

}
