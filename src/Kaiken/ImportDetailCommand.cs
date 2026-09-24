using System;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Botón "Recuperar Textos": lee un archivo DXF y recrea todas sus entidades de texto
/// (TEXT/MTEXT) como TextNotes nativos en la vista activa de Revit.
/// Útil para recuperar textos que se pierden o corrompen al explotar un CAD.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ImportDetailCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIApplication uiApp = commandData.Application;
        UIDocument    uidoc  = uiApp.ActiveUIDocument;
        Document      doc    = uidoc.Document;
        View          view   = uidoc.ActiveView;

        // 1. Obtener el ImportInstance seleccionado
        var selectedIds = uidoc.Selection.GetElementIds();
        ImportInstance? importInst = null;

        if (selectedIds.Count == 1)
        {
            importInst = doc.GetElement(selectedIds.First()) as ImportInstance;
        }

        if (importInst == null)
        {
            TaskDialog.Show("Recuperar Textos",
                "Por favor, selecciona primero el plano importado (ImportInstance) antes de presionar el botón.\n\n" +
                "Esto permite leer la posición exacta, rotación y escala del plano para alinear los textos correctamente.");
            return Result.Cancelled;
        }

        // 2. Elegir archivo DXF
        string? dxfPath = ImportDetailHelpers.ChooseCadFile();
        if (dxfPath is null) return Result.Cancelled;

        if (!dxfPath.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
        {
            TaskDialog.Show("Recuperar Textos", "Por favor, selecciona un archivo con extensión .dxf.");
            return Result.Cancelled;
        }

        // 3. Pre-leer el DXF para detectar la unidad de medida original
        DxfData dxfData;
        try
        {
            dxfData = EnchufesDxf.Read(dxfPath);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Recuperar Textos", $"Error al leer el archivo DXF:\n{ex.Message}");
            return Result.Failed;
        }

        // 4. Preguntar por las unidades de medida usando Command Links interactivos
        string detectedUnitName = dxfData.InsUnits switch
        {
            1 => "Pulgadas",
            2 => "Pies",
            4 => "Milímetros",
            5 => "Centímetros",
            6 => "Metros",
            _ => "Milímetros (no especificado)"
        };

        TaskDialog td = new TaskDialog("Unidades del Detalle");
        td.MainInstruction = "Selecciona la unidad de medida del plano original";
        td.MainContent = $"Unidad detectada en el archivo: {detectedUnitName}.\n\n" +
                         "Seleccionar la unidad correcta asegura que los textos se posicionen " +
                         "y tengan el tamaño exacto en Revit.";
        
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Usar Milímetros (mm) - Recomendado para la mayoría de detalles");
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Usar Metros (m)");
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Usar Centímetros (cm)");
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "Usar Pulgadas (in)");

        TaskDialogResult tdResult = td.Show();
        double feetPerUnit = 1.0 / 304.8; // default mm

        if (tdResult == TaskDialogResult.CommandLink1)
        {
            feetPerUnit = 1.0 / 304.8;
        }
        else if (tdResult == TaskDialogResult.CommandLink2)
        {
            feetPerUnit = 3.280839895;
        }
        else if (tdResult == TaskDialogResult.CommandLink3)
        {
            feetPerUnit = 1.0 / 30.48;
        }
        else if (tdResult == TaskDialogResult.CommandLink4)
        {
            feetPerUnit = 1.0 / 12.0;
        }
        else
        {
            // Si cierra el diálogo sin elegir, usamos la unidad detectada
            feetPerUnit = dxfData.FeetPerUnit;
        }

        // 5. Iniciar la transacción para insertar los textos recreados
        int createdCount = 0;
        using (var t = new Transaction(doc, "Recuperar textos desde DXF"))
        {
            t.Start();
            try
            {
                // Obtener transform del ImportInstance seleccionado para alinear coordenadas
                Transform transform = importInst.GetTransform();
                createdCount = ImportDetailHelpers.RecreateTextsFromDxf(doc, view, dxfPath, transform, feetPerUnit);
                t.Commit();
            }
            catch (Exception ex)
            {
                if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                TaskDialog.Show("Recuperar Textos", $"Error al crear los textos en Revit:\n{ex.Message}");
                return Result.Failed;
            }
        }

        TaskDialog.Show("Recuperar Textos", 
            $"✓ Textos recuperados con éxito.\n" +
            $"✓ Se crearon {createdCount} textos nativos de Revit en la vista actual.");

        return Result.Succeeded;
    }
}
