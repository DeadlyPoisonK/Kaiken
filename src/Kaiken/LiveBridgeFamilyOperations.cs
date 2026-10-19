using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace Kaiken;

/// <summary>
/// Operaciones del puente en vivo que trabajan DENTRO del Editor de Familias
/// (documento de familia activo). Revit no permite conduits reales en una
/// familia cargable, así que los "tramos" se modelan como sólidos cilíndricos
/// (sweep, o forma libre si el sweep no queda bien) con conectores de conduit
/// en los extremos que se pidan — en el proyecto se conecta conduit real ahí.
/// Todas las coordenadas de entrada y salida van en milímetros, en el sistema
/// de coordenadas de la familia.
/// </summary>
public static class LiveBridgeFamilyOperations
{
    private static double ToFt(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
    private static double ToMm(double ft) => Math.Round(UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters), 1);
    private static double[] Mm(XYZ p) => new[] { ToMm(p.X), ToMm(p.Y), ToMm(p.Z) };
    private static double[] Dir(XYZ d) => new[] { Math.Round(d.X, 4), Math.Round(d.Y, 4), Math.Round(d.Z, 4) };
    private static XYZ ReadPoint(JsonElement el) => new(ToFt(el[0].GetDouble()), ToFt(el[1].GetDouble()), ToFt(el[2].GetDouble()));

    private static string? RequireFamily(Document doc) => doc.IsFamilyDocument
        ? null
        : JsonSerializer.Serialize(new { error = "El documento activo no es una familia. Abre la familia en el Editor de Familias." });

    // ---------------------------------------------------------------- inspect

    /// <summary>
    /// Fotografía de la familia: líneas (modelo/referencia/simbólicas),
    /// familias anidadas con sus conectores, conectores propios y sólidos.
    /// Sirve para decidir dónde van los tramos antes de crearlos.
    /// </summary>
    public static string Inspect(Document doc)
    {
        if (RequireFamily(doc) is string err) return err;

        var curves = new FilteredElementCollector(doc)
            .OfClass(typeof(CurveElement))
            .Cast<CurveElement>()
            .Select(ce =>
            {
                var c = ce.GeometryCurve;
                bool bound = c != null && c.IsBound;
                return new
                {
                    id = ce.Id.Value,
                    type = ce.GetType().Name,
                    isReferenceLine = SafeIsReferenceLine(ce),
                    viewSpecific = ce.ViewSpecific,
                    lineStyle = (ce.LineStyle as GraphicsStyle)?.Name ?? ce.LineStyle?.Name,
                    curveType = c?.GetType().Name,
                    start = bound ? Mm(c!.GetEndPoint(0)) : null,
                    end = bound ? Mm(c!.GetEndPoint(1)) : null,
                    lengthMm = c != null ? ToMm(c.Length) : 0,
                };
            })
            .ToList();

        var nested = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Select(fi =>
            {
                var fam = fi.Symbol?.Family;
                var bb = fi.get_BoundingBox(null);
                var conns = new List<object>();
                var cm = fi.MEPModel?.ConnectorManager;
                if (cm != null)
                {
                    foreach (Connector c in cm.Connectors)
                    {
                        double? radiusMm = null;
                        try { if (c.Shape == ConnectorProfileType.Round) radiusMm = ToMm(c.Radius); } catch { /* sin radio */ }
                        conns.Add(new
                        {
                            domain = c.Domain.ToString(),
                            shape = c.Shape.ToString(),
                            origin = Mm(c.Origin),
                            direction = Dir(c.CoordinateSystem.BasisZ),
                            diameterMm = radiusMm.HasValue ? radiusMm * 2 : null,
                        });
                    }
                }
                return new
                {
                    id = fi.Id.Value,
                    family = fam?.Name,
                    type = fi.Symbol?.Name,
                    category = fi.Category?.Name,
                    shared = fam?.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger() == 1,
                    location = (fi.Location as LocationPoint) is LocationPoint lp ? Mm(lp.Point) : null,
                    bboxMin = bb != null ? Mm(bb.Min) : null,
                    bboxMax = bb != null ? Mm(bb.Max) : null,
                    connectors = conns,
                };
            })
            .ToList();

        var ownConnectors = new FilteredElementCollector(doc)
            .OfClass(typeof(ConnectorElement))
            .Cast<ConnectorElement>()
            .Select(ce => new
            {
                id = ce.Id.Value,
                domain = ce.Domain.ToString(),
                origin = Mm(ce.Origin),
                direction = Dir(ce.CoordinateSystem.BasisZ),
            })
            .ToList();

        var forms = new FilteredElementCollector(doc)
            .OfClass(typeof(GenericForm))
            .Cast<GenericForm>()
            .Select(f =>
            {
                var bb = f.get_BoundingBox(null);
                return new
                {
                    id = f.Id.Value,
                    type = f.GetType().Name,
                    isSolid = f.IsSolid,
                    bboxMin = bb != null ? Mm(bb.Min) : null,
                    bboxMax = bb != null ? Mm(bb.Max) : null,
                };
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            family = doc.Title,
            category = doc.OwnerFamily?.FamilyCategory?.Name,
            units = "mm",
            curves,
            nested,
            ownConnectors,
            forms,
        });
    }

