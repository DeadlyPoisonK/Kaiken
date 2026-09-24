using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Lógica compartida entre los comandos de exportación (DWG, PDF, DWF, y los que se
/// agreguen después). Mantiene cada comando enfocado solo en las opciones de export
/// específicas de su formato.
/// </summary>
internal static class ExportHelpers
{
    /// <summary>
    /// Valor usado en los combos "Aplicar a" de los comandos de Revisión para indicar
    /// que no se restringe a ningún Sheet Set (Publish Set) en particular.
    /// </summary>
    public const string AllSheetsOption = "Todas las láminas";

    public static List<ViewSheetSet> GetSheetSets(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheetSet))
            .Cast<ViewSheetSet>()
            .ToList();

    /// <summary>
    /// Restringe una lista de láminas a las que pertenecen al Sheet Set (Publish Set) elegido,
    /// para que comandos como Agregar/Modificar Revisión solo toquen ese set y no pisen la
    /// revisión o fecha de los demás. Si selectedSetName es AllSheetsOption, devuelve la lista
    /// sin cambios. Si el set elegido ya no existe en el documento, devuelve null.
    /// </summary>
    public static List<ViewSheet>? FilterBySelectedSet(List<ViewSheet> sheets, List<ViewSheetSet> sheetSets, string selectedSetName)
    {
        if (selectedSetName == AllSheetsOption) return sheets;

        var chosen = sheetSets.FirstOrDefault(s => s.Name == selectedSetName);
        if (chosen == null) return null;

        var setSheetIds = chosen.Views
            .Cast<View>()
            .OfType<ViewSheet>()
            .Select(s => s.Id)
            .ToHashSet();

        return sheets.Where(s => setSheetIds.Contains(s.Id)).ToList();
    }

    public static string LimpiarNombre(string texto)
    {
        char[] invalidos = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
        foreach (var c in invalidos)
            texto = texto.Replace(c, '_');
        return texto;
    }

    /// <summary>
    /// Muestra la ventana de selección de carpeta + Sheet Set. Devuelve null si el usuario
    /// cancela, o si no hay ningún Sheet Set en el documento (ya se muestra el aviso).
    /// </summary>
    public static (string Folder, ViewSheetSet Set)? PromptForFolderAndSheetSet(Document doc, string dialogTitle)
    {
        var sheetSets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheetSet))
            .Cast<ViewSheetSet>()
            .ToList();

        if (sheetSets.Count == 0)
        {
            TaskDialog.Show(dialogTitle, "No hay ningún Sheet Set guardado en este documento. Crea uno primero (View > Sheet Sets) e intenta de nuevo.");
            return null;
        }

        var window = new ExportSheetSetWindow(sheetSets.Select(s => s.Name), dialogTitle);
        bool? ok = window.ShowDialog();
        if (ok != true) return null;

        var targetSet = sheetSets.FirstOrDefault(s => s.Name == window.SelectedSetName);
        if (targetSet == null)
        {
            TaskDialog.Show(dialogTitle, $"No se encontró el Sheet Set '{window.SelectedSetName}'.");
            return null;
        }

        return (window.SelectedFolder, targetSet);
    }

    /// <summary>
    /// Configuración fija de exportación IFC del proyecto, compartida entre
    /// el botón manual (ExportIfcCommand) y el batch (ExportIfcBatchCommand):
    ///   · IFC version      : IFC4x3 [Experimental]
    ///   · Coordinate Base  : Internal Origin
    ///   · Advanced         : Use Type name only for IFCType name = true
    ///   · Property Sets    : Revit + IFC common + base quantities
    /// </summary>
    public static IFCExportOptions BuildIfcOptions(View3D vista3D)
    {
        var opt = new IFCExportOptions();

        opt.FilterViewId = vista3D.Id;
        opt.FileVersion = IFCVersion.IFC4x3;

        opt.AddOption("SitePlacement", "InternalOrigin");
        opt.AddOption("UseTypeNameOnlyForIfcType", "true");

        opt.AddOption("ExportRevitPropertySets",    "true");
        opt.AddOption("ExportIFCCommonPropertySets", "true");
        opt.AddOption("ExportBaseQuantities",        "true");
        opt.AddOption("ExportMaterialPropertySets",  "false");
        opt.AddOption("ExportSchedulesAsPsets",      "false");

        return opt;
    }
}
