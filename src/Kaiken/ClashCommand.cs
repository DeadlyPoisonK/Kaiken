using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Botón de cinta: revisa los elementos elegidos (por ahora escalerillas, ductos, tuberías o
/// conduits del modelo actual) contra categorías del modelo y de los vínculos, y muestra el
/// resultado en una vista 3D propia ("Kaiken - Interferencias (usuario)"): todo semitransparente,
/// los elementos con choque en naranjo y el volumen exacto de cada choque en rojo.
/// Todo queda guardado en esa vista (los colores como overrides, el rojo en <see cref="ClashStorage"/>);
/// una nueva revisión la reemplaza, y si el usuario la renombra queda archivada.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ClashMarkerCommand : IExternalCommand
{
    private const string Title = "Interferencias";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        var win = new ClashWindow(doc);
        if (win.ShowDialog() != true || win.Options == null) return Result.Cancelled;
        ClashOptions opts = win.Options;

        List<Element> aElements = ClashHelpers.CollectA(doc, opts);
        if (aElements.Count == 0)
        {
            TaskDialog.Show(Title, "No hay elementos de lo elegido en el paso 1 en este modelo.");
            return Result.Cancelled;
        }

        // ── Detección (solo lectura) ────────────────────────────────────────────
        var clashes = new List<ClashResult>();
        var seenPairs = new HashSet<(long, long)>();
        int failed = 0;
        bool cancelled = false;

        var progress = new ClashProgressWindow(aElements.Count);
        progress.Show();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Una sola pasada por las cajas de todo lo que puede chocar (modelo + vínculos).
            ClashIndex index = ClashIndex.Build(doc, opts, progress.SetStatus);
            double tolFt = opts.ToleranceMm * ClashHelpers.MmToFeet;

            long lastReport = -1000;
            for (int i = 0; i < aElements.Count; i++)
            {
                if (progress.CancelRequested) { cancelled = true; break; }
                try { clashes.AddRange(ClashHelpers.FindClashes(aElements[i], index, tolFt, seenPairs)); }
                catch { failed++; }
                if (clock.ElapsedMilliseconds - lastReport > 200 || i == aElements.Count - 1)
                {
                    lastReport = clock.ElapsedMilliseconds;
                    progress.Report(i + 1, clashes.Count, clashes.Select(c => c.AId).Distinct().Count());
                }
            }
        }
        finally { progress.Finish(); }

        if (cancelled)
        {
            var td = new TaskDialog(Title)
            {
                MainInstruction = "Revisión detenida",
                MainContent = $"Se alcanzaron a encontrar {clashes.Count} interferencias. ¿Mostrar lo encontrado hasta ahora?",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            };
            if (td.Show() != TaskDialogResult.Yes) return Result.Cancelled;
        }

        // ── Vista y colores ─────────────────────────────────────────────────────
        var warnings = ClashHelpers.ClosedWorksets(doc, opts.Links);
        if (failed > 0) warnings.Add($"{failed} elementos no se pudieron revisar (geometría no válida)");
        ClashData data = ClashStorage.FromResults(clashes, opts.ToleranceMm * ClashHelpers.MmToFeet);
        View3D view;
        try
        {
            using var t = new Transaction(doc, "Kaiken: marcar interferencias");
            t.Start();
            // La vista por defecto se reemplaza; una renombrada queda guardada con su resultado.
            view = ClashHelpers.GetOrCreateView(doc);
            view.IsSectionBoxActive = false;
            ClashHelpers.ApplyView(doc, view, opts.ACategories, clashes, data);
            if (clashes.Count > 0) ClashStorage.Write(view, data);
            else ClashStorage.Delete(view);
            t.Commit();
        }
        catch (Exception ex)
        {
            TaskDialog.Show(Title, $"No se pudo preparar la vista de interferencias (¿la tiene tomada otro usuario?):\n{ex.Message}");
            return Result.Failed;
        }

        if (uidoc.ActiveView.Id != view.Id) uidoc.ActiveView = view;
        ClashDisplay.Ensure(view);

        if (clashes.Count == 0)
        {
            uidoc.RefreshActiveView();
            string msg = $"Sin interferencias: se revisaron {aElements.Count} elementos.";
            if (warnings.Count > 0) msg += "\n\nOjo:\n" + string.Join("\n", warnings.Select(w => "  • " + w));
            TaskDialog.Show(Title, msg);
            return Result.Succeeded;
        }

        UIView? uiView = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
        var pad = new XYZ(3, 3, 3);
        uiView?.ZoomAndCenterRectangle(
            new XYZ(clashes.Min(c => c.Min.X), clashes.Min(c => c.Min.Y), clashes.Min(c => c.Min.Z)) - pad,
            new XYZ(clashes.Max(c => c.Max.X), clashes.Max(c => c.Max.Y), clashes.Max(c => c.Max.Z)) + pad);
        uidoc.RefreshActiveView();

        ClashNavigator.Open(commandData.Application, view, warnings,
            $"Revisado en {FormatTime(clock.Elapsed)} ({aElements.Count} elementos).");
        return Result.Succeeded;
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{t.TotalSeconds:0.#} s";
}

/// <summary>Abre el navegador de la vista activa (si tiene interferencias) o de la vista por defecto.</summary>
[Transaction(TransactionMode.Manual)]
public class ClashNavigateCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        View3D? view = uidoc.ActiveView is View3D active && ClashStorage.HasData(active)
            ? active
            : ClashHelpers.FindView(uidoc.Document);
        if (view == null || !ClashStorage.HasData(view))
        {
            TaskDialog.Show("Interferencias", "No hay interferencias guardadas: activa una vista de interferencias o corre una revisión.");
            return Result.Cancelled;
        }

        if (uidoc.ActiveView.Id != view.Id) uidoc.ActiveView = view;
        ClashDisplay.Ensure(view);
        ClashNavigator.Open(commandData.Application, view);
        return Result.Succeeded;
    }
}

