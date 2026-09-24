using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace Kaiken;

public enum JogDir { Arriba, Abajo, Izquierda, Derecha }

public class PontifexParams
{
    public double DistanceFt = 0; // 0 = automatic minimum
    public JogDir Direction = JogDir.Abajo; // default is Abajo
    public double AngleDegStart = 45.0;
    public double AngleDegEnd = 45.0;
    public double ClearanceFt = 0; // 0 = automatic minimum

    public bool MergeClose = true;
    public double MergeGapFt { get; set; } = 30.0 / 304.8; // default 30cm
    public bool RotateTee { get; set; }
}

public class ClashInterval
{
    public double A;
    public double B;
    public List<ElementId> Clashers = new();
    public double Mid => (A + B) / 2.0;

    /// <summary>
    /// BB del obstáculo en coordenadas host (world space).
    /// Usado para calcular el offset mínimo necesario según la dirección de evasión.
    /// </summary>
    public XYZ WorldBbMin = new XYZ(+1e9, +1e9, +1e9);
    public XYZ WorldBbMax = new XYZ(-1e9, -1e9, -1e9);

    public void UnionBb(ClashInterval o)
    {
        WorldBbMin = new XYZ(Math.Min(WorldBbMin.X, o.WorldBbMin.X),
                             Math.Min(WorldBbMin.Y, o.WorldBbMin.Y),
                             Math.Min(WorldBbMin.Z, o.WorldBbMin.Z));
        WorldBbMax = new XYZ(Math.Max(WorldBbMax.X, o.WorldBbMax.X),
                             Math.Max(WorldBbMax.Y, o.WorldBbMax.Y),
                             Math.Max(WorldBbMax.Z, o.WorldBbMax.Z));
    }
}

// ---------------------------------------------------------------------------
//  Capa de abstracción por tipo de elemento MEP (tubería, ducto, escalerilla,
//  conduit). Encapsula cómo se crea, se corta y se mide cada tipo.
// ---------------------------------------------------------------------------
public abstract class MepOps
{
    public abstract MEPCurve Create(Document doc, MEPCurve template, XYZ a, XYZ b);
    public abstract double NominalSize(MEPCurve m);        // tamaño representativo (pies)
    public abstract (MEPCurve start, MEPCurve end) Break(Document doc, MEPCurve m, XYZ pt, XYZ d);

    public static MepOps? For(MEPCurve m) => m switch
    {
        Pipe => new PipeOps(),
        Duct => new DuctOps(),
        CableTray => new TrayOps(),
        Conduit => new ConduitOps(),
        _ => null,
    };

    protected static void CopyParam(Element from, Element to, BuiltInParameter bip)
    {
        var pf = from.get_Parameter(bip);
        var pt = to.get_Parameter(bip);
        if (pf != null && pt != null && !pt.IsReadOnly && pf.StorageType == StorageType.Double)
            pt.Set(pf.AsDouble());
    }

    /// <summary>Corte manual (escalerilla/conduit): acorta el original a S..pt y crea pt..E.</summary>
    protected (MEPCurve start, MEPCurve end) ManualBreak(Document doc, MEPCurve m, XYZ pt, XYZ d)
    {
        var lc = (LocationCurve)m.Location;
        var ln = (Line)lc.Curve;
        XYZ cleanPt = pt;
        IntersectionResult proj = ln.Project(pt);
        if (proj != null) cleanPt = proj.XYZPoint;

        XYZ S = ln.GetEndPoint(0), E = ln.GetEndPoint(1);

        // Lo conectado en el extremo E (codo de un salto anterior, fitting, etc.) debe
        // quedar unido al tramo nuevo pt..E. Si no se desconecta antes de acortar,
        // Revit arrastra ese fitting hasta pt y el salto anterior queda roto.
        var farRefs = new List<Connector>();
        Connector? cE = PontifexHelpers.ConnectorAt(m, E);
        if (cE != null && cE.IsConnected)
        {
            foreach (Connector r in cE.AllRefs)
                if (r.Owner != null && r.Owner.Id != m.Id &&
                    (r.ConnectorType == ConnectorType.End || r.ConnectorType == ConnectorType.Curve))
                    farRefs.Add(r);
            foreach (var r in farRefs) cE.DisconnectFrom(r);
        }

        lc.Curve = Line.CreateBound(S, cleanPt);
        var neu = Create(doc, m, cleanPt, E);
        doc.Regenerate();

        Connector? nE = PontifexHelpers.ConnectorAt(neu, E);
        if (nE != null)
            foreach (var r in farRefs)
                try { nE.ConnectTo(r); } catch { }

        // start = lado -d, end = lado +d
        return (E - cleanPt).DotProduct(d) > 0 ? (m, neu) : (neu, m);
    }

