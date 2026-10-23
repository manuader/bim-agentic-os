namespace Bim.Core;

// ADR0008 option A: typed units exist at API boundaries only. Persisted records keep raw
// metre doubles, so none of these types may appear as a property of a serialized record.
public readonly partial record struct Length
{
    public const double MillimetresPerMetre = 1000;
    public static Length FromMetres(double metres) => new(metres);
    // Division is correctly rounded, so FromMillimetres(400).Metres is exactly 0.4.
    public static Length FromMillimetres(double millimetres) => new(millimetres / MillimetresPerMetre);
    public double Millimetres => Metres * MillimetresPerMetre;
}

public readonly record struct Area
{
    public double SquareMetres { get; }
    public Area(double squareMetres) { if (!double.IsFinite(squareMetres)) throw new ArgumentException("Non-finite area"); SquareMetres = squareMetres; }
    public static Area FromSquareMetres(double squareMetres) => new(squareMetres);
    public static Area Of(Length width, Length depth) => new(width.Metres * depth.Metres);
}

public readonly record struct Angle
{
    public double Radians { get; }
    public Angle(double radians) { if (!double.IsFinite(radians)) throw new ArgumentException("Non-finite angle"); Radians = radians; }
    public static Angle FromRadians(double radians) => new(radians);
    public static Angle FromDegrees(double degrees) => new(degrees * Math.PI / 180);
    public double Degrees => Radians * 180 / Math.PI;
}

public readonly record struct Point2(Length X, Length Y);

public static class LengthUnits
{
    public const string Metres = "m";
    public const string Millimetres = "mm";

    // Explicit unit tags only; anything else (including feet) is rejected instead of guessed.
    public static Length ToLength(double value, string unit) => unit switch
    {
        Metres => Length.FromMetres(value),
        Millimetres => Length.FromMillimetres(value),
        _ => throw new ArgumentException("unsupported_length_unit"),
    };

    // Only eliminate less than one millionth millimetre of binary conversion noise.
    public static double NormalizeMillimetres(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("nonfinite_millimetres");
        var integer = Math.Round(value);
        return Math.Abs(value - integer) <= 1e-6 ? integer : value;
    }
}
