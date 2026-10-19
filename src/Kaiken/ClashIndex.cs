using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Kaiken;

/// <summary>Sólido con su caja (en coordenadas del modelo), para no recalcularla en cada par.</summary>
internal readonly record struct BoxedSolid(Solid Solid, XYZ Min, XYZ Max);

/// <summary>Un elemento B (del modelo o de un vínculo) con su caja en coordenadas del modelo.</summary>
internal sealed class ClashCandidate
{
    public Element Element = null!;
    public ElementId LinkId = ElementId.InvalidElementId;
    public string Source = "";
    /// <summary>Transformación vínculo → modelo; null si es del modelo actual.</summary>
    public Transform? ToHost;
    public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

    private List<BoxedSolid>? _solids;

    /// <summary>
    /// Geometría en coordenadas del modelo, calculada la primera vez que se necesita y
    /// reutilizada: una viga que cruzan diez escalerillas se lee una sola vez.
    /// </summary>
    public List<BoxedSolid> Solids => _solids ??= ClashHelpers.Boxed(
        ClashHelpers.GetSolids(Element).Select(s => ToHost == null ? s : SolidUtils.CreateTransformed(s, ToHost)));
}

/// <summary>
/// Índice espacial (grilla uniforme) de todos los elementos B del modelo y los vínculos.
/// Se arma una vez por revisión usando solo las cajas de los elementos (barato), y cada
/// elemento A consulta las celdas que ocupa: así casi ningún par llega al cálculo de sólidos.
/// </summary>
internal sealed class ClashIndex
{
    private const double CellFt = 10.0;            // ≈ 3 m
    private const long MaxCellsPerItem = 4000;     // más grande que esto va a la lista de "grandes"

    private readonly List<ClashCandidate> _items = new();
    private readonly Dictionary<(int, int, int), List<int>> _grid = new();
    private readonly List<int> _big = new();
    private int[] _stamp = Array.Empty<int>();
    private int _query;

    public int Count => _items.Count;

    private static int Cell(double v) => (int)Math.Floor(v / CellFt);

    private void Add(ClashCandidate c)
    {
        int idx = _items.Count;
        _items.Add(c);

        int x0 = Cell(c.MinX), y0 = Cell(c.MinY), z0 = Cell(c.MinZ);
        int x1 = Cell(c.MaxX), y1 = Cell(c.MaxY), z1 = Cell(c.MaxZ);
        long cells = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
        if (cells > MaxCellsPerItem) { _big.Add(idx); return; }

        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                {
                    if (!_grid.TryGetValue((x, y, z), out var list)) _grid[(x, y, z)] = list = new List<int>();
                    list.Add(idx);
                }
    }

    /// <summary>
    /// Candidatos cuya caja se cruza con [min, max] en más que la tolerancia en los tres ejes
    /// (si se cruzan menos, el choque real también es menor que la tolerancia).
    /// </summary>
    public List<ClashCandidate> Query(XYZ min, XYZ max, double tol)
    {
        if (_stamp.Length < _items.Count) _stamp = new int[_items.Count];
        _query++;
        var result = new List<ClashCandidate>();

        void Test(int i)
        {
            if (_stamp[i] == _query) return;
            _stamp[i] = _query;
            ClashCandidate c = _items[i];
            if (Math.Min(max.X, c.MaxX) - Math.Max(min.X, c.MinX) < tol) return;
            if (Math.Min(max.Y, c.MaxY) - Math.Max(min.Y, c.MinY) < tol) return;
            if (Math.Min(max.Z, c.MaxZ) - Math.Max(min.Z, c.MinZ) < tol) return;
            result.Add(c);
        }

        int x0 = Cell(min.X), y0 = Cell(min.Y), z0 = Cell(min.Z);
        int x1 = Cell(max.X), y1 = Cell(max.Y), z1 = Cell(max.Z);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                    if (_grid.TryGetValue((x, y, z), out var list))
                        foreach (int i in list) Test(i);
        foreach (int i in _big) Test(i);
        return result;
    }

    /// <summary>Arma el índice con los elementos B del modelo (si se eligió) y de cada vínculo elegido.</summary>
    public static ClashIndex Build(Document doc, ClashOptions opts, Action<string> status)
    {
        var index = new ClashIndex();
        var filter = new ElementMulticategoryFilter(opts.BCategories);

        if (opts.IncludeHost)
        {
            status("Indexando este modelo…");
            index.AddAll(new FilteredElementCollector(doc).WherePasses(filter).WhereElementIsNotElementType(),
                ElementId.InvalidElementId, "Este modelo", null);
        }

        foreach (var link in opts.Links)
        {
            if (link.GetLinkDocument() is not Document linkDoc) continue;
            status($"Indexando {linkDoc.Title}…");
            Transform toHost = link.GetTotalTransform();
            index.AddAll(new FilteredElementCollector(linkDoc).WherePasses(filter).WhereElementIsNotElementType(),
                link.Id, linkDoc.Title, toHost.IsIdentity ? null : toHost);
        }
        return index;
    }

    private void AddAll(IEnumerable<Element> elements, ElementId linkId, string source, Transform? toHost)
    {
        foreach (var e in elements)
        {
            BoundingBoxXYZ? bb = e.get_BoundingBox(null);
            if (bb == null) continue;

            var c = new ClashCandidate { Element = e, LinkId = linkId, Source = source, ToHost = toHost };
            c.MinX = c.MinY = c.MinZ = double.MaxValue;
            c.MaxX = c.MaxY = c.MaxZ = double.MinValue;
            Transform t = toHost == null ? bb.Transform : toHost.Multiply(bb.Transform);
            foreach (var x in new[] { bb.Min.X, bb.Max.X })
                foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
                    foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                    {
                        XYZ p = t.OfPoint(new XYZ(x, y, z));
                        c.MinX = Math.Min(c.MinX, p.X); c.MinY = Math.Min(c.MinY, p.Y); c.MinZ = Math.Min(c.MinZ, p.Z);
                        c.MaxX = Math.Max(c.MaxX, p.X); c.MaxY = Math.Max(c.MaxY, p.Y); c.MaxZ = Math.Max(c.MaxZ, p.Z);
                    }
            Add(c);
        }
    }
}
