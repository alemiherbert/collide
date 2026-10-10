using Collide.Units;

namespace Collide.Core.Tests.Geometry;

public class UnitsTests
{
    [Theory]
    [InlineData("40 mm", 40)]
    [InlineData("4 cm", 40)]
    [InlineData("0.04 m", 40)]
    [InlineData("1 in", 25.4)]
    [InlineData("1 ft", 304.8)]
    [InlineData("-2.5mm", -2.5)]
    public void ParsesLengthsWithMandatoryUnits(string text, double expectedMm)
    {
        Assert.Equal(expectedMm, Length.Parse(text).Millimetres, 9);
    }

    [Theory]
    [InlineData("40")]
    [InlineData("forty mm")]
    [InlineData("40 furlongs")]
    [InlineData("")]
    public void RejectsAmbiguousOrInvalidLengths(string text)
    {
        Assert.False(Length.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void RevitInternalFeetConvertExactly()
    {
        // Revit stores lengths in decimal feet; 1 ft = 304.8 mm by definition.
        var cover = Length.FromFeet(40 / 304.8);
        Assert.Equal(40, cover.Millimetres, 12);
        Assert.Equal(40 / 304.8, cover.Feet, 15);
    }

    [Fact]
    public void AnglesRequireUnits()
    {
        Assert.Equal(135, Angle.Parse("135 deg").Degrees, 9);
        Assert.Equal(Math.PI, Angle.Parse("3.141592653589793 rad").Radians, 12);
        Assert.False(Angle.TryParse("135", out _, out _));
    }
}