/// <summary>
/// Quita las marcas de interferencias (el rojo, el naranjo y la transparencia) de la vista
/// activa si las tiene —también de una vista renombrada—, o si no de la vista por defecto.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ClashClearCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        View3D? view = uidoc.ActiveView is View3D active && ClashStorage.HasData(active)
            ? active
            : ClashHelpers.FindView(doc);
        if (view == null)
        {
            TaskDialog.Show("Interferencias", "No hay una vista de interferencias en este modelo.");
            return Result.Cancelled;
        }

        bool hadData;
        int cleared;
        try
        {
            using var t = new Transaction(doc, "Kaiken: limpiar interferencias");
            t.Start();
            hadData = ClashStorage.Delete(view);
            cleared = ClashHelpers.ResetView(doc, view);
            view.IsSectionBoxActive = false;
            t.Commit();
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Interferencias", $"No se pudieron quitar las marcas de la vista \"{view.Name}\":\n{ex.Message}");
            return Result.Failed;
        }

        ClashNavigator.CloseFor(doc);
        ClashDisplay.Ensure(view);
        uidoc.RefreshActiveView();
        if (!hadData && cleared == 0)
            TaskDialog.Show("Interferencias", $"La vista \"{view.Name}\" no tenía marcas de interferencias.");
        return Result.Succeeded;
    }
}

/// <summary>Progreso no modal: la revisión corre en el hilo de Revit y cada tanto deja redibujar a WPF.</summary>
internal sealed class ClashProgressWindow : Window
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _status;
    private readonly Button _cancel;
    private bool _finished;
    private readonly int _total;

    public bool CancelRequested { get; private set; }

    public ClashProgressWindow(int total)
    {
        _total = total;
        Title = "Interferencias";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        _bar = new ProgressBar { Height = 18, Maximum = Math.Max(total, 1), Margin = new Thickness(0, 0, 0, 8) };
        _status = new TextBlock { Text = "Revisando…", Margin = new Thickness(0, 0, 0, 8) };
        _cancel = new Button { Content = "Detener", Width = 90, Height = 26, HorizontalAlignment = HorizontalAlignment.Right };
        _cancel.Click += (_, _) => RequestCancel();

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(_status);
        panel.Children.Add(_bar);
        panel.Children.Add(_cancel);
        Content = panel;
    }

    public void SetStatus(string text)
    {
        _status.Text = text;
        Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
    }

    public void Report(int done, int clashes, int elements)
    {
        _bar.Value = done;
        _status.Text = $"Elemento {done} de {_total} · {clashes} interferencias en {elements} elementos";
        Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
    }

    public void Finish()
    {
        _finished = true;
        Close();
    }

    private void RequestCancel()
    {
        CancelRequested = true;
        _cancel.IsEnabled = false;
        _status.Text = "Deteniendo…";
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true;
            RequestCancel();
        }
        base.OnClosing(e);
    }
}
