using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken.Dev;

/// <summary>Prueba de punta a punta de LayerStore en la vista activa. Todo se revierte al final (RollBack).</summary>
public static class LayerTests
{
    public static object Smoke(UIDocument uidoc)
    {
        Document doc = uidoc.Document;
        View view = uidoc.ActiveView;
        var log = new List<string>();
        void Check(string what, bool ok) => log.Add($"{(ok ? "OK  " : "FALLA")} {what}");

        var inView = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElements();
        var model = inView.Where(e => !e.ViewSpecific && e.Category is { CategoryType: CategoryType.Model } && e.Category.AllowsBoundParameters)
            .Take(3).ToList();
        var ann = inView.Where(e => e.ViewSpecific && e.OwnerViewId == view.Id && (e is IndependentTag || e is TextNote || e is Dimension)).Take(2).ToList();
        log.Add($"vista «{view.Name}»: {model.Count} de modelo, {ann.Count} anotaciones ({string.Join(", ", ann.Select(a => a.Category?.Name))})");

        using var group = new TransactionGroup(doc, "Kaiken prueba capas");
        group.Start();
        try
        {
            if (ann.Count == 0)
                Tx(doc, () =>
                {
                    // Textos de prueba (se revierten con el resto).
                    ElementId type = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                    ann.Add(TextNote.Create(doc, view.Id, XYZ.Zero, "Kaiken prueba 1", type));
                    ann.Add(TextNote.Create(doc, view.Id, new XYZ(10, 0, 0), "Kaiken prueba 2", type));
                    log.Add("creados 2 textos de prueba");
                });

            ElementId a = ElementId.InvalidElementId;
            Tx(doc, () =>
            {
                a = LayerStore.Create(doc, "Prueba A").Id;
                var r = LayerStore.Assign(doc, a, model.Concat(ann).Select(e => e.Id).ToList(), view);
                Check($"asignar: {r.Done} hechos, omitidos: {string.Join(" | ", r.Skipped)}", r.Done == model.Count + ann.Count);
            });
            Check("parámetro escrito", model.All(e => e.get_Parameter(new Guid("5E0C7A41-3B2D-4F8E-9A61-D2C4B7E19F35"))?.AsString() == "Prueba A"));

            LayerInfo L() => LayerStore.Load(doc, view, counts: true).Single(l => l.FilterId == a);
            Check($"conteo {L().Count}", L().Count == model.Count + ann.Count);
            Check("selección en la vista", LayerStore.MemberIds(doc, a, view).Count == model.Count + ann.Count);

            Tx(doc, () => { var l = L(); l.Visible = false; LayerStore.Apply(doc, view, l); });
            Check("ojo apagado: filtro oculto", view.GetFilters().Contains(a) && !view.GetFilterVisibility(a));
            Check("ojo apagado: anotaciones ocultas", ann.All(e => e.IsHidden(view)));
            Check("estado leído: no visible", !L().Visible);

            Tx(doc, () => { var l = L(); l.Visible = true; l.ColorOn = true; l.Halftone = true; LayerStore.Apply(doc, view, l); });
            var ogs = view.GetFilterOverrides(a);
            Check("color y semitono en el filtro", ogs.ProjectionLineColor.IsValid && ogs.Halftone && ogs.SurfaceForegroundPatternId != ElementId.InvalidElementId);
            Check("anotaciones visibles y coloreadas", ann.All(e => !e.IsHidden(view) && view.GetElementOverrides(e.Id).ProjectionLineColor.IsValid));
            var st = L();
            Check("estado leído: visible+color+semitono", st.Visible && st.ColorOn && st.Halftone);

            Tx(doc, () => LayerStore.Rename(doc, a, "Prueba B"));
            Check("renombrar: filtro y parámetro", L().Name == "Prueba B" && LayerStore.MemberIds(doc, a, null).Count == model.Count + ann.Count);

            ElementId b = ElementId.InvalidElementId;
            if (model.Count > 0 && ann.Count > 0)
            {
                Tx(doc, () =>
                {
                    b = LayerStore.Create(doc, "Prueba C").Id;
                    LayerStore.Assign(doc, b, new List<ElementId> { model[0].Id, ann[0].Id }, view);
                });
                var layers = LayerStore.Load(doc, view, counts: true);
                Check("mover a otra capa", layers.Single(l => l.FilterId == a).Count == model.Count + ann.Count - 2
                                           && layers.Single(l => l.FilterId == b).Count == 2);
                Check("la anotación movida toma el estado de la capa nueva (sin color)", !view.GetElementOverrides(ann[0].Id).ProjectionLineColor.IsValid);
            }

            Tx(doc, () =>
            {
                var r = LayerStore.Remove(doc, new List<ElementId> { model.Last().Id, ann.Last().Id });
                Check($"quitar: {r.Done}", r.Done == 2);
            });

            Tx(doc, () =>
            {
                var l = L(); l.Visible = false; LayerStore.Apply(doc, view, l);
                LayerStore.Delete(doc, a);
            });
            Check("borrar: filtro eliminado", doc.GetElement(a) == null);
            Check("borrar: anotaciones vuelven a verse", ann.All(e => !e.IsHidden(view)));
            Check("nombre inválido rechazado", LayerStore.ValidateName(doc, "a:b") != null);
        }
        catch (Exception ex)
        {
            log.Add("EXCEPCIÓN: " + ex);
        }
        finally
        {
            group.RollBack();
        }
        log.Add($"revertido; filtros de capa que quedan: {LayerStore.Load(doc, null, false).Count}");
        return log;
    }

    private static void Tx(Document doc, Action body)
    {
        using var t = new Transaction(doc, "paso");
        t.Start();
        body();
        t.Commit();
    }
}
