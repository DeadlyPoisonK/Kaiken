using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Una room encontrada, del modelo actual o de un vínculo. Todas las coordenadas
/// (Point, BoxMin/BoxMax, LevelElevation) ya están en coordenadas del modelo actual.
/// </summary>
public sealed class RoomHit
{
    public Room Room { get; init; } = null!;
    public RevitLinkInstance? Link { get; init; }

    public string Number { get; init; } = "";
    public string Name { get; init; } = "";
    public string LevelName { get; init; } = "";
    public string Source { get; init; } = "";

    public XYZ Point { get; init; } = XYZ.Zero;
    public XYZ? BoxMin { get; init; }
    public XYZ? BoxMax { get; init; }
    public double LevelElevation { get; init; }

    /// <summary>Etiquetas colocadas en el modelo actual (de rooms propias o de vínculos).</summary>
    public List<RoomTag> HostTags { get; } = new();
    /// <summary>Textos de todas las etiquetas (actuales y del vínculo), para buscar.</summary>
    public List<string> TagTexts { get; } = new();
    /// <summary>Vistas del vínculo donde la room está etiquetada (solo informativo: no se puede abrir una vista de un vínculo).</summary>
    public List<string> LinkedTagViews { get; } = new();

    public bool IsLinked => Link != null;

    public string TagInfo
    {
        get
        {
            var parts = new List<string>();
            if (HostTags.Count > 0) parts.Add($"{HostTags.Count} aquí");
            if (LinkedTagViews.Count > 0) parts.Add($"{LinkedTagViews.Count} en vínculo");
            return parts.Count == 0 ? "—" : string.Join(", ", parts);
        }
    }

    /// <summary>Texto normalizado (sin tildes, minúsculas) contra el que se busca.</summary>
    public string Haystack { get; set; } = "";
}

/// <summary>Una vista del modelo actual a la que se puede ir para ver la room.</summary>
public sealed class RoomViewOption
{
    public View View { get; init; } = null!;
    public string Label { get; init; } = "";
    public override string ToString() => Label;
}

public static class FindRoomHelpers
{
    private const double LevelTolerance = 0.3; // pies (~9 cm)

    /// <summary>
    /// Junta las rooms colocadas del modelo actual y de todos los vínculos cargados, con
    /// los textos de sus etiquetas (las del modelo actual y las de dentro de cada vínculo).
    /// </summary>
    public static List<RoomHit> CollectRooms(Document doc)
    {
        var hits = new List<RoomHit>();

        // Etiquetas del modelo actual, indexadas por (vínculo, room). Para rooms propias el vínculo es InvalidElementId.
        var hostTags = new Dictionary<(ElementId link, ElementId room), List<RoomTag>>();
        foreach (var tag in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_RoomTags)
                     .WhereElementIsNotElementType().OfType<RoomTag>())
        {
            if (tag.IsOrphaned) continue;
            LinkElementId tagged;
            try { tagged = tag.TaggedRoomId; } catch { continue; }
            if (tagged == null) continue;

            var key = tagged.LinkInstanceId == ElementId.InvalidElementId
                ? (ElementId.InvalidElementId, tagged.HostElementId)
                : (tagged.LinkInstanceId, tagged.LinkedElementId);
            if (!hostTags.TryGetValue(key, out var list)) hostTags[key] = list = new List<RoomTag>();
            list.Add(tag);
        }

        foreach (var room in PlacedRooms(doc))
            hits.Add(BuildHit(doc, room, null, Transform.Identity, "Modelo actual", hostTags, null));

        // Etiquetas dentro de cada documento vinculado (se calculan una vez por documento, no por instancia).
        var linkedTagsByDoc = new Dictionary<string, Dictionary<ElementId, List<(string text, string view)>>>();

        foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            Document? linkDoc;
            try { linkDoc = link.GetLinkDocument(); } catch { linkDoc = null; }
            if (linkDoc == null) continue; // vínculo descargado

            string docKey = linkDoc.PathName is { Length: > 0 } p ? p : linkDoc.Title;
            if (!linkedTagsByDoc.TryGetValue(docKey, out var linkedTags))
                linkedTagsByDoc[docKey] = linkedTags = CollectLinkedTags(linkDoc);

            Transform tf = link.GetTotalTransform();
            string source = link.Name;

