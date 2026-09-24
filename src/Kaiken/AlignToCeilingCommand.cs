using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace Kaiken;

/// <summary>
/// Comando que busca la cara inferior (enrasado) del cielo más cercano,
/// tanto en el modelo local como en modelos vinculados, y desplaza el elemento verticalmente para
/// alinearlo a dicha cara, conservando su posición horizontal (X, Y).
/// </summary>
[Transaction(TransactionMode.Manual)]
public class AlignToCeilingCommand : IExternalCommand
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
                    "Selecciona uno o varios elementos para alinear al cielo más cercano"
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
            TaskDialog.Show("Alinear a Cielo", "No se seleccionó ningún elemento válido.");
            return Result.Cancelled;
        }

        // Radio de búsqueda: solo se considera el cielo más cercano por ENCIMA del elemento, dentro de 4 m
        // (altura típica de entrepiso). Si no se encuentra ninguno, el elemento se marca en rojo para revisión manual.
        double searchRadiusFt = UnitUtils.ConvertToInternalUnits(4.0, UnitTypeId.Meters);

        // Obtener todos los vínculos cargados
        var linkInstances = new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .Where(li => li.GetLinkDocument() != null)
            .ToList();

        int okCount = 0;
        int noCeilingCount = 0;
        int failedCount = 0;
        var errorMessages = new List<string>();
        var unresolvedIds = new List<ElementId>();

        using (var tx = new Transaction(doc, $"Alinear {elementsToProcess.Count} elementos a cielo"))
        {
            tx.Start();

            foreach (var fi in elementsToProcess)
            {
                if (!(fi.Location is LocationPoint lp)) continue;

                XYZ P = lp.Point;

                // Solo se considera un cielo por ENCIMA del elemento. Si no se encuentra ninguno dentro del
                // radio de búsqueda, el elemento se deja sin tocar y se marca para revisión manual.
                Ceiling? closestCeiling = null;
                Face? closestFace = null;
                XYZ? closestProjPt = null;
                double minDistance = double.MaxValue;

                // 2. Buscar cielos en el modelo local en la zona del elemento
                var localOutline = new Outline(
                    P - new XYZ(searchRadiusFt, searchRadiusFt, searchRadiusFt),
                    P + new XYZ(searchRadiusFt, searchRadiusFt, searchRadiusFt)
                );
                var localFilter = new BoundingBoxIntersectsFilter(localOutline);
                var localCeilings = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Ceilings)
                    .WhereElementIsNotElementType()
                    .WherePasses(localFilter)
                    .Cast<Ceiling>()
                    .ToList();

                foreach (var ceiling in localCeilings)
                {
                    var bottomFaces = GetCeilingBottomFaces(ceiling);
                    foreach (var face in bottomFaces)
                    {
                        try
                        {
                            var proj = face.Project(P);
                            if (proj != null)
                            {
                                XYZ normal = face.ComputeNormal(proj.UVPoint);
                                // Filtrar para conservar caras que miren hacia abajo (normal.Z negativa)
                                if (normal.Z > -0.5) continue;

                                XYZ projPt = proj.XYZPoint;
                                // Solo considerar cielos por ENCIMA del elemento
                                if (projPt.Z <= P.Z) continue;

                                double dist = P.DistanceTo(projPt);
                                if (dist < minDistance)
                                {
                                    minDistance = dist;
                                    closestCeiling = ceiling;
                                    closestFace = face;
                                    closestProjPt = projPt;
                                }
                            }
                        }
                        catch { /* Ignorar */ }
                    }
                }

                // 3. Buscar cielos en los vínculos de Revit
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
                    var linkCeilings = new FilteredElementCollector(linkDoc)
                        .OfCategory(BuiltInCategory.OST_Ceilings)
                        .WhereElementIsNotElementType()
                        .WherePasses(linkFilter)
                        .Cast<Ceiling>()
                        .ToList();

                    foreach (var ceiling in linkCeilings)
                    {
                        var bottomFaces = GetCeilingBottomFaces(ceiling);
                        foreach (var face in bottomFaces)
                        {
                            try
                            {
                                var proj = face.Project(localP);
                                if (proj != null)
                                {
                                    XYZ localNormal = face.ComputeNormal(proj.UVPoint);
                                    if (localNormal.Z > -0.5) continue;

                                    XYZ localProjPt = proj.XYZPoint;
                                    XYZ hostProjPt = linkXf.OfPoint(localProjPt);
                                    // Solo considerar cielos por ENCIMA del elemento
                                    if (hostProjPt.Z <= P.Z) continue;

                                    double dist = P.DistanceTo(hostProjPt);
                                    if (dist < minDistance)
                                    {
                                        minDistance = dist;
                                        closestCeiling = ceiling;
                                        closestFace = face;
                                        closestProjPt = hostProjPt;
                                    }
                                }
                            }
                            catch { /* Ignorar */ }
                        }
                    }
                }

                // 4. Alinear si encontramos un cielo
                if (closestCeiling != null && closestProjPt != null)
                {
                    try
                    {
                        // Desplazar verticalmente al punto proyectado, manteniendo X e Y originales
                        XYZ translation = new XYZ(0.0, 0.0, closestProjPt.Z - P.Z);
                        if (translation.GetLength() > 1e-9)
                        {
                            ElementTransformUtils.MoveElement(doc, fi.Id, translation);
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
                    noCeilingCount++;
                    unresolvedIds.Add(fi.Id);
                }
            }

            // Marcar en rojo, en la vista activa, los elementos donde no se encontró cielo, para revisión manual
            if (unresolvedIds.Count > 0)
            {
                MarkElementsForReview(doc, uidoc.ActiveView, unresolvedIds);
            }

            tx.Commit();
        }

        uidoc.RefreshActiveView();

        // Mostrar reporte al usuario
        double searchRadiusM = UnitUtils.ConvertFromInternalUnits(searchRadiusFt, UnitTypeId.Meters);
        string summary = $"Proceso finalizado:\n" +
                         $" • {okCount} elementos alineados correctamente.\n" +
                         $" • {noCeilingCount} marcados en rojo para revisión manual (no se encontró ningún cielo por encima a menos de {searchRadiusM:0.#} m).\n";

        if (failedCount > 0)
        {
            summary += $" • {failedCount} fallidos debido a errores de Revit.\n\n" +
                       $"Primeros errores:\n" + string.Join("\n", errorMessages.Take(5));
        }

        TaskDialog.Show("Alinear a Cielo", summary);

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
    /// Obtiene las caras inferiores de un cielo.
    /// </summary>
    private static List<Face> GetCeilingBottomFaces(Ceiling ceiling)
    {
        var faces = new List<Face>();

        try
        {
            var bottomRefs = HostObjectUtils.GetBottomFaces(ceiling);
            foreach (var r in bottomRefs)
            {
                if (ceiling.GetGeometryObjectFromReference(r) is Face f)
                {
                    faces.Add(f);
                }
            }
        }
        catch
        {
            // Fallback
        }

        // Fallback: Si no se pudo obtener nada o está vacío, extraer la geometría
        if (faces.Count == 0)
        {
            Options opt = new Options { DetailLevel = ViewDetailLevel.Fine };
            GeometryElement geom = ceiling.get_Geometry(opt);
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
