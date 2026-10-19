using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace Kaiken;

/// <summary>Por qué un rociador quedó (o no) conectado.</summary>
public enum SprinklerOutcome
{
    Connected,
    /// <summary>Conectado, pero la tubería estaba al lado contrario de donde mira su conector.</summary>
    ConnectedOpposite,
    AlreadyConnected,
    NoPipeAbove,
    Misaligned,
    NotVertical,
    Failed,
}

/// <summary>
/// Lógica de "Conectar rociadores": para un rociador sin conectar busca la tubería del
/// sistema elegido que pasa directamente sobre (o bajo, si es upright) su conector — o, si no
/// hay, al lado contrario —, lo alinea en planta con el eje, crea la bajada vertical y la une
/// a la tubería con Te o Tap según la preferencia de ruteo del tipo de tubería, o con codo si
/// el rociador queda en el extremo — lo mismo que hace "Connect Into" de Revit, pero en lote.
/// </summary>
public static class ConnectSprinklersHelpers
{
    /// <summary>Radio en planta para distinguir "desfasado" de "no hay tubería encima".</summary>
    private static readonly double SearchRadius = UnitUtils.ConvertToInternalUnits(50, UnitTypeId.Centimeters);

    /// <summary>A menos de esto del extremo, dentro del tramo, se recorta la tubería y se une con codo en vez de Te.</summary>
    private static readonly double EndReach = UnitUtils.ConvertToInternalUnits(15, UnitTypeId.Centimeters);

    /// <summary>
    /// Hasta cuánto se alarga una tubería cuyo extremo libre quedó corto antes del rociador (caso
    /// típico de ramales dibujados hasta el borde del símbolo; el "Connect Into" de Revit ahí falla).
    /// </summary>
    private static readonly double ExtendReach = UnitUtils.ConvertToInternalUnits(50, UnitTypeId.Centimeters);

    /// <summary>Largo mínimo de la bajada provisoria, para que el accesorio quepa sin invertirla.</summary>
    private static readonly double TempDropLength = UnitUtils.ConvertToInternalUnits(30, UnitTypeId.Centimeters);

