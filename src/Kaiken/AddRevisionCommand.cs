using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Combina en un solo paso lo que antes eran 3 acciones manuales:
/// "Modificar Revisión" + "Cambiar Fecha/REV" (parámetros de texto en las láminas)
/// y "04.1-REV to SHEET.dyn" (sincroniza la Revisión nativa de Revit a todas
/// las láminas vía GetAdditionalRevisionIds/SetAdditionalRevisionIds).
///
/// La sugerencia de próxima revisión se calcula leyendo la última Revisión
/// nativa ya existente en el documento (no hay valores hardcodeados).
/// </summary>
[Transaction(TransactionMode.Manual)]
public class AddRevisionCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Document doc = commandData.Application.ActiveUIDocument.Document;

        string suggestedRevision = SuggestNextRevision(doc);
        string suggestedDate = DateTime.Now.ToString("dd-MM-yyyy");
        var sheetSets = ExportHelpers.GetSheetSets(doc);

        var win = new AddRevisionWindow(suggestedRevision, suggestedDate, sheetSets.Select(s => s.Name));
        if (win.ShowDialog() != true)
        {
            return Result.Cancelled;
        }

        string revCode = win.RevisionCode;
        string dateText = win.RevisionDate;
        bool filterLength = win.FilterLength29;
        RevisionSettings cfg = KaikenSettings.Current.Revisions;

        var allSheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(s => !s.IsTemplate)
            .ToList();

        // Si se eligió un Sheet Set (Publish Set) específico, todo lo que sigue (parámetros
        // de texto y sincronización de la Revisión nativa) queda restringido a esas láminas,
        // para no pisar la revisión/fecha de otros sets que usan códigos distintos.
        var scopedSheets = ExportHelpers.FilterBySelectedSet(allSheets, sheetSets, win.SelectedSetName);
        if (scopedSheets == null)
        {
            TaskDialog.Show("Agregar Revisión", $"No se encontró el Sheet Set '{win.SelectedSetName}'.");
            return Result.Succeeded;
        }
        allSheets = scopedSheets;

        // Láminas como la portada usan el mismo parámetro de revisión pero su Número de
        // Plano es corto, así que el filtro de longitud las dejaría afuera; las listadas en
        // AlwaysIncludeSheets (settings.json) se incluyen siempre (si pertenecen al set elegido).
        var paramSheets = filterLength
            ? allSheets.Where(cfg.PassesLengthFilter).ToList()
            : allSheets;

        var errors = new List<string>();
        var warnings = new List<string>();
        int numParamUpdated = 0;
        int dateUpdatedCount = 0;
        int numSheetNumUpdated = 0;
        int revSyncedSheets = 0;

        using (Transaction tx = new Transaction(doc, "Agregar Revisión"))
        {
            tx.Start();

            // 1. Parámetros de texto en las láminas filtradas
            foreach (var sheet in paramSheets)
            {
                Parameter pRev = sheet.LookupParameter(cfg.RevisionParameter);
                if (pRev != null && !pRev.IsReadOnly)
                {
                    try { pRev.Set(revCode); numParamUpdated++; }
                    catch (Exception ex) { errors.Add($"Err {cfg.RevisionParameter} en {sheet.SheetNumber}: {ex.Message}"); }
                }

                Parameter pDate = sheet.LookupParameter(cfg.DateParameter);
                if (pDate != null && !pDate.IsReadOnly)
                {
                    try { pDate.Set(dateText); dateUpdatedCount++; }
                    catch (Exception ex) { errors.Add($"Err {cfg.DateParameter} en {sheet.SheetNumber}: {ex.Message}"); }
                }

                string currentNum = sheet.SheetNumber ?? "";
                string? newSheetNum = cfg.WithRevisionSegment(currentNum, revCode);
                if (newSheetNum != null && newSheetNum != currentNum)
                {
                    try { sheet.SheetNumber = newSheetNum; numSheetNumUpdated++; }
                    catch (Exception ex) { errors.Add($"Err SheetNumber en {currentNum}: {ex.Message}"); }
                }
            }

            // 2. Obtener o crear la Revisión nativa
            Revision newRev = GetOrCreateRevision(doc, revCode, dateText, warnings);

            // 3. Sincronizar TODAS las revisiones nativas a TODAS las láminas (igual que el .dyn)
            var allRevisionIds = new FilteredElementCollector(doc)
                .OfClass(typeof(Revision))
                .Select(r => r.Id)
                .ToList();

            foreach (var sheet in allSheets)
            {
                var current = new HashSet<ElementId>(sheet.GetAdditionalRevisionIds());
                bool changed = false;
                foreach (var rid in allRevisionIds)
                {
                    if (current.Add(rid)) changed = true;
                }
                if (changed)
                {
                    try { sheet.SetAdditionalRevisionIds(current.ToList()); revSyncedSheets++; }
                    catch (Exception ex) { errors.Add($"Err sincronizando revisiones en {sheet.SheetNumber}: {ex.Message}"); }
                }
            }

            tx.Commit();
        }

        string msg = $"Proceso finalizado.\n\n" +
                     $"• Aplicado a: {win.SelectedSetName}\n" +
                     $"• Láminas procesadas (parámetros): {paramSheets.Count}\n" +
                     $"• {cfg.RevisionParameter} actualizados: {numParamUpdated}\n" +
                     $"• {cfg.DateParameter} actualizados: {dateUpdatedCount}\n" +
                     $"• Números de plano (10º segmento) actualizados: {numSheetNumUpdated}\n" +
                     $"• Láminas sincronizadas con la Revisión nativa: {revSyncedSheets} de {allSheets.Count}";

        if (warnings.Count > 0)
        {
            msg += $"\n\nAvisos ({warnings.Count}):\n" + string.Join("\n", warnings);
        }

        if (errors.Count > 0)
        {
            msg += $"\n\nObservaciones ({errors.Count}):\n" + string.Join("\n", errors.Take(5));
        }

        TaskDialog.Show("Agregar Revisión - Resultado", msg);
        return Result.Succeeded;
    }

    /// <summary>
    /// Busca una Revisión nativa existente cuyo número ya coincida; si no existe, la crea.
    /// En Revit 2022+ el texto de la revisión (ej. "RE") no se escribe directo en el objeto
    /// Revision: lo calcula la RevisionNumberingSequence asignada, tomando el siguiente valor
    /// de su lista alfanumérica (Prefix + valor de la lista + Suffix). Este método reutiliza la
    /// misma secuencia que ya usan las revisiones existentes del documento y, si el valor pedido
    /// todavía no está en esa lista, lo agrega automáticamente (equivalente a escribirlo a mano en
    /// Administrar > Revisiones > Numeración) — así no hace falta hardcodear ni tocar la UI para
    /// futuras revisiones.
    /// </summary>
    private static Revision GetOrCreateRevision(Document doc, string revisionText, string dateText, List<string> warnings)
    {
        string target = (revisionText ?? "").Trim();

        var existing = new FilteredElementCollector(doc)
            .OfClass(typeof(Revision))
            .Cast<Revision>()
            .FirstOrDefault(r => string.Equals((r.RevisionNumber ?? "").Trim(), target, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.RevisionDate = dateText;
            existing.Description = "";
            return existing;
        }

        // Reutiliza la secuencia de numeración de la última revisión existente (si hay),
        // o la primera secuencia alfanumérica del documento como respaldo.
        RevisionNumberingSequence? seq = null;
        var lastRevision = Revision.GetAllRevisionIds(doc)
            .Select(id => doc.GetElement(id) as Revision)
            .LastOrDefault(r => r != null);
        if (lastRevision != null)
        {
            seq = doc.GetElement(lastRevision.RevisionNumberingSequenceId) as RevisionNumberingSequence;
        }
        if (seq == null)
        {
            seq = RevisionNumberingSequence.GetAllRevisionNumberingSequences(doc)
                .Select(id => doc.GetElement(id) as RevisionNumberingSequence)
                .FirstOrDefault(s => s != null && s.NumberType == RevisionNumberType.Alphanumeric);
        }

        Revision rev = Revision.Create(doc);
        rev.Description = "";
        rev.RevisionDate = dateText;

        if (seq != null)
        {
            rev.RevisionNumberingSequenceId = seq.Id;

            if (seq.NumberType == RevisionNumberType.Alphanumeric)
            {
                try
                {
                    AlphanumericRevisionSettings settings = seq.GetAlphanumericRevisionSettings();
                    string prefix = settings.Prefix ?? "";
                    string suffix = settings.Suffix ?? "";

                    string core = target;
                    if (prefix.Length > 0 && core.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        core = core.Substring(prefix.Length);
                    if (suffix.Length > 0 && core.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        core = core.Substring(0, core.Length - suffix.Length);

                    IList<string> sequenceList = settings.GetSequence();
                    if (!sequenceList.Any(v => string.Equals(v, core, StringComparison.OrdinalIgnoreCase)))
                    {
                        sequenceList.Add(core);
                        settings.SetSequence(sequenceList);
                        seq.SetAlphanumericRevisionSettings(settings);
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"No se pudo extender la lista de numeración alfanumérica para '{target}': {ex.Message}");
                }
            }
        }
        else
        {
            warnings.Add("No se encontró ninguna secuencia de numeración de revisiones en el documento; Revit asignó el número por defecto.");
        }

        string actual = (rev.RevisionNumber ?? "").Trim();
        if (!string.Equals(actual, target, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Revit asignó el número '{actual}' a la nueva Revisión (en vez de '{target}'). " +
                         "Revísalo en Administrar > Revisiones y, si hace falta, ajusta la lista de numeración alfanumérica.");
        }

        return rev;
    }

    /// <summary>
    /// Sugiere la próxima revisión leyendo la última Revisión nativa existente en el documento
    /// e incrementando su última letra (ej. "RD" -> "RE"). No hay valores hardcodeados: si no hay
    /// revisiones previas o el patrón no es reconocible, devuelve cadena vacía para que el usuario
    /// la escriba a mano.
    /// </summary>
    private static string SuggestNextRevision(Document doc)
    {
        try
        {
            var orderedIds = Revision.GetAllRevisionIds(doc);
            Revision last = orderedIds
                .Select(id => doc.GetElement(id) as Revision)
                .LastOrDefault(r => r != null && !string.IsNullOrWhiteSpace(r.RevisionNumber));

            if (last == null) return "";

            string num = last.RevisionNumber.Trim();
            char lastChar = num[num.Length - 1];
            if (char.IsLetter(lastChar) && char.ToUpperInvariant(lastChar) != 'Z')
            {
                char next = (char)(lastChar + 1);
                return num.Substring(0, num.Length - 1) + next;
            }
            return num;
        }
        catch
        {
            return "";
        }
    }
}
