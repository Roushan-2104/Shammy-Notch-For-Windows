using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;
using Shammy.Core.Notch;

namespace Shammy.Views;

/// <summary>
/// The cat around the pet's eyes: two ears, slit pupils, whiskers and a small nose. The eyes are drawn where and
/// how they always were (the notch pet, the activity costumes and the large face each have their own); these
/// parts are placed against an 11-wide eye, so one drawing serves all three. Off when the pet style is Eyes.
/// </summary>
internal static class CatParts
{
    static readonly Brush Pink = Frozen(Color.FromRgb(0xFF, 0x9D, 0xB0));
    static readonly Brush PupilInk = Frozen(Color.FromRgb(0x0C, 0x0D, 0x10));

    // Drawn for the left eye, with (0,0) at that eye's top-left corner: the ear leans outward, its tip a third
    // of the way in from the outer edge. The right ear is the mirror image.
    static readonly Geometry EarOuter = Parsed("M-1.6,-0.3 Q-2.1,-5.1 0.6,-7.7 Q5.2,-5.5 7.6,-1.9 Q3,-2.1 -1.6,-0.3 Z");
    static readonly Geometry EarInner = Parsed("M0.3,-1.9 Q0,-3.7 1.3,-5.1 Q3.6,-3.7 4.9,-2.6 Q2.5,-2.6 0.3,-1.9 Z");
    // Two whiskers for the left side, with (0,0) at the eye's outer edge, level with its middle.
    static readonly Geometry WhiskerLines = Parsed("M-2.4,1.2 L-7.4,-0.1 M-2.4,3.9 L-7.1,5.2");
    static readonly Geometry NoseShape = Parsed("M-1.3,0 H1.3 L0,1.7 Z");

    /// <summary>How far below the middle of the eyes the nose sits.</summary>
    public const double NoseDrop = 3.7;

    public static bool On => Screens.PetLook.Kind == PetKind.Cat;

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static Geometry Parsed(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    /// <summary>
    /// One ear. Place it at its eye's top-left corner (as if the eye were 11 wide); it draws upward from there.
    /// It folds outward, stretches and drops from where it meets the head.
    /// </summary>
    public sealed class Ear : Canvas
    {
        const double BaseX = 3, BaseY = -1.1;

        readonly ScaleTransform _height = new(1, 1, BaseX, BaseY);
        readonly RotateTransform _tilt = new(0, BaseX, BaseY);
        readonly TranslateTransform _drop = new();
        readonly Path _outer = new() { Data = EarOuter, StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round };
        double _rest;

        public Ear(bool right, Brush fill)
        {
            IsHitTestVisible = false;
            Children.Add(_outer);
            Children.Add(new Path { Data = EarInner, Fill = Pink, Stroke = Pink, StrokeThickness = 0.6, StrokeLineJoin = PenLineJoin.Round });
            var group = new TransformGroup { Children = { _height, _tilt, _drop } };
            // The mirror comes last, so "outward" means the same for both ears.
            if (right) group.Children.Add(new ScaleTransform(-1, 1, 5.5, 0));
            RenderTransform = group;
            Fill = fill;
        }

        public Brush Fill
        {
            set => _outer.Fill = _outer.Stroke = value;
        }

        /// <summary>Tilt outward in degrees, height (1 at rest) and drop in pixels.</summary>
        public void Hold(double tilt, double height, double drop)
        {
            _rest = tilt;
            _tilt.Angle = -tilt;
            _height.ScaleY = height;
            _drop.Y = drop;
        }

        /// <summary>A flick away from the resting tilt (0 puts it back).</summary>
        public void Flick(double degrees) => _tilt.Angle = -(_rest + degrees);
    }

    /// <summary>
    /// The whiskers of one side. Place it at the eye's outer edge, level with the middle of the eye; they pivot
    /// from where they meet the face.
    /// </summary>
    public sealed class Whiskers : Canvas
    {
        readonly RotateTransform _lift;
        readonly Path _lines = new() { Data = WhiskerLines, StrokeThickness = 1.1, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = 0.82 };
        double _rest;

        public Whiskers(bool right, Brush stroke)
        {
            IsHitTestVisible = false;
            _lift = new RotateTransform(0, -2.4, 2.5);
            Children.Add(_lines);
            var group = new TransformGroup { Children = { _lift } };
            if (right) group.Children.Add(new ScaleTransform(-1, 1, 0, 0));
            RenderTransform = group;
            Stroke = stroke;
        }

        public Brush Stroke
        {
            set => _lines.Stroke = value;
        }

        /// <summary>Raised (negative) or drooping (positive), in degrees.</summary>
        public void Hold(double lift)
        {
            _rest = lift;
            _lift.Angle = lift;
        }

        public void Twitch(double degrees) => _lift.Angle = _rest + degrees;
    }

    /// <summary>The nose: a small pink triangle. Place its top middle between the eyes, <see cref="NoseDrop"/> below their middle.</summary>
    public static Path Nose() => new()
    {
        Data = NoseShape,
        Fill = Pink,
        Stroke = Pink,
        StrokeThickness = 0.7,
        StrokeLineJoin = PenLineJoin.Round,
        IsHitTestVisible = false,
    };

    /// <summary>
    /// A pupil for an eye of this size, drawn around its own (0,0): place it at the middle of the eye, a touch
    /// low. Null when the eye is too low to show one, or the pet is not a cat.
    /// </summary>
    public static Path? Pupil(double eyeWidth, double eyeHeight, double open)
    {
        if (!On || open <= 0) return null;
        var (halfWidth, halfHeight) = CatLooks.Pupil(eyeWidth, eyeHeight, open);
        if (halfWidth <= 0) return null;
        return new Path { Data = Geometry.Parse(CatLooks.PupilPath(halfWidth, halfHeight)), Fill = PupilInk, IsHitTestVisible = false };
    }

    /// <summary>Where the pupil's centre sits below the eye's own middle.</summary>
    public const double PupilLow = 0.4;
}
