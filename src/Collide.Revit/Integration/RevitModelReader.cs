using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Collide.Geometry;
using Collide.Model;
using Collide.Monitoring;
using Collide.Units;
using Serilog;
using RevitElementId = Autodesk.Revit.DB.ElementId;

namespace Collide.Revit.Integration;

/// <summary>
/// Reads reinforcement and hosts from a Revit document into immutable engine records.
/// MUST be called inside a valid Revit API context (command or external event); never from a background thread.
/// </summary>
internal sealed class RevitModelReader
{
    /// <summary>Optional instance parameter giving the bar layer label used by intentional-contact rules.</summary>
    public const string LayerParameterName = "Collide Layer";

    /// <summary>Optional instance parameter overriding the inferred bar role (e.g. "OpeningTrimmer", "Starter").</summary>
    public const string RoleParameterName = "Collide Role";

    private static readonly BuiltInCategory[] HostCategories =
    [
        BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Floors,
        BuiltInCategory.OST_Walls, BuiltInCategory.OST_StructuralFoundation,
    ];

    private readonly Document _doc;
    private readonly ILogger _log;

    public RevitModelReader(Document doc, ILogger log)
    {
        _doc = doc;
        _log = log;
    }

    public static string ModelIdOf(Document doc) => string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;

    // ------------------------------------------------------------------ full snapshot

    public ReinforcementModel ReadAll(MonitoringScope scope, UIDocument? uiDocument)
    {
        var inScope = ScopeFilter(scope, uiDocument);
        var bars = new List<RebarBar>();
        var sets = new List<RebarSet>();
        var hostIds = new HashSet<long>();

        foreach (var rebar in new FilteredElementCollector(_doc).OfClass(typeof(Rebar)).Cast<Rebar>())
        {
            if (!inScope(rebar)) continue;
            var (b, s) = ReadRebar(rebar);
            bars.AddRange(b);
            if (s is not null) sets.Add(s);
            hostIds.Add(rebar.GetHostId().Value);
        }

        foreach (var ris in new FilteredElementCollector(_doc).OfClass(typeof(RebarInSystem)).Cast<RebarInSystem>())
        {
            if (!inScope(ris)) continue;
            var (b, s) = ReadRebarInSystem(ris);
            bars.AddRange(b);
            if (s is not null) sets.Add(s);
            hostIds.Add(ris.GetHostId().Value);
        }

        var hosts = new List<StructuralHost>();
        foreach (var category in HostCategories)
        {
            foreach (var element in new FilteredElementCollector(_doc).OfCategory(category).WhereElementIsNotElementType())
            {
                if (!inScope(element) && !hostIds.Contains(element.Id.Value)) continue;
                if (ReadHost(element) is { } host) hosts.Add(host);
            }
        }

        var accessories = new FilteredElementCollector(_doc).OfClass(typeof(RebarCoupler)).Cast<RebarCoupler>()
            .Where(c => inScope(c)).Select(c => ReadCoupler(c)).Where(a => a is not null).Select(a => a!).ToList();

        return ReinforcementModel.Create(ModelIdOf(_doc), bars, hosts, sets, accessories: accessories);
    }

    // ------------------------------------------------------------------ incremental

    public ModelDelta ReadElements(ChangeBatch batch, UIDocument? uiDocument)
    {
        var inScope = ScopeFilter(batch.Scope, uiDocument);
        var bars = new List<RebarBar>();
        var sets = new List<RebarSet>();
        var hosts = new Dictionary<long, StructuralHost>();
        var accessories = new List<Accessory>();
        var removed = new List<string>(batch.DeletedElementIds);
        var replaced = new List<string>(batch.DeletedElementIds);

        foreach (var id in batch.UpsertedElementIds)
        {
            if (!BarIds.TryParseElement(id, out var elementId)) continue;
            var element = _doc.GetElement(elementId);
            replaced.Add(id);
            if (element is null || !element.IsValidObject || !inScope(element))
            {
                removed.Add(id);
                continue;
            }

            switch (element)
            {
                case Rebar rebar:
                {
                    var (b, s) = ReadRebar(rebar);
                    bars.AddRange(b);
                    if (s is not null) sets.Add(s);
                    break;
                }
                case RebarInSystem ris:
                {
                    var (b, s) = ReadRebarInSystem(ris);
                    bars.AddRange(b);
                    if (s is not null) sets.Add(s);
                    break;
                }
                case RebarCoupler coupler:
                    if (ReadCoupler(coupler) is { } acc) accessories.Add(acc);
                    break;
                case FamilyInstance { Host: { } insertHost } when IsInsert(element):
                    if (ReadHost(insertHost) is { } h1) hosts[insertHost.Id.Value] = h1;
                    break;
                case Opening { Host: { } openingHost }:
                    if (ReadHost(openingHost) is { } h2) hosts[openingHost.Id.Value] = h2;
                    break;
                default:
                    if (IsHostCategory(element) && ReadHost(element) is { } h3) hosts[element.Id.Value] = h3;
                    break;
            }
        }

        return new ModelDelta
        {
            UpsertBars = bars,
            UpsertSets = sets,
            UpsertHosts = hosts.Values.ToArray(),
            UpsertAccessories = accessories,
            RemovedElementIds = removed.Distinct().ToArray(),
            ReplacedElementIds = replaced.Distinct().ToArray(),
        };
    }

