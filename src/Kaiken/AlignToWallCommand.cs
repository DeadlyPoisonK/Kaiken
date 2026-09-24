using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace Kaiken;

/// <summary>
/// Comando que busca la cara exterior (enrasado de placa de yeso-cartón u homólogo) del muro más cercano,
/// tanto en el modelo local como en modelos vinculados, y desplaza el elemento horizontalmente para
/// alinearlo a dicha cara, conservando su altura Z y orientando su cara frontal (+Y de la familia)
/// hacia el exterior del muro (hacia la habitación).
/// </summary>
[Transaction(TransactionMode.Manual)]
public class AlignToWallCommand : IExternalCommand
{
    private class FamilyInstanceSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element e)
        {
            return e is FamilyInstance && e.Location is LocationPoint;
        }

        public bool AllowReference(Reference r, XYZ p) => false;
    }

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        // 1. Recoger elementos de la selección actual
        var elementsToProcess = uidoc.Selection.GetElementIds()
            .Select(id => doc.GetElement(id))
            .OfType<FamilyInstance>()
            .Where(fi => fi.Location is LocationPoint)
            .ToList();

        // Si no hay selección, pedir al usuario seleccionar uno o más elementos
        if (elementsToProcess.Count == 0)
        {
            try
            {
                var picked = uidoc.Selection.PickObjects(
                    ObjectType.Element,
                    new FamilyInstanceSelectionFilter(),
                    "Selecciona uno o varios elementos para alinear al muro más cercano"
                );
                elementsToProcess = picked
                    .Select(r => doc.GetElement(r.ElementId))
                    .OfType<FamilyInstance>()
                    .ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
        }

        if (elementsToProcess.Count == 0)
        {
            TaskDialog.Show("Alinear a Muro", "No se seleccionó ningún elemento válido.");
            return Result.Cancelled;
        }

        const double searchRadiusFt = 10.0; // Radio de búsqueda: 10 pies (~3 metros)

        // Obtener todos los vínculos cargados
        var linkInstances = new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .Where(li => li.GetLinkDocument() != null)
            .ToList();

        int okCount = 0;
        int noWallCount = 0;
        int failedCount = 0;
        var errorMessages = new List<string>();
        var unresolvedIds = new List<ElementId>();

        using (var tx = new Transaction(doc, $"Alinear {elementsToProcess.Count} elementos a muro"))
        {
            tx.Start();

            foreach (var fi in elementsToProcess)
            {
                if (!(fi.Location is LocationPoint lp)) continue;

                XYZ P = lp.Point;

                Wall? closestWall = null;
                Face? closestFace = null;
                XYZ? closestProjPt = null;
                XYZ? closestNormal = null;
                double minDistance2D = double.MaxValue;

                // 2. Buscar muros en el modelo local en la zona del elemento
                var localOutline = new Outline(
                    P - new XYZ(searchRadiusFt, searchRadiusFt, searchRadiusFt),
                    P + new XYZ(searchRadiusFt, searchRadiusFt, searchRadiusFt)
                );
                var localFilter = new BoundingBoxIntersectsFilter(localOutline);
                var localWalls = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Walls)
                    .WhereElementIsNotElementType()
                    .WherePasses(localFilter)
                    .Cast<Wall>()
                    .ToList();

                foreach (var wall in localWalls)
                {
                    var sideFaces = GetWallSideFaces(wall);
                    foreach (var face in sideFaces)
                    {
                        try
                        {
                            var proj = face.Project(P);
                            if (proj != null)
                            {
                                XYZ normal = face.ComputeNormal(proj.UVPoint);
                                // Filtrar para conservar solo caras verticales (descartar parte superior/inferior del muro)
                                if (Math.Abs(normal.Z) > 0.1) continue;

                                XYZ projPt = proj.XYZPoint;
                                double dist2D = new XYZ(P.X, P.Y, 0.0).DistanceTo(new XYZ(projPt.X, projPt.Y, 0.0));
                                if (dist2D < minDistance2D)
                                {
                                    minDistance2D = dist2D;
                                    closestWall = wall;
                                    closestFace = face;
                                    closestProjPt = projPt;
                                    closestNormal = normal;
                                }
                            }
                        }
                        catch { /* Ignorar errores al proyectar sobre caras rotas o complejas */ }
                    }
                }

                // 3. Buscar muros en los vínculos de Revit
                foreach (var linkInst in linkInstances)
                {
                    var linkDoc = linkInst.GetLinkDocument();
                    if (linkDoc == null) continue;

                    Transform linkXf = linkInst.GetTotalTransform();
                    Transform linkXfInv = linkXf.Inverse;

                    XYZ localP = linkXfInv.OfPoint(P);

                    // Buscar en el bounding box en coordenadas del vínculo
                    var linkOutline = new Outline(
                        localP - new XYZ(searchRadiusFt, searchRadiusFt, searchRadiusFt),
                        localP + new XYZ(searchRadiusFt, searchRadiusFt, searchRadiusFt)
                    );
                    var linkFilter = new BoundingBoxIntersectsFilter(linkOutline);
                    var linkWalls = new FilteredElementCollector(linkDoc)
                        .OfCategory(BuiltInCategory.OST_Walls)
                        .WhereElementIsNotElementType()
                        .WherePasses(linkFilter)
                        .Cast<Wall>()
                        .ToList();

                    foreach (var wall in linkWalls)
                    {
                        var sideFaces = GetWallSideFaces(wall);
                        foreach (var face in sideFaces)
                        {
                            try
                            {
                                var proj = face.Project(localP);
                                if (proj != null)
                                {
                                    XYZ localNormal = face.ComputeNormal(proj.UVPoint);
                                    if (Math.Abs(localNormal.Z) > 0.1) continue;

                                    XYZ localProjPt = proj.XYZPoint;
                                    XYZ hostProjPt = linkXf.OfPoint(localProjPt);
                                    double dist2D = new XYZ(P.X, P.Y, 0.0).DistanceTo(new XYZ(hostProjPt.X, hostProjPt.Y, 0.0));

                                    if (dist2D < minDistance2D)
                                    {
                                        minDistance2D = dist2D;
                                        closestWall = wall;
                                        closestFace = face;
                                        closestProjPt = hostProjPt;
                                        closestNormal = linkXf.OfVector(localNormal);
                                    }
                                }
                            }
                            catch { /* Ignorar */ }
                        }
                    }
                }

                // 4. Alinear si encontramos un muro
                if (closestWall != null && closestProjPt != null && closestNormal != null)
                {
                    try
                    {
                        // Desplazar horizontalmente al punto proyectado, manteniendo el Z (altura) original
                        XYZ translation = new XYZ(closestProjPt.X - P.X, closestProjPt.Y - P.Y, 0.0);
                        if (translation.GetLength() > 1e-9)
                        {
                            ElementTransformUtils.MoveElement(doc, fi.Id, translation);
                        }

                        // Rotar para orientar el frente (+Y de la familia) con la normal exterior del muro
                        XYZ facingXY = new XYZ(fi.FacingOrientation.X, fi.FacingOrientation.Y, 0.0);
                        XYZ normalXY = new XYZ(closestNormal.X, closestNormal.Y, 0.0);

                        if (facingXY.GetLength() > 1e-9 && normalXY.GetLength() > 1e-9)
                        {
                            facingXY = facingXY.Normalize();
                            normalXY = normalXY.Normalize();

                            double angleRad = Math.Atan2(normalXY.Y, normalXY.X) - Math.Atan2(facingXY.Y, facingXY.X);
                            while (angleRad > Math.PI) angleRad -= 2.0 * Math.PI;
                            while (angleRad < -Math.PI) angleRad += 2.0 * Math.PI;

                            if (Math.Abs(angleRad) > 1e-6)
                            {
                                XYZ newPos = new XYZ(closestProjPt.X, closestProjPt.Y, P.Z);
                                Line axis = Line.CreateBound(newPos, newPos + XYZ.BasisZ);
                                ElementTransformUtils.RotateElement(doc, fi.Id, axis, angleRad);
                            }
                        }

                        okCount++;
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        errorMessages.Add($"Elemento {fi.Id.Value} ('{fi.Name}'): {ex.Message}");
                    }
                }
                else
                {
                    noWallCount++;
                    unresolvedIds.Add(fi.Id);
                }
            }

            // Marcar en rojo, en la vista activa, los elementos donde no se encontró muro, para revisión manual
            if (unresolvedIds.Count > 0)
            {
                MarkElementsForReview(doc, uidoc.ActiveView, unresolvedIds);
            }

            tx.Commit();
        }

        uidoc.RefreshActiveView();

        // Mostrar reporte al usuario
        string summary = $"Proceso finalizado:\n" +
                         $" • {okCount} elementos alineados correctamente.\n" +
                         $" • {noWallCount} marcados en rojo para revisión manual (no se encontró ningún muro a menos de {searchRadiusFt} pies).\n";

        if (failedCount > 0)
        {
            summary += $" • {failedCount} fallidos debido a errores de Revit.\n\n" +
                       $"Primeros errores:\n" + string.Join("\n", errorMessages.Take(5));
        }

        TaskDialog.Show("Alinear a Muro", summary);

        return Result.Succeeded;
    }

    /// <summary>
    /// Marca en rojo, en la vista dada, los elementos indicados (para revisión manual posterior),
    /// mediante override gráfico: líneas rojas gruesas y, si es posible, relleno de superficie rojo sólido.
    /// </summary>
    private static void MarkElementsForReview(Document doc, View view, IList<ElementId> elementIds)
    {
        var red = new Color(255, 0, 0);

        ElementId? solidFillPatternId = new FilteredElementCollector(doc)
            .OfClass(typeof(FillPatternElement))
            .Cast<FillPatternElement>()
            .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill)
            ?.Id;

        var ogs = new OverrideGraphicSettings();
        ogs.SetProjectionLineColor(red);
        ogs.SetProjectionLineWeight(8);
        ogs.SetSurfaceTransparency(0);
        if (solidFillPatternId != null)
        {
            ogs.SetSurfaceForegroundPatternColor(red);
            ogs.SetSurfaceForegroundPatternId(solidFillPatternId);
            ogs.SetCutForegroundPatternColor(red);
            ogs.SetCutForegroundPatternId(solidFillPatternId);
        }

        foreach (var id in elementIds)
        {
            try
            {
                view.SetElementOverrides(id, ogs);
            }
            catch { /* Ignorar elementos que no admitan override en esta vista */ }
        }
    }

    /// <summary>
    /// Obtiene las caras laterales (exterior e interior) de un muro.
    /// Si falla HostObjectUtils, realiza una búsqueda geométrica alternativa.
    /// </summary>
    private static List<Face> GetWallSideFaces(Wall wall)
    {
        var faces = new List<Face>();

        // Intentar obtener las caras laterales a través de HostObjectUtils
        try
        {
            var extRefs = HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior);
            foreach (var r in extRefs)
            {
                if (wall.GetGeometryObjectFromReference(r) is Face f)
                {
                    faces.Add(f);
                }
            }

            var intRefs = HostObjectUtils.GetSideFaces(wall, ShellLayerType.Interior);
            foreach (var r in intRefs)
            {
                if (wall.GetGeometryObjectFromReference(r) is Face f)
                {
                    faces.Add(f);
                }
            }
        }
        catch
        {
            // Fallback en caso de error
        }

        // Fallback: Si no se pudo obtener nada o está vacío, extraer la geometría del muro
        if (faces.Count == 0)
        {
            Options opt = new Options { DetailLevel = ViewDetailLevel.Fine };
            GeometryElement geom = wall.get_Geometry(opt);
            if (geom != null)
            {
                foreach (GeometryObject go in geom)
                {
                    if (go is Solid s && s.Volume > 0)
                    {
                        foreach (Face f in s.Faces)
                        {
                            faces.Add(f);
                        }
                    }
                    else if (go is GeometryInstance gi)
                    {
                        GeometryElement instGeom = gi.GetInstanceGeometry();
                        foreach (GeometryObject igo in instGeom)
                        {
                            if (igo is Solid s2 && s2.Volume > 0)
                            {
                                foreach (Face f in s2.Faces)
                                {
                                    faces.Add(f);
                                }
                            }
                        }
                    }
                }
            }
        }

        return faces;
    }
}
