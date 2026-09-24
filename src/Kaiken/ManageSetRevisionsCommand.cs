using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Permite elegir un Sheet Set (Publish Set) y destildar de una sola vez las revisiones
/// marcadas manualmente en todas sus láminas (ViewSheet.GetAdditionalRevisionIds /
/// SetAdditionalRevisionIds), en vez de abrir "Revisions on Sheet" lámina por lámina.
///
/// AddRevisionCommand sincroniza TODAS las revisiones nativas del documento a TODAS las
/// láminas cada vez que se agrega una revisión nueva; con el tiempo eso acumula revisiones
/// viejas en la tabla de emisiones del título de cada lámina. Esta herramienta es el
/// proceso inverso, acotado a un set puntual.
///
/// No toca revisiones que estén "forzadas" por una nube de revisión (Revision Cloud) en
/// la lámina: esas no forman parte de GetAdditionalRevisionIds y la API no permite
/// sacarlas por este medio.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ManageSetRevisionsCommand : IExternalCommand
{
    private const string DialogTitle = "Editar Revisiones del Set";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Document doc = commandData.Application.ActiveUIDocument.Document;

        var sheetSets = ExportHelpers.GetSheetSets(doc);
        if (sheetSets.Count == 0)
        {
            TaskDialog.Show(DialogTitle, "No hay ningún Sheet Set guardado en este documento. Crea uno primero (View > Sheet Sets) e intenta de nuevo.");
            return Result.Succeeded;
        }

        var setWin = new SelectPublishSetWindow(sheetSets.Select(s => s.Name));
        if (setWin.ShowDialog() != true)
        {
            return Result.Cancelled;
        }

        var chosenSet = sheetSets.FirstOrDefault(s => s.Name == setWin.SelectedSetName);
        if (chosenSet == null)
        {
            TaskDialog.Show(DialogTitle, $"No se encontró el Sheet Set '{setWin.SelectedSetName}'.");
            return Result.Succeeded;
        }

        var sheets = chosenSet.Views
            .Cast<View>()
            .OfType<ViewSheet>()
            .Where(s => !s.IsTemplate)
            .ToList();

        if (sheets.Count == 0)
        {
            TaskDialog.Show(DialogTitle, $"El set '{chosenSet.Name}' no tiene láminas.");
            return Result.Succeeded;
        }

        // Unión de revisiones removibles (adicionales) y de todas las que se ven en pantalla
        // (adicionales + forzadas por nube) en las láminas del set.
        var additionalUnion = new HashSet<ElementId>();
        var allShownUnion = new HashSet<ElementId>();
        foreach (var sheet in sheets)
        {
            foreach (var id in sheet.GetAdditionalRevisionIds()) additionalUnion.Add(id);
            foreach (var id in sheet.GetAllRevisionIds()) allShownUnion.Add(id);
        }

        if (additionalUnion.Count == 0)
        {
            TaskDialog.Show(DialogTitle, $"Las láminas del set '{chosenSet.Name}' no tienen revisiones marcadas manualmente para editar.");
            return Result.Succeeded;
        }

        bool hasLockedByCloud = allShownUnion.Except(additionalUnion).Any();

        // Orden de emisión del documento, para listar las revisiones en el mismo orden
        // que en Administrar > Revisiones.
        var docOrder = Revision.GetAllRevisionIds(doc);
        var items = docOrder
            .Where(additionalUnion.Contains)
            .Select(id => doc.GetElement(id) as Revision)
            .Where(r => r != null)
            .Select(r => new RevisionCheckItem
            {
                Id = r!.Id,
                Label = BuildLabel(r),
                IsChecked = true,
            })
            .ToList();

        var revWin = new ManageSetRevisionsWindow(chosenSet.Name, items, hasLockedByCloud);
        if (revWin.ShowDialog() != true)
        {
            return Result.Cancelled;
        }

        var keepIds = new HashSet<ElementId>(revWin.IdsToKeep);
        var errors = new List<string>();
        int updatedSheets = 0;

        using (Transaction tx = new Transaction(doc, "Editar Revisiones del Set"))
        {
            tx.Start();

            foreach (var sheet in sheets)
            {
                var current = sheet.GetAdditionalRevisionIds();
                var updated = current.Where(keepIds.Contains).ToList();

                if (updated.Count != current.Count)
                {
                    try
                    {
                        sheet.SetAdditionalRevisionIds(updated);
                        updatedSheets++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Err en {sheet.SheetNumber}: {ex.Message}");
                    }
                }
            }

            tx.Commit();
        }

        string msg = $"Proceso finalizado.\n\n" +
                     $"• Set: {chosenSet.Name}\n" +
                     $"• Láminas del set: {sheets.Count}\n" +
                     $"• Láminas actualizadas: {updatedSheets}";

        if (hasLockedByCloud)
        {
            msg += "\n\nAviso: alguna(s) revisión(es) se siguen mostrando en una o más láminas del set porque tienen " +
                   "una nube de revisión (Revision Cloud) asociada ahí; para sacarlas hay que borrar o editar esa nube.";
        }

        if (errors.Count > 0)
        {
            msg += $"\n\nObservaciones ({errors.Count}):\n" + string.Join("\n", errors.Take(5));
        }

        TaskDialog.Show($"{DialogTitle} - Resultado", msg);
        return Result.Succeeded;
    }

    private static string BuildLabel(Revision r)
    {
        string label = $"{r.RevisionNumber}  —  {r.RevisionDate}";
        if (!string.IsNullOrWhiteSpace(r.Description))
        {
            label += $"  ({r.Description})";
        }
        return label;
    }
}
