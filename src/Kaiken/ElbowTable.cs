using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Kaiken;

/// <summary>
/// Center-to-end real (D) de los codos de un tipo MEP para un salto en una dirección dada,
/// medido a varios ángulos en una sola transacción que se descarta. Entre ángulos medidos
/// se interpola linealmente. Lo usan "Unir" (Cualquier °) y Pontifex (Según altura) para
/// elegir el ángulo con que el salto cabe en el espacio disponible.
/// </summary>
public class ElbowTable
{
    /// <summary>Ángulo mínimo que se considera para un salto automático.</summary>
    public const int MinAngle = 5;

    /// <summary>Recta mínima que debe quedar entre dos codos o entre un codo y un extremo.</summary>
    public static readonly double MinStraightFt = 1.0 * PontifexHelpers.CmToFeet;

    /// <summary>Ángulos a medir para la elección automática: cada 5° entre 5° y 90°.</summary>
    public static IEnumerable<double> Grid => Enumerable.Range(1, 18).Select(i => i * 5.0);

    private readonly SortedList<double, double> _ctE = new();

    /// <summary>Center-to-end (pies) del codo al ángulo dado (interpolado entre medidas).</summary>
    public double CtE(double angleDeg)
    {
        if (_ctE.TryGetValue(angleDeg, out double exact)) return exact;
        var keys = _ctE.Keys;
        if (angleDeg <= keys[0]) return _ctE.Values[0];
        if (angleDeg >= keys[^1]) return _ctE.Values[^1];
        int i = 1;
        while (keys[i] < angleDeg) i++;
        double f = (angleDeg - keys[i - 1]) / (keys[i] - keys[i - 1]);
        return _ctE.Values[i - 1] + f * (_ctE.Values[i] - _ctE.Values[i - 1]);
    }

    /// <summary>
    /// ¿Cabe un desvío <paramref name="h"/> entre dos codos de <paramref name="angleDeg"/>?
    /// La diagonal (h / sin θ) debe alojar los dos codos y 1 cm de recta entre ellos.
    /// </summary>
    public bool Fits(double h, double angleDeg) =>
        h / Math.Sin(angleDeg * Math.PI / 180.0) >= 2 * CtE(angleDeg) + MinStraightFt - 1e-9;

    /// <summary>Mayor ángulo entero (≥ MinAngle) con que cabe el desvío h; 0 si no cabe ni a MinAngle.</summary>
    public int MaxAngle(double h)
    {
        for (int a = 90; a >= MinAngle; a--)
            if (Fits(h, a)) return a;
        return 0;
    }

    /// <summary>
    /// Mide los codos creando, lejos del modelo, un salto de prueba por ángulo (tramo recto,
    /// diagonal hacia <paramref name="perp"/>, tramo recto) dentro de una transacción que se
    /// descarta. Se toma el mayor D de los dos codos del salto: en escalerillas y ductos el
    /// codo de entrada y el de salida pueden ser familias distintas (curva interior/exterior).
    /// Si un ángulo no se puede medir, queda la estimación geométrica. Debe llamarse fuera
    /// de una transacción abierta.
    /// </summary>
    public static ElbowTable Measure(Document doc, MEPCurve mep, MepOps ops, XYZ axisDir, XYZ perp, IEnumerable<double> angles)
    {
        var table = new ElbowTable();
        double size = ops.NominalSize(mep);
        var list = angles.Distinct().OrderBy(a => a).ToList();
        foreach (double a in list) table._ctE[a] = PontifexHelpers.EstimateElbow(size, a);

        Line? ax = PontifexHelpers.Axis(mep);
        if (ax == null || perp.GetLength() < 0.5) return table;
        XYZ lateral = axisDir.CrossProduct(perp);
        if (lateral.GetLength() < 0.5) return table;
        lateral = lateral.Normalize();

        double len = Math.Max(10.0, 10 * size);
        double spacing = Math.Max(5.0, 3 * size);
        XYZ origin = ax.GetEndPoint(0) + lateral * 50.0; // a un costado, espacio vacío (se descarta igual)

        using var t = new Transaction(doc, "medir codos");
        try
        {
            t.Start();
            var jogs = new List<(double angle, XYZ p1, XYZ p2, MEPCurve s1, MEPCurve s2, MEPCurve s3)>();
            for (int k = 0; k < list.Count; k++)
            {
                double th = list[k] * Math.PI / 180.0;
                XYZ dir2 = (axisDir * Math.Cos(th) + perp * Math.Sin(th)).Normalize();
                XYZ p1 = origin + lateral * (k * spacing);
                XYZ p2 = p1 + dir2 * len;
                try
                {
                    jogs.Add((list[k], p1, p2,
                        ops.Create(doc, mep, p1 - axisDir * len, p1),
                        ops.Create(doc, mep, p1, p2),
                        ops.Create(doc, mep, p2, p2 + axisDir * len)));
                }
                catch { }
            }
            doc.Regenerate();

            var elbows = new List<(double angle, XYZ p1, XYZ p2, FamilyInstance e1, FamilyInstance e2)>();
            foreach (var j in jogs)
            {
                try
                {
                    var e1 = Elbow(doc, j.s1, j.s2, j.p1);
                    var e2 = Elbow(doc, j.s2, j.s3, j.p2);
                    if (e1 != null && e2 != null) elbows.Add((j.angle, j.p1, j.p2, e1, e2));
                }
                catch { }
            }
            doc.Regenerate();

            // El D de un codo es la distancia entre la esquina del salto y sus conectores.
            foreach (var e in elbows)
            {
                double d = Math.Max(MaxReach(e.e1, e.p1), MaxReach(e.e2, e.p2));
                if (d > 1e-4) table._ctE[e.angle] = d;
            }
        }
        catch { }
        finally
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
        }
        return table;
    }

    private static FamilyInstance? Elbow(Document doc, MEPCurve m1, MEPCurve m2, XYZ corner)
    {
        var c1 = PontifexHelpers.ConnectorAt(m1, corner);
        var c2 = PontifexHelpers.ConnectorAt(m2, corner);
        return c1 != null && c2 != null ? doc.Create.NewElbowFitting(c1, c2) : null;
    }

    private static double MaxReach(FamilyInstance elbow, XYZ corner)
    {
        double best = 0;
        var cm = elbow.MEPModel?.ConnectorManager;
        if (cm == null) return 0;
        foreach (Connector c in cm.Connectors)
            best = Math.Max(best, corner.DistanceTo(c.Origin));
        return best;
    }
}
