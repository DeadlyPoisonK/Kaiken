using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken.Dev;

/// <summary>
/// Prueba del bloqueo en tres pasos (el filtro de selección corre en el panel, entre llamadas):
/// Setup crea y bloquea una capa y selecciona sus elementos más uno libre; Check mira qué quedó
/// seleccionado; Cleanup borra la capa y el bloqueo. Requiere el LayersPanel dev a la vista.
/// </summary>
public static class LockTest
{
    private const string Name = "Prueba bloqueo";

    public static object Setup(UIDocument uidoc)
    {
        Document doc = uidoc.Document;
        View view = uidoc.ActiveView;
        var model = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElements()
            .Where(e => !e.ViewSpecific && e.Category is { CategoryType: CategoryType.Model } && e.Category.AllowsBoundParameters)
            .Take(4).ToList();
        if (model.Count < 4) return new { error = "La vista activa necesita al menos 4 elementos de modelo." };

        var locked = model.Take(3).Select(e => e.Id).ToList();
        using (var t = new Transaction(doc, "Kaiken: Capas - prueba bloqueo"))
        {
            t.Start();
            var f = LayerStore.Create(doc, Name);
            LayerStore.Assign(doc, f.Id, locked, view);
            LayerLocks.Set(LayerLocks.DocKey(doc), f.UniqueId, true); // antes del commit: el refresco del panel ya lo ve
            t.Commit();
        }
        var all = locked.Append(model[3].Id).ToList();
        uidoc.Selection.SetElementIds(all);
        return new { locked = locked.Select(i => i.Value), free = model[3].Id.Value, selectedNow = uidoc.Selection.GetElementIds().Count };
    }

    public static object Check(UIDocument uidoc) =>
        new { selected = uidoc.Selection.GetElementIds().Select(i => i.Value).ToList() };

    public static object Cleanup(UIDocument uidoc)
    {
        Document doc = uidoc.Document;
        var layer = LayerStore.Load(doc, null, false).FirstOrDefault(l => l.Name == Name);
        if (layer == null) return new { removed = false };
        LayerLocks.Set(LayerLocks.DocKey(doc), layer.UniqueId, false);
        using var t = new Transaction(doc, "Kaiken: Capas - limpiar prueba");
        t.Start();
        LayerStore.Delete(doc, layer.FilterId);
        t.Commit();
        return new { removed = true };
    }
}
