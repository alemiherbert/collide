namespace Collide.Geometry;

/// <summary>How a proximity value was obtained.</summary>
public enum ProximityMethod
{
    /// <summary>Exact swept-sphere (capsule) distance between two primitives.</summary>
    Capsule,

    /// <summary>Capsule distance refined at free bar ends using exact flat-ended cylinder distances (convex 1-D minimisation).</summary>
    FlatEndRefined,
}

/// <summary>Measured proximity between two solid primitives. Distances in millimetres.</summary>
public readonly record struct SegmentProximity(
    double SurfaceDistance,
    double CentrelineDistance,
    Vec3 PointA,
    Vec3 PointB,
    double ParamA,
    double ParamB,
    double AngleRadians,
    ProximityMethod Method,
    double ErrorBound)
{
    /// <summary>Penetration depth along the closest-point normal (0 if separated).</summary>
    public double Penetration => Math.Max(0, -SurfaceDistance);
}

public enum VolumeMethod
{
    None,
    AnalyticParallelCylinders,
    VoxelSampling,
}

public readonly record struct OverlapVolume(double CubicMillimetres, VolumeMethod Method, int Samples);

/// <summary>
/// Geometry kernel abstraction. The default <see cref="CapsuleGeometryKernel"/> is dependency-free; a kernel backed
/// by a CAD/BIM solid modeller (e.g. Revit's <c>BooleanOperationsUtils</c>) can be substituted behind this interface.
/// Implementations must be thread-safe and deterministic.
/// </summary>
public interface IGeometryKernel
{
    string Name { get; }

    /// <summary>Plain-language statement of the kernel's exactness, included in reports.</summary>
    string Limitations { get; }

    BarSolid CreateSolid(string id, BarPath path, double physicalDiameter, double chordTolerance);

    SegmentProximity Measure(in SolidSegment a, in SolidSegment b);

    OverlapVolume EstimateOverlapVolume(BarSolid a, BarSolid b, Aabb region);
}
