using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>Una vista abierta: su UniqueId (estable entre sesiones), el encuadre y dónde estaba su ventana.</summary>
public sealed class SavedView
{
    public string UniqueId { get; set; } = "";
    public string Name { get; set; } = "";
    public double[]? Zoom { get; set; } // x1,y1,z1,x2,y2,z2 de GetZoomCorners
    public int[]? Window { get; set; } // posición de su ventana flotante (ViewWindows.GetPlacement); null si no flotaba
    public bool Docked { get; set; } // se sabe que estaba acoplada en la ventana principal
}

public sealed class SavedViewState
{
    public string Model { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public string ActiveViewUniqueId { get; set; } = "";
    public int[]? MainWindow { get; set; } // posición de la ventana principal de Revit
    public List<SavedView> Views { get; set; } = new();
}

/// <summary>
/// Guarda/restaura las vistas abiertas de un modelo, su zoom y la posición de sus ventanas
/// (también las flotantes en otros monitores, ver <see cref="ViewWindows"/>). Un estado por
/// modelo, en %APPDATA%\Kaiken\viewstates.
/// </summary>
public static class ViewStateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string ModelKey(Document doc) =>
        string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;

    public static string FileFor(Document doc)
    {
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(ModelKey(doc).ToLowerInvariant())))[..12];
        string safe = string.Concat(doc.Title.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));
        string dir = Path.Combine(KaikenSettings.FolderPath, "viewstates");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{safe}-{hash}.json");
    }

    public static void Save(Document doc, SavedViewState s) =>
        File.WriteAllText(FileFor(doc), JsonSerializer.Serialize(s, Json));

    public static SavedViewState? Load(Document doc)
    {
        string f = FileFor(doc);
        return File.Exists(f) ? JsonSerializer.Deserialize<SavedViewState>(File.ReadAllText(f)) : null;
    }

    /// <summary>Pestaña de cada vista abierta, o null si no se puede leer la disposición de ventanas.</summary>
    internal static (ViewWindows Windows, Dictionary<ElementId, object> Tabs)? FindTabs(UIDocument uidoc, IntPtr mainHwnd)
    {
        try
        {
            ViewWindows? windows = ViewWindows.Find(mainHwnd);
            return windows == null ? null : (windows, windows.MapOpenViews(uidoc));
        }
        catch { return null; }
    }
}

[Transaction(TransactionMode.Manual)]
public class SaveViewsCommand : IExternalCommand
{
    private const string Title = "Guardar Vistas";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;
        IntPtr mainHwnd = commandData.Application.MainWindowHandle;

        var state = new SavedViewState
        {
            Model = doc.Title,
            SavedAt = DateTime.Now,
            ActiveViewUniqueId = uidoc.ActiveView.UniqueId,
            MainWindow = ViewWindows.GetPlacement(mainHwnd),
        };

        // Si no se puede leer dónde está cada ventana, se guardan igual las vistas y su zoom.
        var found = ViewStateStore.FindTabs(uidoc, mainHwnd);

        foreach (UIView uv in uidoc.GetOpenUIViews())
        {
            if (doc.GetElement(uv.ViewId) is not View v) continue;
            var sv = new SavedView { UniqueId = v.UniqueId, Name = v.Name };
            try
            {
                IList<XYZ> c = uv.GetZoomCorners();
                sv.Zoom = new[] { c[0].X, c[0].Y, c[0].Z, c[1].X, c[1].Y, c[1].Z };
            }
            catch { /* vista sin encuadre guardable: se reabre igual */ }
            if (found is { } f && f.Tabs.TryGetValue(v.Id, out object? tab))
            {
                try
                {
                    sv.Window = f.Windows.FloatingPlacement(tab);
                    sv.Docked = sv.Window == null;
                }
                catch { /* sin posición: al restaurar queda donde Revit la abra */ }
            }
            state.Views.Add(sv);
        }

        if (state.Views.Count == 0)
        {
            TaskDialog.Show(Title, "No hay vistas abiertas que guardar.");
            return Result.Cancelled;
        }

        try { ViewStateStore.Save(doc, state); }
        catch (Exception ex)
        {
            TaskDialog.Show(Title, $"No se pudo guardar:\n{ex.Message}");
            return Result.Failed;
        }

