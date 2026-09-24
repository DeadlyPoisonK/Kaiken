using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace Kaiken;

/// <summary>
/// Avoider: reenruta un elemento MEP seleccionado (tubería, ducto, escalerilla o
/// conduit) creando saltos con codos al ángulo/dirección/distancia elegidos, en cada
/// cruce con obstáculos (MEP + estructura) de la vista. Una transacción, Ctrl+Z.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class AvoiderPipeCommand : IExternalCommand
{
    private class MepFilter : ISelectionFilter
    {
        public bool AllowElement(Element e) => e is Pipe || e is Duct || e is CableTray || e is Conduit;
        public bool AllowReference(Reference r, XYZ p) => false;
    }

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        // 1. Elemento MEP (selección actual o pick)
        MEPCurve? mep = null;
        var sel = uidoc.Selection.GetElementIds().Select(id => doc.GetElement(id)).OfType<MEPCurve>()
                       .Where(m => MepOps.For(m) != null).ToList();
        if (sel.Count == 1) mep = sel[0];
        else
        {
            try
            {
                var r = uidoc.Selection.PickObject(ObjectType.Element, new MepFilter(),
                    "Selecciona una tubería, ducto, escalerilla o conduit para reenrutar");
                mep = doc.GetElement(r) as MEPCurve;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
        }
        if (mep == null) { message = "No se seleccionó un elemento MEP válido."; return Result.Cancelled; }

        try
        {
        MepOps? ops = MepOps.For(mep);
        Line? axis = AvoiderHelpers.Axis(mep);
        if (ops == null) { TaskDialog.Show("Avoider", "Tipo de elemento no soportado."); return Result.Cancelled; }
        if (axis == null) { TaskDialog.Show("Avoider", "El elemento no es recto (su eje no es una línea)."); return Result.Cancelled; }

        XYZ S = axis.GetEndPoint(0), E = axis.GetEndPoint(1);
        XYZ d = (E - S).Normalize();
        double L = axis.Length;
        double size = ops.NominalSize(mep);

        // 2. Pre-analizar cruces para sugerir ángulos
        AvoiderParams dummyParams = new AvoiderParams { ClearanceFt = 0 };
        var preClashes = AvoiderHelpers.FindClashes(doc, mep, doc.ActiveView, dummyParams);
        
        double sugStartAngle = 45.0;
        double sugEndAngle = 45.0;

        if (preClashes.Count > 0)
        {
            double firstC = preClashes.Min(c => c.A);
            double lastC = preClashes.Max(c => c.B);
            
            // Si el choque está a menos de ~45 cm (1.5 pies), sugerir salto vertical (90°)
            if (firstC < 1.5) sugStartAngle = 90.0;
            if (L - lastC < 1.5) sugEndAngle = 90.0;
        }

        // 3. UI y Parámetros
        var win = new AvoiderWindow(sugStartAngle, sugEndAngle);
        if (win.ShowDialog() != true) return Result.Cancelled;
        var p = win.Result;

        XYZ offDir = AvoiderHelpers.OffsetDir(d, p.Direction);
        if (offDir.GetLength() < 0.5)
        {
            TaskDialog.Show("Avoider",
                "La dirección elegida es paralela al eje del elemento (¿elemento vertical con 'Arriba/Abajo'?).\n" +
                "Para un elemento vertical usa Izquierda/Derecha.");
            return Result.Cancelled;
        }

        // Para distancias base mínimas, usamos el codo con ángulo más suave (menor ángulo = mayor codo)
        double minAngle = Math.Min(p.AngleDegStart, p.AngleDegEnd);
        double D = MeasureElbowCtE(doc, mep, ops, d, minAngle, size);
        var (minDist, minMargin) = AvoiderHelpers.MinBridge(D, minAngle);
        
        string adj = "";
        double effDistBase = Math.Max(p.DistanceFt, minDist);
        double margin = Math.Max(p.ClearanceFt, minMargin);
        
        double minGuardLeft = Math.Max(MeasureElbowCtE(doc, mep, ops, d, p.AngleDegStart, size) + 1.0 * AvoiderHelpers.CmToFeet, 3.0 * AvoiderHelpers.CmToFeet);
        double minGuardRight = Math.Max(MeasureElbowCtE(doc, mep, ops, d, p.AngleDegEnd, size) + 1.0 * AvoiderHelpers.CmToFeet, 3.0 * AvoiderHelpers.CmToFeet);

        // 4. Detección de Te conectada al ramal (Caso B) antes de modificar el modelo
        Connector? startTrunkA = null, startTrunkB = null;
        Connector? endTrunkA = null, endTrunkB = null;
        List<ElementId> intermediateStart;
        List<ElementId> intermediateEnd;
        FamilyInstance? teeStart = GetConnectedTee(mep, S, p.RotateTee, out intermediateStart, out startTrunkA, out startTrunkB);
        FamilyInstance? teeEnd = GetConnectedTee(mep, E, p.RotateTee, out intermediateEnd, out endTrunkA, out endTrunkB);

        // 5. Cruces finales con el margen real
        var clashes = AvoiderHelpers.FindClashes(doc, mep, doc.ActiveView, p);
        if (clashes.Count == 0) { TaskDialog.Show("Avoider", "No se detectaron cruces con obstáculos en esta vista."); return Result.Succeeded; }

        int clashesBeforeMerge = clashes.Count;
        clashes = MergeOverlappingJumps(clashes, axis, offDir, size, margin, effDistBase, minAngle);
        if (clashes.Count < clashesBeforeMerge)
            adj += $"\n{clashesBeforeMerge - clashes.Count} cruce(s) próximos fusionados en un salto único.";

        int done = 0, skipped = 0, elbowFails = 0;
        var log = new List<string>();

        using (var tx = new Transaction(doc, "Avoider — reenrutar MEP"))
        {
            tx.Start();
            MEPCurve current = mep;
            double lastA = L;
            foreach (var iv in clashes.OrderByDescending(c => c.A))
            {
                double bT = iv.A - margin - size / 2.0;
                double cT = iv.B + margin + size / 2.0;

                XYZ clashCenter = axis.Evaluate(iv.Mid / L, true);
                double obsExtreme = (offDir.X >= 0 ? iv.WorldBbMax.X : iv.WorldBbMin.X) * offDir.X
                                  + (offDir.Y >= 0 ? iv.WorldBbMax.Y : iv.WorldBbMin.Y) * offDir.Y
                                  + (offDir.Z >= 0 ? iv.WorldBbMax.Z : iv.WorldBbMin.Z) * offDir.Z;
                double pipeProj   = clashCenter.DotProduct(offDir);

                double requiredOffset = Math.Max(obsExtreme - pipeProj + margin + size / 2.0, 0);
                double effDist = Math.Max(effDistBase, requiredOffset);
                if (requiredOffset > effDistBase + 1e-4)
                    log.Add($"Cruce t={iv.Mid:F2}: offset geométrico necesario {requiredOffset * 30.48:F1} cm.");

                XYZ off = offDir * effDist;
                
                // Calculamos runs separados
                double reqRunLeft = effDist / Math.Tan(p.AngleDegStart * Math.PI / 180.0);
                double reqRunRight = effDist / Math.Tan(p.AngleDegEnd * Math.PI / 180.0);
                double clashRunLeft = reqRunLeft;
                double clashRunRight = reqRunRight;

                double clashMinGuardLeft = (iv == clashes.OrderBy(c => c.A).First()) ? 0.0 : minGuardLeft;
                double clashMinGuardRight = minGuardRight;

                double availLeft = bT - clashMinGuardLeft;
                double availRight = lastA - cT - clashMinGuardRight;

                bool teeEvasionLeft = false;
                bool teeEvasionRight = false;

                // Lado izquierdo
                if (clashRunLeft > availLeft)
                {
                    if (iv == clashes.OrderBy(c => c.A).First() && teeStart != null && availLeft < minGuardLeft)
                    {
                        teeEvasionLeft = true;
                        clashRunLeft = 0;
                    }
                    else
                    {
                        clashRunLeft = Math.Max(0.01, availLeft);
                    }
                }

                // Lado derecho
                if (clashRunRight > availRight)
                {
                    if (iv == clashes.OrderByDescending(c => c.A).First() && teeEnd != null && availRight < minGuardRight)
                    {
                        teeEvasionRight = true;
                        clashRunRight = 0;
                    }
                    else
                    {
                        clashRunRight = Math.Max(0.01, availRight);
                    }
                }

                if (teeEvasionLeft && teeStart?.Location is LocationPoint lpStart)
                {
                    bT = (lpStart.Point - S).DotProduct(d);
                }
                if (teeEvasionRight && teeEnd?.Location is LocationPoint lpEnd)
                {
                    cT = (lpEnd.Point - S).DotProduct(d);
                }

                double paT = bT - clashRunLeft;
                double pbT = cT + clashRunRight;
                
                if (!teeEvasionLeft && paT < -1e-5)
                {
                    skipped++;
                    log.Add($"Cruce t={iv.Mid:F2}: No hay espacio al inicio de la tubería para colocar el codo.");
                    continue;
                }
                if (!teeEvasionRight && pbT > L + 1e-5)
                {
                    skipped++;
                    log.Add($"Cruce t={iv.Mid:F2}: No hay espacio al final de la tubería para colocar el codo.");
                    continue;
                }

                // Validaciones de ángulo
                if (!teeEvasionLeft && clashRunLeft < reqRunLeft)
                {
                    double adaptAngleLeft = Math.Atan2(effDist, clashRunLeft) * 180.0 / Math.PI;
                    if (adaptAngleLeft > 89.0)
                    {
                        skipped++;
                        log.Add($"Cruce t={iv.Mid:F2}: izquierda sin espacio (ángulo > 89°).");
                        continue;
                    }
                }
                if (!teeEvasionRight && clashRunRight < reqRunRight)
                {
                    double adaptAngleRight = Math.Atan2(effDist, clashRunRight) * 180.0 / Math.PI;
                    if (adaptAngleRight > 89.0)
                    {
                        skipped++;
                        log.Add($"Cruce t={iv.Mid:F2}: derecha sin espacio (ángulo > 89°).");
                        continue;
                    }
                }

                try
                {
                    XYZ Pa = S + paT * d;
                    XYZ Pb = S + pbT * d;
                    XYZ B = S + bT * d + off;
                    XYZ C = S + cT * d + off;

                    if (teeEvasionRight)
                    {
                        // Evasión de Te a la derecha: rest va hasta la Te, la borramos
                        var (s1, rest) = ops.Break(doc, current, Pa, d);
                        doc.Delete(rest.Id);
                        doc.Delete(teeEnd.Id);
                        foreach (var fid in intermediateEnd) doc.Delete(fid);
                        doc.Regenerate();

                        var seg1 = ops.Create(doc, s1, Pa, B);
                        var seg2 = ops.Create(doc, s1, B, C);
                        var seg3 = ops.Create(doc, s1, C, Pb); // vertical a la Te

                        elbowFails += Elbow(doc, s1, Pa, seg1, Pa);
                        elbowFails += Elbow(doc, seg1, B, seg2, B);
                        elbowFails += Elbow(doc, seg2, C, seg3, C); // codo de 90° automático

                        var branchConn = AvoiderHelpers.ConnectorAt(seg3, Pb);
                        if (branchConn != null && endTrunkA != null && endTrunkB != null)
                        {
                            var newTee = doc.Create.NewTeeFitting(endTrunkA, endTrunkB, branchConn);
                            if (newTee == null) elbowFails++;
                        }
                        else { elbowFails++; }

                        current = s1;
                        lastA = paT;
                        log.Add($"Cruce t={iv.Mid:F2}: Te ramal derecha rotada.");
                    }
                    else if (teeEvasionLeft)
                    {
                        // Evasión de Te a la izquierda
                        var (rest, s2) = ops.Break(doc, current, Pb, d);
                        doc.Delete(rest.Id);
                        doc.Delete(teeStart.Id);
                        foreach (var fid in intermediateStart) doc.Delete(fid);
                        doc.Regenerate();

                        var seg1 = ops.Create(doc, s2, Pa, B); // vertical a la Te
                        var seg2 = ops.Create(doc, s2, B, C);
                        var seg3 = ops.Create(doc, s2, C, Pb);

                        var branchConn = AvoiderHelpers.ConnectorAt(seg1, Pa);
                        if (branchConn != null && startTrunkA != null && startTrunkB != null)
                        {
                            var newTee = doc.Create.NewTeeFitting(startTrunkA, startTrunkB, branchConn);
                            if (newTee == null) elbowFails++;
                        }
                        else { elbowFails++; }

                        elbowFails += Elbow(doc, seg1, B, seg2, B); // codo de 90° automático
                        elbowFails += Elbow(doc, seg2, C, seg3, C);
                        elbowFails += Elbow(doc, seg3, Pb, s2, Pb);

                        log.Add($"Cruce t={iv.Mid:F2}: Te ramal izquierda rotada.");
                    }
                    else
                    {
                        // Evasión estándar (ambos lados con codos normales)
                        var (s1, rest) = ops.Break(doc, current, Pa, d);
                        var (mid, after) = ops.Break(doc, rest, Pb, d);
                        doc.Delete(mid.Id);

                        var seg1 = ops.Create(doc, s1, Pa, B);
                        var seg2 = ops.Create(doc, s1, B, C);
                        var seg3 = ops.Create(doc, s1, C, Pb);

                        elbowFails += Elbow(doc, s1, Pa, seg1, Pa);
                        elbowFails += Elbow(doc, seg1, B, seg2, B);
                        elbowFails += Elbow(doc, seg2, C, seg3, C);
                        elbowFails += Elbow(doc, seg3, Pb, after, Pb);

                        current = s1;
                        lastA = paT;
                    }

                    done++;
                }
                catch (Exception ex) { skipped++; log.Add($"Cruce t={iv.Mid:F2} falló: {ex.Message}"); }
            }
            tx.Commit();
        }

        string kind = mep switch { Pipe => "tubería", Duct => "ducto", CableTray => "escalerilla", Conduit => "conduit", _ => "elemento" };
        string msg = $"Elemento: {kind}\nCruces detectados: {clashes.Count}\nSaltos creados: {done}\nOmitidos: {skipped}";
        if (elbowFails > 0) msg += $"\nCodos no insertados: {elbowFails} (¿el tipo tiene codos de {p.AngleDegStart:F0}° / {p.AngleDegEnd:F0}°?)";
        if (adj.Length > 0) msg += "\n" + adj.Trim();
        if (log.Count > 0) msg += "\n\n" + string.Join("\n", log.Take(8));
        TaskDialog.Show("Avoider", msg);
        return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Avoider", $"Falló, no se modificó el modelo (transacción revertida):\n{ex.Message}");
            return Result.Failed;
        }
    }

    private static double RoundUp10(double cm) => Math.Ceiling(cm / 10.0) * 10.0;

    /// <summary>
    /// Mide el center-to-end real (D) del codo del tipo del elemento: crea dos tramos que
    /// forman el ángulo y les inserta un codo dentro de una transacción que se DESCARTA
    /// (rollback: no deja nada en el modelo). Devuelve D en pies; si algo falla, estima.
    /// </summary>
    private static double MeasureElbowCtE(Document doc, MEPCurve mep, MepOps ops, XYZ axisDir, double angleDeg, double sizeFt)
    {
        double fallback = AvoiderHelpers.EstimateElbow(sizeFt, angleDeg);
        XYZ perp = AvoiderHelpers.OffsetDir(axisDir, JogDir.Arriba);
        if (perp.GetLength() < 0.5) perp = AvoiderHelpers.OffsetDir(axisDir, JogDir.Derecha);
        if (perp.GetLength() < 0.5) return fallback;

        Line? ax = AvoiderHelpers.Axis(mep);
        if (ax == null) return fallback;

        double th = angleDeg * Math.PI / 180.0;
        double len = 5.0;
        XYZ P1 = ax.GetEndPoint(0) + perp * 30.0;            // a un costado, espacio vacío (se descarta igual)
        XYZ dir2 = (axisDir * Math.Cos(th) + perp * Math.Sin(th)).Normalize();
        XYZ P0 = P1 - axisDir * len;
        XYZ P2 = P1 + dir2 * len;

        double D = fallback;
        using (var t = new Transaction(doc, "medir codo"))
        {
            try
            {
                t.Start();
                var s1 = ops.Create(doc, mep, P0, P1);
                var s2 = ops.Create(doc, mep, P1, P2);
                var c1 = AvoiderHelpers.ConnectorAt(s1, P1);
                var c2 = AvoiderHelpers.ConnectorAt(s2, P1);
                if (c1 != null && c2 != null)
                {
                    var elbow = doc.Create.NewElbowFitting(c1, c2);
                    doc.Regenerate();
                    XYZ origin = (elbow.Location as LocationPoint)?.Point ?? P1;
                    double best = 0;
                    foreach (Connector c in elbow.MEPModel.ConnectorManager.Connectors)
                        best = Math.Max(best, origin.DistanceTo(c.Origin));
                    if (best > 1e-4) D = best;
                }
            }
            catch { D = fallback; }
            t.RollBack();
        }
        return D;
    }

    private static int Elbow(Document doc, MEPCurve m1, XYZ p1, MEPCurve m2, XYZ p2)
    {
        var c1 = AvoiderHelpers.ConnectorAt(m1, p1);
        var c2 = AvoiderHelpers.ConnectorAt(m2, p2);
        if (c1 == null || c2 == null) return 1;
        try { doc.Create.NewElbowFitting(c1, c2); return 0; }
        catch { return 1; }
    }

    /// <summary>
    /// Merge geométrico: recorre la lista de cruces ordenados por A y fusiona pares
    /// consecutivos cuya geometría de salto se solaparía (pbT del izquierdo ≥ paT del derecho).
    /// Itera hasta estabilidad para manejar cadenas de 3+ obstáculos próximos.
    /// La BB del cruce fusionado es la unión de ambas, garantizando que el offset
    /// calculado cubra el peor caso de los dos obstáculos.
    /// </summary>
    private static List<ClashInterval> MergeOverlappingJumps(
        List<ClashInterval> clashes, Line axis, XYZ offDir,
        double pipeSize, double margin, double effDistBase, double angleDeg)
    {
        if (clashes.Count <= 1) return clashes;
        double L = axis.Length;
        double tanA = Math.Tan(angleDeg * Math.PI / 180.0);

        bool changed = true;
        while (changed)
        {
            changed = false;
            var result = new List<ClashInterval> { clashes[0] };

            for (int i = 1; i < clashes.Count; i++)
            {
                ClashInterval prev = result[^1];
                ClashInterval curr = clashes[i];

                // Offset real estimado para cada cruce (misma fórmula que en el loop principal)
                double eff_prev = CalcEffDist(prev, axis, L, offDir, pipeSize, margin, effDistBase);
                double eff_curr = CalcEffDist(curr, axis, L, offDir, pipeSize, margin, effDistBase);

                // ¿Se solaparían los saltos?
                // El salto izquierdo termina en: prev.B + margin + run_prev
                // El salto derecho empieza en:  curr.A - margin - run_curr
                double pbT_prev = prev.B + margin + eff_prev / tanA;
                double paT_curr = curr.A - margin - eff_curr / tanA;

                if (pbT_prev >= paT_curr - 1e-4)
                {
                    // Fusionar: un solo salto cubre ambos obstáculos
                    result[^1] = MergeTwo(prev, curr);
                    changed = true;
                }
                else
                {
                    result.Add(curr);
                }
            }
            clashes = result;
        }
        return clashes;
    }

    /// <summary>
    /// Calcula el offset efectivo para un cruce: el máximo entre el piso del usuario
    /// y el mínimo geométrico necesario para liberar el obstáculo en la dirección offDir.
    /// </summary>
    private static double CalcEffDist(ClashInterval iv, Line axis, double L,
        XYZ offDir, double pipeSize, double margin, double effDistBase)
    {
        XYZ center = axis.Evaluate(iv.Mid / L, true);
        double obsExtreme = (offDir.X >= 0 ? iv.WorldBbMax.X : iv.WorldBbMin.X) * offDir.X
                          + (offDir.Y >= 0 ? iv.WorldBbMax.Y : iv.WorldBbMin.Y) * offDir.Y
                          + (offDir.Z >= 0 ? iv.WorldBbMax.Z : iv.WorldBbMin.Z) * offDir.Z;
        double req = Math.Max(obsExtreme - center.DotProduct(offDir) + margin + pipeSize / 2.0, 0);
        return Math.Max(effDistBase, req);
    }

    /// <summary>
    /// Fusiona dos ClashIntervals consecutivos en uno solo.
    /// La BB resultante es la unión de ambas para que el offset cubra el peor caso.
    /// </summary>
    private static ClashInterval MergeTwo(ClashInterval a, ClashInterval b) => new ClashInterval
    {
        A          = Math.Min(a.A, b.A),
        B          = Math.Max(a.B, b.B),
        Clashers   = a.Clashers.Concat(b.Clashers).ToList(),
        WorldBbMin = new XYZ(Math.Min(a.WorldBbMin.X, b.WorldBbMin.X),
                             Math.Min(a.WorldBbMin.Y, b.WorldBbMin.Y),
                             Math.Min(a.WorldBbMin.Z, b.WorldBbMin.Z)),
        WorldBbMax = new XYZ(Math.Max(a.WorldBbMax.X, b.WorldBbMax.X),
                             Math.Max(a.WorldBbMax.Y, b.WorldBbMax.Y),
                             Math.Max(a.WorldBbMax.Z, b.WorldBbMax.Z)),
    };

    /// <summary>
    /// Detecta si el extremo dado de la tubería está conectado al ramal (branch) de una Te.
    /// Si es así, retorna la Te (FamilyInstance) y devuelve los conectores de los troncales (trunk)
    /// de esa Te para poder reconstruirla después.
    /// </summary>
    private static FamilyInstance GetConnectedTee(
        MEPCurve mep, XYZ endPt, bool rotateTee, out List<ElementId> intermediateFittings,
        out Connector trunkConnA, out Connector trunkConnB)
    {
        intermediateFittings = new List<ElementId>();
        trunkConnA = null;
        trunkConnB = null;

        Connector pipeConn = mep.ConnectorManager.Connectors.Cast<Connector>()
            .FirstOrDefault(c => c.Origin.DistanceTo(endPt) < 1e-3);
        if (pipeConn == null || !pipeConn.IsConnected) return null;

        Connector currentConn = pipeConn;

        while (true)
        {
            Connector otherConn = currentConn.AllRefs.Cast<Connector>()
                .FirstOrDefault(c => c.Owner is FamilyInstance && c.Owner.Id != currentConn.Owner.Id);
            if (otherConn == null) return null;

            var fi = otherConn.Owner as FamilyInstance;
            if (fi == null || fi.MEPModel?.ConnectorManager == null) return null;

            var conns = fi.MEPModel.ConnectorManager.Connectors.Cast<Connector>().ToList();
            
            if (conns.Count == 3)
            {
                // Es una Te
                Connector cA = null, cB = null, cBranch = null;
                for (int i = 0; i < conns.Count && cA == null; i++)
                {
                    for (int j = i + 1; j < conns.Count; j++)
                    {
                        double dot = conns[i].CoordinateSystem.BasisZ.DotProduct(conns[j].CoordinateSystem.BasisZ);
                        if (dot < -0.9)
                        {
                            cA = conns[i];
                            cB = conns[j];
                            cBranch = conns.Except(new[] { cA, cB }).First();
                            break;
                        }
                    }
                }

                if (cA == null || cB == null || cBranch == null) return null;

                if (otherConn.Id == cBranch.Id)
                {
                    trunkConnA = cA.AllRefs.Cast<Connector>().FirstOrDefault(x => x.Owner?.Id != fi.Id);
                    trunkConnB = cB.AllRefs.Cast<Connector>().FirstOrDefault(x => x.Owner?.Id != fi.Id);
                    return fi;
                }
                return null;
            }
            else if (conns.Count == 2 && rotateTee)
            {
                // Es una reducción u otro fitting intermedio
                intermediateFittings.Add(fi.Id);
                currentConn = conns.FirstOrDefault(c => c.Id != otherConn.Id);
                if (currentConn == null || !currentConn.IsConnected) return null;
            }
            else
            {
                return null;
            }
        }
    }
}