    /// <summary>Reads the current geometry of specific bars ("elementId#position").</summary>
    public IReadOnlyList<RebarBar?> ReadBars(IReadOnlyList<string> barIds)
    {
        var cache = new Dictionary<long, IReadOnlyList<RebarBar>>();
        return barIds.Select(id =>
        {
            if (!BarIds.TryParse(id, out var elementId, out _)) return null;
            if (!cache.TryGetValue(elementId, out var bars))
            {
                bars = _doc.GetElement(new RevitElementId(elementId)) switch
                {
                    Rebar r => ReadRebar(r).Bars,
                    RebarInSystem ris => ReadRebarInSystem(ris).Bars,
                    _ => [],
                };
                cache[elementId] = bars;
            }

            return bars.FirstOrDefault(b => b.Id == id);
        }).ToArray();
    }

    // ------------------------------------------------------------------ elements

    private (IReadOnlyList<RebarBar> Bars, RebarSet? Set) ReadRebar(Rebar rebar)
    {
        var type = _doc.GetElement(rebar.GetTypeId()) as RebarBarType;
        if (type is null) return ([], null);
        var hostElement = _doc.GetElement(rebar.GetHostId());
        var shape = _doc.GetElement(rebar.GetShapeId()) as RebarShape;
        var role = RoleOf(rebar, hostElement, shape?.RebarStyle);
        var elementId = BarIds.Element(rebar.Id);
        var hookStart = rebar.GetHookTypeId(0) != RevitElementId.InvalidElementId;
        var hookEnd = rebar.GetHookTypeId(1) != RevitElementId.InvalidElementId;
        var n = rebar.NumberOfBarPositions;
        var bars = new List<RebarBar>();
        for (var i = 0; i < n; i++)
        {
            if (!rebar.DoesBarExistAtPosition(i)) continue;
            var curves = rebar.GetTransformedCenterlineCurves(false, false, false, MultiplanarOption.IncludeAllMultiplanarCurves, i);
            var id = BarIds.Format(rebar.Id, i);
            bars.Add(new RebarBar
            {
                Id = id,
                ElementId = elementId,
                PositionIndex = i,
                SetId = n > 1 ? elementId : null,
                Mark = rebar.get_Parameter(BuiltInParameter.REBAR_NUMBER)?.AsString(),
                ShapeCode = shape?.Name,
                NominalDiameter = RevitUnits.Len(type.BarNominalDiameter),
                ModelDiameter = RevitUnits.Len(type.BarModelDiameter),
                Role = role,
                Layer = rebar.LookupParameter(LayerParameterName)?.AsString(),
                HostId = hostElement is null ? null : BarIds.Element(hostElement.Id),
                Path = RevitGeometry.ToPath(curves, hookStart, hookEnd, _log, id),
            });
        }

        RebarSet? set = null;
        if (n > 1)
        {
            Vec3? distribution = null;
            if (rebar.IsRebarShapeDriven())
            {
                var path = rebar.GetShapeDrivenAccessor().GetDistributionPath();
                distribution = RevitUnits.ToDirection((path.GetEndPoint(1) - path.GetEndPoint(0)).Normalize());
            }

            set = new RebarSet
            {
                Id = elementId,
                ElementId = elementId,
                HostId = hostElement is null ? null : BarIds.Element(hostElement.Id),
                Role = role,
                BarIds = bars.Select(b => b.Id).ToArray(),
                DeclaredQuantity = rebar.Quantity,
                LayoutRule = rebar.LayoutRule.ToString(),
                Spacing = rebar.LayoutRule is RebarLayoutRule.MaximumSpacing or RebarLayoutRule.NumberWithSpacing ? RevitUnits.Len(rebar.MaxSpacing) : null,
                DistributionDirection = distribution,
            };
        }

        return (bars, set);
    }

