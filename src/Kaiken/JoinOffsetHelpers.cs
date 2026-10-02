using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Kaiken;

/// <summary>Geometría medida entre los dos tramos a unir, antes de decidir el ángulo de codo.</summary>
public class JoinOffsetGeometry
{
    public Connector ConnA = null!;
    public Connector ConnB = null!;
    public XYZ DirA = XYZ.Zero;      // dirección de salida (outward) del conector abierto de "a"
    public XYZ OffsetVec = XYZ.Zero; // componente perpendicular a DirA entre los dos ejes
    public double OffsetCm;
    public double GapFt;             // distancia a lo largo de DirA entre los dos extremos abiertos (≤ 0 si se traslapan)
    public double LengthAFt, LengthBFt;
}

/// <summary>
/// Lógica del botón "Unir": conecta dos tramos MEP paralelos que están a distinta altura
/// (o con cualquier otro desnivel perpendicular a su eje) mediante un salto de dos codos
/// del mismo ángulo (45°, 90° o el que mejor quepa) y un tramo intermedio diagonal/vertical.
/// Ninguno de los dos tramos originales se desplaza verticalmente: "a" queda intacto y
/// "b" sólo se estira a lo largo de su propio eje hasta encontrarse con el salto.
/// </summary>
public static class JoinOffsetHelpers
{
    private const double ParallelTol = 1e-4;
    private const double MinOffsetFt = 0.01; // ~3 mm: por debajo de esto no vale la pena un salto

    private static List<Connector> OpenConnectors(MEPCurve m) =>
        m.ConnectorManager?.Connectors.Cast<Connector>().Where(c => !c.IsConnected).ToList() ?? new List<Connector>();

    /// <summary>Valida la pareja y mide el desnivel entre sus ejes. No modifica el modelo.</summary>
    public static JoinOffsetGeometry Measure(MEPCurve a, MEPCurve b)
    {
        if (a.Location is not LocationCurve lcA || lcA.Curve is not Line)
            throw new InvalidOperationException("El primer tramo no es recto (no soportado).");
        if (b.Location is not LocationCurve lcB || lcB.Curve is not Line)
            throw new InvalidOperationException("El segundo tramo no es recto (no soportado).");

        var opensA = OpenConnectors(a);
        var opensB = OpenConnectors(b);
        if (opensA.Count == 0) throw new InvalidOperationException("El primer tramo no tiene extremos libres.");
        if (opensB.Count == 0) throw new InvalidOperationException("El segundo tramo no tiene extremos libres.");

        // Par de conectores abiertos más cercano entre sí.
        Connector ca = opensA[0], cb = opensB[0];
        double best = double.MaxValue;
        foreach (var ci in opensA)
            foreach (var cj in opensB)
            {
                double d = ci.Origin.DistanceTo(cj.Origin);
                if (d < best) { best = d; ca = ci; cb = cj; }
            }

        XYZ dirA = ca.CoordinateSystem.BasisZ.Normalize();
        XYZ dirB = cb.CoordinateSystem.BasisZ.Normalize();

        double cross = dirA.CrossProduct(dirB).GetLength();
        if (cross > ParallelTol)
            throw new InvalidOperationException("Los dos tramos no son paralelos entre sí: este botón es para saltos entre tramos que corren en la misma dirección a distinta altura. Si se cruzan en ángulo, únelos directamente con un codo.");

        XYZ w = cb.Origin - ca.Origin;
        XYZ offsetVec = w - dirA.Multiply(w.DotProduct(dirA));
        double offsetFt = offsetVec.GetLength();

        if (offsetFt < MinOffsetFt)
            throw new InvalidOperationException("Los dos tramos ya están prácticamente alineados; no hace falta un salto de codos.");

        return new JoinOffsetGeometry
        {
            ConnA = ca,
            ConnB = cb,
            DirA = dirA,
            OffsetVec = offsetVec,
            OffsetCm = offsetFt / PontifexHelpers.CmToFeet,
            GapFt = w.DotProduct(dirA),
            LengthAFt = ((Line)lcA.Curve).Length,
            LengthBFt = ((Line)lcB.Curve).Length,
        };
    }

