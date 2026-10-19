using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace Kaiken;

/// <summary>Dónde cae el empalme sobre la troncal y en qué dirección (en planta) va la ruta desde la bajante.</summary>
public class AlcTarget
{
    public Pipe Riser = null!;
    public Pipe Trunk = null!;
    public XYZ P = XYZ.Zero;        // extremo inferior libre de la bajante
    public XYZ Top = XYZ.Zero;      // extremo opuesto de la bajante
    public XYZ Qxy = XYZ.Zero;      // punto de empalme en planta (Z de la troncal si no es vertical)
    public bool TrunkVertical;
    public double ZMin, ZMax;       // rango útil de la troncal (solo si es vertical)
    public XYZ U = XYZ.BasisX;      // dirección horizontal de la ruta
    public double L;                // distancia en planta de la bajante al empalme
    public double RiserDiameter, TrunkDiameter;
}

/// <summary>Ruta calculada: bajante recortada/alargada hasta C1, diagonal C1→C2 (si la hay) y horizontal C2→Q.</summary>
public class AlcPlan
{
    public AlcTarget Target = null!;
    public double DeltaDeg;         // ángulo de la diagonal bajo la horizontal (90 = un solo codo)
    public bool SingleElbow;
    public XYZ C1 = XYZ.Zero, C2 = XYZ.Zero, Q = XYZ.Zero;
    public double Turn1Deg, Turn2Deg;
}

/// <summary>
/// Botón "Alc" (alcantarillado): desde el extremo inferior de una bajante hasta una troncal, arma
/// bajante → codo → diagonal → codo → horizontal con 1 % de pendiente → Te/Tap en la troncal.
/// Prefiere codos de 45°; si no caben (espacio horizontal o largo de la bajante) prueba otros ángulos.
/// </summary>
public static class AlcHelpers
{
    public const double Slope = 0.01;
    private static readonly double PhiDeg = Math.Atan(Slope) * 180.0 / Math.PI;
    private static readonly double EndMargin = 20 * PontifexHelpers.CmToFeet;  // margen en los extremos de la troncal
    private static readonly double TeeExtra = 3 * PontifexHelpers.CmToFeet;

    /// <summary>Ángulos de la diagonal a probar, del más cercano a 45° al más lejano; 90 = un solo codo.</summary>
    private static readonly double[] Candidates =
        { 45, 40, 50, 35, 55, 30, 60, 25, 65, 20, 70, 15, 75, 10, 80, 85, 90 };

    private static double Dia(Pipe p) => p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0;

    public static Connector? FreeBottom(Pipe v) =>
        v.ConnectorManager.Connectors.Cast<Connector>()
            .FirstOrDefault(c => c.ConnectorType == ConnectorType.End && !c.IsConnected && c.CoordinateSystem.BasisZ.Z < -0.9);


    /// <summary>
    /// Sin codos (y Te/Tap) en las preferencias de ruteo del tipo de tubería, Revit no puede insertar
    /// accesorios y falla con "failed to insert elbow". Se avisa antes de modificar nada.
    /// </summary>
    private static void CheckRoutingPreferences(Pipe riser, Pipe trunk)
    {
        var rm = riser.PipeType?.RoutingPreferenceManager;
        if (rm != null && rm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Elbows) == 0)
            throw new InvalidOperationException(
                $"El tipo de tubería \"{riser.PipeType!.Name}\" no tiene codos en sus preferencias de ruteo (Editar tipo > Preferencias de ruteo). Carga una familia de codo y asígnala; sin ella Revit no puede insertar los codos.");

