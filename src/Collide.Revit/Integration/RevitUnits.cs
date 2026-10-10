using Autodesk.Revit.DB;
using Collide.Geometry;
using Collide.Units;

namespace Collide.Revit.Integration;

/// <summary>
/// The only place where Revit internal units (decimal feet) meet the engine (millimetres). Conversion uses the exact
/// definition 1 ft = 304.8 mm, identical to <c>UnitUtils.ConvertFromInternalUnits(x, UnitTypeId.Millimeters)</c>.
/// </summary>
internal static class RevitUnits
{
    public static Vec3 ToMm(XYZ p) => new(Length.FromFeet(p.X).Millimetres, Length.FromFeet(p.Y).Millimetres, Length.FromFeet(p.Z).Millimetres);

    /// <summary>Direction vectors are unitless: copy components without scaling.</summary>
    public static Vec3 ToDirection(XYZ d) => new(d.X, d.Y, d.Z);

    public static XYZ ToFeet(Vec3 mm) =>
        new(Length.FromMillimetres(mm.X).Feet, Length.FromMillimetres(mm.Y).Feet, Length.FromMillimetres(mm.Z).Feet);

    public static Length Len(double feet) => Length.FromFeet(feet);
}

/// <summary>Bar identity: "{rebarElementId}#{barPositionIndex}".</summary>
internal static class BarIds
{
    public static string Format(ElementId element, int position) => $"{element.Value}#{position}";

    public static string Element(ElementId element) => element.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParse(string barId, out long elementId, out int position)
    {
        position = 0;
        var parts = barId.Split('#');
        return long.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out elementId) &&
               (parts.Length == 1 || int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out position));
    }

    public static bool TryParseElement(string id, out ElementId elementId)
    {
        elementId = ElementId.InvalidElementId;
        if (!long.TryParse(id.Split('#')[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)) return false;
        elementId = new ElementId(value);
        return true;
    }
}
