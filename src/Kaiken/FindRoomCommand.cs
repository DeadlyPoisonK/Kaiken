using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Botón de cinta: busca rooms (del modelo actual y de los vínculos cargados) por número,
/// nombre, nivel o texto de sus etiquetas, y lleva a una planta donde se ve la room —
/// o la destaca en la vista actual — haciendo zoom y dejándola seleccionada.
/// No modifica el modelo.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class FindRoomCommand : IExternalCommand
{
    private const string Title = "Buscar Room";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        var hits = FindRoomHelpers.CollectRooms(doc);
        if (hits.Count == 0)
        {
            TaskDialog.Show(Title, "No hay rooms colocadas en el modelo actual ni en los vínculos cargados.");
            return Result.Cancelled;
        }

        var win = new FindRoomWindow(doc, hits);
        if (win.ShowDialog() != true || win.SelectedHit == null) return Result.Cancelled;

        RoomHit hit = win.SelectedHit;
        View target;

        if (win.Action == FindRoomWindow.FindRoomAction.GoToView && win.SelectedView != null)
        {
            target = win.SelectedView;
            if (uidoc.ActiveView.Id != target.Id)
            {
                try { uidoc.ActiveView = target; }
                catch (Exception ex)
                {
                    TaskDialog.Show(Title, $"No se pudo abrir la vista \"{target.Name}\":\n{ex.Message}");
                    return Result.Failed;
                }
            }
        }
        else
        {
            target = uidoc.ActiveView;
            if (target is ViewSheet || target is ViewSchedule || target.ViewType == ViewType.ProjectBrowser)
            {
                TaskDialog.Show(Title, "La vista actual no es una vista de modelo. Usa \"Ir a vista\" o abre una planta primero.");
                return Result.Cancelled;
            }
        }

        try
        {
            string? error = FindRoomHelpers.ZoomAndSelect(uidoc, target, hit);
            if (error != null)
            {
                TaskDialog.Show(Title, error);
                return Result.Failed;
            }
        }
        catch (Exception ex)
        {
            TaskDialog.Show(Title, $"No se pudo hacer zoom a la room:\n{ex.Message}");
            return Result.Failed;
        }

        // Avisar si se destacó en una planta de otro nivel (la room probablemente no se ve).
        if (win.Action == FindRoomWindow.FindRoomAction.HighlightHere
            && target is ViewPlan { GenLevel: Level lvl }
            && Math.Abs(lvl.ProjectElevation - hit.LevelElevation) > 0.3)
        {
            TaskDialog.Show(Title,
                $"La room {hit.Number} {hit.Name} está en el nivel \"{hit.LevelName}\", " +
                $"pero la vista actual es del nivel \"{lvl.Name}\": puede que no se vea. Usa \"Ir a vista\" para abrir una planta de su nivel.");
        }

        return Result.Succeeded;
    }
}
