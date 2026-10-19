using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Puente entre la ventana no modal de Interferencias y la API de Revit: cada acción del
/// usuario se encola y se ejecuta por ExternalEvent, que es la única forma de tocar el
/// modelo desde una ventana que no bloquea Revit. Hay un solo navegador abierto a la vez.
/// </summary>
internal static class ClashNavigator
{
    private const string Title = "Interferencias";
    /// <summary>Holgura alrededor del choque para la caja de sección y el zoom (≈ 1,5 m).</summary>
    private const double PadFt = 5.0;

    private static readonly Handler EventHandler = new();
    private static ExternalEvent? _event;
    private static ClashNavigatorWindow? _window;

    /// <summary>Abre el navegador para la vista. Requiere contexto de la API (se llama desde un comando).</summary>
    public static void Open(UIApplication uiapp, View3D view, IList<string>? warnings = null, string? note = null)
    {
        _event ??= ExternalEvent.Create(EventHandler);
        ClashData? data = ClashStorage.Read(view);
        if (data == null)
        {
            TaskDialog.Show(Title, $"La vista «{view.Name}» no tiene interferencias guardadas.");
            return;
        }

        _window?.Close();
        var win = new ClashNavigatorWindow(ClashDisplay.DocKey(view.Document), view.UniqueId);
        new WindowInteropHelper(win) { Owner = uiapp.MainWindowHandle };
        win.Closed += (_, _) =>
        {
            if (_window == win) _window = null;
            Run(app => { if (Resolve(app, win, quiet: true) is (_, View3D v)) ClashDisplay.SetHighlight(v, -1); app.ActiveUIDocument?.RefreshActiveView(); });
        };
        _window = win;
        win.Load(data, view.Name, 0, warnings, note);
        win.Show();
    }

    public static void CloseFor(Document doc)
    {
        if (_window != null && _window.DocKey == ClashDisplay.DocKey(doc)) _window.Close();
    }

    // ── Acciones (las llama la ventana) ────────────────────────────────────────

    public static void GoTo(ClashNavigatorWindow win, int index, bool sectionBox) =>
        Run(app => GoToNow(app, win, index, sectionBox));

    public static void ToggleResolved(ClashNavigatorWindow win, int index, bool sectionBox) => Run(app =>
    {
        if (Resolve(app, win) is not (Document doc, View3D view)) return;
        ClashData? data = ClashStorage.Read(view);
        if (data == null || index >= data.Items.Count) return;

        ClashItem item = data.Items[index];
        item.Status = item.Status == ClashStatus.Pending ? ClashStatus.Resolved : ClashStatus.Pending;
        using (var t = new Transaction(doc, "Kaiken: estado de interferencia"))
        {
            t.Start();
            ClashStorage.Write(view, data);
            ClashHelpers.UpdateMarks(view, data);
            t.Commit();
        }
        ClashDisplay.Ensure(view);
        win.Load(data, view.Name, index);

        // Al resolver, se pasa sola a la siguiente.
        int next = win.SelectedIndex;
        if (next >= 0 && next != index) GoToNow(app, win, next, sectionBox);
        else { ClashDisplay.SetHighlight(view, index); app.ActiveUIDocument.RefreshActiveView(); }
    });

    public static void RecheckAll(ClashNavigatorWindow win) => Run(app =>
    {
        if (Resolve(app, win) is not (Document doc, View3D view)) return;
        ClashData? data = ClashStorage.Read(view);
        if (data == null) return;

        int gone = ClashStorage.Recheck(doc, data);
        using (var t = new Transaction(doc, "Kaiken: re-verificar interferencias"))
        {
            t.Start();
            ClashStorage.Write(view, data);
            ClashHelpers.UpdateMarks(view, data);
            t.Commit();
        }
        ClashDisplay.Ensure(view);
        int sel = win.SelectedIndex;
        win.Load(data, view.Name, sel < 0 ? 0 : sel,
            note: gone == 0 ? "Re-verificado: no hubo cambios." : $"Re-verificado: {gone} ya no chocan.");
        ClashDisplay.SetHighlight(view, win.SelectedIndex);
        app.ActiveUIDocument.RefreshActiveView();
    });

