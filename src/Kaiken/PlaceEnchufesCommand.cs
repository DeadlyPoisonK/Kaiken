using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Paso 2: coloca una familia por cada enchufe del DXF. Clasifica por letra cercana
/// (auto por nombre de tipo, u override en enchufes_letras.json) o, si no hay letra,
/// por bloque genérico (enchufes_mapeo.json). Posición, rotación y altura salen del
/// DXF. Al final reporta por escrito todo lo que no se pudo resolver.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class PlaceEnchufesCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Document doc = commandData.Application.ActiveUIDocument.Document;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Selecciona el DXF (con config y mapeos al lado)",
            Filter = "AutoCAD DXF (*.dxf)|*.dxf|Todos (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return Result.Cancelled;
        string dxfPath = dlg.FileName;

        string cfgPath = EnchufesHelpers.ConfigPath(dxfPath);
        if (!File.Exists(cfgPath))
        {
            TaskDialog.Show("Colocar Enchufes", "Falta enchufes_config.json. Corre primero 'Escanear Enchufes'.");
            return Result.Cancelled;
        }
        var cfg = EnchufesConfig.Load(cfgPath);
        var letras = EnchufesHelpers.LoadMap(EnchufesHelpers.LetrasPath(dxfPath));
        var mapeo = EnchufesHelpers.LoadMap(EnchufesHelpers.MappingPath(dxfPath));

        DxfData data;
        try { data = EnchufesDxf.Read(dxfPath); }
        catch (Exception ex) { message = "No se pudo leer el DXF: " + ex.Message; return Result.Failed; }

        var onLayer = data.Inserts.Where(b => string.Equals(b.Layer, cfg.DxfLayer, StringComparison.OrdinalIgnoreCase)).ToList();
        var textsOnLayer = data.Texts.Where(t => string.Equals(t.Layer, cfg.DxfLayer, StringComparison.OrdinalIgnoreCase)).ToList();
        if (onLayer.Count == 0)
        {
            TaskDialog.Show("Colocar Enchufes", $"No hay enchufes en la capa '{cfg.DxfLayer}'.");
            return Result.Cancelled;
        }

        Level? level = EnchufesHelpers.ResolveLevel(doc, doc.ActiveView);
        if (level == null) { message = "No se encontró un nivel."; return Result.Failed; }

        var symbols = EnchufesHelpers.GetElectricalFixtureSymbols(doc);
        var results = onLayer.Select(e => EnchufesHelpers.Classify(e, textsOnLayer, cfg, letras, mapeo, symbols)).ToList();

        int placed = 0, failed = 0;
        string? firstError = null;

        using (var tx = new Transaction(doc, "Colocar Enchufes"))
        {
            tx.Start();
            foreach (var r in results.Where(r => r.Resolved))
            {
                try
                {
                    var sym = r.Symbol!;
                    if (!sym.IsActive) sym.Activate();
                    XYZ p = EnchufesHelpers.ToModel(r.Insert, cfg, level.Elevation);
                    var inst = doc.Create.NewFamilyInstance(p, sym, level, StructuralType.NonStructural);
                    double rot = (r.Insert.RotationDeg + cfg.ImportAngleDeg) * Math.PI / 180.0;
                    if (Math.Abs(rot) > 1e-9)
                        ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateBound(p, p + XYZ.BasisZ), rot);
                    placed++;
                }
                catch (Exception ex) { failed++; firstError ??= $"{r.Key}: {ex.Message}"; }
            }
            tx.Commit();
        }

        var unresolved = results.Where(r => !r.Resolved)
            .GroupBy(r => (r.Kind, r.Key)).OrderByDescending(g => g.Count()).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Colocados: {placed} / {onLayer.Count}");
        if (failed > 0) sb.AppendLine($"Fallidos al insertar: {failed}  (primer error: {firstError})");
        sb.AppendLine();
        if (unresolved.Count > 0)
        {
            sb.AppendLine("*** NO COLOCADOS — falta crear/mapear la familia ***");
            foreach (var g in unresolved)
            {
                sb.AppendLine($"  {g.Count(),4}x  [{g.Key.Kind}] '{g.Key.Key}'");
                foreach (var r in g.Take(3))
                    sb.AppendLine($"          CAD X={r.Insert.X:F1} Y={r.Insert.Y:F1}  altura={r.Insert.AlturaMeters()?.ToString("0.00") ?? "-"}m");
            }
            sb.AppendLine();
            sb.AppendLine("Acción: crea esas familias en el modelo (o edita enchufes_letras.json /");
            sb.AppendLine("enchufes_mapeo.json para apuntar a una existente) y vuelve a colocar.");
        }
        else sb.AppendLine("Todos los enchufes se resolvieron y colocaron.");

        string reportPath = Path.Combine(Path.GetDirectoryName(dxfPath)!, "enchufes_colocacion.txt");
        File.WriteAllText(reportPath, sb.ToString());
        TaskDialog.Show("Colocar Enchufes", sb.ToString() + $"\n\nReporte: {reportPath}");
        return Result.Succeeded;
    }
}