    private (IReadOnlyList<RebarBar> Bars, RebarSet? Set) ReadRebarInSystem(RebarInSystem ris)
    {
        var type = _doc.GetElement(ris.GetTypeId()) as RebarBarType;
        if (type is null) return ([], null);
        var hostElement = _doc.GetElement(ris.GetHostId());
        var elementId = BarIds.Element(ris.Id);
        var n = ris.NumberOfBarPositions;
        var bars = new List<RebarBar>();
        for (var i = 0; i < n; i++)
        {
            if (!ris.DoesBarExistAtPosition(i)) continue;
            var id = BarIds.Format(ris.Id, i);
            var curves = ris.GetTransformedCenterlineCurves(false, false, false, i);
            bars.Add(new RebarBar
            {
                Id = id,
                ElementId = elementId,
                PositionIndex = i,
                SetId = elementId,
                Mark = ris.get_Parameter(BuiltInParameter.REBAR_NUMBER)?.AsString(),
                NominalDiameter = RevitUnits.Len(type.BarNominalDiameter),
                ModelDiameter = RevitUnits.Len(type.BarModelDiameter),
                Role = BarRole.Distribution,
                Layer = ris.LookupParameter(LayerParameterName)?.AsString(),
                HostId = hostElement is null ? null : BarIds.Element(hostElement.Id),
                Path = RevitGeometry.ToPath(curves, false, false, _log, id),
                // Bars of area/path reinforcement cannot be moved individually through the API used here.
                IsDesignLocked = true,
            });
        }

        var set = new RebarSet
        {
            Id = elementId,
            ElementId = elementId,
            HostId = hostElement is null ? null : BarIds.Element(hostElement.Id),
            Role = BarRole.Distribution,
            BarIds = bars.Select(b => b.Id).ToArray(),
        };
        return (bars, set);
    }

    private Accessory? ReadCoupler(RebarCoupler coupler)
    {
        if (RevitGeometry.BoxOf(coupler) is not { } box) return null;
        var axis = box.HalfExtents.X >= box.HalfExtents.Y && box.HalfExtents.X >= box.HalfExtents.Z ? 0 : box.HalfExtents.Y >= box.HalfExtents.Z ? 1 : 2;
        var dir = box.Axis(axis);
        var half = box.HalfExtents[axis];
        var radius = Math.Max(box.HalfExtents[(axis + 1) % 3], box.HalfExtents[(axis + 2) % 3]);
        var attached = new List<string>();
        foreach (var data in coupler.GetCoupledReinforcementData())
        {
            if (data is RebarReinforcementData rd && _doc.GetElement(rd.RebarId) is Rebar r)
                attached.AddRange(ReadRebar(r).Bars.Select(b => b.Id));
        }

        return new Accessory
        {
            Id = BarIds.Element(coupler.Id),
            ElementId = BarIds.Element(coupler.Id),
            Kind = AccessoryKind.Coupler,
            AxisStart = box.Center - dir * half,
            AxisEnd = box.Center + dir * half,
            OuterDiameter = Length.FromMillimetres(2 * radius),
            AttachedBarIds = attached,
            GeometryApproximate = true,
        };
    }

    public StructuralHost? ReadHost(Element element)
    {
        var category = CategoryOf(element);
        if (category == HostCategory.Unknown) return null;
        var options = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false };
        var solid = RevitGeometry.Solids(element.get_Geometry(options)).OrderByDescending(s => s.Volume).FirstOrDefault();
        if (solid is null) return null;

        var voids = VoidsOf(element);
        Vec3? wallOrientation = element is Wall wall ? RevitUnits.ToDirection(wall.Orientation) : null;
        FaceTag TagOf(Vec3 n)
        {
            if (n.Z > 0.7071) return FaceTag.Top;
            if (n.Z < -0.7071) return FaceTag.Bottom;
            if (wallOrientation is { } o) return Vec3.Dot(n, o) > 0.7071 ? FaceTag.Exterior : Vec3.Dot(n, o) < -0.7071 ? FaceTag.Interior : FaceTag.End;
            return FaceTag.Side;
        }

        var (region, fidelity, note) = RevitGeometry.HostRegion(element, solid, voids, TagOf);
        if (note is not null) _log.Information("Host {Host}: {Note}", element.Id.Value, note);
        (Vec3, Vec3)? axis = element.Location is LocationCurve { Curve: { } curve } && category is HostCategory.Beam or HostCategory.Column
            ? (RevitUnits.ToMm(curve.GetEndPoint(0)), RevitUnits.ToMm(curve.GetEndPoint(1)))
            : null;
        IReadOnlyList<Vec3>? localAxes = null;
        if (element is Wall { Location: LocationCurve { Curve: Line wallLine } } w)
        {
            var x = RevitUnits.ToDirection(wallLine.Direction);
            var y = RevitUnits.ToDirection(w.Orientation);
            localAxes = [x, y, Vec3.Cross(x, y).Normalized()];
        }

