using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace Kaiken;

internal enum ClashStatus
{
    Pending = 0,
    /// <summary>Marcada a mano como resuelta (Re-verificar no la toca).</summary>
    Resolved = 1,
    /// <summary>Re-verificar comprobó que ya no chocan (si vuelven a chocar, vuelve a pendiente).</summary>
    Gone = 2,
}

/// <summary>Resultado de una revisión tal como queda guardado en la vista.</summary>
internal sealed class ClashData
{
    public string RunId = "";
    public double ToleranceFt;
    /// <summary>Triángulos del volumen rojo (3 vértices cada uno, girados hacia afuera).</summary>
    public List<XYZ> Vertices = new();
    public List<ClashItem> Items = new();
}

/// <summary>Un choque guardado: los triángulos [TriStart, TriStart + TriCount) son su volumen.</summary>
internal sealed class ClashItem
{
    public ElementId AId = ElementId.InvalidElementId;
    public ElementId BId = ElementId.InvalidElementId;
    public ElementId LinkId = ElementId.InvalidElementId;
    public string ALabel = "", BLabel = "", Source = "";
    public XYZ Min = XYZ.Zero, Max = XYZ.Zero;
    public int TriStart, TriCount;
    public ClashStatus Status;
}

/// <summary>
/// Guarda el resultado de Interferencias en la propia vista (Extensible Storage): viaja con
/// el modelo y con el central, y al renombrar la vista el resultado queda "archivado" porque
/// la próxima revisión busca la vista por su nombre por defecto y crea otra.
/// </summary>
internal static class ClashStorage
{
    // v2: agrega B, vínculo, estado y tolerancia (los esquemas de Revit no se pueden modificar).
    private static readonly Guid SchemaGuid = new("C4D2A913-58E7-4B6F-A0D3-1F9E7B2C6A40");

    private const string FRunId = "RunId", FTol = "ToleranceFt", FVerts = "Vertices", FTriStart = "TriStart", FTriCount = "TriCount",
        FA = "AIds", FB = "BIds", FLink = "LinkIds", FStatus = "Status",
        FALabel = "ALabels", FBLabel = "BLabels", FSource = "Sources", FMin = "Mins", FMax = "Maxs";

    private static Schema GetSchema()
    {
        Schema? s = Schema.Lookup(SchemaGuid);
        if (s != null) return s;

        var b = new SchemaBuilder(SchemaGuid);
        b.SetSchemaName("KaikenInterferencias2");
        b.SetReadAccessLevel(AccessLevel.Public);
        b.SetWriteAccessLevel(AccessLevel.Public);
        b.AddSimpleField(FRunId, typeof(string));
        b.AddSimpleField(FTol, typeof(double)).SetSpec(SpecTypeId.Length);
        b.AddArrayField(FVerts, typeof(XYZ)).SetSpec(SpecTypeId.Length);
        b.AddArrayField(FTriStart, typeof(int));
        b.AddArrayField(FTriCount, typeof(int));
        b.AddArrayField(FA, typeof(ElementId));
        b.AddArrayField(FB, typeof(ElementId));
        b.AddArrayField(FLink, typeof(ElementId));
        b.AddArrayField(FStatus, typeof(int));
        b.AddArrayField(FALabel, typeof(string));
        b.AddArrayField(FBLabel, typeof(string));
        b.AddArrayField(FSource, typeof(string));
        b.AddArrayField(FMin, typeof(XYZ)).SetSpec(SpecTypeId.Length);
        b.AddArrayField(FMax, typeof(XYZ)).SetSpec(SpecTypeId.Length);
        return b.Finish();
    }

    /// <summary>Id de la revisión guardada en la vista, o null si no tiene. Barato: se usa en cada redibujo.</summary>
    public static string? RunIdOf(View view)
    {
        Schema? s = Schema.Lookup(SchemaGuid);
        if (s == null) return null;
        Entity e = view.GetEntity(s);
        return e.IsValid() ? e.Get<string>(FRunId) : null;
    }

    public static bool HasData(View view) => RunIdOf(view) != null;

    /// <summary>Convierte los choques de una revisión a datos guardables (triángulos + lista).</summary>
    public static ClashData FromResults(List<ClashResult> clashes, double toleranceFt)
    {
        var data = new ClashData { ToleranceFt = toleranceFt };
        foreach (var c in clashes)
        {
            int start = data.Vertices.Count / 3;
            foreach (var s in c.Solids) Triangulate(s, data.Vertices);
            data.Items.Add(new ClashItem
            {
                AId = c.AId, BId = c.BId, LinkId = c.LinkId,
                ALabel = c.ALabel, BLabel = c.BLabel, Source = c.Source,
                Min = c.Min, Max = c.Max, TriStart = start, TriCount = data.Vertices.Count / 3 - start,
            });
        }
        return data;
    }

