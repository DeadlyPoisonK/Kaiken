using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Port a C# del grafo "04-Modificar Revision.dyn".
/// Modifica el código de revisión (INFO_10_Revision) y el 10º segmento del
/// Número de Plano (Sheet Number) en las láminas del documento, opcionalmente
/// restringido a un Sheet Set (Publish Set) específico para no pisar la
/// revisión de otros sets que usan códigos/fechas distintos.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ModifyRevisionCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Document doc = commandData.Application.ActiveUIDocument.Document;

        var sheetSets = ExportHelpers.GetSheetSets(doc);

        var win = new ModifyRevisionWindow(sheetSets.Select(s => s.Name));
        if (win.ShowDialog() != true)
        {
            return Result.Cancelled;
        }

        string newRev = win.RevisionCode;
        bool filterLength = win.FilterLength29;
        RevisionSettings cfg = KaikenSettings.Current.Revisions;
        bool updateParam = win.UpdateParam;
        bool updateSheetNum = win.UpdateSheetNumber;

        var allSheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(s => !s.IsTemplate)
            .ToList();

        var scopedSheets = ExportHelpers.FilterBySelectedSet(allSheets, sheetSets, win.SelectedSetName);
        if (scopedSheets == null)
        {
            TaskDialog.Show("Modificar Revisión", $"No se encontró el Sheet Set '{win.SelectedSetName}'.");
            return Result.Succeeded;
        }
        allSheets = scopedSheets;

        var targetSheets = filterLength
            ? allSheets.Where(s => (s.SheetNumber ?? "").Length > cfg.MinSheetNumberLength).ToList()
            : allSheets;

        if (targetSheets.Count == 0)
        {
            TaskDialog.Show("Modificar Revisión", "No se encontraron láminas que cumplan el criterio de selección.");
            return Result.Succeeded;
        }

        int updatedCount = 0;
        int numParamUpdated = 0;
        int numSheetNumUpdated = 0;
        var errors = new List<string>();

        using (Transaction tx = new Transaction(doc, "Modificar Código de Revisión en Láminas"))
        {
            tx.Start();

            foreach (var sheet in targetSheets)
            {
                bool sheetModified = false;

                // 1. Parámetro de revisión
                if (updateParam)
                {
                    Parameter pRev = sheet.LookupParameter(cfg.RevisionParameter);
                    if (pRev != null && !pRev.IsReadOnly)
                    {
                        try
                        {
                            pRev.Set(newRev);
                            numParamUpdated++;
                            sheetModified = true;
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Err {cfg.RevisionParameter} en {sheet.SheetNumber}: {ex.Message}");
                        }
                    }
                }

                // 2. Segmento de revisión del Sheet Number
                if (updateSheetNum)
                {
                    string currentNum = sheet.SheetNumber ?? "";
                    string? newSheetNum = cfg.WithRevisionSegment(currentNum, newRev);
                    if (newSheetNum != null && newSheetNum != currentNum)
                    {
                        try
                        {
                            sheet.SheetNumber = newSheetNum;
                            numSheetNumUpdated++;
                            sheetModified = true;
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Err SheetNumber en {currentNum}: {ex.Message}");
                        }
                    }
                }

                if (sheetModified) updatedCount++;
            }

            tx.Commit();
        }

        string msg = $"Proceso finalizado con éxito.\n\n" +
                     $"• Aplicado a: {win.SelectedSetName}\n" +
                     $"• Láminas procesadas: {targetSheets.Count}\n" +
                     $"• Láminas modificadas: {updatedCount}\n" +
                     $"• Parámetros {cfg.RevisionParameter} actualizados: {numParamUpdated}\n" +
                     $"• Números de plano (Sheet Number) actualizados: {numSheetNumUpdated}";

        if (errors.Count > 0)
        {
            msg += $"\n\nObservaciones ({errors.Count}):\n" + string.Join("\n", errors.Take(5));
        }

        TaskDialog.Show("Modificar Revisión - Resultado", msg);
        return Result.Succeeded;
    }
}
