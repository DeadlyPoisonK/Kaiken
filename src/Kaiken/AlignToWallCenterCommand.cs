using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace Kaiken;

/// <summary>
/// Comando que busca la línea de eje (centro) del muro más cercano, tanto en el modelo local
/// como en modelos vinculados, y desplaza el elemento horizontalmente para alinearlo a dicho eje,
/// conservando su altura Z y orientando su cara frontal (+Y de la familia) hacia el lado del muro
/// en el que ya se encontraba el elemento.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class AlignToWallCenterCommand : IExternalCommand
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
                    "Selecciona uno o varios elementos para alinear al centro del muro más cercano"
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
            TaskDialog.Show("Alinear a Centro de Muro", "No se seleccionó ningún elemento válido.");
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

        using (var tx = new Transaction(doc, $"Alinear {elementsToProcess.Count} elementos al centro de muro"))
        {
            tx.Start();

            foreach (var fi in elementsToProcess)
            {
                if (!(fi.Location is LocationPoint lp)) continue;

                XYZ P = lp.Point;

                Wall? closestWall = null;
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

                XYZ facingRef = new XYZ(fi.FacingOrientation.X, fi.FacingOrientation.Y, 0.0);

                foreach (var wall in localWalls)
                {
                    TryProjectOntoWallAxis(wall, P, facingRef, ref minDistance2D, ref closestWall, ref closestProjPt, ref closestNormal);
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

                    XYZ facingRefLocal = linkXfInv.OfVector(facingRef);

                    foreach (var wall in linkWalls)
                    {
                        TryProjectOntoWallAxis(
                            wall, localP, facingRefLocal, ref minDistance2D, ref closestWall, ref closestProjPt, ref closestNormal,
                            linkXf
                        );
                    }
                }

                // 4. Alinear si encontramos un muro
                if (closestWall != null && closestProjPt != null && closestNormal != null)
                {
                    try
                    {
                        // Desplazar horizontalmente al punto proyectado sobre el eje, manteniendo el Z (altura) original
                        XYZ translation = new XYZ(closestProjPt.X - P.X, closestProjPt.Y - P.Y, 0.0);
                        if (translation.GetLength() > 1e-9)
                        {
                            ElementTransformUtils.MoveElement(doc, fi.Id, translation);
                        }

                        // Rotar para orientar el frente (+Y de la familia) hacia el lado del muro donde ya estaba
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
                         $" • {okCount} elementos alineados al centro del muro correctamente.\n" +
                         $" • {noWallCount} marcados en rojo para revisión manual (no se encontró ningún muro a menos de {searchRadiusFt} pies).\n";

        if (failedCount > 0)
        {
            summary += $" • {failedCount} fallidos debido a errores de Revit.\n\n" +
                       $"Primeros errores:\n" + string.Join("\n", errorMessages.Take(5));
        }

        TaskDialog.Show("Alinear a Centro de Muro", summary);

        return Result.Succeeded;
    }

    /// <summary>
    /// Proyecta el punto <paramref name="p"/> sobre la línea de eje (centro) del muro dado y, si resulta
    /// ser la coincidencia más cercana hallada hasta ahora, actualiza los valores por referencia.
    /// Si <paramref name="linkXf"/> se provee, <paramref name="p"/> y <paramref name="facingRef"/> se
    /// interpretan en coordenadas del vínculo, y el punto/normal resultantes se transforman de vuelta
    /// a coordenadas del modelo host.
    /// </summary>
    private static void TryProjectOntoWallAxis(
        Wall wall,
        XYZ p,
        XYZ facingRef,
        ref double minDistance2D,
        ref Wall? closestWall,
        ref XYZ? closestProjPt,
        ref XYZ? closestNormal,
        Transform? linkXf = null)
    {
        if (!(wall.Location is LocationCurve lc)) return;

        Curve curve = lc.Curve;

        try
        {
            IntersectionResult? proj = curve.Project(p);
            if (proj == null) return;

            XYZ projPt = proj.XYZPoint;

            // Tangente del eje del muro en el punto proyectado, y su normal en el plano XY
            Transform derivatives = curve.ComputeDerivatives(proj.Parameter, false);
            XYZ tangent = derivatives.BasisX;
            XYZ tangentXY = new XYZ(tangent.X, tangent.Y, 0.0);
            if (tangentXY.GetLength() < 1e-9) return; // Muro vertical o eje degenerado: ignorar

            tangentXY = tangentXY.Normalize();
            XYZ normal = new XYZ(-tangentXY.Y, tangentXY.X, 0.0);

            // Elegir el lado de la normal (hay dos, perpendiculares al eje) que más se parezca a la
            // orientación frontal ACTUAL del elemento, para conservar el lado hacia el que ya miraba
            // en vez de depender de su posición (que puede estar casi encima del eje del muro,
            // p.ej. en elementos embebidos en el muro, haciendo esa señal poco confiable).
            if (normal.DotProduct(facingRef) < 0)
            {
                normal = -normal;
            }

            XYZ hostProjPt = linkXf != null ? linkXf.OfPoint(projPt) : projPt;
            XYZ hostNormal = linkXf != null ? linkXf.OfVector(normal) : normal;
            XYZ hostP = linkXf != null ? linkXf.OfPoint(p) : p;

            double dist2D = new XYZ(hostP.X, hostP.Y, 0.0).DistanceTo(new XYZ(hostProjPt.X, hostProjPt.Y, 0.0));
            if (dist2D < minDistance2D)
            {
                minDistance2D = dist2D;
                closestWall = wall;
                closestProjPt = hostProjPt;
                closestNormal = hostNormal;
            }
        }
        catch { /* Ignorar errores al proyectar sobre ejes rotos o complejos */ }
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
}
