using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace Kaiken;

/// <summary>
/// Botón de cinta: arregla en un solo paso una o varias Te de ramal horizontal
/// (gases medicinales, PCI, etc.) — mide el espacio físico real, sugiere la altura
/// mínima, y hace TODO el trabajo en una sola transacción (Ctrl+Z deshace todo).
/// Soporta selección múltiple: si se seleccionan varias Te, las procesa todas juntas.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class FixBranchTeeCommand : IExternalCommand
{
    private class TeeFilter : ISelectionFilter
    {
        public bool AllowElement(Element e)
        {
            if (e is FamilyInstance fi &&
                fi.Category != null &&
                (fi.Category.Id.Value == (long)BuiltInCategory.OST_PipeFitting ||
                 fi.Category.Id.Value == (long)BuiltInCategory.OST_DuctFitting ||
                 fi.Category.Id.Value == (long)BuiltInCategory.OST_CableTrayFitting ||
                 fi.Category.Id.Value == (long)BuiltInCategory.OST_ConduitFitting) &&
                fi.MEPModel?.ConnectorManager?.Connectors.Size == 3)
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

        // 1. Recoger TODAS las Te de la selección actual (fittings de 3 conectores).
        var teeIds = uidoc.Selection.GetElementIds()
            .Select(id => doc.GetElement(id))
            .OfType<FamilyInstance>()
            .Where(fi => fi.Category != null &&
                         (fi.Category.Id.Value == (long)BuiltInCategory.OST_PipeFitting ||
                          fi.Category.Id.Value == (long)BuiltInCategory.OST_DuctFitting ||
                          fi.Category.Id.Value == (long)BuiltInCategory.OST_CableTrayFitting ||
                          fi.Category.Id.Value == (long)BuiltInCategory.OST_ConduitFitting) &&
                         fi.MEPModel?.ConnectorManager?.Connectors.Size == 3)
            .Select(fi => fi.Id.Value)
            .ToList();

        // Si no hay ninguna en la selección, pedir que seleccione una o varias.
        if (teeIds.Count == 0)
        {
            try
            {
                var picked = uidoc.Selection.PickObjects(ObjectType.Element, new TeeFilter(),
                    "Selecciona una o varias Te (fittings de 3 conectores) a corregir");
                teeIds = picked.Select(r => doc.GetElement(r).Id.Value).ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
        }

        if (teeIds.Count == 0)
        {
            TaskDialog.Show("Rou-T", "No se seleccionó ninguna Te válida.");
            return Result.Cancelled;
        }

        // 2. Medir cada Te y calcular sugerencias.
        var suggestions = new List<(long teeId, LiveBridgeOperations.TeeRiseSuggestion suggestion)>();
        var failedIds = new List<(long teeId, string error)>();

        foreach (long teeId in teeIds)
        {
            try
            {
                var suggestion = LiveBridgeOperations.SuggestTeeRiseCore(doc, teeId, marginCm: 1.0);
                suggestions.Add((teeId, suggestion));
            }
            catch (Exception ex)
            {
                failedIds.Add((teeId, ex.Message));
            }
        }

        if (suggestions.Count == 0)
        {
            string errores = string.Join("\n", failedIds.Select(f => $"  • Te {f.teeId}: {f.error}"));
            TaskDialog.Show("Rou-T", $"No se pudo medir ninguna de las {teeIds.Count} Te seleccionadas:\n{errores}");
            return Result.Cancelled;
        }

        // Usar la sugerencia "más exigente" (la que necesita más espacio) como valor por defecto
        // para que todas puedan resolverse con los mismos parámetros.
        bool anyHasReduction = suggestions.Any(s => s.suggestion.HasReduction);
        var worstCase = anyHasReduction
            ? suggestions.Where(s => s.suggestion.HasReduction)
                .OrderByDescending(s => s.suggestion.SuggestedBigStubCm + s.suggestion.SuggestedSmallStubCm)
                .First().suggestion
            : suggestions.OrderByDescending(s => s.suggestion.SuggestedRiseCm).First().suggestion;

        // 3. Confirma con el usuario (valores editables). Si hay varias Te, lo informa.
        string multiInfo = suggestions.Count > 1
            ? $"Se procesarán {suggestions.Count} Te en lote. Los valores mostrados son los del caso más exigente.\n"
            : "";
        if (failedIds.Count > 0)
            multiInfo += $"⚠ {failedIds.Count} Te no se pudieron medir y se omitirán.\n";

        var win = new TeeRiseWindow(worstCase, multiInfo, suggestions.Count);
        if (win.ShowDialog() != true) return Result.Cancelled;
        var choice = win.Result;

        // 4. Arreglar TODAS las Te en UNA sola transacción (Ctrl+Z deshace todo junto).
        using var t = new Transaction(doc, $"Rou-T: arreglo de {suggestions.Count} Te");
        t.Start();
        try
        {
            int ok = 0;
            var erroresEnFix = new List<string>();

            foreach (var (teeId, suggestion) in suggestions)
            {
                try
                {
                    // Si esta Te individual tiene reducción, usar BigStub/SmallStub;
                    // si no, usar RiseCm. Pero respetar el downward del usuario.
                    bool useReduction = suggestion.HasReduction;
                    var fixResult = useReduction
                        ? LiveBridgeOperations.FixBranchTeeUpCore(doc, teeId, null, choice.BigStubCm, choice.SmallStubCm, choice.Downward)
                        : LiveBridgeOperations.FixBranchTeeUpCore(doc, teeId, choice.RiseCm, null, null, choice.Downward);

                    var reconnect = LiveBridgeOperations.ReconnectOrphanToStubCore(
                        doc, fixResult.OrphanedOldChainStartId.Value, fixResult.OpenStubId.Value);

                    ok++;
                }
                catch (Exception ex)
                {
                    erroresEnFix.Add($"Te {teeId}: {ex.Message}");
                }
            }

            if (ok == 0)
            {
                t.RollBack();
                string msg = string.Join("\n", erroresEnFix.Take(5));
                TaskDialog.Show("Rou-T", $"Falló en todas las Te, se revirtió la transacción:\n{msg}");
                return Result.Failed;
            }

            t.Commit();

            string sentido = choice.Downward ? "hacia abajo" : "hacia arriba";
            string resumen = ok == 1
                ? $"1 Te corregida {sentido}."
                : $"{ok} Te corregidas {sentido}.";

            if (erroresEnFix.Count > 0)
                resumen += $"\n⚠ {erroresEnFix.Count} Te fallaron:\n" + string.Join("\n", erroresEnFix.Take(3));

            TaskDialog.Show("Rou-T", $"Listo.\n\n{resumen}");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            TaskDialog.Show("Rou-T", $"Falló, no se modificó el modelo (transacción revertida):\n{ex.Message}");
            return Result.Failed;
        }
    }
}