    /// <summary>Clasifica las dos mitades tras un BreakCurve por utilitario.</summary>
    protected static (MEPCurve start, MEPCurve end) Classify(MEPCurve orig, MEPCurve created, XYZ pt, XYZ d)
    {
        return PontifexHelpers.FarSign(orig, pt, d) > 0 ? (created, orig) : (orig, created);
    }

    /// <summary>
    /// After CopyElements + LocationCurve relocation, the cross-section keeps its
    /// old world-space orientation.  For rectangular elements on diagonal paths this
    /// makes the profile look "twisted".  This method computes the rotation angle
    /// needed around the new path axis to bring the cross-section back to "upright"
    /// (width horizontal, height following gravity) and applies it.
    /// </summary>
    protected static void FixCrossSectionRotation(Document doc, MEPCurve elem,
        XYZ dOld, XYZ dNew, XYZ a, XYZ b)
    {
        // If paths are (anti-)parallel, no rotation is needed
        double cross = dOld.CrossProduct(dNew).GetLength();
        if (cross < 1e-6) return;

        // "Natural up" for a path direction = projection of world-Z onto the
        // plane perpendicular to the path.
        XYZ upOld = ProjectOntoPerp(XYZ.BasisZ, dOld);
        XYZ upNew = ProjectOntoPerp(XYZ.BasisZ, dNew);
        if (upOld == null || upNew == null) return;

        // Project upOld onto the new path's perpendicular plane — this is where
        // the cross-section's "up" currently points after relocation.
        XYZ upOldInNew = ProjectOntoPerp(upOld, dNew);
        if (upOldInNew == null) return;

        // Signed angle from upOldInNew to upNew around dNew
        double dot = Math.Max(-1.0, Math.Min(1.0, upOldInNew.DotProduct(upNew)));
        double angle = Math.Acos(dot);
        if (Math.Abs(angle) < 1e-6) return;

        // Determine sign via cross product
        if (dNew.DotProduct(upOldInNew.CrossProduct(upNew)) < 0)
            angle = -angle;

        Line rotAxis = Line.CreateBound(a, a + dNew);
        ElementTransformUtils.RotateElement(doc, elem.Id, rotAxis, angle);
    }

    private static XYZ? ProjectOntoPerp(XYZ v, XYZ axis)
    {
        XYZ proj = v - axis.Multiply(v.DotProduct(axis));
        return proj.GetLength() < 1e-9 ? null : proj.Normalize();
    }
}

public class PipeOps : MepOps
{
    public override MEPCurve Create(Document doc, MEPCurve t, XYZ a, XYZ b)
    {
        var ids = ElementTransformUtils.CopyElements(doc, new[] { t.Id }.ToList(), XYZ.Zero);
        var np = (MEPCurve)doc.GetElement(ids.First());
        if (np.Location is LocationCurve lc) lc.Curve = Line.CreateBound(a, b);
        return np;
    }
    public override double NominalSize(MEPCurve m) => m.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0;
    public override (MEPCurve, MEPCurve) Break(Document doc, MEPCurve m, XYZ pt, XYZ d)
    {
        Line? ln = PontifexHelpers.Axis(m);
        XYZ cleanPt = pt;
        if (ln != null)
        {
            IntersectionResult proj = ln.Project(pt);
            if (proj != null) cleanPt = proj.XYZPoint;
        }
        ElementId id = PlumbingUtils.BreakCurve(doc, m.Id, cleanPt);
        return Classify(m, (MEPCurve)doc.GetElement(id), cleanPt, d);
    }
}