    private static bool? SafeIsReferenceLine(CurveElement ce)
    {
        try { return (ce as ModelCurve)?.IsReferenceLine; } catch { return null; }
    }

    // ------------------------------------------------------- tramos de conduit

    /// <summary>
    /// Crea un cilindro por tramo (start→end, mm) de diámetro diameterMm, y un
    /// conector de conduit en cada extremo marcado con connectorAtStart /
    /// connectorAtEnd, mirando hacia afuera del tramo. mode: "auto" (intenta
    /// Sweep editable; si la geometría no cae donde debe, usa forma libre),
    /// "sweep" o "freeform". Todo en una sola transacción (un solo Ctrl+Z).
    /// </summary>
    public static string CreateConduitRun(Document doc, JsonElement args)
    {
        if (RequireFamily(doc) is string err) return err;

        double diameter = ToFt(args.GetProperty("diameterMm").GetDouble());
        double connDiameter = args.TryGetProperty("connectorDiameterMm", out var cd) ? ToFt(cd.GetDouble()) : diameter;
        string mode = args.TryGetProperty("mode", out var m) ? m.GetString() ?? "auto" : "auto";
        string? materialName = args.TryGetProperty("material", out var mat) ? mat.GetString() : null;

        var warnings = new List<string>();
        var created = new List<object>();

        using var t = new Transaction(doc, "Kaiken: tramos de conduit");
        t.Start();
        try
        {
            ElementId? materialId = FindMaterial(doc, materialName, warnings);

            int index = 0;
            foreach (var seg in args.GetProperty("segments").EnumerateArray())
            {
                XYZ a = ReadPoint(seg.GetProperty("start"));
                XYZ b = ReadPoint(seg.GetProperty("end"));
                if (a.DistanceTo(b) < ToFt(1))
                {
                    warnings.Add($"Tramo {index}: largo cero, se omite.");
                    index++;
                    continue;
                }

                GenericForm? form = null;
                string kind = "";
                if (mode == "auto" || mode == "sweep")
                {
                    form = TryCreateSweep(doc, a, b, diameter / 2, warnings, index);
                    kind = "Sweep";
                }
                if (form == null && (mode == "auto" || mode == "freeform"))
                {
                    form = CreateFreeFormCylinder(doc, a, b, diameter / 2);
                    kind = "FreeForm";
                }
                if (form == null)
                {
                    warnings.Add($"Tramo {index}: no se pudo crear el sólido.");
                    index++;
                    continue;
                }

                if (materialId != null)
                    form.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(materialId);

                doc.Regenerate();

                XYZ dir = (b - a).Normalize();
                var connectors = new List<object>();
                if (seg.TryGetProperty("connectorAtStart", out var cs) && cs.GetBoolean())
                    connectors.Add(AddConduitConnector(doc, form, a, dir.Negate(), connDiameter, warnings, index, "inicio"));
                if (seg.TryGetProperty("connectorAtEnd", out var ceEnd) && ceEnd.GetBoolean())
                    connectors.Add(AddConduitConnector(doc, form, b, dir, connDiameter, warnings, index, "fin"));

                created.Add(new { segment = index, formId = form.Id.Value, kind, connectors });
                index++;
            }

            t.Commit();
            return JsonSerializer.Serialize(new { ok = true, created, warnings });
        }
        catch (Exception ex)
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            return JsonSerializer.Serialize(new { error = ex.Message, warnings });
        }
    }

    /// <summary>
    /// Sweep real (editable con "Edit Sweep"). El perfil se dibuja en el XY
    /// local y Revit lo lleva al plano perpendicular al inicio del recorrido;
    /// como eso depende de la API, se valida el BoundingBox resultante y si no
    /// calza se borra y se devuelve null (el llamador cae a forma libre).
    /// </summary>
    private static GenericForm? TryCreateSweep(Document doc, XYZ a, XYZ b, double r, List<string> warnings, int index)
    {
        try
        {
            XYZ dir = (b - a).Normalize();
            XYZ normal = Math.Abs(dir.Z) > 0.999 ? XYZ.BasisX : XYZ.BasisZ.CrossProduct(dir).Normalize();
            var sketchPlane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(normal, a));

            var path = new CurveArray();
            path.Append(Line.CreateBound(a, b));

            var loop = new CurveArray();
            loop.Append(Arc.Create(XYZ.Zero, r, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
            loop.Append(Arc.Create(XYZ.Zero, r, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
            var loops = new CurveArrArray();
            loops.Append(loop);
            var profile = doc.Application.Create.NewCurveLoopsProfile(loops);

            var sweep = doc.FamilyCreate.NewSweep(true, path, sketchPlane, profile, 0, ProfilePlaneLocation.Start);
            doc.Regenerate();

            if (BoundingBoxMatches(sweep.get_BoundingBox(null), a, b, r))
                return sweep;

            doc.Delete(sweep.Id);
            warnings.Add($"Tramo {index}: el sweep no quedó sobre la línea, se usó forma libre.");
            return null;
        }
        catch (Exception ex)
        {
            warnings.Add($"Tramo {index}: sweep falló ({ex.Message}), se usó forma libre.");
            return null;
        }
    }

    private static bool BoundingBoxMatches(BoundingBoxXYZ? bb, XYZ a, XYZ b, double r)
    {
        if (bb == null) return false;
        double tol = r + ToFt(2);
        var min = new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        var max = new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
        return Math.Abs(bb.Min.X - min.X) <= tol && Math.Abs(bb.Min.Y - min.Y) <= tol && Math.Abs(bb.Min.Z - min.Z) <= tol
            && Math.Abs(bb.Max.X - max.X) <= tol && Math.Abs(bb.Max.Y - max.Y) <= tol && Math.Abs(bb.Max.Z - max.Z) <= tol;
    }

    private static GenericForm CreateFreeFormCylinder(Document doc, XYZ a, XYZ b, double r)
    {
        XYZ dir = (b - a).Normalize();
        XYZ u = Math.Abs(dir.Z) > 0.999 ? XYZ.BasisX : XYZ.BasisZ.CrossProduct(dir).Normalize();
        XYZ v = dir.CrossProduct(u).Normalize();

        var circle = new CurveLoop();
        circle.Append(Arc.Create(a, r, 0, Math.PI, u, v));
        circle.Append(Arc.Create(a, r, Math.PI, 2 * Math.PI, u, v));

        Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { circle }, dir, a.DistanceTo(b));
        return FreeFormElement.Create(doc, solid);
    }

    /// <summary>
    /// Conector de conduit sobre la cara plana del sólido más cercana a
    /// <paramref name="point"/> cuya normal apunta en <paramref name="outward"/>.
    /// Revit lo centra en la cara; se reporta la distancia al punto pedido.
    /// </summary>
    private static object AddConduitConnector(Document doc, Element form, XYZ point, XYZ outward, double diameter,
        List<string> warnings, int index, string label)
    {
        var face = FindPlanarFace(form, point, outward);
        if (face?.Reference == null)
        {
            warnings.Add($"Tramo {index} ({label}): no encontré cara plana para el conector.");
            return new { error = "sin cara" };
        }

        var ce = ConnectorElement.CreateConduitConnector(doc, face.Reference);
        if (ce.CoordinateSystem.BasisZ.DotProduct(outward) < 0)
            ce.FlipDirection();
        SetConnectorDiameter(ce, diameter, warnings, index);

        return new
        {
            id = ce.Id.Value,
            origin = Mm(ce.Origin),
            direction = Dir(ce.CoordinateSystem.BasisZ),
            offsetFromRequestedMm = ToMm(ce.Origin.DistanceTo(point)),
        };
    }

    private static PlanarFace? FindPlanarFace(Element form, XYZ point, XYZ outward)
    {
        var options = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
        PlanarFace? best = null;
        double bestDistance = double.MaxValue;

        foreach (var solid in SolidsOf(form.get_Geometry(options)))
        {
            foreach (Face f in solid.Faces)
            {
                if (f is not PlanarFace pf || pf.FaceNormal.DotProduct(outward) < 0.99) continue;
                var bb = pf.GetBoundingBox();
                XYZ center = pf.Evaluate(new UV((bb.Min.U + bb.Max.U) / 2, (bb.Min.V + bb.Max.V) / 2));
                double d = center.DistanceTo(point);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = pf;
                }
            }
        }
        return best;
    }

    private static IEnumerable<Solid> SolidsOf(GeometryElement? geometry)
    {
        if (geometry == null) yield break;
        foreach (var obj in geometry)
        {
            if (obj is Solid s && s.Faces.Size > 0) yield return s;
            else if (obj is GeometryInstance gi)
                foreach (var inner in SolidsOf(gi.GetInstanceGeometry()))
                    yield return inner;
        }
    }

    private static void SetConnectorDiameter(ConnectorElement ce, double diameter, List<string> warnings, int index)
    {
        var pDiameter = ce.get_Parameter(BuiltInParameter.CONNECTOR_DIAMETER);
        if (pDiameter != null && !pDiameter.IsReadOnly && pDiameter.Set(diameter)) return;

        var pRadius = ce.get_Parameter(BuiltInParameter.CONNECTOR_RADIUS);
        if (pRadius != null && !pRadius.IsReadOnly && pRadius.Set(diameter / 2)) return;

        warnings.Add($"Tramo {index}: no pude fijar el diámetro del conector.");
    }

    private static ElementId? FindMaterial(Document doc, string? name, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var material = new FilteredElementCollector(doc)
            .OfClass(typeof(Material))
            .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (material == null) warnings.Add($"No existe el material '{name}' en la familia; se deja por categoría.");
        return material?.Id;
    }

    // ------------------------------------------------------------------- caja

    /// <summary>
    /// Caja rectangular por extrusión entre min y max (mm). Con
    /// wallThicknessMm crea una caja abierta arriba (muros + fondo) en vez de
    /// un bloque macizo. Sirve también para la barra (bloque delgado).
    /// </summary>
    public static string CreateBox(Document doc, JsonElement args)
    {
        if (RequireFamily(doc) is string err) return err;

        XYZ p0 = ReadPoint(args.GetProperty("min"));
        XYZ p1 = ReadPoint(args.GetProperty("max"));
        var min = new XYZ(Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), Math.Min(p0.Z, p1.Z));
        var max = new XYZ(Math.Max(p0.X, p1.X), Math.Max(p0.Y, p1.Y), Math.Max(p0.Z, p1.Z));
        double? wall = args.TryGetProperty("wallThicknessMm", out var w) && w.GetDouble() > 0 ? ToFt(w.GetDouble()) : null;
        string? materialName = args.TryGetProperty("material", out var mat) ? mat.GetString() : null;

        if (wall.HasValue && (2 * wall.Value >= max.X - min.X || 2 * wall.Value >= max.Y - min.Y || wall.Value >= max.Z - min.Z))
            return JsonSerializer.Serialize(new { error = "El espesor de muro no cabe en la caja." });

        var warnings = new List<string>();
        using var t = new Transaction(doc, "Kaiken: caja");
        t.Start();
        try
        {
            ElementId? materialId = FindMaterial(doc, materialName, warnings);
            var ids = new List<long>();

            if (wall == null)
            {
                ids.Add(Extrude(doc, new[] { Rectangle(min, max, min.Z) }, min.Z, max.Z - min.Z, materialId).Id.Value);
            }
            else
            {
                double tw = wall.Value;
                var innerMin = new XYZ(min.X + tw, min.Y + tw, min.Z);
                var innerMax = new XYZ(max.X - tw, max.Y - tw, max.Z);
                // fondo
                ids.Add(Extrude(doc, new[] { Rectangle(min, max, min.Z) }, min.Z, tw, materialId).Id.Value);
                // muros: perfil con hueco, sobre el fondo
                double z = min.Z + tw;
                ids.Add(Extrude(doc, new[] { Rectangle(min, max, z), Rectangle(innerMin, innerMax, z) }, z, max.Z - z, materialId).Id.Value);
            }

            t.Commit();
            return JsonSerializer.Serialize(new { ok = true, formIds = ids, warnings });
        }
        catch (Exception ex)
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            return JsonSerializer.Serialize(new { error = ex.Message, warnings });
        }
    }

    private static CurveArray Rectangle(XYZ min, XYZ max, double z)
    {
        var a = new XYZ(min.X, min.Y, z);
        var b = new XYZ(max.X, min.Y, z);
        var c = new XYZ(max.X, max.Y, z);
        var d = new XYZ(min.X, max.Y, z);
        var arr = new CurveArray();
        arr.Append(Line.CreateBound(a, b));
        arr.Append(Line.CreateBound(b, c));
        arr.Append(Line.CreateBound(c, d));
        arr.Append(Line.CreateBound(d, a));
        return arr;
    }

    private static Extrusion Extrude(Document doc, IEnumerable<CurveArray> loops, double z, double height, ElementId? materialId)
    {
        var profile = new CurveArrArray();
        foreach (var loop in loops) profile.Append(loop);
        var sketchPlane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z)));
        var extrusion = doc.FamilyCreate.NewExtrusion(true, profile, sketchPlane, height);
        if (materialId != null)
            extrusion.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(materialId);
        return extrusion;
    }

    // ----------------------------------------------------------------- borrar

    /// <summary>
    /// Borra sólidos y conectores creados por estas operaciones (para iterar
    /// sin Ctrl+Z). Solo acepta GenericForm y ConnectorElement: nunca toca
    /// líneas, planos de referencia ni familias anidadas.
    /// </summary>
    public static string DeleteElements(Document doc, JsonElement args)
    {
        if (RequireFamily(doc) is string err) return err;

        var ids = args.GetProperty("ids").EnumerateArray().Select(e => new ElementId(e.GetInt64())).ToList();
        var rejected = ids.Where(id => doc.GetElement(id) is not (GenericForm or ConnectorElement)).Select(id => id.Value).ToList();
        if (rejected.Count > 0)
            return JsonSerializer.Serialize(new { error = "Solo se borran sólidos o conectores.", rejected });

        using var t = new Transaction(doc, "Kaiken: borrar sólidos/conectores");
        t.Start();
        var deleted = doc.Delete(ids).Select(id => id.Value).ToList();
        t.Commit();
        return JsonSerializer.Serialize(new { ok = true, deleted });
    }
}
