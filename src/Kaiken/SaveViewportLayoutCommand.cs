using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Guarda la alineación de las láminas marcadas en el Project Browser (ya alineadas a mano) en un JSON:
/// por sector, el scope box de la vista, su escala y rotación, y dónde cae en el papel el centro del
/// recorte. Si el archivo ya existe se combina: se reemplazan los sectores guardados ahora y el resto
/// se conserva, así se puede ir armando con láminas de varios modelos. No modifica el modelo.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class SaveViewportLayoutCommand : IExternalCommand
{
    private const string Title = "Guardar Alineación";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        var sheets = uidoc.Selection.GetElementIds()
            .Select(id => doc.GetElement(id)).OfType<ViewSheet>()
            .OrderBy(s => s.SheetNumber, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (sheets.Count == 0)
        {
            TaskDialog.Show(Title, "Selecciona en el Project Browser las láminas ya alineadas (una por sector) y vuelve a ejecutar el comando.");
            return Result.Cancelled;
        }

        var entries = new List<ViewportLayoutEntry>();
        var notes = new List<string>();
        foreach (ViewSheet sheet in sheets)
        {
            string? sector = ViewportLayoutHelpers.Sector(sheet);
            if (sector == null) { notes.Add($"{sheet.Name}: el nombre no termina en un sector (ej. A1)."); continue; }
            Viewport? vp = ViewportLayoutHelpers.MainViewport(doc, sheet);
            if (vp == null) { notes.Add($"{sheet.Name}: no tiene una vista en planta."); continue; }
            var view = (View)doc.GetElement(vp.ViewId);
            string scope = ViewportLayoutHelpers.ScopeBoxName(doc, view);
            if (scope.Length == 0) { notes.Add($"{sheet.Name}: la vista {view.Name} no tiene scope box."); continue; }
            if (entries.Any(e => SameKey(e, sector, scope))) { notes.Add($"{sheet.Name}: {sector} con {scope} ya viene de otra lámina."); continue; }
            var pos = ViewportLayoutHelpers.CropCenterOnSheetMm(doc, sheet, vp, view);
            if (pos == null) { notes.Add($"{sheet.Name}: no se pudo proyectar la vista en la lámina."); continue; }

            entries.Add(new ViewportLayoutEntry
            {
                Sector = sector,
                ScopeBox = scope,
                Scale = view.Scale,
                Rotation = vp.Rotation.ToString(),
                SheetXmm = Math.Round(pos.Value.X, 3),
                SheetYmm = Math.Round(pos.Value.Y, 3),
                SourceSheet = sheet.Name,
                SourceView = view.Name,
            });
        }
        if (entries.Count == 0)
        {
            TaskDialog.Show(Title, "Ninguna lámina seleccionada se pudo guardar:\n\n" + string.Join("\n", notes));
            return Result.Cancelled;
        }

        string? last = ViewportLayoutFile.LastFile();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Guardar alineación (si el archivo existe, se combina con lo que ya tiene)",
            Filter = "Alineación de viewports (*.json)|*.json",
            InitialDirectory = last != null ? Path.GetDirectoryName(last) : ViewportLayoutFile.DefaultFolder,
            FileName = last != null ? Path.GetFileName(last) : "Alineacion.json",
            OverwritePrompt = false,
        };
        Directory.CreateDirectory(ViewportLayoutFile.DefaultFolder);
        if (dlg.ShowDialog() != true) return Result.Cancelled;

        ViewportLayoutFile file;
        try { file = File.Exists(dlg.FileName) ? ViewportLayoutFile.Load(dlg.FileName) : new ViewportLayoutFile(); }
        catch (Exception ex)
        {
            TaskDialog.Show(Title, $"No se pudo leer el archivo existente, no se guardó nada:\n{ex.Message}");
            return Result.Failed;
        }

        int replaced = file.Entries.RemoveAll(old => entries.Any(e => SameKey(old, e.Sector, e.ScopeBox)));
        file.Entries.AddRange(entries);
        file.Entries = file.Entries.OrderBy(e => e.Sector, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.ScopeBox, StringComparer.OrdinalIgnoreCase).ToList();
        file.SourceModel = doc.Title;
        file.Created = DateTime.Now;
        file.Save(dlg.FileName);

        var report = new List<string>
        {
            $"Guardados {entries.Count} sector(es) en {dlg.FileName}" + (replaced > 0 ? $" ({replaced} reemplazado(s))." : "."),
            string.Join("\n", entries.Select(e => $"{e.Sector}  ·  {e.ScopeBox}  ·  1:{e.Scale}")),
            $"El archivo tiene {file.Entries.Count} sector(es) en total.",
        };
        if (notes.Count > 0) report.Add("No guardadas:\n" + string.Join("\n", notes));
        TaskDialog.Show(Title, string.Join("\n\n", report));
        return Result.Succeeded;
    }

    private static bool SameKey(ViewportLayoutEntry e, string sector, string scope) =>
        string.Equals(e.Sector, sector, StringComparison.OrdinalIgnoreCase) && string.Equals(e.ScopeBox, scope, StringComparison.OrdinalIgnoreCase);
}