        return new StructuralHost
        {
            Id = BarIds.Element(element.Id),
            Category = category,
            Name = string.IsNullOrEmpty(element.Name) ? BarIds.Element(element.Id) : $"{element.Name} [{element.Id.Value}]",
            Level = _doc.GetElement(element.LevelId)?.Name,
            Region = region,
            Fidelity = fidelity,
            Voids = voids,
            Axis = axis,
            LocalAxes = localAxes,
        };
    }

    private IReadOnlyList<HostVoid> VoidsOf(Element element)
    {
        var voids = new List<HostVoid>();
        if (element is HostObject hostObject)
        {
            foreach (var insertId in hostObject.FindInserts(true, false, false, false))
            {
                var insert = _doc.GetElement(insertId);
                if (insert is null || RevitGeometry.BoxOf(insert) is not { } box) continue;
                voids.Add(new HostVoid(BarIds.Element(insertId), VoidKind.Opening, box, insert.Name));
            }
        }

        if (element.get_BoundingBox(null) is { } hostBox)
        {
            var outline = new Outline(hostBox.Min, hostBox.Max);
            foreach (var shaft in new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_ShaftOpening).WherePasses(new BoundingBoxIntersectsFilter(outline)))
            {
                if (RevitGeometry.BoxOf(shaft) is { } box) voids.Add(new HostVoid(BarIds.Element(shaft.Id), VoidKind.Opening, box, "Shaft opening"));
            }
        }

        return voids.GroupBy(v => v.Id).Select(g => g.First()).ToArray();
    }

    // ------------------------------------------------------------------ classification helpers

    private static HostCategory CategoryOf(Element element) => element.Category?.BuiltInCategory switch
    {
        BuiltInCategory.OST_StructuralFraming => HostCategory.Beam,
        BuiltInCategory.OST_StructuralColumns => HostCategory.Column,
        BuiltInCategory.OST_Floors => HostCategory.Slab,
        BuiltInCategory.OST_Walls => HostCategory.Wall,
        BuiltInCategory.OST_StructuralFoundation => FoundationCategory(element),
        _ => HostCategory.Unknown,
    };

    private static HostCategory FoundationCategory(Element element)
    {
        var name = (element as FamilyInstance)?.Symbol?.FamilyName ?? element.Name;
        if (name.Contains("pile cap", StringComparison.OrdinalIgnoreCase)) return HostCategory.PileCap;
        if (name.Contains("pile", StringComparison.OrdinalIgnoreCase)) return HostCategory.Pile;
        return HostCategory.Footing;
    }

    private static bool IsHostCategory(Element element) => CategoryOf(element) != HostCategory.Unknown;

    private static bool IsInsert(Element element) =>
        element.Category?.BuiltInCategory is BuiltInCategory.OST_Doors or BuiltInCategory.OST_Windows;

    private static BarRole RoleOf(Element rebar, Element? host, RebarStyle? style)
    {
        var explicitRole = rebar.LookupParameter(RoleParameterName)?.AsString();
        if (explicitRole is not null && Enum.TryParse<BarRole>(explicitRole, true, out var parsed)) return parsed;
        var category = host is null ? HostCategory.Unknown : CategoryOf(host);
        if (style == RebarStyle.StirrupTie) return category == HostCategory.Column ? BarRole.Tie : BarRole.Stirrup;
        return category switch
        {
            HostCategory.Beam or HostCategory.Column or HostCategory.Pile or HostCategory.BoundaryElement or HostCategory.CouplingBeam => BarRole.Longitudinal,
            HostCategory.Slab or HostCategory.Wall or HostCategory.Footing or HostCategory.PileCap => BarRole.Distribution,
            _ => BarRole.Unknown,
        };
    }

    private Func<Element, bool> ScopeFilter(MonitoringScope scope, UIDocument? uiDocument)
    {
        switch (scope.Kind)
        {
            case MonitoringScopeKind.ActiveView when uiDocument?.ActiveView is { } view:
                var visible = new FilteredElementCollector(_doc, view.Id).ToElementIds().Select(i => i.Value).ToHashSet();
                return e => visible.Contains(e.Id.Value);
            case MonitoringScopeKind.ActiveSelection when uiDocument is not null:
                var selected = uiDocument.Selection.GetElementIds().Select(i => i.Value).ToHashSet();
                return e => selected.Contains(e.Id.Value) || (e is Rebar r && selected.Contains(r.GetHostId().Value));
            case MonitoringScopeKind.CurrentLevel when uiDocument?.ActiveView?.GenLevel is { } level:
                return e => e.LevelId == level.Id || (e is Rebar r && _doc.GetElement(r.GetHostId())?.LevelId == level.Id)
                                                  || (e is RebarInSystem ris && _doc.GetElement(ris.GetHostId())?.LevelId == level.Id);
            default:
                return _ => true;
        }
    }
}
