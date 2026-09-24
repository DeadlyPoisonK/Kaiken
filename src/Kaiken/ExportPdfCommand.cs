using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Port directo (a C#) del grafo "Export PDF.dyn" de Kevin. Exporta cada hoja de un
/// Sheet Set a un .pdf nombrado "Número - Nombre" (a diferencia del DWG, que usa
/// solo el número — así estaba el grafo original, se preserva igual).
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ExportPdfCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Document doc = commandData.Application.ActiveUIDocument.Document;

        var prompt = ExportHelpers.PromptForFolderAndSheetSet(doc, "Exportar PDF");
        if (prompt == null) return Result.Cancelled;
        var (folderPath, targetSet) = prompt.Value;

        var outLog = new List<string>();
        var options = new PDFExportOptions();

        // Igual que el DWF: el grafo original envolvía esto en una transacción
        // (TransactionManager.EnsureInTransaction). Se preserva por si el export a PDF
        // exige lo mismo en esta versión de Revit.
        using var t = new Transaction(doc, "Exportar PDF");
        t.Start();
        try
        {
            foreach (var view in targetSet.Views.Cast<Autodesk.Revit.DB.View>())
            {
                if (view is ViewSheet sheet)
                {
                    string fullName = $"{sheet.SheetNumber} - {sheet.Name}";
                    string fileName = ExportHelpers.LimpiarNombre(fullName);
                    options.FileName = fileName;

                    var viewIds = new List<ElementId> { sheet.Id };

                    try
                    {
                        doc.Export(folderPath, viewIds, options);
                        outLog.Add("Exportado: " + fileName);
                    }
                    catch (Exception ex)
                    {
                        outLog.Add($"Error en {fileName}: {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            t.Commit();
        }

        TaskDialog.Show("Exportar PDF - resultado", string.Join("\n", outLog));
        return Result.Succeeded;
    }
}
