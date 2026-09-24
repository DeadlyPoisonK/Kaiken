using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace Kaiken;

/// <summary>
/// Botón de cinta: arregla en un solo paso uno o varios codos de 90°
/// cambiando la altura en la conexión. Inserta dos codos y un tramo vertical.
/// Soporta selección múltiple y es totalmente undoable.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class FixBranchElbowCommand : IExternalCommand
{
    private class ElbowOrPipeFilter : ISelectionFilter
    {
        public bool AllowElement(Element e)
        {
            if (e is MEPCurve) return true;
            if (e is FamilyInstance fi &&
                fi.Category != null &&
                (fi.Category.Id.Value == (long)BuiltInCategory.OST_PipeFitting ||
                 fi.Category.Id.Value == (long)BuiltInCategory.OST_DuctFitting ||
                 fi.Category.Id.Value == (long)BuiltInCategory.OST_CableTrayFitting ||
                 fi.Category.Id.Value == (long)BuiltInCategory.OST_ConduitFitting) &&
                fi.MEPModel?.ConnectorManager?.Connectors.Size == 2)
            {
                return true;
            }
            return false;
        }

        public bool AllowReference(Reference r, XYZ p) => false;
    }

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        // 1. Recoger todos los codos de 90° y tuberías de la selección actual.
        var selectedElements = uidoc.Selection.GetElementIds()
            .Select(id => doc.GetElement(id))
            .ToList();

        var elbowIds = selectedElements
            .OfType<FamilyInstance>()
            .Where(fi => fi.Category != null &&
                         (fi.Category.Id.Value == (long)BuiltInCategory.OST_PipeFitting ||
                          fi.Category.Id.Value == (long)BuiltInCategory.OST_DuctFitting ||
                          fi.Category.Id.Value == (long)BuiltInCategory.OST_CableTrayFitting ||
                          fi.Category.Id.Value == (long)BuiltInCategory.OST_ConduitFitting) &&
                         fi.MEPModel?.ConnectorManager?.Connectors.Size == 2)
            .Select(fi => fi.Id.Value)
            .ToList();

        var selectedPipeIds = selectedElements
            .OfType<MEPCurve>()
            .Select(m => m.Id.Value)
            .ToHashSet();

        // Si no hay ningún codo en la selección, pedir que seleccione.
        if (elbowIds.Count == 0)
        {
            try
            {
                var picked = uidoc.Selection.PickObjects(ObjectType.Element, new ElbowOrPipeFilter(),
                    "Selecciona los codos de 90° a corregir (opcionalmente selecciona también la tubería de anclaje)");
                var pickedElements = picked.Select(r => doc.GetElement(r)).ToList();

                elbowIds = pickedElements
                    .OfType<FamilyInstance>()
                    .Where(fi => fi.Category != null &&
                                 (fi.Category.Id.Value == (long)BuiltInCategory.OST_PipeFitting ||
                                  fi.Category.Id.Value == (long)BuiltInCategory.OST_DuctFitting ||
                                  fi.Category.Id.Value == (long)BuiltInCategory.OST_CableTrayFitting ||
                                  fi.Category.Id.Value == (long)BuiltInCategory.OST_ConduitFitting) &&
                                 fi.MEPModel?.ConnectorManager?.Connectors.Size == 2)
                    .Select(fi => fi.Id.Value)
                    .ToList();

                selectedPipeIds = pickedElements
                    .OfType<MEPCurve>()
                    .Select(m => m.Id.Value)
                    .ToHashSet();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
        }

        if (elbowIds.Count == 0)
        {
            TaskDialog.Show("Rou-C", "No se seleccionó ningún codo válido.");
            return Result.Cancelled;
        }

        // 2. Medir cada codo y calcular sugerencias.
        var suggestions = new List<(long elbowId, LiveBridgeOperations.ElbowRiseSuggestion suggestion)>();
        var failedIds = new List<(long elbowId, string error)>();

        foreach (long elbowId in elbowIds)
        {
            try
            {
                var suggestion = LiveBridgeOperations.SuggestElbowRiseCore(doc, elbowId, marginCm: 1.0);
                suggestions.Add((elbowId, suggestion));
            }
            catch (Exception ex)
            {
                failedIds.Add((elbowId, ex.Message));
            }
        }

        if (suggestions.Count == 0)
        {
            string errores = string.Join("\n", failedIds.Select(f => $"  • Codo {f.elbowId}: {f.error}"));
            TaskDialog.Show("Rou-C", $"No se pudo medir ninguno de los {elbowIds.Count} codos seleccionados:\n{errores}");
            return Result.Cancelled;
        }

        // Usar la sugerencia "más exigente" como valor por defecto.
        var worstCase = suggestions.OrderByDescending(s => s.suggestion.SuggestedRiseCm).First().suggestion;

        // 3. Confirmar con el usuario
        string multiInfo = suggestions.Count > 1
            ? $"Se procesarán {suggestions.Count} codos en lote. Los valores mostrados son los del caso más exigente.\n"
            : "";
        if (failedIds.Count > 0)
            multiInfo += $"⚠ {failedIds.Count} codos no se pudieron medir y se omitirán.\n";

        var win = new ElbowRiseWindow(worstCase, multiInfo, suggestions.Count);
        if (win.ShowDialog() != true) return Result.Cancelled;
        var choice = win.Result;

        // 4. Arreglar todos los codos en una sola transacción
        using var t = new Transaction(doc, $"Rou-C: arreglo de {suggestions.Count} codos");
        t.Start();
        try
        {
            int ok = 0;
            var erroresEnFix = new List<string>();

            foreach (var (elbowId, _) in suggestions)
            {
                try
                {
                    // Detectar si alguna tubería de anclaje seleccionada está conectada a este codo
                    long? anchorPipeId = null;
                    var elbow = doc.GetElement(new ElementId(elbowId)) as FamilyInstance;
                    if (elbow != null)
                    {
                        var connectors = elbow.MEPModel?.ConnectorManager?.Connectors.Cast<Connector>().ToList();
                        if (connectors != null && connectors.Count == 2)
                        {
                            var refA = connectors[0].AllRefs.Cast<Connector>().FirstOrDefault(c => c.Owner.Id != elbow.Id);
                            var refB = connectors[1].AllRefs.Cast<Connector>().FirstOrDefault(c => c.Owner.Id != elbow.Id);
                            
                            long? idA = refA?.Owner?.Id?.Value;
                            long? idB = refB?.Owner?.Id?.Value;

                            if (idA != null && selectedPipeIds.Contains(idA.Value))
                            {
                                anchorPipeId = idA;
                            }
                            else if (idB != null && selectedPipeIds.Contains(idB.Value))
                            {
                                anchorPipeId = idB;
                            }
                        }
                    }

                    LiveBridgeOperations.FixBranchElbowCore(doc, elbowId, choice.RiseCm, choice.Downward, choice.Invert, anchorPipeId);
                    ok++;
                }
                catch (Exception ex)
                {
                    erroresEnFix.Add($"Codo {elbowId}: {ex.Message}");
                }
            }

            if (ok == 0)
            {
                t.RollBack();
                string msg = string.Join("\n", erroresEnFix.Take(5));
                TaskDialog.Show("Rou-C", $"Falló en todos los codos, se revirtió la transacción:\n{msg}");
                return Result.Failed;
            }

            t.Commit();

            string sentido = choice.Downward ? "hacia abajo" : "hacia arriba";
            string resumen = ok == 1
                ? $"1 codo corregido {sentido}."
                : $"{ok} codos corregidos {sentido}.";

            if (erroresEnFix.Count > 0)
                resumen += $"\n⚠ {erroresEnFix.Count} codos fallaron:\n" + string.Join("\n", erroresEnFix.Take(3));

            TaskDialog.Show("Rou-C", $"Listo.\n\n{resumen}");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            TaskDialog.Show("Rou-C", $"Falló, no se modificó el modelo (transacción revertida):\n{ex.Message}");
            return Result.Failed;
        }
    }
}
