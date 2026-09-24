using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace Kaiken;

public class EnchufesConfig
{
    public string DxfLayer { get; set; } = ".El-ENC-Enchufes";
    public double Scale { get; set; } = 3.280839895;   // pies por unidad de dibujo
    public double OffsetX_ft { get; set; } = 0.0;
    public double OffsetY_ft { get; set; } = 0.0;
    public double ImportAngleDeg { get; set; } = 0.0;
    public bool UseAltura { get; set; } = true;
    public int InsUnits { get; set; } = 6;
    public double CodeRadius { get; set; } = 1.5;       // radio (unidades de dibujo) para asociar la letra

    public static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    public static EnchufesConfig Load(string path) =>
        JsonSerializer.Deserialize<EnchufesConfig>(File.ReadAllText(path)) ?? new EnchufesConfig();
}

public class MapEntry
{
    public string Family { get; set; } = "";
    public string Type { get; set; } = "";
    public bool IsEmpty => string.IsNullOrWhiteSpace(Family) && string.IsNullOrWhiteSpace(Type);
}

/// <summary>Resultado de clasificar un enchufe.</summary>
public class Classification
{
    public BlockInsert Insert = null!;
    public string Key = "";        // el código o el nombre de bloque usado
    public string Kind = "";       // "letra" | "generico"
    public FamilySymbol? Symbol;
    public string Note = "";       // ej. sustitución temporal
    public bool Resolved => Symbol != null;
}

public static class EnchufesHelpers
{
    public const double MetersToFeet = 3.280839895;
    public static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly Regex CodePattern = new(@"^[A-Za-zÁÉÍÓÚÑ]{1,3}\d{0,2}$", RegexOptions.Compiled);

    public static string ConfigPath(string dxf) => Path.Combine(Path.GetDirectoryName(dxf)!, "enchufes_config.json");
    public static string MappingPath(string dxf) => Path.Combine(Path.GetDirectoryName(dxf)!, "enchufes_mapeo.json");
    public static string LetrasPath(string dxf) => Path.Combine(Path.GetDirectoryName(dxf)!, "enchufes_letras.json");

    public static List<FamilySymbol> GetElectricalFixtureSymbols(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_ElectricalFixtures).Cast<FamilySymbol>().ToList();

    public static ImportInstance? FindImportInstance(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(ImportInstance))
            .Cast<ImportInstance>().FirstOrDefault();