            foreach (var room in PlacedRooms(linkDoc))
                hits.Add(BuildHit(doc, room, link, tf, source, hostTags, linkedTags));
        }

        return hits
            .OrderBy(h => h.Source == "Modelo actual" ? 0 : 1)
            .ThenBy(h => h.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.LevelElevation)
            .ThenBy(h => h.Number, NaturalComparer.Instance)
            .ToList();
    }

    private static IEnumerable<Room> PlacedRooms(Document d) =>
        new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType().OfType<Room>()
            .Where(r => r.Location != null && r.Area > 0);

    private static Dictionary<ElementId, List<(string text, string view)>> CollectLinkedTags(Document linkDoc)
    {
        var result = new Dictionary<ElementId, List<(string, string)>>();
        foreach (var tag in new FilteredElementCollector(linkDoc).OfCategory(BuiltInCategory.OST_RoomTags)
                     .WhereElementIsNotElementType().OfType<RoomTag>())
        {
            if (tag.IsOrphaned) continue;
            ElementId roomId;
            try { roomId = tag.TaggedLocalRoomId; } catch { continue; }
            if (roomId == null || roomId == ElementId.InvalidElementId) continue;

            string viewName = linkDoc.GetElement(tag.OwnerViewId) is View v ? v.Name : "";
            if (!result.TryGetValue(roomId, out var list)) result[roomId] = list = new List<(string, string)>();
            list.Add((SafeTagText(tag), viewName));
        }
        return result;
    }

    private static RoomHit BuildHit(
        Document doc, Room room, RevitLinkInstance? link, Transform tf, string source,
        Dictionary<(ElementId, ElementId), List<RoomTag>> hostTags,
        Dictionary<ElementId, List<(string text, string view)>>? linkedTags)
    {
        XYZ local = (room.Location as LocationPoint)?.Point ?? XYZ.Zero;

        XYZ? min = null, max = null;
        if (room.get_BoundingBox(null) is BoundingBoxXYZ bb)
            (min, max) = TransformBox(bb.Min, bb.Max, tf);

        double levelElev = room.Level != null
            ? tf.OfPoint(new XYZ(local.X, local.Y, room.Level.ProjectElevation)).Z
            : tf.OfPoint(local).Z;

        var hit = new RoomHit
        {
            Room = room,
            Link = link,
            Number = room.Number ?? "",
            Name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "",
            LevelName = room.Level?.Name ?? "",
            Source = source,
            Point = tf.OfPoint(local),
            BoxMin = min,
            BoxMax = max,
            LevelElevation = levelElev,
        };

        var key = (link?.Id ?? ElementId.InvalidElementId, room.Id);
        if (hostTags.TryGetValue(key, out var tags))
        {
            hit.HostTags.AddRange(tags);
            hit.TagTexts.AddRange(tags.Select(SafeTagText));
        }
        if (linkedTags != null && linkedTags.TryGetValue(room.Id, out var lt))
        {
            hit.TagTexts.AddRange(lt.Select(t => t.text));
            hit.LinkedTagViews.AddRange(lt.Select(t => t.view).Where(v => v.Length > 0).Distinct());
        }

        hit.Haystack = Normalize(string.Join(" ",
            new[] { hit.Number, hit.Name, hit.LevelName }.Concat(hit.TagTexts)));
        return hit;
    }

    private static string SafeTagText(RoomTag tag)
    {
        try { return tag.TagText ?? ""; } catch { return ""; }
    }

    /// <summary>Todas las palabras de la búsqueda deben aparecer (sin importar tildes ni mayúsculas).</summary>
    public static bool Matches(RoomHit hit, string query)
    {
        var terms = Normalize(query).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return terms.All(t => hit.Haystack.Contains(t, StringComparison.Ordinal));
    }

    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString().Replace('\r', ' ').Replace('\n', ' ');
    }

    /// <summary>
    /// Vistas del modelo actual donde ver la room: primero las que tienen su etiqueta,
    /// luego las plantas (de piso y de cielo) del nivel donde está la room.
    /// </summary>
    public static List<RoomViewOption> CandidateViews(Document doc, RoomHit hit)
    {
        var options = new List<RoomViewOption>();
        var seen = new HashSet<ElementId>();

        foreach (var tag in hit.HostTags)
        {
            if (doc.GetElement(tag.OwnerViewId) is not View v || v.IsTemplate || !seen.Add(v.Id)) continue;
            options.Add(new RoomViewOption { View = v, Label = $"{v.Name}  (con etiqueta)" });
        }

        var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .Where(v => !v.IsTemplate && v.GenLevel != null
                        && (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan))
            .ToList();

        // El nivel del modelo actual que corresponde a la room: el mismo (con tolerancia) o, si no hay,
        // el más alto por debajo de ella (útil cuando los niveles del vínculo no coinciden con los del host).
        var levelElevs = plans.Select(v => v.GenLevel.ProjectElevation).Distinct().ToList();
        double? target = levelElevs.Where(e => Math.Abs(e - hit.LevelElevation) < LevelTolerance)
                             .Select(e => (double?)e).FirstOrDefault()
                         ?? levelElevs.Where(e => e <= hit.Point.Z + LevelTolerance)
                             .Select(e => (double?)e).DefaultIfEmpty(null).Max();
        if (target == null) return options;

        var onLevel = plans
            .Where(v => Math.Abs(v.GenLevel.ProjectElevation - target.Value) < 1e-6 && seen.Add(v.Id))
            .Select(v => (view: v, inCrop: CropContains(v, hit.Point)))
            .Where(x => x.inCrop != false)
            .OrderBy(x => x.view.ViewType == ViewType.FloorPlan ? 0 : 1)
            .ThenBy(x => x.view.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var (v, _) in onLevel)
        {
            string kind = v.ViewType == ViewType.FloorPlan ? "planta" : "cielo";
            options.Add(new RoomViewOption { View = v, Label = $"{v.Name}  ({kind}, {v.GenLevel.Name})" });
        }
        return options;
    }

    /// <summary>null si la vista no recorta; true/false si el punto cae dentro del recorte en planta.</summary>
    private static bool? CropContains(View v, XYZ p)
    {
        try
        {
            if (!v.CropBoxActive) return null;
            BoundingBoxXYZ crop = v.CropBox;
            XYZ q = crop.Transform.Inverse.OfPoint(p);
            return q.X >= crop.Min.X && q.X <= crop.Max.X && q.Y >= crop.Min.Y && q.Y <= crop.Max.Y;
        }
        catch { return null; }
    }

    /// <summary>Hace zoom a la room en la vista y la deja seleccionada (junto con sus etiquetas de esa vista).</summary>
    public static string? ZoomAndSelect(UIDocument uidoc, View view, RoomHit hit)
    {
        UIView? uiView = uidoc.GetOpenUIViews().FirstOrDefault(u => u.ViewId == view.Id);
        if (uiView == null) return "La vista no está abierta.";

        if (hit.BoxMin != null && hit.BoxMax != null)
        {
            XYZ size = hit.BoxMax - hit.BoxMin;
            double margin = Math.Max(Math.Max(size.X, size.Y) * 0.35, 3.0); // al menos ~1 m alrededor
            var pad = new XYZ(margin, margin, 0);
            uiView.ZoomAndCenterRectangle(hit.BoxMin - pad, hit.BoxMax + pad);
        }
        else
        {
            var pad = new XYZ(10, 10, 0);
            uiView.ZoomAndCenterRectangle(hit.Point - pad, hit.Point + pad);
        }

        var tagsHere = hit.HostTags.Where(t => t.OwnerViewId == view.Id).ToList();
        try
        {
            if (hit.Link == null)
            {
                var ids = new List<ElementId> { hit.Room.Id };
                ids.AddRange(tagsHere.Select(t => t.Id));
                uidoc.Selection.SetElementIds(ids);
            }
            else
            {
                var refs = new List<Reference> { new Reference(hit.Room).CreateLinkReference(hit.Link) };
                refs.AddRange(tagsHere.Select(t => new Reference(t)));
                uidoc.Selection.SetReferences(refs);
            }
        }
        catch
        {
            // Si la room vinculada no se puede seleccionar, al menos quedan seleccionadas sus etiquetas.
            try { uidoc.Selection.SetElementIds(tagsHere.Select(t => t.Id).ToList()); } catch { }
        }
        return null;
    }

    private static (XYZ min, XYZ max) TransformBox(XYZ a, XYZ b, Transform tf)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (double x in new[] { a.X, b.X })
        foreach (double y in new[] { a.Y, b.Y })
        foreach (double z in new[] { a.Z, b.Z })
        {
            XYZ p = tf.OfPoint(new XYZ(x, y, z));
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); minZ = Math.Min(minZ, p.Z);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); maxZ = Math.Max(maxZ, p.Z);
        }
        return (new XYZ(minX, minY, minZ), new XYZ(maxX, maxY, maxZ));
    }

    /// <summary>Ordena "2" antes que "10" y "A-2" antes que "A-10".</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            x ??= ""; y ??= "";
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    string nx = x[si..i].TrimStart('0'), ny = y[sj..j].TrimStart('0');
                    int c = nx.Length != ny.Length ? nx.Length.CompareTo(ny.Length) : string.CompareOrdinal(nx, ny);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
