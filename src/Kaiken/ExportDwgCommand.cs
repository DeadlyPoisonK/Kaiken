using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace Kaiken;

/// <summary>
/// Port directo (a C#) del grafo "Export DWG.dyn" de Kevin. Misma lógica:
/// exporta cada hoja de un Sheet Set a un .dwg nombrado por número de hoja,
/// usando colores RGB reales (TrueColorPerView — el fix que ya tenían aplicado
/// para el bug de azul que salía magenta), cierra diálogos automáticamente,
/// y limpia archivos incidentales (.pcp/.jpg/.png) que Revit deja tras exportar.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ExportDwgCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Document doc = commandData.Application.ActiveUIDocument.Document;
        UIApplication uiApp = commandData.Application;

        var prompt = ExportHelpers.PromptForFolderAndSheetSet(doc, "Exportar DWG");
        if (prompt == null) return Result.Cancelled;
        var (folderPath, targetSet) = prompt.Value;

        var outLog = new List<string>();
        var dwgGenerados = new HashSet<string>();

        var options = new DWGExportOptions
        {
            MergedViews = true,
            Colors = ExportColorMode.TrueColorPerView,
        };

        EventHandler<DialogBoxShowingEventArgs> autoCloseHandler = (sender, args) => args.OverrideResult(1);

        var archivosAntes = Directory.Exists(folderPath)
            ? new HashSet<string>(Directory.GetFiles(folderPath).Select(Path.GetFileName)!)
            : new HashSet<string>();

        uiApp.DialogBoxShowing += autoCloseHandler;
        try
        {
            foreach (var view in targetSet.Views.Cast<Autodesk.Revit.DB.View>())
            {
                if (view is ViewSheet sheet)
                {
                    string fileName = ExportHelpers.LimpiarNombre(sheet.SheetNumber);
                    var viewIds = new List<ElementId> { sheet.Id };

                    try
                    {
                        doc.Export(folderPath, fileName, viewIds, options);
                        outLog.Add("ÉXITO: " + fileName);
                        dwgGenerados.Add(fileName + ".dwg");
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
        }

        // Limpieza de archivos incidentales que Revit deja junto a los DWG
        if (Directory.Exists(folderPath))
        {
            var archivosDespues = Directory.GetFiles(folderPath).Select(Path.GetFileName)!;
            var nuevos = archivosDespues.Except(archivosAntes);
            string[] extensionesABorrar = { ".pcp", ".jpg", ".jpeg", ".png" };

            foreach (var archivo in nuevos)
            {
                if (archivo == null || dwgGenerados.Contains(archivo)) continue;
                string ext = Path.GetExtension(archivo).ToLowerInvariant();
                if (extensionesABorrar.Contains(ext))
                {
                    try
                    {
                        File.Delete(Path.Combine(folderPath, archivo));
                        outLog.Add("LIMPIEZA: eliminado " + archivo);
                    }
                    catch (Exception ex)
                    {
                        outLog.Add($"LIMPIEZA FALLÓ en {archivo}: {ex.Message}");
                    }
                }
            }
        }

        TaskDialog.Show("Exportar DWG - resultado", string.Join("\n", outLog));
        return Result.Succeeded;
    }
}
