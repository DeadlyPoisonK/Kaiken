using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace Kaiken;

/// <summary>
/// Botón de cinta "Alc" (alcantarillado): eliges una o varias bajantes (tuberías verticales) y luego la
/// troncal; para cada bajante arma la ruta bajante → codo → diagonal → codo → horizontal con 1 % de
/// pendiente → Te/Tap en la troncal, para después ajustarla a mano en 3D. Prefiere codos de 45° y, si
/// no caben, prueba otros ángulos. Cada bajante va en su propia transacción; todo queda en un solo Ctrl+Z.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class AlcCommand : IExternalCommand
{
    private const string Title = "Alc";

    private class PipeFilter : ISelectionFilter
    {
        public bool AllowElement(Element e) => e is Pipe;
        public bool AllowReference(Reference r, XYZ p) => false;
    }

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        List<Pipe> risers = uidoc.Selection.GetElementIds().Select(id => doc.GetElement(id)).OfType<Pipe>().ToList();
        Pipe trunk;
        try
        {
            if (risers.Count == 0)
                risers = uidoc.Selection.PickObjects(ObjectType.Element, new PipeFilter(),
                        "Selecciona las bajantes (tuberías verticales) y presiona Finalizar")
                    .Select(r => doc.GetElement(r)).OfType<Pipe>().ToList();
            if (risers.Count == 0) return Result.Cancelled;

            trunk = (Pipe)doc.GetElement(uidoc.Selection.PickObject(ObjectType.Element, new PipeFilter(),
                "Selecciona la troncal (tubería) a la que se conectan"));
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

        // Se mide el codo una vez por tipo y diámetro, fuera de la transacción (la medición se descarta sola).
        var tables = new Dictionary<(ElementId, long), ElbowTable>();
        var targets = new List<(Pipe riser, AlcTarget? target, string error)>();
        foreach (Pipe r in risers)
        {
            try { targets.Add((r, AlcHelpers.Locate(r, trunk), "")); }
            catch (Exception ex) { targets.Add((r, null, ex.Message)); }
        }

        foreach (var (_, target, _) in targets)
        {
            if (target == null) continue;
            var key = (target.Riser.GetTypeId(), (long)Math.Round(target.RiserDiameter * 100000));
            if (tables.ContainsKey(key)) continue;
            tables[key] = ElbowTable.Measure(doc, target.Riser, new PipeOps(), new XYZ(0, 0, -1), target.U, ElbowTable.Grid);
        }

        var ok = new List<string>();
        var failed = new List<string>();

        using var group = new TransactionGroup(doc, Title);
        group.Start();
        foreach (var (riser, target, error) in targets)
        {
            string name = $"Id {riser.Id.Value}";
            if (target == null) { failed.Add($"{name}: {error}"); continue; }

            using var t = new Transaction(doc, "Alc");
            t.Start();
            try
            {
                var key = (target.Riser.GetTypeId(), (long)Math.Round(target.RiserDiameter * 100000));
                AlcPlan plan = AlcHelpers.Plan(target, tables[key]);
                AlcHelpers.Execute(doc, plan);
                t.Commit();
                ok.Add(plan.SingleElbow
                    ? $"{name}: un codo de {plan.Turn1Deg:0}°"
                    : $"{name}: codos de {plan.Turn1Deg:0}° y {plan.Turn2Deg:0.#}°");
            }
            catch (Exception ex)
            {
                if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                failed.Add($"{name}: {ex.Message}");
            }
        }
        if (ok.Count > 0) group.Assimilate(); else group.RollBack();

        string summary = $"Conectadas: {ok.Count} de {risers.Count}.";
        if (ok.Count > 0) summary += "\n\n" + string.Join("\n", ok);
        if (failed.Count > 0) summary += "\n\nNo se pudieron conectar (sin cambios):\n" + string.Join("\n", failed);
        TaskDialog.Show(Title, summary);
        return ok.Count > 0 ? Result.Succeeded : Result.Failed;
    }
}
