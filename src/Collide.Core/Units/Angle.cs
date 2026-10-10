using System.Globalization;

namespace Collide.Units;

/// <summary>A plane angle stored in radians.</summary>
public readonly struct Angle : IEquatable<Angle>, IComparable<Angle>
{
    private readonly double _rad;

    private Angle(double radians) => _rad = radians;

    public static Angle Zero => default;
    public static Angle FromRadians(double value) => new(value);
    public static Angle FromDegrees(double value) => new(value * Math.PI / 180.0);

    public double Radians => _rad;
    public double Degrees => _rad * 180.0 / Math.PI;

    public static bool operator <(Angle a, Angle b) => a._rad < b._rad;
    public static bool operator >(Angle a, Angle b) => a._rad > b._rad;
    public static bool operator <=(Angle a, Angle b) => a._rad <= b._rad;
    public static bool operator >=(Angle a, Angle b) => a._rad >= b._rad;
    public static bool operator ==(Angle a, Angle b) => a._rad.Equals(b._rad);
    public static bool operator !=(Angle a, Angle b) => !a._rad.Equals(b._rad);

    public bool Equals(Angle other) => _rad.Equals(other._rad);
    public override bool Equals(object? obj) => obj is Angle a && Equals(a);
    public override int GetHashCode() => _rad.GetHashCode();
    public int CompareTo(Angle other) => _rad.CompareTo(other._rad);

    public override string ToString() => Degrees.ToString("0.###", CultureInfo.InvariantCulture) + " deg";

    /// <summary>Parses "135 deg", "135°" or "2.356 rad". A unit is mandatory.</summary>
    public static Angle Parse(string text)
    {
        if (TryParse(text, out var value, out var error)) return value;
        throw new FormatException(error);
    }

    public static bool TryParse(string? text, out Angle value, out string? error)
    {
        value = default;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Angle value is empty.";
            return false;
        }

        var s = text.Trim();
        var split = 0;
        while (split < s.Length && (char.IsDigit(s[split]) || s[split] is '.' or '-' or '+')) split++;
        var unit = s[split..].Trim().ToLowerInvariant();
        if (!double.TryParse(s[..split], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            error = $"'{text}' does not start with a valid number.";
            return false;
        }

        switch (unit)
        {
            case "deg": case "degrees": case "°": value = FromDegrees(number); return true;
            case "rad": case "radians": value = FromRadians(number); return true;
            default:
                error = $"'{text}' must specify 'deg' or 'rad'.";
                return false;
        }
    }
}
