using System.Globalization;

namespace Collide.Units;

/// <summary>A volume stored in cubic millimetres.</summary>
public readonly struct Volume : IEquatable<Volume>, IComparable<Volume>
{
    private readonly double _mm3;

    private Volume(double cubicMillimetres) => _mm3 = cubicMillimetres;

    public static Volume Zero => default;
    public static Volume FromCubicMillimetres(double value) => new(value);
    public static Volume FromCubicMetres(double value) => new(value * 1e9);

    public double CubicMillimetres => _mm3;
    public double CubicCentimetres => _mm3 / 1000.0;
    public double CubicMetres => _mm3 / 1e9;

    public static Volume operator +(Volume a, Volume b) => new(a._mm3 + b._mm3);
    public static Volume operator *(Volume a, double k) => new(a._mm3 * k);

    public bool Equals(Volume other) => _mm3.Equals(other._mm3);
    public override bool Equals(object? obj) => obj is Volume v && Equals(v);
    public override int GetHashCode() => _mm3.GetHashCode();
    public int CompareTo(Volume other) => _mm3.CompareTo(other._mm3);

    public override string ToString() => _mm3.ToString("0.###", CultureInfo.InvariantCulture) + " mm³";
}