    /// <summary>Bajada más corta que esto no se dibuja: el rociador va directo a la salida del accesorio.</summary>
    private static readonly double MinDropLength = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Centimeters);

    /// <summary>Cuánto puede correrse en altura un rociador para calzar directo en la salida del accesorio.</summary>
    public static readonly double MaxLift = UnitUtils.ConvertToInternalUnits(10, UnitTypeId.Centimeters);

    /// <summary>Color con el que se marcan los que no se pudieron conectar (el rojo es el del sistema de incendio).</summary>
    public static readonly Color MarkColor = new(160, 32, 240);

    public static List<FamilyInstance> GetSprinklersInView(Document doc, View view) =>
        new FilteredElementCollector(doc, view.Id)
            .OfCategory(BuiltInCategory.OST_Sprinklers)
            .WhereElementIsNotElementType()
            .OfType<FamilyInstance>()
            .ToList();

    /// <summary>Tipos de sistema de las tuberías visibles en la vista.</summary>
    public static List<PipingSystemType> GetPipingSystemTypesInView(Document doc, View view) =>
        new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(Pipe))
            .Cast<Pipe>().Select(SystemTypeId)
            .Where(id => id != ElementId.InvalidElementId)
            .Distinct()
            .Select(id => doc.GetElement(id) as PipingSystemType)
            .Where(t => t != null)
            .Select(t => t!)
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Marcado por defecto: clasificación de incendio, o nombre con "incendio"/"húmeda".</summary>
    public static bool IsFireSystem(PipingSystemType t)
    {
        if (t.SystemClassification is MEPSystemClassification.FireProtectWet
            or MEPSystemClassification.FireProtectDry
            or MEPSystemClassification.FireProtectPreaction
            or MEPSystemClassification.FireProtectOther)
            return true;

        string name = t.Name.ToLowerInvariant();
        return name.Contains("incendio") || name.Contains("humeda") || name.Contains("húmeda");
    }

    public static ElementId SystemTypeId(Pipe p) =>
        p.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;

    private static IEnumerable<Connector> PipingConnectors(FamilyInstance fi) =>
        fi.MEPModel?.ConnectorManager?.Connectors.Cast<Connector>()
            .Where(c => c.Domain == Domain.DomainPiping && c.ConnectorType == ConnectorType.End)
        ?? Enumerable.Empty<Connector>();

    public static bool IsConnected(FamilyInstance sprinkler) =>
        PipingConnectors(sprinkler).Any(c => c.IsConnected);

    /// <summary>
    /// Conecta un rociador. Debe llamarse dentro de una transacción abierta; si devuelve algo
    /// distinto de Connected, quien llama debe deshacer esa transacción. <paramref name="lift"/>:
    /// cuánto se corrió en altura el rociador para calzar directo en el accesorio (0 si no).
    /// </summary>
    public static SprinklerOutcome Connect(
        Document doc, FamilyInstance sprinkler, ISet<ElementId> systemTypeIds,
        double tolerance, double maxDistance, out string detail, out double lift)
    {
        detail = "";
        lift = 0;
        var connectors = PipingConnectors(sprinkler).ToList();
        if (connectors.Any(c => c.IsConnected)) return SprinklerOutcome.AlreadyConnected;

        Connector? sc = connectors.FirstOrDefault();
        if (sc == null)
        {
            detail = "sin conector de tubería";
            return SprinklerOutcome.Failed;
        }

        XYZ p = sc.Origin;
        double dirZ = sc.CoordinateSystem.BasisZ.Z;
        if (Math.Abs(dirZ) < 0.9) return SprinklerOutcome.NotVertical;
        int sign = dirZ > 0 ? 1 : -1;

        // --- Buscar la tubería hacia donde mira el conector; si no hay, al lado contrario ---
        bool anyMisaligned = false;
        bool opposite = false;
        Candidate? found = FindPipe(doc, p, sign, systemTypeIds, tolerance, maxDistance, ref anyMisaligned);
        if (found == null)
        {
            found = FindPipe(doc, p, -sign, systemTypeIds, tolerance, maxDistance, ref anyMisaligned);
            opposite = found != null;
        }
        if (found == null)
            return anyMisaligned ? SprinklerOutcome.Misaligned : SprinklerOutcome.NoPipeAbove;

        Pipe best = found.Value.Pipe;
        XYZ bestPoint = found.Value.Point;
        int end = found.Value.End;

        // --- Alinear: el desfase (ya dentro de la tolerancia) se corrige moviendo el rociador
        // en planta, para que la bajada quede vertical y la Te/codo la acepte ---
        var shift = new XYZ(bestPoint.X - p.X, bestPoint.Y - p.Y, 0);
        if (shift.GetLength() > 1e-6)
        {
            try
            {
                ElementTransformUtils.MoveElement(doc, sprinkler.Id, shift);
                p += shift;
            }
            catch (Exception ex)
            {
                detail = $"no se pudo alinear el rociador con la tubería ({ex.Message})";
                return SprinklerOutcome.Failed;
            }
        }

        // --- Rociador en el extremo de la tubería: se ajusta ese extremo al eje del rociador
        // (alarga o recorta) para unirlo después con un codo ---
        Connector? pipeEnd = null;
        if (end >= 0)
        {
            var line = (Line)((LocationCurve)best.Location).Curve;
            pipeEnd = ClosestConnector(best, line.GetEndPoint(end));
            try
            {
                if (line.GetEndPoint(end).DistanceTo(bestPoint) > 1e-6)
                {
                    XYZ far = line.GetEndPoint(1 - end);
                    ((LocationCurve)best.Location).Curve =
                        end == 0 ? Line.CreateBound(bestPoint, far) : Line.CreateBound(far, bestPoint);
                    doc.Regenerate();
                }
            }
            catch (Exception ex)
            {
                detail = $"no se pudo ajustar el extremo de la tubería ({ex.Message})";
                return SprinklerOutcome.Failed;
            }
        }

        // --- Bajada provisoria, larga, desde el eje de la tubería hacia el rociador. Primero se
        // pone el accesorio y después se recorta: con rociadores pegados a la tubería (montantes
        // con vela) la bajada real es más corta que el brazo de la Te/codo y Revit la invertiría
        // ("modified to be in the opposite direction") ---
        XYZ toSprinkler = p - bestPoint;
        double gap = toSprinkler.GetLength();
        if (gap < 1e-6)
        {
            detail = "el conector del rociador queda sobre el eje de la tubería";
            return SprinklerOutcome.Failed;
        }
        XYZ dir = toSprinkler / gap;

        Pipe drop;
        try
        {
            drop = Pipe.Create(doc, SystemTypeId(best), best.GetTypeId(), best.ReferenceLevel.Id,
                bestPoint + dir * Math.Max(gap, TempDropLength), bestPoint);
            if (sc.Shape == ConnectorProfileType.Round)
                drop.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(sc.Radius * 2);
        }
        catch (Exception ex)
        {
            detail = $"no se pudo crear la bajada ({ex.Message})";
            return SprinklerOutcome.Failed;
        }

        Connector dropTop = ClosestConnector(drop, bestPoint);
        if (!Join(doc, best, bestPoint, pipeEnd, dropTop, out detail))
            return SprinklerOutcome.Failed;
        doc.Regenerate();

        Connector? outlet = dropTop.AllRefs.Cast<Connector>().FirstOrDefault(c => c.Owner.Id != drop.Id);
        if (outlet == null)
        {
            detail = "el accesorio no quedó unido a la bajada";
            return SprinklerOutcome.Failed;
        }

        // --- Bajada definitiva: de la salida del accesorio al rociador. Si no cabe un tramo, el
        // rociador se conecta directo a la salida, corriéndolo en altura lo justo ---
        XYZ top = dropTop.Origin;
        double room = (p - top).DotProduct(dir);
        try
        {
            if (room > MinDropLength)
            {
                ((LocationCurve)drop.Location).Curve = Line.CreateBound(p, top);
                doc.Regenerate();
                ClosestConnector(drop, p).ConnectTo(sc);
            }
            else
            {
                if (-room > MaxLift)
                {
                    detail = $"el rociador queda {UnitUtils.ConvertFromInternalUnits(-room, UnitTypeId.Centimeters):0.#} cm " +
                             "dentro del accesorio; muévelo a mano";
                    return SprinklerOutcome.Failed;
                }
                dropTop.DisconnectFrom(outlet);
                doc.Delete(drop.Id);
                XYZ move = outlet.Origin - p;
                ElementTransformUtils.MoveElement(doc, sprinkler.Id, move);
                lift = move.GetLength();
                doc.Regenerate();
                outlet.ConnectTo(PipingConnectors(sprinkler).First());
            }
        }
        catch (Exception ex)
        {
            detail = $"no se pudo unir el rociador ({ex.Message})";
            return SprinklerOutcome.Failed;
        }

        return opposite ? SprinklerOutcome.ConnectedOpposite : SprinklerOutcome.Connected;
    }

    /// <summary>
    /// Une la bajada a la tubería: codo si el rociador está en el extremo (<paramref name="pipeEnd"/>),
    /// si no Tap o Te según la preferencia de ruteo del tipo de tubería.
    /// </summary>
    private static bool Join(Document doc, Pipe pipe, XYZ point, Connector? pipeEnd, Connector dropTop, out string detail)
    {
        detail = "";
        if (pipeEnd != null)
        {
            try
            {
                doc.Create.NewElbowFitting(pipeEnd, dropTop);
                return true;
            }
            catch (Exception ex)
            {
                detail = $"Codo: {ex.Message}";
                return false;
            }
        }

        var junction = pipe.PipeType?.RoutingPreferenceManager?.PreferredJunctionType ?? PreferredJunctionType.Tee;
        string tapError = "";
        if (junction == PreferredJunctionType.Tap)
        {
            // Si el Tap falla se prueba con Te en una sub-transacción limpia
            using var st = new SubTransaction(doc);
            st.Start();
            try
            {
                doc.Create.NewTakeoffFitting(dropTop, pipe);
                st.Commit();
                return true;
            }
            catch (Exception ex)
            {
                st.RollBack();
                tapError = $"Tap: {ex.Message}; ";
            }
        }

        try
        {
            ElementId otherId = PlumbingUtils.BreakCurve(doc, pipe.Id, point);
            var other = (Pipe)doc.GetElement(otherId);
            Connector c1 = ClosestConnector(pipe, point);
            Connector c2 = ClosestConnector(other, point);
            doc.Create.NewTeeFitting(c1, c2, dropTop);
            return true;
        }
        catch (Exception ex)
        {
            detail = $"{tapError}Te: {ex.Message}";
            return false;
        }
    }

    /// <summary>Tubería encontrada y punto de su eje donde llega la bajada. End: -1 a media tubería (Te/Tap), 0/1 en ese extremo (codo).</summary>
    private readonly record struct Candidate(Pipe Pipe, XYZ Point, int End);

    /// <summary>
    /// La tubería del sistema elegido más cercana en vertical, del lado <paramref name="sign"/>
    /// (+1 arriba, -1 abajo) del conector. Acepta rociadores más allá del extremo de la
    /// tubería (hasta <see cref="ExtendReach"/>) si ese extremo está libre.
    /// </summary>
    private static Candidate? FindPipe(
        Document doc, XYZ p, int sign, ISet<ElementId> systemTypeIds,
        double tolerance, double maxDistance, ref bool anyMisaligned)
    {
        double zA = p.Z, zB = p.Z + sign * maxDistance;
        double r = SearchRadius + ExtendReach;
        var outline = new Outline(
            new XYZ(p.X - r, p.Y - r, Math.Min(zA, zB)),
            new XYZ(p.X + r, p.Y + r, Math.Max(zA, zB)));

        Candidate? best = null;
        double bestDz = double.MaxValue;

        foreach (Pipe pipe in new FilteredElementCollector(doc)
                     .OfClass(typeof(Pipe))
                     .WherePasses(new BoundingBoxIntersectsFilter(outline))
                     .Cast<Pipe>())
        {
            if (!systemTypeIds.Contains(SystemTypeId(pipe))) continue;
            if (pipe.Location is not LocationCurve { Curve: Line line }) continue;

            XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
            XYZ ab = b - a;
            double lenXY2 = ab.X * ab.X + ab.Y * ab.Y;
            if (lenXY2 < 1e-9 || Math.Abs(ab.Normalize().Z) > 0.2) continue; // vertical o muy inclinada

            double lenXY = Math.Sqrt(lenXY2);
            double t = ((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / lenXY2;
            double s = t * lenXY; // posición en planta a lo largo del tramo, desde a
            if (s < -ExtendReach || s > lenXY + ExtendReach) continue;

            // Cerca de un extremo (o pasado de él) va con codo, si ese extremo está libre;
            // si está ocupado, solo sirve una Te dentro del tramo.
            int end = Math.Min(s, lenXY - s) < EndReach ? (s < lenXY - s ? 0 : 1) : -1;
            if (end >= 0 && ClosestConnector(pipe, end == 0 ? a : b).IsConnected)
            {
                if (t <= 0 || t >= 1) continue;
                end = -1;
            }

            XYZ q = a + t * ab;
            double dz = (q.Z - p.Z) * sign;
            if (dz <= 0 || dz > maxDistance) continue;

            double offset = Math.Sqrt(Math.Pow(q.X - p.X, 2) + Math.Pow(q.Y - p.Y, 2));
            if (offset > SearchRadius) continue;
            if (offset > tolerance)
            {
                anyMisaligned = true;
                continue;
            }

            if (dz < bestDz)
            {
                best = new Candidate(pipe, q, end);
                bestDz = dz;
            }
        }
        return best;
    }

    private static Connector ClosestConnector(MEPCurve curve, XYZ point) =>
        curve.ConnectorManager.Connectors.Cast<Connector>()
            .Where(c => c.ConnectorType == ConnectorType.End)
            .OrderBy(c => c.Origin.DistanceTo(point))
            .First();

    /// <summary>Override morado (líneas y relleno sólido) para los que quedaron sin conectar.</summary>
    public static void Mark(Document doc, View view, IEnumerable<ElementId> ids)
    {
        ElementId? solidFill = new FilteredElementCollector(doc)
            .OfClass(typeof(FillPatternElement))
            .Cast<FillPatternElement>()
            .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill)
            ?.Id;

        var ogs = new OverrideGraphicSettings();
        ogs.SetProjectionLineColor(MarkColor);
        ogs.SetProjectionLineWeight(8);
        if (solidFill != null)
        {
            ogs.SetSurfaceForegroundPatternColor(MarkColor);
            ogs.SetSurfaceForegroundPatternId(solidFill);
        }

        foreach (var id in ids)
        {
            try { view.SetElementOverrides(id, ogs); }
            catch { /* elemento que no admite override en esta vista */ }
        }
    }

    /// <summary>Quita la marca morada de una corrida anterior (sin tocar otros overrides del usuario).</summary>
    public static int Unmark(View view, IEnumerable<ElementId> ids)
    {
        int cleared = 0;
        foreach (var id in ids)
        {
            try
            {
                Color c = view.GetElementOverrides(id).ProjectionLineColor;
                if (c.IsValid && c.Red == MarkColor.Red && c.Green == MarkColor.Green && c.Blue == MarkColor.Blue)
                {
                    view.SetElementOverrides(id, new OverrideGraphicSettings());
                    cleared++;
                }
            }
            catch { }
        }
        return cleared;
    }
}

/// <summary>
/// Sin diálogos por cada rociador: borra las advertencias y, si hay un error, deshace solo
/// la transacción de ese rociador (queda contado como fallido).
/// </summary>
internal sealed class SilentFailures : IFailuresPreprocessor
{
    public string? LastError { get; private set; }

    public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
    {
        LastError = null;
        foreach (var f in fa.GetFailureMessages())
        {
            if (f.GetSeverity() == FailureSeverity.Warning)
            {
                fa.DeleteWarning(f);
            }
            else
            {
                LastError = f.GetDescriptionText();
                return FailureProcessingResult.ProceedWithRollBack;
            }
        }
        return FailureProcessingResult.Continue;
    }
}