public class DuctOps : MepOps
{
    public override MEPCurve Create(Document doc, MEPCurve t, XYZ a, XYZ b)
    {
        // Get the original path direction before copying
        Line? origAxis = PontifexHelpers.Axis(t);
        XYZ dOld = origAxis != null
            ? (origAxis.GetEndPoint(1) - origAxis.GetEndPoint(0)).Normalize()
            : XYZ.BasisX;

        var ids = ElementTransformUtils.CopyElements(doc, new[] { t.Id }.ToList(), XYZ.Zero);
        var nd = (MEPCurve)doc.GetElement(ids.First());
        if (nd.Location is LocationCurve lc) lc.Curve = Line.CreateBound(a, b);

        // For rectangular ducts on non-parallel paths, fix the cross-section rotation
        XYZ dNew = (b - a).Normalize();
        FixCrossSectionRotation(doc, nd, dOld, dNew, a, b);

        return nd;
    }
    public override double NominalSize(MEPCurve m)
    {
        double dia = m.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)?.AsDouble() ?? 0;
        if (dia > 0) return dia;
        double w = m.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble() ?? 0;
        double h = m.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.AsDouble() ?? 0;
        return Math.Max(w, h);
    }
    public override (MEPCurve, MEPCurve) Break(Document doc, MEPCurve m, XYZ pt, XYZ d)
    {
        Line? ln = PontifexHelpers.Axis(m);
        XYZ cleanPt = pt;
        if (ln != null)
        {
            IntersectionResult proj = ln.Project(pt);
            if (proj != null) cleanPt = proj.XYZPoint;
        }
        ElementId id = MechanicalUtils.BreakCurve(doc, m.Id, cleanPt);
        return Classify(m, (MEPCurve)doc.GetElement(id), cleanPt, d);
    }
}

public class TrayOps : MepOps
{
    public override MEPCurve Create(Document doc, MEPCurve t, XYZ a, XYZ b)
    {
        Line? origAxis = PontifexHelpers.Axis(t);
        XYZ dOld = origAxis != null
            ? (origAxis.GetEndPoint(1) - origAxis.GetEndPoint(0)).Normalize()
            : XYZ.BasisX;

        var ids = ElementTransformUtils.CopyElements(doc, new[] { t.Id }.ToList(), XYZ.Zero);
        var nt = (MEPCurve)doc.GetElement(ids.First());
        if (nt.Location is LocationCurve lc) lc.Curve = Line.CreateBound(a, b);

        XYZ dNew = (b - a).Normalize();
        FixCrossSectionRotation(doc, nt, dOld, dNew, a, b);

        return nt;
    }
    public override double NominalSize(MEPCurve m)
    {
        double w = m.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM)?.AsDouble() ?? 0;
        double h = m.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM)?.AsDouble() ?? 0;
        return Math.Max(w, h);
    }
    public override (MEPCurve, MEPCurve) Break(Document doc, MEPCurve m, XYZ pt, XYZ d) => ManualBreak(doc, m, pt, d);
}

public class ConduitOps : MepOps
{
    public override MEPCurve Create(Document doc, MEPCurve t, XYZ a, XYZ b)
    {
        var ids = ElementTransformUtils.CopyElements(doc, new[] { t.Id }.ToList(), XYZ.Zero);
        var nc = (MEPCurve)doc.GetElement(ids.First());
        if (nc.Location is LocationCurve lc) lc.Curve = Line.CreateBound(a, b);
        return nc;
    }
    public override double NominalSize(MEPCurve m) => m.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)?.AsDouble() ?? 0;
    public override (MEPCurve, MEPCurve) Break(Document doc, MEPCurve m, XYZ pt, XYZ d) => ManualBreak(doc, m, pt, d);
}

// ---------------------------------------------------------------------------
public static class PontifexHelpers
{
    public static readonly BuiltInCategory[] ObstacleCats =
    {
        BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting,
        BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting,
        BuiltInCategory.OST_CableTray, BuiltInCategory.OST_Conduit,
        BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns,
        BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors,
    };

    public static Line? Axis(MEPCurve m) => (m.Location as LocationCurve)?.Curve as Line;

    public static double FarSign(MEPCurve m, XYZ pt, XYZ d)
    {
        var ln = Axis(m);
        if (ln == null) return 0;
        XYZ e0 = ln.GetEndPoint(0), e1 = ln.GetEndPoint(1);
        XYZ far = e0.DistanceTo(pt) > e1.DistanceTo(pt) ? e0 : e1;
        return (far - pt).DotProduct(d);
    }

    public static HashSet<ElementId> ConnectedElementIds(MEPCurve m)
    {
        var set = new HashSet<ElementId>();
        var cm = m.ConnectorManager;
        if (cm == null) return set;
        foreach (Connector c in cm.Connectors)
            foreach (Connector r in c.AllRefs)
                if (r.Owner != null && r.Owner.Id != m.Id) set.Add(r.Owner.Id);
        return set;
    }

