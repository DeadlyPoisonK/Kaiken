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
    AlreadyConnected,
    NoPipeAbove,
    Misaligned,
    NotVertical,
    Failed,
}

/// <summary>
/// Lógica de "Conectar rociadores": para un rociador sin conectar busca la tubería del
/// sistema elegido que pasa directamente sobre (o bajo, si es upright) su conector, crea la
/// bajada vertical y la une a la tubería con Te o Tap según la preferencia de ruteo del
/// tipo de tubería — lo mismo que hace "Connect Into" de Revit, pero en lote.
/// </summary>
public static class ConnectSprinklersHelpers
{
    /// <summary>Radio en planta para distinguir "desfasado" de "no hay tubería encima".</summary>
    private static readonly double SearchRadius = UnitUtils.ConvertToInternalUnits(50, UnitTypeId.Centimeters);

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
    /// distinto de Connected, quien llama debe deshacer esa transacción.
    /// </summary>
    public static SprinklerOutcome Connect(
        Document doc, FamilyInstance sprinkler, ISet<ElementId> systemTypeIds,
        double tolerance, double maxDistance, out string detail)
    {
        detail = "";
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

        // --- Buscar la tubería sobre el conector ---
        double zA = p.Z, zB = p.Z + sign * maxDistance;
        var outline = new Outline(
            new XYZ(p.X - SearchRadius, p.Y - SearchRadius, Math.Min(zA, zB)),
            new XYZ(p.X + SearchRadius, p.Y + SearchRadius, Math.Max(zA, zB)));

        Pipe? best = null;
        XYZ? bestPoint = null;
        double bestDz = double.MaxValue;
        bool anyMisaligned = false;

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

            double t = ((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / lenXY2;
            if (t <= 0 || t >= 1) continue; // el rociador no queda bajo el tramo

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
                best = pipe;
                bestPoint = q;
                bestDz = dz;
            }
        }

        if (best == null || bestPoint == null)
            return anyMisaligned ? SprinklerOutcome.Misaligned : SprinklerOutcome.NoPipeAbove;

        // --- Bajada: del conector del rociador al eje de la tubería ---
        Pipe drop;
        try
        {
            drop = Pipe.Create(doc, SystemTypeId(best), best.GetTypeId(), best.ReferenceLevel.Id, p, bestPoint);
            if (sc.Shape == ConnectorProfileType.Round)
                drop.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(sc.Radius * 2);
        }
        catch (Exception ex)
        {
            detail = $"no se pudo crear la bajada ({ex.Message})";
            return SprinklerOutcome.Failed;
        }

        Connector dropBottom = ClosestConnector(drop, p);
        Connector dropTop = ClosestConnector(drop, bestPoint);
        try
        {
            dropBottom.ConnectTo(sc);
        }
        catch (Exception ex)
        {
            detail = $"no se pudo unir la bajada al rociador ({ex.Message})";
            return SprinklerOutcome.Failed;
        }

        // --- Unión a la tubería: Tap o Te según la preferencia de ruteo ---
        var junction = best.PipeType?.RoutingPreferenceManager?.PreferredJunctionType ?? PreferredJunctionType.Tee;
        string tapError = "";
        if (junction == PreferredJunctionType.Tap)
        {
            // Si el Tap falla se prueba con Te en una sub-transacción limpia
            using var st = new SubTransaction(doc);
            st.Start();
            try
            {
                doc.Create.NewTakeoffFitting(dropTop, best);
                st.Commit();
                return SprinklerOutcome.Connected;
            }
            catch (Exception ex)
            {
                st.RollBack();
                tapError = $"Tap: {ex.Message}; ";
            }
        }

        try
        {
            ElementId otherId = PlumbingUtils.BreakCurve(doc, best.Id, bestPoint);
            var other = (Pipe)doc.GetElement(otherId);
            Connector c1 = ClosestConnector(best, bestPoint);
            Connector c2 = ClosestConnector(other, bestPoint);
            doc.Create.NewTeeFitting(c1, c2, dropTop);
            return SprinklerOutcome.Connected;
        }
        catch (Exception ex)
        {
            detail = $"{tapError}Te: {ex.Message}";
            return SprinklerOutcome.Failed;
        }
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