        int floating = state.Views.Count(x => x.Window != null);
        int unknown = state.Views.Count(x => x.Window == null && !x.Docked);
        string msg = $"Guardadas {state.Views.Count} vistas abiertas de \"{doc.Title}\"" +
                     (floating > 0 ? $", {floating} en ventanas flotantes." : ".");
        if (found == null)
            msg += "\n\nNo se pudo leer la disposición de las ventanas en esta versión de Revit: se guardaron solo las vistas y su zoom.";
        else if (unknown > 0)
            msg += $"\n\nNo se pudo ubicar la ventana de {unknown} vista(s): al restaurar se abrirán donde Revit las ponga.";
        TaskDialog.Show(Title, msg);
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public class RestoreViewsCommand : IExternalCommand
{
    private const string Title = "Restaurar Vistas";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;
        IntPtr mainHwnd = commandData.Application.MainWindowHandle;

        SavedViewState? state;
        try { state = ViewStateStore.Load(doc); }
        catch (Exception ex)
        {
            TaskDialog.Show(Title, $"No se pudo leer el estado guardado:\n{ex.Message}");
            return Result.Failed;
        }
        if (state == null)
        {
            TaskDialog.Show(Title, $"No hay un estado guardado para \"{doc.Title}\". Usa primero Guardar Vistas.");
            return Result.Cancelled;
        }

        // Primero la ventana principal, para que las vistas acopladas se encuadren con su tamaño final.
        if (state.MainWindow != null && ViewWindows.SetPlacement(mainHwnd, state.MainWindow))
            (HwndSource.FromHwnd(mainHwnd)?.RootVisual as UIElement)?.UpdateLayout();

        // 1. Abrir las vistas. Las flotantes se encuadran después, cuando su ventana tenga el tamaño final.
        var missing = new List<string>();
        var opened = new List<(SavedView Saved, View View)>();
        foreach (SavedView sv in state.Views)
        {
            if (doc.GetElement(sv.UniqueId) is not View v || v.IsTemplate)
            {
                missing.Add(sv.Name);
                continue;
            }
            try
            {
                uidoc.ActiveView = v;
                opened.Add((sv, v));
                if (sv.Window == null) Zoom(uidoc, v, sv);
            }
            catch (Exception ex)
            {
                missing.Add($"{sv.Name} ({ex.Message})");
            }
        }

        // 2. Ventanas: sacar a flotar y ubicar las que flotaban, y devolver a la principal las que no.
        var notPlaced = new List<string>();
        if (opened.Any(o => o.Saved.Window != null || o.Saved.Docked))
        {
            var found = ViewStateStore.FindTabs(uidoc, mainHwnd);
            foreach (var (sv, v) in opened)
            {
                if (sv.Window == null && !sv.Docked) continue;
                if (found is not { } f || !f.Tabs.TryGetValue(v.Id, out object? tab))
                {
                    if (sv.Window != null)
                    {
                        notPlaced.Add(sv.Name);
                        Zoom(uidoc, v, sv);
                    }
                    continue;
                }
                try
                {
                    if (sv.Window != null)
                    {
                        if (!f.Windows.Float(tab, sv.Window)) notPlaced.Add($"{sv.Name} (su monitor no está conectado)");
                        Zoom(uidoc, v, sv);
                    }
                    else f.Windows.Dock(tab);
                }
                catch (Exception ex)
                {
                    notPlaced.Add($"{sv.Name} ({(ex.InnerException ?? ex).Message})");
                }
            }
        }

        // Al final queda activa la vista que lo estaba al guardar.
        if (doc.GetElement(state.ActiveViewUniqueId) is View active && !active.IsTemplate)
        {
            try { uidoc.ActiveView = active; } catch { }
        }

        var report = new List<string>();
        if (missing.Count > 0)
            report.Add($"Restauradas {opened.Count} de {state.Views.Count} vistas (guardado el {state.SavedAt:g}).\n\nNo se pudieron abrir:\n" +
                       string.Join("\n", missing));
        if (notPlaced.Count > 0)
            report.Add("No se pudo ubicar la ventana de:\n" + string.Join("\n", notPlaced));
        if (report.Count > 0)
            TaskDialog.Show(Title, string.Join("\n\n", report));
        return Result.Succeeded;
    }

    private static void Zoom(UIDocument uidoc, View v, SavedView sv)
    {
        if (sv.Zoom is not { Length: 6 }) return;
        try
        {
            UIView? uv = uidoc.GetOpenUIViews().FirstOrDefault(x => x.ViewId == v.Id);
            uv?.ZoomAndCenterRectangle(new XYZ(sv.Zoom[0], sv.Zoom[1], sv.Zoom[2]),
                                       new XYZ(sv.Zoom[3], sv.Zoom[4], sv.Zoom[5]));
        }
        catch { /* el encuadre no es crítico */ }
    }
}