    /// <summary>
    /// Intersecta el sólido de <paramref name="e"/> con las líneas de prueba (en el espacio
    /// del documento de <paramref name="e"/>) y devuelve el intervalo [tmin, tmax] sobre el
    /// eje host S + t·d. <paramref name="toHost"/> lleva puntos del documento de e al host
    /// (null si e está en el documento host). También devuelve la BB de e en coordenadas host.
    /// </summary>
    private static (double tmin, double tmax, XYZ wMin, XYZ wMax) IntersectSolidCurve(
        Element e, IList<Line> lines, XYZ S, XYZ d, Transform? toHost = null)
    {
        double tmin = double.MaxValue;
        double tmax = double.MinValue;
        XYZ wMin = new XYZ(+1e9, +1e9, +1e9);
        XYZ wMax = new XYZ(-1e9, -1e9, -1e9);
        Options opt = new Options { DetailLevel = ViewDetailLevel.Fine };
        GeometryElement geom = e.get_Geometry(opt);
        if (geom == null) return (tmin, tmax, wMin, wMax);
        var options = new SolidCurveIntersectionOptions();

        void ProcessSolid(Solid s)
        {
            if (s.Volume <= 0) return;
            foreach (Line line in lines)
            {
                SolidCurveIntersection? sci;
                try { sci = s.IntersectWithCurve(line, options); }
                catch { continue; }
                if (sci == null) continue;
                for (int i = 0; i < sci.SegmentCount; i++)
                {
                    Curve c = sci.GetCurveSegment(i);
                    XYZ p0 = c.GetEndPoint(0);
                    XYZ p1 = c.GetEndPoint(1);
                    if (toHost != null) { p0 = toHost.OfPoint(p0); p1 = toHost.OfPoint(p1); }
                    double t1 = (p0 - S).DotProduct(d);
                    double t2 = (p1 - S).DotProduct(d);
                    tmin = Math.Min(tmin, Math.Min(t1, t2));
                    tmax = Math.Max(tmax, Math.Max(t1, t2));
                }
            }
        }

        void ProcessGeometry(GeometryElement ge)
        {
            foreach (GeometryObject go in ge)
            {
                if (go is Solid s) ProcessSolid(s);
                else if (go is GeometryInstance gi) ProcessGeometry(gi.GetInstanceGeometry());
            }
        }
        ProcessGeometry(geom);

        if (tmin <= tmax)
        {
            BoundingBoxXYZ? bb = e.get_BoundingBox(null);
            if (bb != null)
            {
                foreach (var corner in Corners(bb))
                {
                    XYZ wc = toHost != null ? toHost.OfPoint(corner) : corner;
                    wMin = new XYZ(Math.Min(wMin.X, wc.X), Math.Min(wMin.Y, wc.Y), Math.Min(wMin.Z, wc.Z));
                    wMax = new XYZ(Math.Max(wMax.X, wc.X), Math.Max(wMax.Y, wc.Y), Math.Max(wMax.Z, wc.Z));
                }
            }
        }
        return (tmin, tmax, wMin, wMax);
    }

    /// <summary>
    /// Líneas de prueba paralelas al eje: el eje mismo y 8 líneas en el contorno de la
    /// sección (radio = tamaño/2). Así se detectan cruces donde el obstáculo toca el cuerpo
    /// del elemento pero no su eje (p.ej. dos tuberías que se cruzan a distinta altura).
    /// </summary>
    private static List<Line> ProbeLines(XYZ S, XYZ E, XYZ d, double sizeFt)
    {
        var lines = new List<Line> { Line.CreateBound(S, E) };
        double r = sizeFt / 2.0;
        if (r < 1e-4) return lines;

        XYZ u = OffsetDir(d, JogDir.Arriba);
        if (u.GetLength() < 0.5) u = OffsetDir(d, JogDir.Derecha);
        if (u.GetLength() < 0.5) return lines;
        XYZ v = d.CrossProduct(u).Normalize();

        for (int k = 0; k < 8; k++)
        {
            double a = k * Math.PI / 4.0;
            XYZ o = (u * Math.Cos(a) + v * Math.Sin(a)) * r;
            lines.Add(Line.CreateBound(S + o, E + o));
        }
        return lines;
    }

