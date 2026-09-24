using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace Kaiken;

/// <summary>
/// Port directo (a C#) del grafo "Export DWF.dyn" de Kevin. Exporta cada hoja de un
/// Sheet Set a un .dwf nombrado "Número - Nombre", cerrando diálogos automáticamente
/// durante el proceso (igual que el DWG — es crítico desuscribirse en el finally,
/// tal como advertía el comentario del script original, o Revit seguiría auto-cerrando
/// TODOS los diálogos, incluso los de guardar, hasta reiniciar).
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ExportDwfCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Document doc = commandData.Application.ActiveUIDocument.Document;
        UIApplication uiApp = commandData.Application;

        var prompt = ExportHelpers.PromptForFolderAndSheetSet(doc, "Exportar DWF");
        if (prompt == null) return Result.Cancelled;
        var (folderPath, targetSet) = prompt.Value;

        var outLog = new List<string>();
        // Nota: el grafo original fijaba options.ExportMergeRegion = False, pero esa
        // propiedad no existe en la Revit API 2025 (probablemente cambió entre versiones).
        // Se deja la configuración por defecto; si notas alguna diferencia visual en los
        // DWF exportados avísame y ajustamos las opciones reales disponibles.
        var options = new DWFExportOptions();

        EventHandler<DialogBoxShowingEventArgs> autoCloseHandler = (sender, args) => args.OverrideResult(1);

        // El export a DWF exige que el documento esté "modifiable" (dentro de una
        // transacción abierta), igual que en el grafo Dynamo original. Sin esto,
        // Revit tira "Exporting to DWF/DWFX requires that document is modifiable."
        using var t = new Transaction(doc, "Exportar DWF");
        t.Start();

        uiApp.DialogBoxShowing += autoCloseHandler;
        try
        {
            foreach (var view in targetSet.Views.Cast<Autodesk.Revit.DB.View>())
            {
                if (view is ViewSheet sheet)
                {
                    string fullName = $"{sheet.SheetNumber} - {sheet.Name}";
                    string fileName = ExportHelpers.LimpiarNombre(fullName);

                    var viewSet = new ViewSet();
                    viewSet.Insert(sheet);

                    try
                    {
                        doc.Export(folderPath, fileName, viewSet, options);
                        outLog.Add("ÉXITO: " + fileName);
                    }
                    catch (Exception ex)
                    {
                        outLog.Add($"FALLO en {fileName}: {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            uiApp.DialogBoxShowing -= autoCloseHandler;
            t.Commit();
        }

        TaskDialog.Show("Exportar DWF - resultado", string.Join("\n", outLog));
        return Result.Succeeded;
    }
}