        var tm = trunk.PipeType?.RoutingPreferenceManager;
        if (tm != null && tm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions) == 0)
            throw new InvalidOperationException(
                $"El tipo de tubería de la troncal \"{trunk.PipeType!.Name}\" no tiene Te ni Tap en sus preferencias de ruteo (Uniones). Carga una familia de Te y asígnala; sin ella Revit no puede empalmar la ruta a la troncal.");
    }

    /// <summary>Valida la pareja y ubica el empalme. No modifica el modelo.</summary>
    public static AlcTarget Locate(Pipe riser, Pipe trunk)
    {
        if (riser.Id == trunk.Id) throw new InvalidOperationException("La bajante y la troncal son el mismo tubo.");
        if (riser.Location is not LocationCurve { Curve: Line rl } || Math.Abs(rl.Direction.Z) < 0.99)
            throw new InvalidOperationException("La bajante no es una tubería vertical.");
        Connector bottom = FreeBottom(riser) ??
            throw new InvalidOperationException("La bajante no tiene extremo inferior libre (ya está conectada abajo).");
        if (trunk.Location is not LocationCurve { Curve: Line tl })
            throw new InvalidOperationException("La troncal no es recta (no soportado).");

        CheckRoutingPreferences(riser, trunk);

        var t = new AlcTarget { Riser = riser, Trunk = trunk, P = bottom.Origin, RiserDiameter = Dia(riser), TrunkDiameter = Dia(trunk) };
        XYZ e0 = rl.GetEndPoint(0), e1 = rl.GetEndPoint(1);
        t.Top = e0.Z > e1.Z ? e0 : e1;

        XYZ a = tl.GetEndPoint(0), b = tl.GetEndPoint(1);
        t.TrunkVertical = Math.Abs(tl.Direction.Z) > 0.99;
        if (t.TrunkVertical)
        {
            t.Qxy = new XYZ(a.X, a.Y, 0);
            t.ZMin = Math.Min(a.Z, b.Z) + EndMargin;
            t.ZMax = Math.Max(a.Z, b.Z) - EndMargin;
            if (t.ZMin > t.ZMax) throw new InvalidOperationException("La troncal vertical es muy corta para empalmar una Te.");
        }
        else
        {
            XYZ ab = b - a;
            double lenXY2 = ab.X * ab.X + ab.Y * ab.Y;
            if (lenXY2 < 1e-9) throw new InvalidOperationException("La troncal no tiene recorrido en planta.");
            double s = ((t.P.X - a.X) * ab.X + (t.P.Y - a.Y) * ab.Y) / lenXY2;
            double len = ab.GetLength();
            if (s * len < EndMargin || (1 - s) * len < EndMargin)
                throw new InvalidOperationException(
                    "La bajante no queda frente a la troncal (su proyección cae fuera del tramo). Elige como troncal el tubo que sí la enfrenta, o la vertical que baja.");
            t.Qxy = a + s * ab;
        }

        XYZ v = new XYZ(t.Qxy.X - t.P.X, t.Qxy.Y - t.P.Y, 0);
        t.L = v.GetLength();
        if (t.L < 1e-3) throw new InvalidOperationException("La bajante está justo sobre la troncal: no hay recorrido horizontal.");
        t.U = v / t.L;
        return t;
    }

    /// <summary>
    /// Elige el ángulo y calcula la ruta. La bajante se acorta o alarga hasta el codo (C1). Si la troncal
    /// es horizontal su cota manda y la bajante se ajusta; si es vertical se conserva la bajante tal cual
    /// y la cota del empalme sale de ella (con tope en el rango de la troncal).
    /// </summary>
    public static AlcPlan Plan(AlcTarget t, ElbowTable elbows)
    {
        double ms = ElbowTable.MinStraightFt;
        double minTee = t.TrunkDiameter / 2 + 1.5 * t.RiserDiameter + TeeExtra;
        double cosPhi = Math.Cos(Math.Atan(Slope));
        string firstFail = "";

        foreach (double delta in Candidates)
        {
            bool single = delta >= 89.99;
            double turn1 = single ? 90 - PhiDeg : 90 - delta;
            double turn2 = single ? 0 : delta - PhiDeg;
            double rad = delta * Math.PI / 180.0;
            double m = single ? 0 : elbows.CtE(turn1) + elbows.CtE(turn2) + ms;
            double run = m * Math.Cos(rad), drop = m * Math.Sin(rad);

            double d = t.L - run;
            double needH = (single ? elbows.CtE(turn1) : elbows.CtE(turn2)) + minTee;
            if (d / cosPhi < needH)
            {
                if (firstFail == "") firstFail = $"falta recorrido horizontal ({(t.L - run) / PontifexHelpers.CmToFeet:0} cm disponibles, hacen falta {needH / PontifexHelpers.CmToFeet:0})";
                continue;
            }

            double zQ;
            if (t.TrunkVertical)
            {
                double keep = t.P.Z - drop - Slope * d;
                zQ = Math.Min(Math.Max(keep, t.ZMin), t.ZMax);
            }
            else
            {
                zQ = ZOnTrunk(t);
            }
            double z1 = zQ + Slope * d + drop;

            double needV = elbows.CtE(turn1) + ms;
            if (t.Top.Z - z1 < needV)
            {
                if (firstFail == "")
                    firstFail = zQ > t.Top.Z - needV
                        ? $"la troncal queda {(zQ - t.P.Z) / PontifexHelpers.CmToFeet:0} cm sobre el extremo de la bajante y no hay largo de bajante para llegar hasta allá (la ruta baja desde la bajante hacia la troncal)"
                        : "la bajante queda muy corta para alojar el codo";
                continue;
            }

            XYZ c1 = new XYZ(t.P.X, t.P.Y, z1);
            XYZ c2 = c1 + t.U * run + new XYZ(0, 0, -drop);
            return new AlcPlan
            {
                Target = t, DeltaDeg = delta, SingleElbow = single,
                C1 = c1, C2 = c2, Q = new XYZ(t.Qxy.X, t.Qxy.Y, zQ),
                Turn1Deg = turn1, Turn2Deg = turn2,
            };
        }
        throw new InvalidOperationException($"No hay ángulo con que la ruta quepa: {firstFail}.");
    }

    private static double ZOnTrunk(AlcTarget t)
    {
        var line = (Line)((LocationCurve)t.Trunk.Location).Curve;
        XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
        XYZ ab = b - a;
        double s = ((t.Qxy.X - a.X) * ab.X + (t.Qxy.Y - a.Y) * ab.Y) / (ab.X * ab.X + ab.Y * ab.Y);
        return a.Z + s * ab.Z;
    }

    /// <summary>Crea la ruta en el modelo. Debe llamarse dentro de una transacción; quien llama la revierte si lanza.</summary>
    public static void Execute(Document doc, AlcPlan p)
    {
        var t = p.Target;
        var ops = new PipeOps();

        ((LocationCurve)t.Riser.Location).Curve = Line.CreateBound(t.Top, p.C1);
        doc.Regenerate();

        MEPCurve? diag = p.SingleElbow ? null : ops.Create(doc, t.Riser, p.C1, p.C2);
        MEPCurve horiz = ops.Create(doc, t.Riser, p.C2, p.Q);
        doc.Regenerate();

        Connect(doc, t.Riser, diag ?? horiz, p.C1);
        if (diag != null) Connect(doc, diag, horiz, p.C2);

        JoinToTrunk(doc, t.Trunk, horiz, p.Q);
    }

    private static void Connect(Document doc, MEPCurve a, MEPCurve b, XYZ corner)
    {
        Connector? ca = PontifexHelpers.ConnectorAt(a, corner);
        Connector? cb = PontifexHelpers.ConnectorAt(b, corner);
        if (ca == null || cb == null)
            throw new InvalidOperationException("No se pudieron ubicar los conectores del codo tras regenerar.");
        doc.Create.NewElbowFitting(ca, cb);
    }

    /// <summary>Tap o Te según la preferencia de ruteo de la troncal (igual que Conectar Rociadores).</summary>
    private static void JoinToTrunk(Document doc, Pipe trunk, MEPCurve branch, XYZ q)
    {
        Connector branchEnd = PontifexHelpers.ConnectorAt(branch, q) ??
            throw new InvalidOperationException("No se encontró el conector de la horizontal en la troncal.");

        var junction = trunk.PipeType?.RoutingPreferenceManager?.PreferredJunctionType ?? PreferredJunctionType.Tee;
        string tapError = "";
        if (junction == PreferredJunctionType.Tap)
        {
            using var st = new SubTransaction(doc);
            st.Start();
            try
            {
                doc.Create.NewTakeoffFitting(branchEnd, trunk);
                st.Commit();
                return;
            }
            catch (Exception ex)
            {
                st.RollBack();
                tapError = $"Tap: {ex.Message}; ";
            }
        }

        try
        {
            ElementId otherId = PlumbingUtils.BreakCurve(doc, trunk.Id, q);
            var other = (Pipe)doc.GetElement(otherId);
            Connector c1 = Closest(trunk, q), c2 = Closest(other, q);
            doc.Create.NewTeeFitting(c1, c2, branchEnd);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{tapError}Te: {ex.Message}");
        }
    }

    private static Connector Closest(MEPCurve curve, XYZ pt) =>
        curve.ConnectorManager.Connectors.Cast<Connector>()
            .Where(c => c.ConnectorType == ConnectorType.End)
            .OrderBy(c => c.Origin.DistanceTo(pt)).First();
}