    public static List<ClashInterval> FindClashes(Document doc, MEPCurve mep, View view, PontifexParams p)
    {
        var result = new List<ClashInterval>();
        Line? axis = Axis(mep);
        if (axis == null) return result;
        XYZ S = axis.GetEndPoint(0), E = axis.GetEndPoint(1);
        double L = S.DistanceTo(E);
        if (L < 1e-6) return result;
        XYZ d = (E - S).Normalize();
        double endGuard = Math.Max(p.ClearanceFt, 0.05);

        // ── 1. Obstáculos en el documento HOST ──────────────────────────────────
        var connected = ConnectedElementIds(mep);
        var clashers = new FilteredElementCollector(doc, view.Id)
            .WherePasses(new ElementMulticategoryFilter(ObstacleCats))
            .WherePasses(new ElementIntersectsElementFilter(mep))
            .Where(e => e.Id != mep.Id && !connected.Contains(e.Id))
            .ToList();

        double size = MepOps.For(mep)?.NominalSize(mep) ?? 0;
        var probes = ProbeLines(S, E, d, size);

        foreach (var c in clashers)
        {
            var (tmin, tmax, wMin, wMax) = IntersectSolidCurve(c, probes, S, d);
            tmin = Math.Max(0, tmin); tmax = Math.Min(L, tmax);
            if (tmax <= tmin) continue;
            if (tmax < endGuard || tmin > L - endGuard) continue;
            result.Add(new ClashInterval { A = tmin, B = tmax, Clashers = { c.Id }, WorldBbMin = wMin, WorldBbMax = wMax });
        }

        // ── 2. Obstáculos en archivos Revit VINCULADOS (RevitLink) ─────────────
        // Las líneas de prueba se llevan al espacio del link y se intersectan con el
        // sólido real de cada elemento; los puntos de corte vuelven al host. Así se
        // obtiene el intervalo EXACTO donde el elemento cruza (sin proyectar toda la
        // BB de losas o muros sobre el eje).
        var linkInstances = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>();

        foreach (var linkInst in linkInstances)
        {
            var linkDoc = linkInst.GetLinkDocument();
            if (linkDoc == null) continue;   // link no cargado

            Transform linkXf    = linkInst.GetTotalTransform();
            Transform linkXfInv = linkXf.Inverse;

            // Líneas de prueba llevadas al espacio del link. Los t se calculan siempre
            // sobre el eje HOST (S, d): IntersectSolidCurve devuelve los puntos al host.
            var localProbes = probes.Select(l => (Line)l.CreateTransformed(linkXfInv)).ToList();

            // Prefiltro por caja: sin él se calcula la geometría de TODOS los muros,
            // losas y vigas del link, lo que en modelos grandes es lentísimo.
            XYZ la = linkXfInv.OfPoint(S), lb = linkXfInv.OfPoint(E);
            double pad = size / 2.0 + 0.1;
            var outline = new Outline(
                new XYZ(Math.Min(la.X, lb.X) - pad, Math.Min(la.Y, lb.Y) - pad, Math.Min(la.Z, lb.Z) - pad),
                new XYZ(Math.Max(la.X, lb.X) + pad, Math.Max(la.Y, lb.Y) + pad, Math.Max(la.Z, lb.Z) + pad));

            var linkObstacles = new FilteredElementCollector(linkDoc)
                .WherePasses(new ElementMulticategoryFilter(ObstacleCats))
                .WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .ToList();

            foreach (var lo in linkObstacles)
            {
                var (tmin, tmax, wMin, wMax) = IntersectSolidCurve(lo, localProbes, S, d, linkXf);

                tmin = Math.Max(0, tmin); tmax = Math.Min(L, tmax);
                if (tmax <= tmin + 1e-6) continue;
                if (tmax < endGuard || tmin > L - endGuard) continue;

                result.Add(new ClashInterval { A = tmin, B = tmax, Clashers = { linkInst.Id }, WorldBbMin = wMin, WorldBbMax = wMax });
            }
        }

        result = result.OrderBy(r => r.A).ToList();
        return p.MergeClose ? Merge(result, p.MergeGapFt) : result;
    }

