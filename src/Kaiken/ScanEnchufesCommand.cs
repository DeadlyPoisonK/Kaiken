using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Paso 1: lee el DXF (una pasada), detecta la capa de enchufes, calibra contra el
/// Import, clasifica cada enchufe (letra cercana → auto por tipo → genérico) y
/// escribe config + los dos mapeos (letras y genérico) + un reporte con TODO lo que
/// no se pudo resolver, para que se creen las familias faltantes antes de colocar.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class ScanEnchufesCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Document doc = commandData.Application.ActiveUIDocument.Document;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Selecciona el DXF del plano de enchufes",
            Filter = "AutoCAD DXF (*.dxf)|*.dxf|Todos (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return Result.Cancelled;
        string dxfPath = dlg.FileName;

        DxfData data;
        try { data = EnchufesDxf.Read(dxfPath); }
        catch (Exception ex) { message = "No se pudo leer el DXF: " + ex.Message; return Result.Failed; }

        string cfgPath = EnchufesHelpers.ConfigPath(dxfPath);
        var cfg = File.Exists(cfgPath) ? EnchufesConfig.Load(cfgPath) : new EnchufesConfig();
        string targetLayer = cfg.DxfLayer;

        var byLayer = data.Inserts.GroupBy(b => b.Layer).OrderByDescending(g => g.Count()).ToList();
        var onLayer = data.Inserts.Where(b => string.Equals(b.Layer, targetLayer, StringComparison.OrdinalIgnoreCase)).ToList();
        string autoNote = "";
        if (onLayer.Count == 0)
        {
            var guess = byLayer.FirstOrDefault(g => g.Key.IndexOf("enc", StringComparison.OrdinalIgnoreCase) >= 0);
            if (guess != null) { targetLayer = guess.Key; onLayer = guess.ToList(); autoNote = "(auto-detectada)"; }
        }
        var textsOnLayer = data.Texts.Where(t => string.Equals(t.Layer, targetLayer, StringComparison.OrdinalIgnoreCase)).ToList();
        var symbols = EnchufesHelpers.GetElectricalFixtureSymbols(doc);

        // Calibración
        var import = EnchufesHelpers.FindImportInstance(doc);
        string diag = "No se encontró un Import en el modelo.";
        EnchufesConfig? derived = import != null
            ? EnchufesHelpers.DeriveConfigFromImport(import, data.FeetPerUnit, data.InsUnits, targetLayer, out diag)
            : null;
        var finalCfg = derived ?? cfg;
        finalCfg.DxfLayer = targetLayer; finalCfg.InsUnits = data.InsUnits;
        finalCfg.Save(cfgPath);

        // Mapeos: defaults + preservar existentes + agregar observados vacíos
        var letras = EnchufesHelpers.LoadMap(EnchufesHelpers.LetrasPath(dxfPath));
        foreach (var kv in EnchufesHelpers.DefaultLetras()) letras.TryAdd(kv.Key, kv.Value);
        var mapeo = EnchufesHelpers.LoadMap(EnchufesHelpers.MappingPath(dxfPath));
        foreach (var kv in EnchufesHelpers.DefaultMapeo()) mapeo.TryAdd(kv.Key, kv.Value);

        // Clasificar
        var results = onLayer.Select(e =>
            EnchufesHelpers.Classify(e, textsOnLayer, finalCfg, letras, mapeo, symbols)).ToList();

        // agregar códigos y bloques observados a los mapeos (vacíos) para visibilidad
        foreach (var code in results.Where(r => r.Kind == "letra").Select(r => r.Key).Distinct())
            letras.TryAdd(code, new MapEntry());
        foreach (var blk in results.Where(r => r.Kind == "generico").Select(r => r.Key).Distinct())
            mapeo.TryAdd(blk, new MapEntry());
        EnchufesHelpers.SaveMap(EnchufesHelpers.LetrasPath(dxfPath), letras);
        EnchufesHelpers.SaveMap(EnchufesHelpers.MappingPath(dxfPath), mapeo);

        int okLetra = results.Count(r => r.Kind == "letra" && r.Resolved);
        int okGen = results.Count(r => r.Kind == "generico" && r.Resolved);
        var unresolved = results.Where(r => !r.Resolved)
            .GroupBy(r => (r.Kind, r.Key)).OrderByDescending(g => g.Count()).ToList();

        // Reporte
        var sb = new StringBuilder();
        sb.AppendLine("=== ESCANEO DE ENCHUFES ===");
        sb.AppendLine($"DXF: {dxfPath}");
        sb.AppendLine($"$INSUNITS={data.InsUnits} feetPerUnit={data.FeetPerUnit:G6} | Capa: {targetLayer} {autoNote} -> {onLayer.Count} enchufes, {textsOnLayer.Count} textos");
        sb.AppendLine($"Calibración -> {diag}");
        sb.AppendLine();
        sb.AppendLine($"CLASIFICACIÓN: {okLetra} por letra + {okGen} genéricos = {okLetra + okGen} resueltos de {onLayer.Count}");
        sb.AppendLine();
        if (unresolved.Count > 0)
        {
            sb.AppendLine("*** NO RESUELTOS (crear/mapear familia antes de colocar) ***");
            foreach (var g in unresolved)
            {
                var pos = g.First().Insert;
                sb.AppendLine($"  {g.Count(),4}x  [{g.Key.Kind}] '{g.Key.Key}'  (ej. CAD X={pos.X:F1} Y={pos.Y:F1})");
            }
            sb.AppendLine();
        }
        sb.AppendLine("--- Códigos (letras) detectados junto a enchufes ---");
        foreach (var g in results.Where(r => r.Kind == "letra").GroupBy(r => r.Key).OrderByDescending(g => g.Count()))
            sb.AppendLine($"  {g.Count(),4}  '{g.Key}'  -> {(g.First().Resolved ? g.First().Symbol!.FamilyName : "SIN FAMILIA")}");
        sb.AppendLine();
        sb.AppendLine("--- Bloques genéricos (sin letra) ---");
        foreach (var g in results.Where(r => r.Kind == "generico").GroupBy(r => r.Key).OrderByDescending(g => g.Count()))
            sb.AppendLine($"  {g.Count(),4}  '{g.Key}'  -> {(g.First().Resolved ? g.First().Symbol!.FamilyName : "SIN MAPEO")}");
        sb.AppendLine();
        sb.AppendLine($"--- Familias 'Dispositivos Eléctricos' cargadas: {symbols.Count} ---");
        foreach (var s in symbols.OrderBy(s => s.FamilyName).ThenBy(s => s.Name))
            sb.AppendLine($"  [{s.FamilyName}] :: {s.Name}");

        string reportPath = Path.Combine(Path.GetDirectoryName(dxfPath)!, "enchufes_scan.txt");
        File.WriteAllText(reportPath, sb.ToString());

        TaskDialog.Show("Escanear Enchufes",
            $"{onLayer.Count} enchufes | resueltos: {okLetra + okGen} ({okLetra} por letra, {okGen} genéricos)\n" +
            $"No resueltos: {results.Count(r => !r.Resolved)}\n" +
            (derived != null ? "Calibración lista.\n" : "OJO: sin Import para calibrar.\n") +
            $"\nRevisa el reporte y los mapeos (enchufes_letras.json / enchufes_mapeo.json):\n{reportPath}");
        return Result.Succeeded;
    }
}
