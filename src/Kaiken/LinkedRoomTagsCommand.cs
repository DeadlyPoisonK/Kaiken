using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Botón de cinta: etiqueta las habitaciones de un vínculo en la planta activa (las del nivel de
/// la vista que aún no tienen etiqueta), con el tipo de etiqueta elegido. Reemplaza a "Tag All Not
/// Tagged" donde este no encuentra las habitaciones del vínculo (ver LinkedRoomTagsHelpers).
/// </summary>
[Transaction(TransactionMode.Manual)]
public class LinkedRoomTagsCommand : IExternalCommand
{
    private const string Title = "Etiquetar Rooms de Vínculo";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        if (uidoc.ActiveView is not ViewPlan view || view.GenLevel == null || view.IsTemplate)
        {
            TaskDialog.Show(Title, "Abre una planta (de piso, cielo o estructural) con nivel: las etiquetas se ponen en la vista activa.");
            return Result.Cancelled;
        }

        var types = LinkedRoomTagsHelpers.RoomTagTypes(doc);
        if (types.Count == 0)
        {
            TaskDialog.Show(Title, "El proyecto no tiene ninguna familia de etiqueta de habitación (Room Tag) cargada.");
            return Result.Cancelled;
        }

        var win = new LinkedRoomTagsWindow(doc, view, types);
        if (win.ShowDialog() != true) return Result.Cancelled;

        LinkedRoomScan scan = win.SelectedScan!;
        FamilySymbol type = win.SelectedType!;

        using var t = new Transaction(doc, "Kaiken: Etiquetar Rooms de Vínculo");
        t.Start();
        try
        {
            var (created, errors) = LinkedRoomTagsHelpers.CreateTags(doc, view, scan.Untagged, type.Id);
            if (created == 0)
            {
                t.RollBack();
                TaskDialog.Show(Title, "No se pudo crear ninguna etiqueta; no se modificó el modelo.\n\n" + string.Join("\n", errors.Take(5)));
                return Result.Failed;
            }
            t.Commit();

            string resumen = (created == 1 ? "1 habitación etiquetada" : $"{created} habitaciones etiquetadas") +
                             $" de {LinkedRoomTagsHelpers.LinkFileName(scan.Link)}, con \"{LinkedRoomTagsHelpers.TypeLabel(type)}\".";
            if (errors.Count > 0)
                resumen += $"\n\n{errors.Count} no se pudieron etiquetar:\n" + string.Join("\n", errors.Take(5)) +
                           (errors.Count > 5 ? $"\n(y {errors.Count - 5} más)" : "");
            TaskDialog.Show(Title, resumen);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            TaskDialog.Show(Title, $"Falló, no se modificó el modelo (transacción revertida):\n{ex.Message}");
            return Result.Failed;
        }
    }
}
