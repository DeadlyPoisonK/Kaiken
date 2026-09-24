using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace Kaiken;

[Transaction(TransactionMode.Manual)]
public class DwgToBlocksCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        // 1. Detectar si el usuario tenía seleccionado un vínculo CAD antes de pulsar el botón
        ImportInstance? preSelectedImport = null;
        ICollection<ElementId> selIds = uidoc.Selection.GetElementIds();
        if (selIds.Count > 0)
        {
            preSelectedImport = selIds
                .Select(id => doc.GetElement(id))
                .OfType<ImportInstance>()
                .FirstOrDefault();
        }

        // 2. Mostrar Ventana Interactiva
        var window = new DwgToBlocksWindow(doc, preSelectedImport);
        if (window.ShowDialog() != true)
        {
            return Result.Cancelled;
        }

        ImportInstance importElem = window.SelectedImport!;
        string blockName = window.SelectedBlockName;
        bool selectAllBlocks = window.SelectAllBlocks;
        string layerFilter = window.SelectedLayer;
        FamilySymbol symbol = window.SelectedSymbol!;
        Level level = window.SelectedLevel!;
        double elevationOffsetMeters = window.ElevationOffsetMeters;
        bool useDefaultElevation = window.UseDefaultFamilyElevation;
        double angleOffsetDeg = window.AngleOffsetDeg;
        bool pinElements = window.PinElements;

        // 3. Escanear Bloques del DWG
        List<CadBlockInstanceInfo> allBlocks = DwgToBlocksHelpers.ExtractBlocksFromImport(importElem);

        // Filtrar por el nombre del bloque seleccionado (o traer todos si el usuario eligió
        // "Todos los bloques de la capa" — útil cuando el DWG mezcla nombres inconsistentes,
        // como bloques anónimos "*U10" que no calzan con el nombre esperado).
        List<CadBlockInstanceInfo> targetBlocks = selectAllBlocks
            ? allBlocks.ToList()
            : allBlocks
                .Where(b => string.Equals(b.BlockName, blockName, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (!string.IsNullOrWhiteSpace(layerFilter))
        {
            targetBlocks = targetBlocks
                .Where(b => string.Equals(b.LayerName, layerFilter, StringComparison.OrdinalIgnoreCase) ||
                            b.LayerName.ToLower().Contains(layerFilter.ToLower()))
                .ToList();
        }

        if (targetBlocks.Count == 0)
        {
            string blockDesc = selectAllBlocks ? "ningún bloque" : $"instancias del bloque '{blockName}'";
            TaskDialog.Show("DWG a Bloques", $"No se encontraron {blockDesc} " +
                (string.IsNullOrEmpty(layerFilter) ? "" : $"en la capa '{layerFilter}' ") + "en el plano CAD seleccionado.");
            return Result.Cancelled;
        }

        // 4. Inserción Masiva en Transacción de Revit
        int placedCount = 0;
        int failedCount = 0;
        string? firstError = null;

        using (Transaction tx = new Transaction(doc, $"DWG a Bloques: {blockName}"))
        {
            tx.Start();

            if (!symbol.IsActive)
            {
                symbol.Activate();
            }

            double zOffsetFt = elevationOffsetMeters * DwgToBlocksHelpers.MetersToFeet;

            foreach (var block in targetBlocks)
            {
                try
                {
                    // Posición con elevación sobre el nivel
                    double placementZ = useDefaultElevation ? level.Elevation : (level.Elevation + zOffsetFt);
                    XYZ placementPoint = new XYZ(block.Position.X, block.Position.Y, placementZ);

                    // Inserción de la familia
                    FamilyInstance inst = doc.Create.NewFamilyInstance(
                        placementPoint, symbol, level, StructuralType.NonStructural);

                    // Establecer parámetro explícito de elevación / desfase desde anfitrión si NO se usa elevación por defecto
                    if (!useDefaultElevation)
                    {
                        Parameter? paramElev = inst.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                        if (paramElev != null && !paramElev.IsReadOnly)
                        {
                            paramElev.Set(zOffsetFt);
                        }
                        else
                        {
                            Parameter? paramOffset = inst.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
                            if (paramOffset != null && !paramOffset.IsReadOnly)
                            {
                                paramOffset.Set(zOffsetFt);
                            }
                        }
                    }

                    // Rotación total (Ángulo extraído del CAD + Desfase del usuario)
                    double totalAngleDeg = block.RotationDeg + angleOffsetDeg;
                    double totalAngleRad = totalAngleDeg * (Math.PI / 180.0);

                    if (Math.Abs(totalAngleRad) > 1e-6)
                    {
                        Line axis = Line.CreateBound(placementPoint, placementPoint + XYZ.BasisZ);
                        ElementTransformUtils.RotateElement(doc, inst.Id, axis, totalAngleRad);
                    }

                    // Fijar elemento si fue requerido
                    if (pinElements)
                    {
                        inst.Pinned = true;
                    }

                    placedCount++;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    firstError ??= ex.Message;
                }
            }

            // doc.Regenerate debe llamarse dentro de la transacción activa
            doc.Regenerate();
            tx.Commit();
        }

        // Refrescar la vista activa
        uidoc.RefreshActiveView();

        // 5. Reportar Resultado
        string blockLabel = selectAllBlocks ? "todos los bloques de la capa" : $"el bloque CAD '{blockName}'";
        string msg = $"Se colocaron exitosamente {placedCount} familias '{symbol.FamilyName}: {symbol.Name}' " +
                     $"para {blockLabel}.";

        if (failedCount > 0)
        {
            msg += $"\n\nAtención: {failedCount} elementos no pudieron colocarse. (Error: {firstError})";
        }

        TaskDialog.Show("DWG a Bloques", msg);
        return Result.Succeeded;
    }
}
