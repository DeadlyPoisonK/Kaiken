using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace Kaiken;

/// <summary>Una habitación de un vínculo que se puede etiquetar en la vista, con su punto en coordenadas del anfitrión.</summary>
internal sealed record LinkedRoomCandidate(RevitLinkInstance Link, Room Room, XYZ Point);

/// <summary>Lo que hay de un vínculo en el nivel de la vista: habitaciones, cuántas caen en el recorte y cuántas ya tienen etiqueta.</summary>
internal sealed class LinkedRoomScan
{
    public required RevitLinkInstance Link { get; init; }
    public int OnLevel { get; set; }
    public int OutsideCrop { get; set; }
    public int AlreadyTagged { get; set; }
    public List<LinkedRoomCandidate> Untagged { get; } = new();
    /// <summary>Niveles del vínculo que coinciden con la cota de la vista, con cuántas habitaciones aporta cada uno.</summary>
    public Dictionary<string, int> Levels { get; } = new();

    public string Label => $"{LinkedRoomTagsHelpers.LinkFileName(Link)} — {Untagged.Count} sin etiqueta";
}

/// <summary>
/// Etiqueta habitaciones de vínculos en una planta, sin "Tag All Not Tagged".
///
/// Por qué: en Revit 2025.4, con el vínculo de INTERIORES del hospital, Tag All no encontraba
/// ninguna habitación (terminaba sin crear nada), aunque una por una sí se podían etiquetar. La
/// consulta de la API de "elementos del vínculo visibles en esta vista",
/// FilteredElementCollector(doc, view.Id, link.Id), tumbó Revit con ese mismo vínculo
/// (AccessViolation nativo, no capturable); seguramente Tag All usa lo mismo y falla en silencio.
/// Por eso aquí las habitaciones se leen del documento del vínculo y se filtran a mano: cota del
/// nivel de la vista y región de recorte. No cambiar esto por esa sobrecarga del collector.
/// </summary>
internal static class LinkedRoomTagsHelpers
{
    /// <summary>Diferencia de cota (pies) para considerar que un nivel del vínculo es el de la vista.</summary>
    private const double LevelTolerance = 0.5;

    public static string LinkFileName(RevitLinkInstance link)
    {
        // El nombre de la instancia es "archivo.rvt : n : location ...": basta el archivo.
        int i = link.Name.IndexOf(" : ", StringComparison.Ordinal);
        return i > 0 ? link.Name.Substring(0, i) : link.Name;
    }

    /// <summary>Vínculos cargados y visibles en la vista que tienen habitaciones en su nivel.</summary>
    public static List<LinkedRoomScan> Scan(Document doc, ViewPlan view, bool onlyInsideCrop)
    {
        double levelZ = view.GenLevel.ProjectElevation;
        BoundingBoxXYZ? crop = onlyInsideCrop && view.CropBoxActive ? view.CropBox : null;
        Transform? toCrop = crop?.Transform.Inverse;

        var tagged = new HashSet<(long link, long room)>(new FilteredElementCollector(doc, view.Id)
            .OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsNotElementType()
            .OfType<RoomTag>().Where(rt => rt.IsTaggingLink)
            .Select(rt => (rt.TaggedRoomId.LinkInstanceId.Value, rt.TaggedRoomId.LinkedElementId.Value)));

        var result = new List<LinkedRoomScan>();
        foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            if (link.IsHidden(view) || link.GetLinkDocument() is not { } linkDoc) continue;
            Transform tr = link.GetTotalTransform();
            var scan = new LinkedRoomScan { Link = link };

            foreach (var room in new FilteredElementCollector(linkDoc).OfCategory(BuiltInCategory.OST_Rooms).OfType<Room>())
            {
                // Sin colocar, redundantes o sin cerrar (área 0), y las de opciones de diseño: fuera.
                if (room.Area <= 0 || room.Location is not LocationPoint lp || room.Level == null || room.DesignOption != null) continue;
                if (Math.Abs(room.Level.ProjectElevation + tr.Origin.Z - levelZ) > LevelTolerance) continue;
                scan.OnLevel++;
                scan.Levels[room.Level.Name] = scan.Levels.TryGetValue(room.Level.Name, out int n) ? n + 1 : 1;

                XYZ p = tr.OfPoint(lp.Point);
                if (crop != null)
                {
                    XYZ q = toCrop!.OfPoint(p);
                    if (q.X < crop.Min.X || q.X > crop.Max.X || q.Y < crop.Min.Y || q.Y > crop.Max.Y) { scan.OutsideCrop++; continue; }
                }
                if (tagged.Contains((link.Id.Value, room.Id.Value))) { scan.AlreadyTagged++; continue; }
                scan.Untagged.Add(new LinkedRoomCandidate(link, room, p));
            }

            if (scan.OnLevel > 0) result.Add(scan);
        }
        return result.OrderByDescending(s => s.Untagged.Count).ThenBy(s => s.Link.Name).ToList();
    }

    /// <summary>Tipos de etiqueta de habitación del proyecto, por "Familia : Tipo".</summary>
    public static List<FamilySymbol> RoomTagTypes(Document doc) =>
        new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsElementType()
            .OfType<FamilySymbol>()
            .OrderBy(s => s.FamilyName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string TypeLabel(FamilySymbol s) => $"{s.FamilyName} : {s.Name}";

    /// <summary>Crea las etiquetas (dentro de una transacción abierta). Devuelve cuántas creó y los errores.</summary>
    public static (int created, List<string> errors) CreateTags(Document doc, ViewPlan view,
        IEnumerable<LinkedRoomCandidate> rooms, ElementId tagTypeId)
    {
        int created = 0;
        var errors = new List<string>();
        foreach (var c in rooms)
        {
            try
            {
                RoomTag? tag = doc.Create.NewRoomTag(new LinkElementId(c.Link.Id, c.Room.Id), new UV(c.Point.X, c.Point.Y), view.Id);
                if (tag == null) { errors.Add($"{c.Room.Number} {c.Room.Name}: Revit no creó la etiqueta"); continue; }
                if (tag.GetTypeId() != tagTypeId) tag.ChangeTypeId(tagTypeId);
                created++;
            }
            catch (Exception ex)
            {
                errors.Add($"{c.Room.Number} {c.Room.Name}: {ex.Message}");
            }
        }
        return (created, errors);
    }
}
