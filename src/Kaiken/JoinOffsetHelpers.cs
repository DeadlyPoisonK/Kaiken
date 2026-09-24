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
}

/// <summary>
/// Lógica del botón "Unir": conecta dos tramos MEP paralelos que están a distinta altura
/// (o con cualquier otro desnivel perpendicular a su eje) mediante un salto de dos codos
/// del mismo ángulo (45°+45° o 90°+90°) y un tramo intermedio diagonal/vertical.
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
        };
    }

    /// <summary>
    /// Crea el salto: estira "b" a lo largo de su propio eje, crea el tramo intermedio
    /// (vertical para 90°/90°, diagonal a 45° para 45°/45°) y los dos codos.
    /// </summary>
    public static void Execute(Document doc, MEPCurve a, MEPCurve b, JoinOffsetGeometry geo, bool use45)
    {
        double offsetFt = geo.OffsetVec.GetLength();
        // A 45°, el tramo intermedio recorre tanto a lo largo del eje como en perpendicular
        // (mismo ángulo en ambos codos); a 90°, el tramo intermedio es puramente perpendicular.
        double axialRun = use45 ? offsetFt : 0.0;

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