    /// <summary>
    /// Opción "Cualquier °": elige el ángulo entero de los dos codos según el desnivel y la
    /// distancia entre los extremos. Se busca que el salto llene justo el espacio entre ambos
    /// (atan(desnivel / distancia)), sin pasar del mayor ángulo con que la diagonal aloja los
    /// dos codos ni bajar de 15° (salvo que no quepa otro). Si los extremos se traslapan, se usa
    /// el mayor ángulo que cabe, para acortar "b" lo menos posible.
    /// </summary>
    public static double ChooseAngle(JoinOffsetGeometry geo, ElbowTable elbows)
    {
        const double PreferredMinAngle = 15.0;
        double h = geo.OffsetVec.GetLength();

        int maxAngle = elbows.MaxAngle(h);
        if (maxAngle == 0)
            throw new InvalidOperationException(
                $"El desnivel ({geo.OffsetCm:0.0} cm) es muy chico para dos codos de este tipo, ni siquiera a {ElbowTable.MinAngle}°.");

        double fit = geo.GapFt > 1e-6 ? Math.Atan2(h, geo.GapFt) * 180.0 / Math.PI : 90.0;
        double angle = Math.Floor(Math.Min(fit, maxAngle));
        angle = Math.Max(angle, Math.Min(PreferredMinAngle, maxAngle));

        double need = elbows.CtE(angle) + ElbowTable.MinStraightFt;
        double shortenB = Math.Max(0, Run(h, angle) - geo.GapFt);
        if (geo.LengthAFt < need)
            throw new InvalidOperationException(
                $"El primer tramo es muy corto para alojar un codo de {angle:0}° (necesita {need / PontifexHelpers.CmToFeet:0} cm).");
        if (geo.LengthBFt - shortenB < need)
            throw new InvalidOperationException(
                $"Con codos de {angle:0}° el segundo tramo queda muy corto: los extremos están demasiado traslapados para este desnivel.");
        return angle;
    }

    /// <summary>Largo a lo largo del eje de la diagonal de un desvío h con codos a angleDeg.</summary>
    private static double Run(double h, double angleDeg) =>
        angleDeg >= 89.999 ? 0.0 : h / Math.Tan(angleDeg * Math.PI / 180.0);

    /// <summary>
    /// Crea el salto: estira "b" a lo largo de su propio eje, crea el tramo intermedio
    /// (vertical a 90°, diagonal al ángulo dado en otro caso) y los dos codos.
    /// </summary>
    public static void Execute(Document doc, MEPCurve a, MEPCurve b, JoinOffsetGeometry geo, double angleDeg)
    {
        // El tramo intermedio avanza a lo largo del eje desnivel / tan(θ) (mismo ángulo en
        // ambos codos); a 90° es puramente perpendicular.
        double axialRun = Run(geo.OffsetVec.GetLength(), angleDeg);

        XYZ jogStart = geo.ConnA.Origin;
        XYZ jogEnd = jogStart + geo.DirA.Multiply(axialRun) + geo.OffsetVec;

        MoveOpenEnd(b, geo.ConnB, jogEnd);
        doc.Regenerate();

        MepOps ops = MepOps.For(a) ?? throw new InvalidOperationException("Tipo de elemento MEP no soportado.");
        MEPCurve mid = ops.Create(doc, a, jogStart, jogEnd);
        doc.Regenerate();

        Connector? freshA = PontifexHelpers.ConnectorAt(a, jogStart);
        Connector? midAtStart = PontifexHelpers.ConnectorAt(mid, jogStart);
        Connector? midAtEnd = PontifexHelpers.ConnectorAt(mid, jogEnd);
        Connector? freshB = PontifexHelpers.ConnectorAt(b, jogEnd);

        if (freshA == null || midAtStart == null || midAtEnd == null || freshB == null)
            throw new InvalidOperationException("No se pudieron ubicar los conectores del salto tras regenerar el modelo.");

        doc.Create.NewElbowFitting(freshA, midAtStart);
        doc.Create.NewElbowFitting(midAtEnd, freshB);
    }

    private static void MoveOpenEnd(MEPCurve m, Connector openConn, XYZ newPt)
    {
        var lc = (LocationCurve)m.Location;
        var line = (Line)lc.Curve;
        XYZ p0 = line.GetEndPoint(0), p1 = line.GetEndPoint(1);
        bool moveP0 = p0.DistanceTo(openConn.Origin) < p1.DistanceTo(openConn.Origin);
        lc.Curve = Line.CreateBound(moveP0 ? newPt : p0, moveP0 ? p1 : newPt);
    }
}
