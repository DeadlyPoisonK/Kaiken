using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Aplica una alineación guardada con Guardar Alineación a las láminas de este modelo. Cada lámina
/// cuyo sector (última palabra del nombre, ej. "A1") esté en el archivo toma el scope box guardado
/// (por nombre) y su viewport se mueve para que el centro del recorte caiga en el mismo punto del
/// papel, relativo al cajetín. Se compara por el centro del recorte proyectado en la lámina, no por
/// la caja del viewport, que cambia con las anotaciones. Además el viewport queda "Sin título", con
/// el recorte activo pero oculto, el annotation crop marcado y, si se elige, los scope boxes y section
/// boxes ocultos. Todo va en una transacción: un Ctrl+Z lo deshace.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class AlignViewportsCommand : IExternalCommand
{
    private const string Title = "Alinear Viewports";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        string? last = ViewportLayoutFile.LastFile();
        Directory.CreateDirectory(ViewportLayoutFile.DefaultFolder);
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Alineación a aplicar (creada con Guardar Alineación)",
            Filter = "Alineación de viewports (*.json)|*.json",
            InitialDirectory = last != null ? Path.GetDirectoryName(last) : ViewportLayoutFile.DefaultFolder,
            FileName = last != null ? Path.GetFileName(last) : "",
        };
        if (dlg.ShowDialog() != true) return Result.Cancelled;

        ViewportLayoutFile layout;
        try { layout = ViewportLayoutFile.Load(dlg.FileName); }
        catch (Exception ex)
        {
            TaskDialog.Show(Title, $"No se pudo leer {dlg.FileName}:\n{ex.Message}");
            return Result.Failed;
        }
        if (layout.Entries.Count == 0)
        {
            TaskDialog.Show(Title, "El archivo no tiene sectores guardados.");
            return Result.Cancelled;
        }

        var bySector = layout.Entries.GroupBy(e => e.Sector, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var scopeBoxes = ViewportLayoutHelpers.ScopeBoxesByName(doc);

        var rows = new List<AlignViewportRow>();
        var notes = new List<string>();
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Where(s => !s.IsPlaceholder)
            .OrderBy(s => s.SheetNumber, StringComparer.CurrentCultureIgnoreCase);
        foreach (ViewSheet sheet in sheets)
        {
            string? sector = ViewportLayoutHelpers.Sector(sheet);
            if (sector == null || !bySector.TryGetValue(sector, out var candidates)) continue;
            Viewport? vp = ViewportLayoutHelpers.MainViewport(doc, sheet);
            if (vp == null) { notes.Add($"{sheet.Name}: no tiene una vista en planta, no se alinea."); continue; }
            var view = (View)doc.GetElement(vp.ViewId);
            string scope = ViewportLayoutHelpers.ScopeBoxName(doc, view);

            // Si el sector tiene varias entradas (ej. A1 a 1:250 y la malla a tierra A1), la del
            // scope box que ya tiene la vista; si no, la de su escala.
            ViewportLayoutEntry entry =
                candidates.FirstOrDefault(e => string.Equals(e.ScopeBox, scope, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(e => e.Scale == view.Scale)
                ?? candidates[0];
            bool scopeExists = scopeBoxes.ContainsKey(entry.ScopeBox);
            bool sameScale = view.Scale == entry.Scale;
            string scopeLabel = scope.Length == 0 ? "(ninguno)" : scope;
            rows.Add(new AlignViewportRow
            {
                IsChecked = scopeExists && sameScale,
                CanToggle = scopeExists,
                IsReference = string.Equals(entry.SourceSheet, sheet.Name, StringComparison.CurrentCultureIgnoreCase),
                Sector = sector,
                SheetLabel = sheet.Name,
                ViewName = view.Name,
                Scale = sameScale ? $"1:{view.Scale}" : $"1:{view.Scale} (guardada 1:{entry.Scale})",
                ScopeBox = string.Equals(scope, entry.ScopeBox, StringComparison.OrdinalIgnoreCase) ? scopeLabel : $"{scopeLabel} → {entry.ScopeBox}",
                Note = !scopeExists ? "Falta el scope box en este modelo" : !sameScale ? "Otra escala" : "",
                Entry = entry,
                ViewportId = vp.Id,
            });
        }
        if (rows.Count == 0)
        {
            TaskDialog.Show(Title, "Ninguna lámina de este modelo termina en un sector del archivo (" +
                                   string.Join(", ", bySector.Keys.OrderBy(k => k)) + ").");
            return Result.Cancelled;
        }

        var win = new AlignViewportsWindow(rows, Path.GetFileName(dlg.FileName));
        new WindowInteropHelper(win).Owner = commandData.Application.MainWindowHandle;
        if (win.ShowDialog() != true) return Result.Cancelled;
        bool hideBoxes = win.HideBoxes;
        var chosen = rows.Where(r => r.IsChecked).ToList();

        int aligned = 0;
        var errors = new List<string>();
        var templateWarnings = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        using (var tx = new Transaction(doc, Title))
        {
            tx.Start();
            ElementId? noTitleType = chosen.Count > 0
                ? ViewportLayoutHelpers.FindNoTitleType(doc, (Viewport)doc.GetElement(chosen[0].ViewportId))
                : null;
            if (chosen.Count > 0 && noTitleType == null)
                notes.Add($"No hay un tipo de viewport \"{ViewportLayoutHelpers.NoTitleTypeName}\": no se cambió el tipo.");

            // 1. Scope box, rotación y configuración (el scope box primero: define el recorte).
            var ready = new List<AlignViewportRow>();
            foreach (var row in chosen)
            {
                var vp = (Viewport)doc.GetElement(row.ViewportId);
                var view = (View)doc.GetElement(vp.ViewId);
                try
                {
                    Parameter? scope = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                    ElementId wanted = scopeBoxes[row.Entry.ScopeBox];
                    if (scope == null || scope.IsReadOnly)
                    {
                        if (ViewportLayoutHelpers.ScopeBoxId(view) != wanted) { errors.Add($"{row.SheetLabel}: no se puede asignar el scope box a la vista."); continue; }
                    }
                    else if (scope.AsElementId() != wanted) scope.Set(wanted);

                    if (Enum.TryParse(row.Entry.Rotation, out ViewportRotation rotation) && vp.Rotation != rotation) vp.Rotation = rotation;
                    ViewportLayoutHelpers.ConfigureView(view, hideBoxes, templateWarnings);
                    if (noTitleType != null && vp.GetTypeId() != noTitleType) vp.ChangeTypeId(noTitleType);
                    ready.Add(row);
                }
                catch (Exception ex)
                {
                    errors.Add($"{row.SheetLabel}: {ex.Message}");
                }
            }

            doc.Regenerate();

            // 2. Mover cada viewport para que el centro del recorte caiga donde dice el archivo.
            foreach (var row in ready)
            {
                try
                {
                    var vp = (Viewport)doc.GetElement(row.ViewportId);
                    var view = (View)doc.GetElement(vp.ViewId);
                    var sheet = (ViewSheet)doc.GetElement(vp.SheetId);
                    var pos = ViewportLayoutHelpers.CropCenterOnSheetMm(doc, sheet, vp, view);
                    if (pos == null) { errors.Add($"{row.SheetLabel}: no se pudo proyectar la vista en la lámina."); continue; }
                    XYZ delta = ViewportLayoutHelpers.MmToFeet(row.Entry.SheetXmm - pos.Value.X, row.Entry.SheetYmm - pos.Value.Y);
                    if (delta.GetLength() > 1e-6) vp.SetBoxCenter(vp.GetBoxCenter() + delta);
                    aligned++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{row.SheetLabel}: {ex.Message}");
                }
            }

            if (aligned == 0) tx.RollBack();
            else tx.Commit();
        }

        var report = new List<string> { $"Alineadas {aligned} de {chosen.Count} lámina(s) marcadas con {Path.GetFileName(dlg.FileName)}." };
        if (templateWarnings.Count > 0)
            report.Add("La plantilla de vista controla la visibilidad, no se pudieron ocultar scope/section boxes en las vistas con:\n" + string.Join("\n", templateWarnings));
        if (notes.Count > 0) report.Add("Avisos:\n" + string.Join("\n", notes.Take(15)) + (notes.Count > 15 ? $"\n… y {notes.Count - 15} más" : ""));
        if (errors.Count > 0) report.Add("Errores:\n" + string.Join("\n", errors.Take(15)) + (errors.Count > 15 ? $"\n… y {errors.Count - 15} más" : ""));
        TaskDialog.Show(Title, string.Join("\n\n", report));
        return aligned > 0 ? Result.Succeeded : Result.Cancelled;
    }
}