    /// <summary>Guarda los datos en la vista con un RunId nuevo (el dibujo se rehace). Requiere transacción abierta.</summary>
    public static void Write(View view, ClashData data)
    {
        data.RunId = Guid.NewGuid().ToString();
        var e = new Entity(GetSchema());
        e.Set(FRunId, data.RunId);
        e.Set(FTol, data.ToleranceFt, UnitTypeId.Feet);
        e.Set<IList<XYZ>>(FVerts, data.Vertices, UnitTypeId.Feet);
        e.Set<IList<int>>(FTriStart, data.Items.Select(i => i.TriStart).ToList());
        e.Set<IList<int>>(FTriCount, data.Items.Select(i => i.TriCount).ToList());
        e.Set<IList<ElementId>>(FA, data.Items.Select(i => i.AId).ToList());
        e.Set<IList<ElementId>>(FB, data.Items.Select(i => i.BId).ToList());
        e.Set<IList<ElementId>>(FLink, data.Items.Select(i => i.LinkId).ToList());
        e.Set<IList<int>>(FStatus, data.Items.Select(i => (int)i.Status).ToList());
        e.Set<IList<string>>(FALabel, data.Items.Select(i => i.ALabel).ToList());
        e.Set<IList<string>>(FBLabel, data.Items.Select(i => i.BLabel).ToList());
        e.Set<IList<string>>(FSource, data.Items.Select(i => i.Source).ToList());
        e.Set<IList<XYZ>>(FMin, data.Items.Select(i => i.Min).ToList(), UnitTypeId.Feet);
        e.Set<IList<XYZ>>(FMax, data.Items.Select(i => i.Max).ToList(), UnitTypeId.Feet);
        view.SetEntity(e);
    }

    public static ClashData? Read(View view)
    {
        Schema? s = Schema.Lookup(SchemaGuid);
        if (s == null) return null;
        Entity e = view.GetEntity(s);
        if (!e.IsValid()) return null;

        var data = new ClashData
        {
            RunId = e.Get<string>(FRunId),
            ToleranceFt = e.Get<double>(FTol, UnitTypeId.Feet),
            Vertices = e.Get<IList<XYZ>>(FVerts, UnitTypeId.Feet).ToList(),
        };
        var starts = e.Get<IList<int>>(FTriStart);
        var counts = e.Get<IList<int>>(FTriCount);
        var aIds = e.Get<IList<ElementId>>(FA);
        var bIds = e.Get<IList<ElementId>>(FB);
        var linkIds = e.Get<IList<ElementId>>(FLink);
        var status = e.Get<IList<int>>(FStatus);
        var aLabels = e.Get<IList<string>>(FALabel);
        var bLabels = e.Get<IList<string>>(FBLabel);
        var sources = e.Get<IList<string>>(FSource);
        var mins = e.Get<IList<XYZ>>(FMin, UnitTypeId.Feet);
        var maxs = e.Get<IList<XYZ>>(FMax, UnitTypeId.Feet);
        for (int i = 0; i < starts.Count; i++)
        {
            data.Items.Add(new ClashItem
            {
                TriStart = starts[i], TriCount = counts[i],
                AId = aIds[i], BId = bIds[i], LinkId = linkIds[i], Status = (ClashStatus)status[i],
                ALabel = aLabels[i], BLabel = bLabels[i], Source = sources[i], Min = mins[i], Max = maxs[i],
            });
        }
        return data;
    }

    /// <summary>Borra el resultado guardado en la vista. Requiere transacción abierta.</summary>
    public static bool Delete(View view)
    {
        Schema? s = Schema.Lookup(SchemaGuid);
        return s != null && view.GetEntity(s).IsValid() && view.DeleteEntity(s);
    }

    /// <summary>
    /// Vuelve a intersectar cada choque (salvo los resueltos a mano): si ya no chocan pasan a
    /// "ya no choca"; si siguen, se actualiza su volumen (por si el elemento se movió).
    /// Devuelve cuántos dejaron de chocar.
    /// </summary>
    public static int Recheck(Document doc, ClashData data)
    {
        var newVerts = new List<XYZ>();
        int gone = 0;
        foreach (var item in data.Items)
        {
            List<Solid>? pieces = null;
            if (item.Status != ClashStatus.Resolved)
            {
                try { pieces = ClashHelpers.Recheck(doc, item, data.ToleranceFt); }
                catch { pieces = null; }   // geometría que no se pudo leer: se deja como estaba
            }

            int start = newVerts.Count / 3;
            if (pieces != null && pieces.Count > 0)
            {
                foreach (var s in pieces) Triangulate(s, newVerts);
                (item.Min, item.Max) = ClashHelpers.Aabb(pieces);
                item.Status = ClashStatus.Pending;
            }
            else
            {
                newVerts.AddRange(data.Vertices.Skip(item.TriStart * 3).Take(item.TriCount * 3));
                if (pieces != null && item.Status == ClashStatus.Pending) { item.Status = ClashStatus.Gone; gone++; }
            }
            item.TriStart = start;
            item.TriCount = newVerts.Count / 3 - start;
        }
        data.Vertices = newVerts;
        return gone;
    }

    /// <summary>Triángulos de las caras del sólido, con el orden de vértices girado hacia afuera.</summary>
    private static void Triangulate(Solid solid, List<XYZ> into)
    {
        foreach (Face face in solid.Faces)
        {
            Mesh mesh;
            try { mesh = face.Triangulate(); } catch { continue; }
            XYZ? planarNormal = (face as PlanarFace)?.FaceNormal;

            for (int i = 0; i < mesh.NumTriangles; i++)
            {
                MeshTriangle t = mesh.get_Triangle(i);
                XYZ a = t.get_Vertex(0), b = t.get_Vertex(1), c = t.get_Vertex(2);
                XYZ n = (b - a).CrossProduct(c - a);
                if (n.GetLength() < 1e-12) continue;

                XYZ? fn = planarNormal;
                if (fn == null)
                {
                    IntersectionResult? pr = face.Project((a + b + c) / 3.0);
                    if (pr != null) fn = face.ComputeNormal(pr.UVPoint);
                }
                if (fn != null && n.DotProduct(fn) < 0) (b, c) = (c, b);

                into.Add(a); into.Add(b); into.Add(c);
            }
        }
    }
}
