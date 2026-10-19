using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Crea de una sola vez varias vistas dependientes de una vista principal, con los nombres de una
/// lista, sin abrirlas (Revit crea y abre una por una). Los nombres se pueden escribir, generar
/// ("&lt;vista&gt; - Dependent N") o copiar de otras vistas del mismo tipo; si una vista ya usa el nombre,
/// según se elija se renombra con " (antigua)", se elimina o se omite esa dependiente.
/// Las dependientes nacen como las crea Revit (recorte de la principal): de la vista que tenía el
/// nombre no se copia nada más. Todo va en una transacción: un Ctrl+Z lo deshace.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class DependentViewsCommand : IExternalCommand
{
    private const string Title = "Crear Dependientes";
    private const string OldSuffix = " (antigua)";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate)
            .ToList();
        var infos = views.Select(v => new DependentViewInfo
        {
            Id = v.Id,
            Name = v.Name,
            ViewType = v.ViewType,
            PrimaryId = v.GetPrimaryViewId(),
        }).ToList();
        var infoById = infos.ToDictionary(i => i.Id);

        var parents = views
            .Where(v => v.CanViewBeDuplicated(ViewDuplicateOption.AsDependent))
            .Select(v => new DependentParentOption { View = infoById[v.Id], Label = v.Title })
            .OrderBy(o => o.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (parents.Count == 0)
        {
            TaskDialog.Show(Title, "No hay vistas en este modelo que admitan dependientes.");
            return Result.Cancelled;
        }

        // Vista principal sugerida: la marcada en el Project Browser, si no la activa
        // (si es una dependiente, su principal).
        var candidates = uidoc.Selection.GetElementIds().Append(uidoc.ActiveView.Id);
        DependentParentOption initial = candidates
            .Select(id => infoById.TryGetValue(id, out var i) && i.PrimaryId != ElementId.InvalidElementId ? i.PrimaryId : id)
            .Select(id => parents.FirstOrDefault(p => p.View.Id == id))
            .FirstOrDefault(p => p != null) ?? parents[0];

        var win = new DependentViewsWindow(parents, initial, infos);
        new WindowInteropHelper(win).Owner = commandData.Application.MainWindowHandle;
        if (win.ShowDialog() != true) return Result.Cancelled;

        if (doc.GetElement(win.ParentId) is not View parent) return Result.Failed;

        // Vistas del mismo tipo que hoy usan cada nombre de la lista.
        var sameType = views.Where(v => v.ViewType == parent.ViewType).ToList();
        var byName = sameType.GroupBy(v => v.Name).ToDictionary(g => g.Key, g => g.First());
        var conflicts = win.Names.Where(byName.ContainsKey).Select(n => byName[n]).ToList();

        var conflictMode = win.Conflict;
        if (conflictMode == DependentNameConflict.DeleteExisting && conflicts.Count > 0 && !ConfirmDelete(doc, conflicts, uidoc.ActiveView))
            return Result.Cancelled;

        var taken = new HashSet<string>(sameType.Select(v => v.Name));
        var renamed = new List<string>();
        var deleted = new List<string>();
        var skipped = new List<string>();
        var errors = new List<string>();
        int created = 0;

        using (var tx = new Transaction(doc, Title))
        {
            tx.Start();
            foreach (string name in win.Names)
            {
                byName.TryGetValue(name, out View? existing);
                if (existing != null && !existing.IsValidObject) existing = null; // se fue al eliminar otra vista
                if (existing != null && conflictMode == DependentNameConflict.Skip)
                {
                    skipped.Add(name);
                    continue;
                }

                using var st = new SubTransaction(doc);
                st.Start();
                string? renamedTo = null;
                bool wasDeleted = false;
                try
                {
                    if (existing != null)
                    {
                        // La vista activa no se puede eliminar: esa se renombra.
                        if (conflictMode == DependentNameConflict.DeleteExisting && existing.Id != uidoc.ActiveView.Id)
                        {
                            doc.Delete(existing.Id);
                            wasDeleted = true;
                        }
                        else
                        {
                            renamedTo = FreeName(name + OldSuffix, taken);
                            existing.Name = renamedTo;
                        }
                    }

                    var dep = (View)doc.GetElement(parent.Duplicate(ViewDuplicateOption.AsDependent));
                    dep.Name = name;
                    st.Commit();
                }
                catch (Exception ex)
                {
                    st.RollBack();
                    errors.Add($"{name}: {ex.Message}");
                    continue;
                }

                created++;
                taken.Add(name);
                if (renamedTo != null)
                {
                    taken.Add(renamedTo);
                    renamed.Add($"{name} → {renamedTo}");
                }
                if (wasDeleted) deleted.Add(name);
            }

            if (created == 0 && renamed.Count == 0 && deleted.Count == 0) tx.RollBack();
            else tx.Commit();
        }

        var report = new List<string> { $"Creadas {created} de {win.Names.Count} dependientes de \"{parent.Name}\"." };
        if (renamed.Count > 0) report.Add("Vistas existentes renombradas:\n" + string.Join("\n", renamed));
        if (deleted.Count > 0) report.Add($"Vistas existentes eliminadas: {deleted.Count}");
        if (skipped.Count > 0) report.Add("No creadas porque el nombre ya existe:\n" + string.Join("\n", skipped));
        if (errors.Count > 0) report.Add("Errores:\n" + string.Join("\n", errors.Take(15)) + (errors.Count > 15 ? $"\n… y {errors.Count - 15} más" : ""));
        TaskDialog.Show(Title, string.Join("\n\n", report));
        return created > 0 ? Result.Succeeded : Result.Cancelled;
    }

    /// <summary>Avisa qué se pierde al eliminar: vistas en láminas y sus propias dependientes.</summary>
    private static bool ConfirmDelete(Document doc, List<View> toDelete, View active)
    {
        var onSheets = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()
            .Select(vp => vp.ViewId)
            .ToHashSet();
        var lines = new List<string>();
        foreach (View v in toDelete)
        {
            var notes = new List<string>();
            if (v.Id == active.Id) notes.Add("es la vista activa: se renombrará en vez de eliminarse");
            if (onSheets.Contains(v.Id)) notes.Add("está en una lámina");
            int deps = v.GetDependentViewIds().Count;
            if (deps > 0) notes.Add($"se eliminan también sus {deps} dependiente(s)");
            lines.Add(notes.Count == 0 ? v.Name : $"{v.Name}  ({string.Join("; ", notes)})");
        }

        var td = new TaskDialog(Title)
        {
            MainInstruction = $"Se eliminarán {toDelete.Count} vista(s) existentes para usar su nombre",
            MainContent = string.Join("\n", lines.Take(25)) + (lines.Count > 25 ? $"\n… y {lines.Count - 25} más" : "") +
                          "\n\nSe puede deshacer con Ctrl+Z.",
            CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel,
        };
        return td.Show() == TaskDialogResult.Ok;
    }

    private static string FreeName(string baseName, HashSet<string> taken)
    {
        if (!taken.Contains(baseName)) return baseName;
        for (int i = 2; ; i++)
        {
            string candidate = $"{baseName} {i}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}
