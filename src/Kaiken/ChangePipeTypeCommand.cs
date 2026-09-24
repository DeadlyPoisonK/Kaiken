using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Botón de cinta: cambia en lote la Familia (Tipo) de las tuberías de la vista actual
/// que tengan una Familia de origen y un diámetro elegidos, hacia una Familia destino —
/// sin tocar el diámetro ni ningún otro parámetro de instancia. Pensado para normalizar
/// tuberías que quedaron con una familia genérica (ej. "03ALC_Tuberia_PVC") hacia las
/// familias por diámetro (PVC.U 50mm / 75mm / 110mm) y así poder sacar cómputos por Familia.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ChangePipeTypeCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;
        View activeView = doc.ActiveView;

        var pipesInView = ChangePipeTypeHelpers.GetPipesInView(doc, activeView);
        if (pipesInView.Count == 0)
        {
            TaskDialog.Show("Cambiar Familia de Tubería", "No hay tuberías en la vista actual.");
            return Result.Cancelled;
        }

        var allTypes = ChangePipeTypeHelpers.GetAllPipeTypes(doc);
        if (allTypes.Count < 2)
        {
            TaskDialog.Show("Cambiar Familia de Tubería", "El proyecto no tiene suficientes Familias de Tubería para elegir un destino distinto.");
            return Result.Cancelled;
        }

        var win = new ChangePipeTypeWindow(doc, pipesInView, allTypes);
        if (win.ShowDialog() != true) return Result.Cancelled;

        PipeType sourceType = win.SelectedSourceType!;
        PipeType targetType = win.SelectedTargetType!;
        double? diameterMm = win.SelectedDiameterMm;

        var toChange = pipesInView
            .Where(p => (doc.GetElement(p.GetTypeId()) as PipeType)?.Id == sourceType.Id)
            .Where(p => !diameterMm.HasValue || ChangePipeTypeHelpers.DiameterMm(p) == diameterMm.Value)
            .ToList();

        if (toChange.Count == 0)
        {
            TaskDialog.Show("Cambiar Familia de Tubería", "No se encontró ninguna tubería que coincida con la Familia y el diámetro elegidos.");
            return Result.Cancelled;
        }

        int ok = 0, failed = 0;
        var errors = new List<string>();

        using var t = new Transaction(doc, "Cambiar Familia de Tubería");
        t.Start();
        try
        {
            foreach (var pipe in toChange)
            {
                try
                {
                    pipe.ChangeTypeId(targetType.Id);
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"Tubería {pipe.Id}: {ex.Message}");
                }
            }

            if (ok == 0)
            {
                t.RollBack();
                string msg = string.Join("\n", errors.Take(5));
                TaskDialog.Show("Cambiar Familia de Tubería", $"Falló en todas las tuberías, se revirtió la transacción:\n{msg}");
                return Result.Failed;
            }

            t.Commit();

            string resumen = ok == 1
                ? $"1 tubería cambiada de \"{sourceType.Name}\" a \"{targetType.Name}\"."
                : $"{ok} tuberías cambiadas de \"{sourceType.Name}\" a \"{targetType.Name}\".";

            if (failed > 0)
                resumen += $"\n⚠ {failed} tuberías fallaron:\n" + string.Join("\n", errors.Take(3));

            TaskDialog.Show("Cambiar Familia de Tubería", resumen);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            TaskDialog.Show("Cambiar Familia de Tubería", $"Falló, no se modificó el modelo (transacción revertida):\n{ex.Message}");
            return Result.Failed;
        }
    }
}