    public static EnchufesConfig? DeriveConfigFromImport(ImportInstance import, double feetPerUnit,
        int insUnits, string dxfLayer, out string diag)
    {
        diag = "";
        var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Coarse };
        GeometryInstance? gi = import.get_Geometry(opt)?.OfType<GeometryInstance>().FirstOrDefault();
        if (gi == null) return null;
        Transform t = gi.Transform;
        double s = t.BasisX.GetLength();
        double angle = Math.Atan2(t.BasisX.Y, t.BasisX.X);
        double effScale = Math.Abs(s - 1.0) < 0.25 ? feetPerUnit * s : s;
        diag = $"Import: Origin=({t.Origin.X:F3},{t.Origin.Y:F3}) |BasisX|={s:F4} ángulo={angle * 180 / Math.PI:F3}° " +
               $"$INSUNITS={insUnits} feetPerUnit={feetPerUnit:G6} -> Scale={effScale:G6}";
        return new EnchufesConfig
        {
            DxfLayer = dxfLayer, Scale = effScale,
            OffsetX_ft = t.Origin.X, OffsetY_ft = t.Origin.Y,
            ImportAngleDeg = angle * 180.0 / Math.PI, UseAltura = true, InsUnits = insUnits,
        };
    }

    public static XYZ ToModel(BlockInsert b, EnchufesConfig cfg, double levelElevationFt)
    {
        double bx = cfg.Scale * b.X, by = cfg.Scale * b.Y, x, y;
        if (Math.Abs(cfg.ImportAngleDeg) > 1e-9)
        {
            double a = cfg.ImportAngleDeg * Math.PI / 180.0;
            x = cfg.OffsetX_ft + bx * Math.Cos(a) - by * Math.Sin(a);
            y = cfg.OffsetY_ft + bx * Math.Sin(a) + by * Math.Cos(a);
        }
        else { x = cfg.OffsetX_ft + bx; y = cfg.OffsetY_ft + by; }

        double z = levelElevationFt;
        if (cfg.UseAltura) { var alt = b.AlturaMeters(); if (alt.HasValue) z += alt.Value * MetersToFeet; }
        return new XYZ(x, y, z);
    }

    public static Level? ResolveLevel(Document doc, View view) =>
        view.GenLevel ?? new FilteredElementCollector(doc).OfClass(typeof(Level))
            .Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();

    // ---------- Clasificación ----------

    /// <summary>¿El texto es un código de especialización (no una altura/nombre de sala)?</summary>
    public static bool IsCode(string text, Dictionary<string, MapEntry> letras)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (letras.ContainsKey(text.Trim())) return true;
        return CodePattern.IsMatch(text.Trim());
    }

    /// <summary>Código más cercano (mismo criterio de capa) dentro del radio; null si no hay.</summary>
    public static string? NearestCode(BlockInsert e, List<DxfText> texts, double radius,
        Dictionary<string, MapEntry> letras)
    {
        string? best = null; double bd = radius;
        foreach (var t in texts)
        {
            if (!IsCode(t.Text, letras)) continue;
            double d = Math.Sqrt((t.X - e.X) * (t.X - e.X) + (t.Y - e.Y) * (t.Y - e.Y));
            if (d < bd) { bd = d; best = t.Text.Trim(); }
        }
        return best;
    }

    public static FamilySymbol? FindByFamilyType(string family, string type, List<FamilySymbol> symbols)
    {
        if (!string.IsNullOrWhiteSpace(family))
            return symbols.FirstOrDefault(s => s.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase)
                        && s.Name.Equals(type, StringComparison.OrdinalIgnoreCase))
                ?? symbols.FirstOrDefault(s => s.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(type))
            return symbols.FirstOrDefault(s => s.Name.Equals(type, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    /// <summary>Resuelve un código: override letras.json → auto por nombre de tipo/familia.</summary>
    public static FamilySymbol? ResolveCode(string code, Dictionary<string, MapEntry> letras, List<FamilySymbol> symbols)
    {
        if (letras.TryGetValue(code, out var e) && !e.IsEmpty)
            return FindByFamilyType(e.Family, e.Type, symbols);
        // auto por nombre de tipo, luego por nombre de familia
        return symbols.FirstOrDefault(s => s.Name.Equals(code, StringComparison.OrdinalIgnoreCase))
            ?? symbols.FirstOrDefault(s => s.FamilyName.Equals(code, StringComparison.OrdinalIgnoreCase));
    }

    public static Classification Classify(BlockInsert e, List<DxfText> texts, EnchufesConfig cfg,
        Dictionary<string, MapEntry> letras, Dictionary<string, MapEntry> mapeo, List<FamilySymbol> symbols)
    {
        var r = new Classification { Insert = e };
        string? code = NearestCode(e, texts, cfg.CodeRadius, letras);
        if (code != null)
        {
            r.Kind = "letra"; r.Key = code;
            r.Symbol = ResolveCode(code, letras, symbols);
            if (letras.TryGetValue(code, out var le) && !string.IsNullOrWhiteSpace(le.Type) && le.Type.StartsWith("[sust"))
                r.Note = le.Type;
            return r;
        }
        r.Kind = "generico"; r.Key = e.Block;
        if (mapeo.TryGetValue(e.Block, out var me) && !me.IsEmpty)
            r.Symbol = FindByFamilyType(me.Family, me.Type, symbols);
        return r;
    }

    // ---------- IO de mapeos ----------
    public static Dictionary<string, MapEntry> LoadMap(string path) =>
        File.Exists(path)
            ? (JsonSerializer.Deserialize<Dictionary<string, MapEntry>>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new())
            : new(StringComparer.OrdinalIgnoreCase);

    public static void SaveMap(string path, Dictionary<string, MapEntry> map) =>
        File.WriteAllText(path, JsonSerializer.Serialize(map, JsonOpts));

    /// <summary>Prerelleno de códigos que NO se auto-resuelven por nombre de tipo.</summary>
    public static Dictionary<string, MapEntry> DefaultLetras() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"]  = new MapEntry { Family = "ENCHUFE ASEO",                Type = "ENCHUFE ASEO 250W-220V." },
        ["N"]  = new MapEntry { Family = "ENCHUFE NEGATOSCOPIO",        Type = "ENCHUFE NEGATOSCOPIO 250W-220V. H 1.10m" },
        ["P"]  = new MapEntry { Family = "ENCHUFE PROYECTOR",           Type = "" },
        ["TV"] = new MapEntry { Family = "ENCHUFE TELEVISION",          Type = "ENCHUFE TELEVISION 250W-220V. H 2.20m" },
        ["H2"] = new MapEntry { Family = "ENCHUFE HERVIDOR ELECTRICO",  Type = "ENCHUFE HERVIDOR ELECTRICO 2.2kW-220V. H 1.10m" },
        ["MD"] = new MapEntry { Family = "ENCHUFE MONITOR DESFIBRILADOR", Type = "" },
        ["EE"] = new MapEntry { Family = "ENCHUFE ENCIMERA ELECTRICA",  Type = "" },
        ["ARR. DIAGNOSTICO MURAL"] = new MapEntry { Family = "ARRANQUE LV", Type = "[sustituto temporal: crear familia DIAGNOSTICO MURAL]" },
    };

    /// <summary>Prerelleno del mapeo genérico (bloques sin letra).</summary>
    public static Dictionary<string, MapEntry> DefaultMapeo() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enchufe Normal Doble"]        = new MapEntry { Family = "ENCHUFE ALUMBRADO DOBLE",   Type = "ENCHUFE ALUMBRADO DOBLE 10Amp/220V" },
        ["Enchufe Normal Simple"]       = new MapEntry { Family = "ENCHUFE ALUMBRADO SIMPLE",  Type = "ENCHUFE ALUMBRADO SIMPLE 10Amp/220V" },
        ["Enchufe Computacion Doble"]   = new MapEntry { Family = "ENCHUFE COMPUTACION DOBLE", Type = "ENCHUFE COMPUTACION DOBLE 16Amp/220V" },
        ["Enchufe Computacion Simple"]  = new MapEntry { Family = "ENCHUFE COMPUTACION SIMPLE", Type = "" },
        ["Enchufe Fuerza Simple"]       = new MapEntry { Family = "ENCHUFE FUERZA SIMPLE",     Type = "ENCHUFE FUERZA SIMPLE" },
        ["Enchufe Fuerza Doble"]        = new MapEntry { Family = "ENCHUFE FUERZA DOBLE",      Type = "ENCHUFE FUERZA DOBLE" },
        ["Enchufe Grado Medico Simple 1"] = new MapEntry { Family = "ENCHUFE GRADO MEDICO SIMPLE", Type = "ENCHUFE GRADO MEDICO SIMPLE 10-16A-220V" },
    };
}
