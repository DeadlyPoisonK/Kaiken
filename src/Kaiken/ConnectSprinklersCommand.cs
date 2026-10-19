using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Botón de cinta: conecta en lote los rociadores de la vista actual (de las Familias/Tipos
/// elegidos) a la tubería de incendio que pasa directamente sobre ellos. Los ya conectados se
/// descartan. Van uno por uno, cada uno en su propia transacción (si uno falla no arrastra a
/// los demás), con ventana de progreso y opción de detener; todo queda agrupado en un solo
/// Ctrl+Z. Los que no se pudieron conectar quedan marcados en morado en la vista.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ConnectSprinklersCommand : IExternalCommand
{
    private const string Title = "Conectar Rociadores";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;
        View view = doc.ActiveView;

        var sprinklers = ConnectSprinklersHelpers.GetSprinklersInView(doc, view);
        if (sprinklers.Count == 0)
        {
            TaskDialog.Show(Title, "No hay rociadores en la vista actual.");
            return Result.Cancelled;
        }

        var systemTypes = ConnectSprinklersHelpers.GetPipingSystemTypesInView(doc, view);
        if (systemTypes.Count == 0)
        {
            TaskDialog.Show(Title, "No hay tuberías en la vista actual a las que conectar.");
            return Result.Cancelled;
        }

        var win = new ConnectSprinklersWindow(sprinklers, systemTypes);
        if (win.ShowDialog() != true) return Result.Cancelled;

        var chosen = win.SelectedSprinklers;
        var pending = chosen.Where(s => !ConnectSprinklersHelpers.IsConnected(s)).Select(s => s.Id).ToList();
        int alreadyConnected = chosen.Count - pending.Count;

        // Limpia el morado de los que ya están conectados (p.ej. conectados a mano después de una
        // corrida anterior), en toda la vista y no solo en los tipos marcados.
        var connectedInView = sprinklers.Where(ConnectSprinklersHelpers.IsConnected).Select(s => s.Id).ToList();
        if (pending.Count == 0)
        {
            int cleared;
            using (var t = new Transaction(doc, "Limpiar marca de rociadores conectados"))
            {
                t.Start();
                cleared = ConnectSprinklersHelpers.Unmark(view, connectedInView);
                if (cleared > 0) t.Commit(); else t.RollBack();
            }
            TaskDialog.Show(Title, $"Los {chosen.Count} rociadores elegidos ya están conectados." +
                (cleared > 0 ? $"\nSe quitó la marca morada de {cleared}." : ""));
            return Result.Succeeded;
        }

        double tolerance = UnitUtils.ConvertToInternalUnits(win.ToleranceCm, UnitTypeId.Centimeters);
        double maxDistance = UnitUtils.ConvertToInternalUnits(win.MaxDistanceM, UnitTypeId.Meters);

        var counts = new Dictionary<SprinklerOutcome, int>();
        foreach (SprinklerOutcome o in Enum.GetValues(typeof(SprinklerOutcome))) counts[o] = 0;
        counts[SprinklerOutcome.AlreadyConnected] = alreadyConnected;

        var connectedIds = new List<ElementId>();
        var notConnectedIds = new List<ElementId>();
        var errors = new List<string>();
        var lifts = new List<double>();
        int processed = 0;
        int clearedMarks = 0;
        bool cancelled = false;

        var progress = new ConnectSprinklersProgressWindow(pending.Count);
        progress.Show();

        using var group = new TransactionGroup(doc, Title);
        group.Start();
        try
        {
            foreach (var id in pending)
            {
                if (progress.CancelRequested)
                {
                    cancelled = true;
                    break;
                }

                var outcome = ConnectOne(doc, id, win.SelectedSystemTypeIds, tolerance, maxDistance, out string detail, out double lift);
                counts[outcome]++;
                if (outcome is SprinklerOutcome.Connected or SprinklerOutcome.ConnectedOpposite)
                {
                    connectedIds.Add(id);
                    if (lift > 1e-6) lifts.Add(lift);
                }
                else if (outcome != SprinklerOutcome.AlreadyConnected) notConnectedIds.Add(id);
                if (outcome == SprinklerOutcome.Failed) errors.Add($"Rociador {id}: {detail}");

                processed++;
                progress.Report(processed, pending.Count,
                    $"Conectados: {connectedIds.Count}   ·   Omitidos: {notConnectedIds.Count}");
            }

            using (var t = new Transaction(doc, "Marcar rociadores sin conectar"))
            {
                t.Start();
                clearedMarks = ConnectSprinklersHelpers.Unmark(view, connectedIds.Concat(connectedInView));
                ConnectSprinklersHelpers.Mark(doc, view, notConnectedIds);
                t.Commit();
            }

            group.Assimilate();
        }
        catch (Exception ex)
        {
            if (group.HasStarted() && !group.HasEnded()) group.RollBack();
            progress.Finish();
            TaskDialog.Show(Title, $"Falló, se deshizo todo lo hecho por este comando:\n{ex.Message}");
            return Result.Failed;
        }

        progress.Finish();

        var lines = new List<string>
        {
            $"Conectados: {connectedIds.Count}",
            $"Ya estaban conectados (descartados): {counts[SprinklerOutcome.AlreadyConnected]}",
        };
        if (counts[SprinklerOutcome.ConnectedOpposite] > 0)
            lines.Add($"  De ellos, con la tubería al lado contrario de su conector (revisar si el tipo " +
                      $"colgante/montante es el correcto): {counts[SprinklerOutcome.ConnectedOpposite]}");
        if (lifts.Count > 0)
            lines.Add($"  De ellos, pegados a la tubería y conectados directo al accesorio, corridos en altura " +
                      $"hasta {UnitUtils.ConvertFromInternalUnits(lifts.Max(), UnitTypeId.Centimeters):0.#} cm: {lifts.Count}");
        if (counts[SprinklerOutcome.Misaligned] > 0)
            lines.Add($"Desfasados respecto al eje de la tubería: {counts[SprinklerOutcome.Misaligned]}");
        if (counts[SprinklerOutcome.NoPipeAbove] > 0)
            lines.Add($"Sin tubería del sistema elegido encima: {counts[SprinklerOutcome.NoPipeAbove]}");
        if (counts[SprinklerOutcome.NotVertical] > 0)
            lines.Add($"Con conector no vertical: {counts[SprinklerOutcome.NotVertical]}");
        if (counts[SprinklerOutcome.Failed] > 0)
            lines.Add($"Fallaron al conectar: {counts[SprinklerOutcome.Failed]}");
        if (clearedMarks > 0)
            lines.Add($"Marca morada quitada a {clearedMarks} ya conectados.");
        if (notConnectedIds.Count > 0)
            lines.Add($"\nLos {notConnectedIds.Count} no conectados quedaron marcados en morado en esta vista.");
        if (cancelled)
            lines.Add($"\nDetenido por el usuario: quedaron {pending.Count - processed} sin procesar.");
        if (errors.Count > 0)
            lines.Add("\n" + string.Join("\n", errors.Take(5)));

        TaskDialog.Show(Title, string.Join("\n", lines));

        if (notConnectedIds.Count > 0)
            uidoc.Selection.SetElementIds(notConnectedIds);

        return Result.Succeeded;
    }

    /// <summary>Un rociador = una transacción. Si no queda conectado, se deshace completa.</summary>
    private static SprinklerOutcome ConnectOne(
        Document doc, ElementId id, ISet<ElementId> systemTypeIds, double tolerance, double maxDistance,
        out string detail, out double lift)
    {
        detail = "";
        lift = 0;
        if (doc.GetElement(id) is not FamilyInstance sprinkler)
        {
            detail = "el elemento ya no existe";
            return SprinklerOutcome.Failed;
        }

        using var t = new Transaction(doc, "Conectar rociador");
        var failures = new SilentFailures();
        var options = t.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(failures);
        options.SetClearAfterRollback(true);
        t.SetFailureHandlingOptions(options);

        t.Start();
        SprinklerOutcome outcome;
        try
        {
            outcome = ConnectSprinklersHelpers.Connect(doc, sprinkler, systemTypeIds, tolerance, maxDistance, out detail, out lift);
        }
        catch (Exception ex)
        {
            outcome = SprinklerOutcome.Failed;
            detail = ex.Message;
        }

        if (outcome is not (SprinklerOutcome.Connected or SprinklerOutcome.ConnectedOpposite))
        {
            t.RollBack();
            return outcome;
        }

        if (t.Commit() != TransactionStatus.Committed)
        {
            detail = failures.LastError ?? "Revit rechazó la conexión";
            return SprinklerOutcome.Failed;
        }
        return outcome;
    }
}
