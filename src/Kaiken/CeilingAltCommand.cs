using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace Kaiken;

/// <summary>
/// Comando para crear un Ceiling-Alt en una o varias tuberías continuas al cruzar un muro.
/// Corta las tuberías a una distancia fija antes del muro, eleva el tramo que cruza,
/// y une ambos lados con una tubería vertical y dos codos de 90°.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class CeilingAltCommand : IExternalCommand
{
    private class PipeFilter : ISelectionFilter
    {
        public bool AllowElement(Element e) => e is MEPCurve mep && MepOps.For(mep) != null;
        public bool AllowReference(Reference r, XYZ p) => false;
    }

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        // 1. Recoger todas las tuberías de la selección actual
        var selPipes = uidoc.Selection.GetElementIds()
                       .Select(id => doc.GetElement(id))
                       .OfType<MEPCurve>()
                       .Where(m => MepOps.For(m) != null)
                       .ToList();

        // Si no hay ninguna en la selección, pedir que seleccione una o varias
        if (selPipes.Count == 0)
        {
            try
            {
                var picked = uidoc.Selection.PickObjects(ObjectType.Element, new PipeFilter(),
                    "Selecciona una o varias tuberías continuas para realizar el salto de nivel (Ceiling-Alt)");
                selPipes = picked.Select(r => doc.GetElement(r.ElementId)).OfType<MEPCurve>().ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
        }

        if (selPipes.Count == 0)
        {
            message = "No se seleccionaron tuberías válidas.";
            return Result.Cancelled;
        }

        // Validar que todas las seleccionadas sean rectas
        var validPipes = new List<MEPCurve>();
        foreach (var pipe in selPipes)
        {
            if (AvoiderHelpers.Axis(pipe) != null)
            {
                validPipes.Add(pipe);
            }
        }

        if (validPipes.Count == 0)
        {
            TaskDialog.Show("Ceiling-Alt", "Ninguna de las tuberías seleccionadas es recta (su eje debe ser una línea).");
            return Result.Cancelled;
        }

        // 2. Calcular la altura sugerida basada en los cielos (Ceilings) localizados en el área de la tubería
        double defaultElevationM = 3.05;
        try
        {
            // Creamos un BoundingBox que encierra a todas las tuberías seleccionadas expandido horizontalmente para detectar cielos debajo/alrededor
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var pipe in validPipes)
            {
                Line? ax = AvoiderHelpers.Axis(pipe);
                if (ax == null) continue;
                XYZ p0 = ax.GetEndPoint(0);
                XYZ p1 = ax.GetEndPoint(1);
                minX = Math.Min(minX, Math.Min(p0.X, p1.X));
                minY = Math.Min(minY, Math.Min(p0.Y, p1.Y));
                minZ = Math.Min(minZ, Math.Min(p0.Z, p1.Z));
                maxX = Math.Max(maxX, Math.Max(p0.X, p1.X));
                maxY = Math.Max(maxY, Math.Max(p0.Y, p1.Y));
                maxZ = Math.Max(maxZ, Math.Max(p0.Z, p1.Z));
            }

            if (minX < maxX)
            {
                // Expandir la caja 1 metro (3.28 pies) horizontalmente y en vertical hacia abajo (para capturar el cielo que pasa por debajo)
                XYZ boxMin = new XYZ(minX - 3.28, minY - 3.28, minZ - 10.0);
                XYZ boxMax = new XYZ(maxX + 3.28, maxY + 3.28, maxZ + 2.0);
                Outline searchOutline = new Outline(boxMin, boxMax);

                var ceilingFilter = new BoundingBoxIntersectsFilter(searchOutline);

                // Obtener cielos del Host que están cerca de las tuberías
                var ceilings = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Ceilings)
                    .WhereElementIsNotElementType()
                    .WherePasses(ceilingFilter)
                    .Cast<Ceiling>()
                    .ToList();

                // Buscar en links en la misma área
                var linkInstances = new FilteredElementCollector(doc, doc.ActiveView.Id)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>();

                foreach (var linkInst in linkInstances)
                {
                    var linkDoc = linkInst.GetLinkDocument();
                    if (linkDoc != null)
                    {
                        Transform linkXf = linkInst.GetTotalTransform();
                        Transform linkXfInv = linkXf.Inverse;

                        // Transformar caja de búsqueda al espacio local del link
                        XYZ localMin = linkXfInv.OfPoint(boxMin);
                        XYZ localMax = linkXfInv.OfPoint(boxMax);
                        Outline localOutline = new Outline(
                            new XYZ(Math.Min(localMin.X, localMax.X), Math.Min(localMin.Y, localMax.Y), Math.Min(localMin.Z, localMax.Z)),
                            new XYZ(Math.Max(localMin.X, localMax.X), Math.Max(localMin.Y, localMax.Y), Math.Max(localMin.Z, localMax.Z))
                        );

                        var localCeilingFilter = new BoundingBoxIntersectsFilter(localOutline);

                        var linkCeilings = new FilteredElementCollector(linkDoc)
                            .OfCategory(BuiltInCategory.OST_Ceilings)
                            .WhereElementIsNotElementType()
                            .WherePasses(localCeilingFilter)
                            .Cast<Ceiling>()
                            .ToList();

                        if (linkCeilings.Count > 0)
                        {
                            ceilings.AddRange(linkCeilings);
                        }
                    }
                }

                if (ceilings.Count > 0)
                {
                    // Buscamos el cielo más alto que encontremos en esta zona específica
                    double maxCeilingOffsetFt = 0;
                    foreach (var ceiling in ceilings)
                    {
                        var offsetParam = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                        if (offsetParam != null)
                        {
                            maxCeilingOffsetFt = Math.Max(maxCeilingOffsetFt, offsetParam.AsDouble());
                        }
                    }

                    if (maxCeilingOffsetFt > 0)
                    {
                        defaultElevationM = (maxCeilingOffsetFt / 3.28084) + 0.05;
                    }
                }
            }
        }
        catch { /* Fallback a 3.05 */ }

        // Pedir parámetros de configuración
        var win = new CeilingAltWindow(defaultElevationM);
        if (win.ShowDialog() != true) return Result.Cancelled;
        var config = win.Result;

        double offsetFt = config.OffsetCm * AvoiderHelpers.CmToFeet;

        // 3. Ejecutar todo en una única transacción (Ctrl+Z deshace todo junto)
        using var tx = new Transaction(doc, $"Crear Ceiling-Alt en {validPipes.Count} tubería(s)");
        tx.Start();
        try
        {
            int ok = 0;
            var errors = new List<string>();

            foreach (var pipe in validPipes)
            {
                try
                {
                    Line axis = AvoiderHelpers.Axis(pipe)!;
                    XYZ S = axis.GetEndPoint(0);
                    XYZ E = axis.GetEndPoint(1);
                    XYZ d = (E - S).Normalize();

                    // Buscar intersección con muros para esta tubería específica
                    var wallClash = FindWallIntersections(doc, pipe, doc.ActiveView);
                    if (wallClash == null)
                    {
                        errors.Add($"Tubería {pipe.Id.Value}: No se cruzó con ningún muro en la vista activa.");
                        continue;
                    }

                    // Determinar cuál extremo de la tubería está en el lado del pasillo (corridor).
                    // Regla de negocio solicitada:
                    // - El extremo que conecta hacia una VÁLVULA (OST_PipeAccessory) o hacia un codo que BAJA (cambio vertical) es el PASILLO (debe quedar abajo).
                    // - El extremo que conecta hacia un codo que gira en HORIZONTAL o continúa recto es la HABITACIÓN (debe elevarse).
                    // Analizamos los conectores de la tubería en los extremos S y E.
                    bool sIsPasillo = false;
                    bool eIsPasillo = false;

                    Connector? connS = AvoiderHelpers.ConnectorAt(pipe, S);
                    Connector? connE = AvoiderHelpers.ConnectorAt(pipe, E);

                    bool IsPasilloConnection(Connector? conn)
                    {
                        if (conn == null) return false;
                        foreach (Connector refConn in conn.AllRefs)
                        {
                            Element owner = refConn.Owner;
                            if (owner == null || owner.Id == pipe.Id) continue;

                            // 1. ¿Es una válvula u accesorio de tubería/ducto?
                            if (owner.Category != null && 
                                (owner.Category.Id.Value == (long)BuiltInCategory.OST_PipeAccessory ||
                                 owner.Category.Id.Value == (long)BuiltInCategory.OST_DuctAccessory))
                            {
                                return true; // Conecta a válvula/accesorio -> es Pasillo
                            }

                            // 2. ¿Es un codo u otro fitting?
                            if (owner is FamilyInstance fi && owner.Category != null && 
                                (owner.Category.Id.Value == (long)BuiltInCategory.OST_PipeFitting ||
                                 owner.Category.Id.Value == (long)BuiltInCategory.OST_DuctFitting ||
                                 owner.Category.Id.Value == (long)BuiltInCategory.OST_CableTrayFitting ||
                                 owner.Category.Id.Value == (long)BuiltInCategory.OST_ConduitFitting))
                            {
                                // Si es un codo, ver si alguna de sus conexiones va hacia abajo (vertical)
                                if (fi.Location is LocationPoint lp)
                                {
                                    // Buscar otras tuberías conectadas a este codo
                                    var fiCm = fi.MEPModel?.ConnectorManager;
                                    if (fiCm != null)
                                    {
                                        foreach (Connector cFit in fiCm.Connectors)
                                        {
                                            foreach (Connector cRefFit in cFit.AllRefs)
                                            {
                                                if (cRefFit.Owner is MEPCurve otherCurve && otherCurve.Id != pipe.Id)
                                                {
                                                    Line? otherAxis = AvoiderHelpers.Axis(otherCurve);
                                                    if (otherAxis != null)
                                                    {
                                                        XYZ otherDir = (otherAxis.GetEndPoint(1) - otherAxis.GetEndPoint(0)).Normalize();
                                                        // Si tiene una inclinación vertical marcada (Z != 0), es un codo que baja
                                                        if (Math.Abs(otherDir.Z) > 0.7)
                                                        {
                                                            return true; // Codo que baja -> es Pasillo
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        return false;
                    }

                    sIsPasillo = IsPasilloConnection(connS);
                    eIsPasillo = IsPasilloConnection(connE);

                    // Si no detectamos con claridad por fittings, usamos fallback: el extremo de menor Z o S por defecto.
                    bool cutNearS = true;
                    if (sIsPasillo && !eIsPasillo)
                    {
                        cutNearS = true;
                    }
                    else if (eIsPasillo && !sIsPasillo)
                    {
                        cutNearS = false;
                    }
                    else
                    {
                        // Fallback clásico: el que esté más abajo o S por defecto
                        cutNearS = S.Z <= E.Z;
                    }

                    // Si el usuario marcó la opción de invertir sentido manualmente
                    if (config.InvertDirection)
                    {
                        cutNearS = !cutNearS;
                    }

                    double tWall = cutNearS ? wallClash.TMin : wallClash.TMax;
                    double tCut = cutNearS ? (tWall - offsetFt) : (tWall + offsetFt);

                    if (tCut <= 0.1 || tCut >= axis.Length - 0.1)
                    {
                        errors.Add($"Tubería {pipe.Id.Value}: Demasiado corta para el corte a {config.OffsetCm} cm del muro.");
                        continue;
                    }

                    XYZ cutPoint = axis.Evaluate(tCut / axis.Length, true);

                    MepOps ops = MepOps.For(pipe)!;
                    var (pipeLeft, pipeRight) = ops.Break(doc, pipe, cutPoint, d);
                    doc.Regenerate();

                    // El tramo que se va a subir es el de la oficina (no pasillo).
                    // Si cutNearS es true, el pasillo está en S (izquierda/pipeLeft), y la oficina en E (derecha/pipeRight).
                    // Si cutNearS es false, el pasillo está en E (derecha/pipeRight), y la oficina en S (izquierda/pipeLeft).
                    MEPCurve pipeToMove = cutNearS ? pipeRight : pipeLeft;
                    MEPCurve pipeToKeep = cutNearS ? pipeLeft : pipeRight;

                    // Obtener elevación del nivel de referencia de esta tubería
                    double levelElevationFt = 0;
                    ElementId levelId = pipe.ReferenceLevel?.Id ?? pipe.LevelId;
                    if (levelId != ElementId.InvalidElementId && doc.GetElement(levelId) is Level level)
                    {
                        levelElevationFt = level.Elevation;
                    }

                    double targetElevationFt = levelElevationFt + (config.ElevationM * 3.28084);

                    var lcMove = (LocationCurve)pipeToMove.Location;
                    var lineMove = (Line)lcMove.Curve;
                    XYZ moveEnd = lineMove.GetEndPoint(0);

                    double currentZ = moveEnd.Z;
                    double deltaZ = targetElevationFt - currentZ;

                    // Mover el tramo seleccionado hacia arriba
                    ElementTransformUtils.MoveElement(doc, pipeToMove.Id, new XYZ(0, 0, deltaZ));
                    doc.Regenerate();

                    XYZ raisedCutPoint = new XYZ(cutPoint.X, cutPoint.Y, cutPoint.Z + deltaZ);

                    // Crear la tubería vertical conectando la punta baja (cutPoint) con la alta (raisedCutPoint)
                    var verticalPipe = ops.Create(doc, pipeToKeep, cutPoint, raisedCutPoint);
                    doc.Regenerate();

                    Connector? connLow = AvoiderHelpers.ConnectorAt(pipeToKeep, cutPoint);
                    Connector? connVertBottom = AvoiderHelpers.ConnectorAt(verticalPipe, cutPoint);
                    Connector? connVertTop = AvoiderHelpers.ConnectorAt(verticalPipe, raisedCutPoint);
                    Connector? connHigh = AvoiderHelpers.ConnectorAt(pipeToMove, raisedCutPoint);

                    if (connLow == null || connVertBottom == null || connVertTop == null || connHigh == null)
                    {
                        throw new InvalidOperationException("No se pudieron ubicar todos los conectores.");
                    }

                    // Conectar con codos
                    doc.Create.NewElbowFitting(connLow, connVertBottom);
                    doc.Create.NewElbowFitting(connVertTop, connHigh);

                    ok++;
                }
                catch (Exception ex)
                {
                    errors.Add($"Tubería {pipe.Id.Value}: {ex.Message}");
                }
            }

            if (ok == 0)
            {
                tx.RollBack();
                string msg = string.Join("\n", errors.Take(5));
                TaskDialog.Show("Ceiling-Alt", $"No se pudo procesar ninguna tubería. Transacción cancelada:\n\n{msg}");
                return Result.Failed;
            }

            tx.Commit();

            string resumen = ok == 1 ? "1 tubería modificada." : $"{ok} tuberías modificadas.";
            if (errors.Count > 0)
            {
                resumen += $"\n\nOmitidas o fallidas ({errors.Count}):\n" + string.Join("\n", errors.Take(3));
            }

            TaskDialog.Show("Ceiling-Alt", $"Listo.\n\n{resumen}");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
            TaskDialog.Show("Ceiling-Alt", "Falló la ejecución del lote:\n" + ex.Message);
            return Result.Failed;
        }
    }

    private class WallClashInfo
    {
        public double TMin;
        public double TMax;
        public ElementId WallId = ElementId.InvalidElementId;
    }

    private WallClashInfo? FindWallIntersections(Document doc, MEPCurve mep, View view)
    {
        Line? axis = AvoiderHelpers.Axis(mep);
        if (axis == null) return null;

        XYZ S = axis.GetEndPoint(0);
        XYZ E = axis.GetEndPoint(1);
        double L = S.DistanceTo(E);
        XYZ d = (E - S).Normalize();

        var connected = AvoiderHelpers.ConnectedElementIds(mep);

        // 1. Muros del host
        var hostWalls = new FilteredElementCollector(doc, view.Id)
            .OfCategory(BuiltInCategory.OST_Walls)
            .WherePasses(new ElementIntersectsElementFilter(mep))
            .Where(w => !connected.Contains(w.Id))
            .ToList();

        foreach (var wall in hostWalls)
        {
            var (tmin, tmax, _, _) = IntersectSolidWall(wall, axis, S, d);
            if (tmax > tmin && tmin >= 0 && tmax <= L)
            {
                return new WallClashInfo { TMin = tmin, TMax = tmax, WallId = wall.Id };
            }
        }

        // 2. Muros en links de Revit
        var linkInstances = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>();

        foreach (var linkInst in linkInstances)
        {
            var linkDoc = linkInst.GetLinkDocument();
            if (linkDoc == null) continue;

            Transform linkXf = linkInst.GetTotalTransform();
            Transform linkXfInv = linkXf.Inverse;

            XYZ S_l = linkXfInv.OfPoint(S);
            XYZ d_l = linkXfInv.OfVector(d);

            var linkWalls = new FilteredElementCollector(linkDoc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .Where(w => !connected.Contains(w.Id) && w.Id != linkInst.Id)
                .ToList();

            foreach (var lw in linkWalls)
            {
                Curve localMepCurve = (mep.Location as LocationCurve).Curve.CreateTransformed(linkXfInv);
                Line? localAxis = localMepCurve as Line;
                if (localAxis == null) continue;

                var (tmin, tmax, _, _) = IntersectSolidWall(lw, localAxis, S_l, d_l, linkXf);
                if (tmax > tmin && tmin >= 0 && tmax <= L)
                {
                    return new WallClashInfo { TMin = tmin, TMax = tmax, WallId = linkInst.Id };
                }
            }
        }

        return null;
    }

    private static (double tmin, double tmax, XYZ wMin, XYZ wMax) IntersectSolidWall(Element e, Line line, XYZ S, XYZ d, Transform? transform = null)
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
            try
            {
                SolidCurveIntersection sci = s.IntersectWithCurve(line, options);
                if (sci != null)
                {
                    for (int i = 0; i < sci.SegmentCount; i++)
                    {
                        Curve c = sci.GetCurveSegment(i);
                        XYZ p0 = c.GetEndPoint(0);
                        XYZ p1 = c.GetEndPoint(1);
                        if (transform != null) { p0 = transform.OfPoint(p0); p1 = transform.OfPoint(p1); }
                        double t1 = (p0 - S).DotProduct(d);
                        double t2 = (p1 - S).DotProduct(d);
                        tmin = Math.Min(tmin, Math.Min(t1, t2));
                        tmax = Math.Max(tmax, Math.Max(t1, t2));
                    }
                }
            }
            catch { /* Ignorar sólidos inválidos o abiertos */ }
        }

        foreach (GeometryObject go in geom)
        {
            if (go is Solid s) ProcessSolid(s);
            else if (go is GeometryInstance gi)
            {
                GeometryElement instGeom = gi.GetInstanceGeometry();
                foreach (GeometryObject igo in instGeom)
                    if (igo is Solid s2) ProcessSolid(s2);
            }
        }
        return (tmin, tmax, wMin, wMax);
    }
}