    /// <summary>
    /// Intersección paramétrica de un rayo (S + t·d) con un AABB [bMin, bMax].
    /// Para transformaciones rígidas los t en espacio BB son iguales a los t en
    /// espacio host (distancias en pies). Devuelve los t de entrada y salida.
    /// </summary>
    private static bool RayAabb(XYZ S, XYZ d, XYZ bMin, XYZ bMax,
        out double tEntry, out double tExit)
    {
        tEntry = double.MinValue;
        tExit  = double.MaxValue;

        double[] sA  = { S.X,    S.Y,    S.Z    };
        double[] dA  = { d.X,    d.Y,    d.Z    };
        double[] mnA = { bMin.X, bMin.Y, bMin.Z };
        double[] mxA = { bMax.X, bMax.Y, bMax.Z };

        for (int i = 0; i < 3; i++)
        {
            if (Math.Abs(dA[i]) < 1e-10)
            {
                // Rayo paralelo a esta cara: debe estar dentro del slab
                if (sA[i] < mnA[i] - 1e-6 || sA[i] > mxA[i] + 1e-6) return false;
            }
            else
            {
                double t1 = (mnA[i] - sA[i]) / dA[i];
                double t2 = (mxA[i] - sA[i]) / dA[i];
                if (t1 > t2) (t1, t2) = (t2, t1);
                tEntry = Math.Max(tEntry, t1);
                tExit  = Math.Min(tExit,  t2);
            }
        }
        return tEntry <= tExit + 1e-6;
    }

    private static List<ClashInterval> Merge(List<ClashInterval> items, double gap)
    {
        var merged = new List<ClashInterval>();
        foreach (var it in items)
        {
            if (merged.Count > 0 && it.A - merged[^1].B <= gap)
            {
                merged[^1].B = Math.Max(merged[^1].B, it.B);
                merged[^1].Clashers.AddRange(it.Clashers);
                // La BB también se une: si no, el desvío se calcula solo con el primer
                // obstáculo y el salto puede chocar con el segundo.
                merged[^1].UnionBb(it);
            }
            else merged.Add(it);
        }
        return merged;
    }

    private static IEnumerable<XYZ> Corners(BoundingBoxXYZ bb)
    {
        Transform t = bb.Transform;
        XYZ mn = bb.Min, mx = bb.Max;
        foreach (var x in new[] { mn.X, mx.X })
            foreach (var y in new[] { mn.Y, mx.Y })
                foreach (var z in new[] { mn.Z, mx.Z })
                    yield return t.OfPoint(new XYZ(x, y, z));
    }

    public static XYZ OffsetDir(XYZ pipeDir, JogDir dir)
    {
        XYZ z = XYZ.BasisZ;
        XYZ raw = dir switch
        {
            JogDir.Arriba => z,
            JogDir.Abajo => -z,
            JogDir.Izquierda => SafeNormal(z.CrossProduct(pipeDir)),
            JogDir.Derecha => SafeNormal(pipeDir.CrossProduct(z)),
            _ => z,
        };
        XYZ perp = raw - pipeDir.Multiply(raw.DotProduct(pipeDir));
        return perp.GetLength() < 1e-6 ? XYZ.Zero : perp.Normalize();
    }

    private static XYZ SafeNormal(XYZ v) => v.GetLength() < 1e-9 ? XYZ.BasisY : v.Normalize();

    public static Connector? ConnectorAt(MEPCurve mep, XYZ pt, double tol = 1e-3)
    {
        foreach (Connector c in mep.ConnectorManager.Connectors)
            if (c.Origin.DistanceTo(pt) < tol) return c;
        return null;
    }

    public const double CmToFeet = 1.0 / 30.48;

    /// <summary>
    /// Espacio mínimo del salto dado el center-to-end REAL del codo (D, en pies) y el
    /// ángulo. Regla: entre dos codos debe caber 2·D + 1 cm de recta. En la diagonal esa
    /// longitud proyectada al desvío exige desvío ≥ sin(θ)·(2D + 1cm). El margen a cada
    /// lado del obstáculo debe ser ≥ D + 0.5 cm. Devuelve (desvío mínimo, margen mínimo).
    /// </summary>
    public static (double minDistFt, double minMarginFt) MinBridge(double elbowCtEFt, double angleDeg)
    {
        double th = angleDeg * Math.PI / 180.0;
        double minStraight = 1.0 * CmToFeet;                         // 1 cm de recta entre codos
        double D = Math.Max(elbowCtEFt, 0.02);
        double minDist = Math.Sin(th) * (2 * D + minStraight);
        double minMargin = D + minStraight / 2.0;
        return (minDist, minMargin);
    }

    /// <summary>Estimación geométrica del codo (fallback si no se pudo medir): radio ≈ tamaño.</summary>
    public static double EstimateElbow(double sizeFt, double angleDeg) =>
        Math.Max(sizeFt, 0.02) * Math.Tan(angleDeg * Math.PI / 360.0);
}
