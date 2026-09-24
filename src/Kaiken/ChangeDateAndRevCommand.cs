using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Port a C# del grafo "03.2-Cambiar Fecha y REV en parametros.dyn".
/// Modifica la fecha de revisión (INFO_Fecha) y sincroniza el parámetro
/// INFO_10_Revision leyendo el 10º segmento del Número de Plano.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ChangeDateAndRevCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Document doc = commandData.Application.ActiveUIDocument.Document;

        var sheetSets = ExportHelpers.GetSheetSets(doc);

        var win = new ChangeDateAndRevWindow(sheetSets.Select(s => s.Name));
        if (win.ShowDialog() != true)
        {
            return Result.Cancelled;
        }

        string newDate = win.RevisionDate;
        bool syncRev = win.SyncRevFromSheetNumber;
        bool filterLength = win.FilterLength29;
        RevisionSettings cfg = KaikenSettings.Current.Revisions;

        var allSheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(s => !s.IsTemplate)
            .ToList();

        var scopedSheets = ExportHelpers.FilterBySelectedSet(allSheets, sheetSets, win.SelectedSetName);
        if (scopedSheets == null)
        {
            TaskDialog.Show("Cambiar Fecha y REV", $"No se encontró el Sheet Set '{win.SelectedSetName}'.");
            return Result.Succeeded;
        }
        allSheets = scopedSheets;

        var targetSheets = filterLength
            ? allSheets.Where(s => (s.SheetNumber ?? "").Length > cfg.MinSheetNumberLength).ToList()
            : allSheets;

        if (targetSheets.Count == 0)
        {
            TaskDialog.Show("Cambiar Fecha y REV", "No se encontraron láminas que cumplan el criterio de selección.");
            return Result.Succeeded;
        }

        int updatedCount = 0;
        int dateUpdatedCount = 0;
        int revSyncedCount = 0;
        var errors = new List<string>();

        using (Transaction tx = new Transaction(doc, "Cambiar Fecha y REV en Parámetros"))
        {
            tx.Start();

            foreach (var sheet in targetSheets)
            {
                bool sheetModified = false;

                // 1. Actualizar el parámetro de fecha
                Parameter pDate = sheet.LookupParameter(cfg.DateParameter);
                if (pDate != null && !pDate.IsReadOnly)
                {
                    try
                    {
                        pDate.Set(newDate);
                        dateUpdatedCount++;
                        sheetModified = true;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Err {cfg.DateParameter} en {sheet.SheetNumber}: {ex.Message}");
                    }
                }

                // 2. Sincronizar el parámetro de revisión con el segmento de revisión del Sheet Number
                if (syncRev)
                {
                    string? extractedRev = cfg.GetRevisionSegment(sheet.SheetNumber ?? "");
                    if (extractedRev != null)
                    {
                        Parameter pRev = sheet.LookupParameter(cfg.RevisionParameter);
                        if (pRev != null && !pRev.IsReadOnly)
                        {
                            try
                            {
                                pRev.Set(extractedRev);
                                revSyncedCount++;
                                sheetModified = true;
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"Err {cfg.RevisionParameter} en {sheet.SheetNumber}: {ex.Message}");
                            }
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
                     $"• Parámetros {cfg.DateParameter} actualizados: {dateUpdatedCount}\n" +
                     $"• Parámetros {cfg.RevisionParameter} sincronizados: {revSyncedCount}";

        if (errors.Count > 0)
        {
            msg += $"\n\nObservaciones ({errors.Count}):\n" + string.Join("\n", errors.Take(5));
        }

        TaskDialog.Show("Cambiar Fecha y REV - Resultado", msg);
        return Result.Succeeded;
    }
}
