using System.Globalization;

namespace Collide.Units;

/// <summary>
/// A physical length. Stored internally in millimetres. All conversions are explicit;
/// Revit's internal unit (decimal feet) must be converted with <see cref="FromFeet"/> at the adapter boundary.
/// </summary>
public readonly struct Length : IEquatable<Length>, IComparable<Length>
{
    public const double MillimetresPerFoot = 304.8;
    public const double MillimetresPerInch = 25.4;

    private readonly double _mm;

    private Length(double millimetres) => _mm = millimetres;

    public static Length Zero => default;

    public static Length FromMillimetres(double value) => new(value);
    public static Length FromCentimetres(double value) => new(value * 10.0);
    public static Length FromMetres(double value) => new(value * 1000.0);
    public static Length FromFeet(double value) => new(value * MillimetresPerFoot);
    public static Length FromInches(double value) => new(value * MillimetresPerInch);

    public double Millimetres => _mm;
    public double Metres => _mm / 1000.0;
    public double Feet => _mm / MillimetresPerFoot;
    public double Inches => _mm / MillimetresPerInch;

    public bool IsFinite => double.IsFinite(_mm);

    public static Length operator +(Length a, Length b) => new(a._mm + b._mm);
    public static Length operator -(Length a, Length b) => new(a._mm - b._mm);
    public static Length operator -(Length a) => new(-a._mm);
    public static Length operator *(Length a, double k) => new(a._mm * k);
    public static Length operator *(double k, Length a) => new(a._mm * k);
    public static Length operator /(Length a, double k) => new(a._mm / k);
    public static double operator /(Length a, Length b) => a._mm / b._mm;
    public static bool operator <(Length a, Length b) => a._mm < b._mm;
    public static bool operator >(Length a, Length b) => a._mm > b._mm;
    public static bool operator <=(Length a, Length b) => a._mm <= b._mm;
    public static bool operator >=(Length a, Length b) => a._mm >= b._mm;
    public static bool operator ==(Length a, Length b) => a._mm.Equals(b._mm);
    public static bool operator !=(Length a, Length b) => !a._mm.Equals(b._mm);

    public static Length Max(Length a, Length b) => a._mm >= b._mm ? a : b;
    public static Length Min(Length a, Length b) => a._mm <= b._mm ? a : b;
    public Length Abs() => new(Math.Abs(_mm));

    public bool Equals(Length other) => _mm.Equals(other._mm);
    public override bool Equals(object? obj) => obj is Length l && Equals(l);
    public override int GetHashCode() => _mm.GetHashCode();
    public int CompareTo(Length other) => _mm.CompareTo(other._mm);

    public override string ToString() => _mm.ToString("0.###", CultureInfo.InvariantCulture) + " mm";

    /// <summary>
    /// Parses a length with a mandatory unit, e.g. "40 mm", "4 cm", "0.04 m", "1.5 in", "0.125 ft".
    /// A bare number is rejected so that configuration files can never be ambiguous about units.
    /// </summary>
    public static Length Parse(string text)
    {
        if (TryParse(text, out var value, out var error)) return value;
        throw new FormatException(error);
    }

    public static bool TryParse(string? text, out Length value, out string? error)
    {
        value = default;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Length value is empty.";
            return false;
        }

        var s = text.Trim();
        var split = 0;
        while (split < s.Length && (char.IsDigit(s[split]) || s[split] is '.' or '-' or '+' or 'e' or 'E')) split++;

        var numberPart = s[..split].Trim();
        var unitPart = s[split..].Trim().ToLowerInvariant();
        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
        {
            error = $"'{text}' does not start with a valid number.";
            return false;
        }

        switch (unitPart)
        {
            case "mm": value = FromMillimetres(number); return true;
            case "cm": value = FromCentimetres(number); return true;
            case "m": value = FromMetres(number); return true;
            case "in": case "inch": case "inches": case "\"": value = FromInches(number); return true;
            case "ft": case "feet": case "foot": case "'": value = FromFeet(number); return true;
            case "":
                error = $"'{text}' has no unit. Specify one of mm, cm, m, in, ft.";
                return false;
            default:
                error = $"'{text}' has unknown unit '{unitPart}'. Use mm, cm, m, in or ft.";
                return false;
        }
    }
}
