using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace Kaiken;

/// <summary>
/// Capas bloqueadas: personales, no del modelo. Se guardan en %APPDATA%\Kaiken\capas-bloqueadas.json
/// por modelo (el central, si tiene worksharing) y por UniqueId del filtro de la capa. Así bloquear
/// no toma prestado nada del central y no le quita la selección a los compañeros.
/// Bloquear no toca los elementos: el panel los saca de la selección (ver LayersPanel.OnSelectionChanged).
/// </summary>
internal static class LayerLocks
{
    private static string FilePath => Path.Combine(KaikenSettings.FolderPath, "capas-bloqueadas.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Identifica el modelo entre sesiones: la ruta del central, o la del archivo.</summary>
    public static string DocKey(Document doc)
    {
        try
        {
            if (doc.IsWorkshared && doc.GetWorksharingCentralModelPath() is { } central)
                return ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
        }
        catch { /* central no disponible: la ruta local sirve */ }
        return string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;
    }

    private static Dictionary<string, List<string>> ReadAll()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(FilePath))
                       ?? new();
        }
        catch (Exception ex) { App.BridgeLog($"capas: no se pudo leer {FilePath}: {ex.Message}"); }
        return new();
    }

    /// <summary>UniqueId de los filtros de capa bloqueados en este modelo (se lee del archivo cada vez: lo comparten el panel instalado y el dev).</summary>
    public static HashSet<string> Locked(string docKey) =>
        ReadAll().TryGetValue(docKey, out var ids) ? ids.ToHashSet() : new();

    public static void Set(string docKey, string filterUniqueId, bool locked)
    {
        var all = ReadAll();
        var ids = all.TryGetValue(docKey, out var list) ? list.ToHashSet() : new HashSet<string>();
        if (locked) ids.Add(filterUniqueId);
        else ids.Remove(filterUniqueId);
        if (ids.Count > 0) all[docKey] = ids.OrderBy(i => i).ToList();
        else all.Remove(docKey);
        Directory.CreateDirectory(KaikenSettings.FolderPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all, Json));
    }
}
