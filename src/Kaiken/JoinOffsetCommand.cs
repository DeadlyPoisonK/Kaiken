using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace Kaiken;

/// <summary>
/// Botón de cinta "Unir": conecta dos tramos MEP (tubería, ducto, bandeja o conduit) que
/// corren paralelos pero están a distinta altura, con un salto de dos codos del mismo
/// ángulo (45°+45°, 90°+90° o "Cualquier °": el que mejor quepa según desnivel y distancia).
/// Ninguno de los dos tramos originales
/// cambia de altura: el primero queda intacto y el segundo sólo se estira a lo largo de su
/// propio eje hasta encontrarse con el salto.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class JoinOffsetCommand : IExternalCommand
{
    private class MepCurveFilter : ISelectionFilter
    {
        public bool AllowElement(Element e) => e is MEPCurve;
        public bool AllowReference(Reference r, XYZ p) => false;
    }

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        var selected = uidoc.Selection.GetElementIds()
            .Select(id => doc.GetElement(id))
            .OfType<MEPCurve>()
            .ToList();

        if (selected.Count != 2)
        {
            try
            {
                var picked = uidoc.Selection.PickObjects(ObjectType.Element, new MepCurveFilter(),
                    "Selecciona los 2 tramos (tubería/ducto/bandeja/conduit) a unir con un salto de codos");
                selected = picked.Select(r => doc.GetElement(r)).OfType<MEPCurve>().ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
        }

        if (selected.Count != 2)
        {
            TaskDialog.Show("Unir", "Selecciona exactamente 2 tramos para unir.");
            return Result.Cancelled;
        }

        MEPCurve pipeA = selected[0];
        MEPCurve pipeB = selected[1];

        if (pipeA.GetType() != pipeB.GetType())
        {
            TaskDialog.Show("Unir", "Los dos tramos deben ser del mismo tipo (ambos tubería, ambos ducto, etc.).");
            return Result.Cancelled;
        }

        JoinOffsetGeometry geo;
        try
        {
            geo = JoinOffsetHelpers.Measure(pipeA, pipeB);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Unir", $"No se puede unir esta pareja:\n{ex.Message}");
            return Result.Cancelled;
        }

        var win = new JoinOffsetWindow(geo.OffsetCm);
        if (win.ShowDialog() != true) return Result.Cancelled;
        bool auto = win.Result.AngleDeg == null;
        double angleDeg;
        if (win.Result.AngleDeg is double fixedAngle) angleDeg = fixedAngle;
        else
        {
            MepOps? ops = MepOps.For(pipeA);
            if (ops == null)
            {
                TaskDialog.Show("Unir", "Tipo de elemento MEP no soportado.");
                return Result.Cancelled;
            }
            var elbows = ElbowTable.Measure(doc, pipeA, ops, geo.DirA, geo.OffsetVec.Normalize(), ElbowTable.Grid);
            try
            {
                angleDeg = JoinOffsetHelpers.ChooseAngle(geo, elbows);
            }
            catch (InvalidOperationException ex)
            {
                TaskDialog.Show("Unir", $"No hay un ángulo con que el salto quepa:\n{ex.Message}");
                return Result.Cancelled;
            }
        }

        using var t = new Transaction(doc, "Unir");
        t.Start();
        try
        {
            JoinOffsetHelpers.Execute(doc, pipeA, pipeB, geo, angleDeg);
            t.Commit();

            TaskDialog.Show("Unir", $"Listo. Tramos unidos con dos codos de {angleDeg:0}°" +
                (auto ? " (elegido según desnivel y distancia)." : "."));
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            string hint = auto ? "" : "\n\nSi es por falta de espacio, prueba con \"Cualquier °\".";
            TaskDialog.Show("Unir", $"Falló, no se modificó el modelo (transacción revertida):\n{ex.Message}{hint}");
            return Result.Failed;
        }
    }
}