    public static void ShowAll(ClashNavigatorWindow win) => Run(app =>
    {
        if (Resolve(app, win) is not (Document doc, View3D view)) return;
        ActivateView(app, view);
        if (view.IsSectionBoxActive)
        {
            using var t = new Transaction(doc, "Kaiken: ver todas las interferencias");
            t.Start();
            view.IsSectionBoxActive = false;
            t.Commit();
        }

        ClashData? data = ClashStorage.Read(view);
        var items = data?.Items.Where(i => i.Status == ClashStatus.Pending).ToList();
        if (items != null && items.Count > 0)
        {
            Zoom(app, view,
                new XYZ(items.Min(i => i.Min.X), items.Min(i => i.Min.Y), items.Min(i => i.Min.Z)),
                new XYZ(items.Max(i => i.Max.X), items.Max(i => i.Max.Y), items.Max(i => i.Max.Z)));
        }
        app.ActiveUIDocument.RefreshActiveView();
    });

    // ── Internos ───────────────────────────────────────────────────────────────

    private static void GoToNow(UIApplication app, ClashNavigatorWindow win, int index, bool sectionBox)
    {
        if (Resolve(app, win) is not (Document doc, View3D view)) return;
        ClashData? data = ClashStorage.Read(view);
        if (data == null || index < 0 || index >= data.Items.Count) return;
        ClashItem item = data.Items[index];

        ActivateView(app, view);
        var pad = new XYZ(PadFt, PadFt, PadFt);
        if (sectionBox)
        {
            using var t = new Transaction(doc, "Kaiken: ir a interferencia");
            t.Start();
            view.SetSectionBox(new BoundingBoxXYZ { Min = item.Min - pad, Max = item.Max + pad });
            view.IsSectionBoxActive = true;
            t.Commit();
        }

        Zoom(app, view, item.Min, item.Max);
        if (doc.GetElement(item.AId) != null)
            app.ActiveUIDocument.Selection.SetElementIds(new List<ElementId> { item.AId });
        ClashDisplay.SetHighlight(view, index);
        app.ActiveUIDocument.RefreshActiveView();
    }

    private static void ActivateView(UIApplication app, View3D view)
    {
        UIDocument uidoc = app.ActiveUIDocument;
        if (uidoc.ActiveView.Id != view.Id) uidoc.ActiveView = view;
    }

    private static void Zoom(UIApplication app, View3D view, XYZ min, XYZ max)
    {
        var pad = new XYZ(PadFt, PadFt, PadFt);
        app.ActiveUIDocument.GetOpenUIViews()
            .FirstOrDefault(v => v.ViewId == view.Id)
            ?.ZoomAndCenterRectangle(min - pad, max + pad);
    }

    /// <summary>El documento y la vista del navegador, si siguen abiertos y son el modelo activo.</summary>
    private static (Document doc, View3D view)? Resolve(UIApplication app, ClashNavigatorWindow win, bool quiet = false)
    {
        Document? doc = app.ActiveUIDocument?.Document;
        if (doc == null || ClashDisplay.DocKey(doc) != win.DocKey)
        {
            if (!quiet) TaskDialog.Show(Title, "Activa el modelo donde se revisaron las interferencias.");
            return null;
        }
        if (doc.GetElement(win.ViewUid) is not View3D view)
        {
            if (!quiet) TaskDialog.Show(Title, "La vista de interferencias ya no existe.");
            return null;
        }
        return (doc, view);
    }

    private static void Run(Action<UIApplication> action)
    {
        if (_event == null) return;
        EventHandler.Enqueue(action);
        _event.Raise();
    }

    private sealed class Handler : IExternalEventHandler
    {
        private readonly Queue<Action<UIApplication>> _queue = new();

        public void Enqueue(Action<UIApplication> a)
        {
            lock (_queue) _queue.Enqueue(a);
        }

        public void Execute(UIApplication app)
        {
            while (true)
            {
                Action<UIApplication> a;
                lock (_queue)
                {
                    if (_queue.Count == 0) return;
                    a = _queue.Dequeue();
                }
                try { a(app); }
                catch (Exception ex) { TaskDialog.Show(Title, ex.Message); }
            }
        }

        public string GetName() => "Kaiken Interferencias";
    }
}
