using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;

namespace Kaiken;

/// <summary>
/// Dibuja en rojo el volumen de cada choque con DirectContext3D. Los triángulos viven en la
/// vista (<see cref="ClashStorage"/>), así que el rojo sobrevive al cerrar y reabrir el modelo,
/// pero no es un elemento del modelo: solo se ve en esa vista, no sale al exportar ni en otras vistas.
/// Hay un dibujo por vista con datos; se registra al activar la vista (ver App).
/// </summary>
internal static class ClashDisplay
{
    private static readonly Dictionary<string, ClashDrawServer> Servers = new();

    internal static string DocKey(Document doc) => string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;
    private static string Key(View view) => DocKey(view.Document) + "|" + view.UniqueId;

    private static MultiServerService Service =>
        (MultiServerService)ExternalServiceRegistry.GetService(ExternalServices.BuiltInExternalServices.DirectContext3DService);

    /// <summary>
    /// Deja el dibujo de la vista al día con lo guardado en ella: lo crea, lo reemplaza si hubo
    /// una revisión nueva, o lo quita si se limpió. Requiere contexto de la API de Revit.
    /// </summary>
    public static void Ensure(View view)
    {
        if (view is not View3D || view.IsTemplate) return;
        string key = Key(view);
        string? runId = ClashStorage.RunIdOf(view);
        Servers.TryGetValue(key, out var current);
        if (current != null && current.RunId == runId) return;

        if (current != null) Remove(key);
        if (runId == null) return;

        ClashData? data = ClashStorage.Read(view);
        if (data == null || data.Vertices.Count == 0) return;

        var server = new ClashDrawServer(view, data);
        MultiServerService svc = Service;
        svc.AddServer(server);
        IList<Guid> active = svc.GetActiveServerIds();
        active.Add(server.GetServerId());
        svc.SetActiveServers(active);
        Servers[key] = server;
    }

    /// <summary>Destaca en amarillo un choque (índice en la lista) o ninguno (-1).</summary>
    public static void SetHighlight(View view, int index)
    {
        if (Servers.TryGetValue(Key(view), out var server)) server.Highlight = index;
    }

    /// <summary>Quita los dibujos de un documento que se cierra.</summary>
    public static void Forget(Document doc)
    {
        string prefix = DocKey(doc) + "|";
        foreach (var key in Servers.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            Remove(key);
    }

    private static void Remove(string key)
    {
        if (!Servers.Remove(key, out var server)) return;
        try { Service.RemoveServer(server.GetServerId()); } catch { }
    }
}

internal sealed class ClashDrawServer : IDirectContext3DServer
{
    /// <summary>Rojo opaco del volumen de choque; amarillo el que se está revisando.</summary>
    private static readonly ColorWithTransparency Red = new(230, 0, 0, 0);
    private static readonly ColorWithTransparency Yellow = new(255, 230, 0, 0);

    /// <summary>Límite de vértices por buffer: los índices son de 16 bits.</summary>
    private const int MaxVertices = 60000;

    private readonly Guid _id = Guid.NewGuid();
    private readonly string _docKey;
    private readonly string _viewUid;
    private readonly Outline _outline;
    private readonly List<XYZ> _vertices;
    private readonly List<ClashItem> _items;
    private List<Batch>? _batches;
    private int _highlight = -1;

    public string RunId { get; }

    /// <summary>Índice del choque destacado; al cambiarlo se rehacen los buffers (son pocos triángulos).</summary>
    public int Highlight
    {
        get => _highlight;
        set { if (_highlight != value) { _highlight = value; _batches = null; } }
    }

    public ClashDrawServer(View view, ClashData data)
    {
        _docKey = ClashDisplay.DocKey(view.Document);
        _viewUid = view.UniqueId;
        RunId = data.RunId;
        _vertices = data.Vertices;
        _items = data.Items;

        XYZ min = new(_vertices.Min(p => p.X), _vertices.Min(p => p.Y), _vertices.Min(p => p.Z));
        XYZ max = new(_vertices.Max(p => p.X), _vertices.Max(p => p.Y), _vertices.Max(p => p.Z));
        _outline = new Outline(min, max);
    }

    private sealed class Batch
    {
        public VertexBuffer Vb = null!;
        public IndexBuffer Ib = null!;
        public int VertexCount, IndexCount, PrimitiveCount;
        public VertexFormat Format = null!;
        public EffectInstance Effect = null!;
    }

    /// <summary>Solo los choques pendientes; los resueltos no se dibujan.</summary>
    private List<Batch> BuildBatches()
    {
        var tris = new List<(int tri, ColorWithTransparency color)>();
        for (int k = 0; k < _items.Count; k++)
        {
            ClashItem it = _items[k];
            if (it.Status != ClashStatus.Pending) continue;
            var color = k == _highlight ? Yellow : Red;
            for (int t = it.TriStart; t < it.TriStart + it.TriCount; t++) tris.Add((t, color));
        }

        var batches = new List<Batch>();
        int perBatch = MaxVertices / 3;
        for (int start = 0; start < tris.Count; start += perBatch)
        {
            int n = Math.Min(perBatch, tris.Count - start);
            int nVerts = n * 3;

            int vbSize = nVerts * VertexPositionNormalColored.GetSizeInFloats();
            var vb = new VertexBuffer(vbSize);
            vb.Map(vbSize);
            VertexStreamPositionNormalColored vs = vb.GetVertexStreamPositionNormalColored();
            for (int k = start; k < start + n; k++)
            {
                var (t, color) = tris[k];
                XYZ a = _vertices[3 * t], b = _vertices[3 * t + 1], c = _vertices[3 * t + 2];
                XYZ nrm = (b - a).CrossProduct(c - a).Normalize();
                vs.AddVertex(new VertexPositionNormalColored(a, nrm, color));
                vs.AddVertex(new VertexPositionNormalColored(b, nrm, color));
                vs.AddVertex(new VertexPositionNormalColored(c, nrm, color));
            }
            vb.Unmap();

            int ibSize = n * IndexTriangle.GetSizeInShortInts();
            var ib = new IndexBuffer(ibSize);
            ib.Map(ibSize);
            IndexStreamTriangle istream = ib.GetIndexStreamTriangle();
            for (int i = 0; i < n; i++)
                istream.AddTriangle(new IndexTriangle(3 * i, 3 * i + 1, 3 * i + 2));
            ib.Unmap();

            batches.Add(new Batch
            {
                Vb = vb, Ib = ib,
                VertexCount = nVerts, IndexCount = n * 3, PrimitiveCount = n,
                Format = new VertexFormat(VertexFormatBits.PositionNormalColored),
                Effect = new EffectInstance(VertexFormatBits.PositionNormalColored),
            });
        }
        return batches;
    }

    // ── IDirectContext3DServer ─────────────────────────────────────────────────

    public Guid GetServerId() => _id;
    public string GetVendorId() => "Kaiken";
    public ExternalServiceId GetServiceId() => ExternalServices.BuiltInExternalServices.DirectContext3DService;
    public string GetName() => "Kaiken Interferencias";
    public string GetDescription() => "Volúmenes de interferencia en rojo";
    public string GetApplicationId() => "";
    public string GetSourceId() => "";
    public bool UsesHandles() => false;
    public bool UseInTransparentPass(View dBView) => false;
    public Outline GetBoundingBox(View dBView) => _outline;

    /// <summary>
    /// Solo en su vista, y solo mientras la vista guarde ESTA revisión: si se limpió, se
    /// deshizo con Ctrl+Z o se reemplazó por otra, el rojo deja de dibujarse al tiro.
    /// </summary>
    public bool CanExecute(View dBView)
    {
        try
        {
            return dBView.UniqueId == _viewUid
                && ClashDisplay.DocKey(dBView.Document) == _docKey
                && ClashStorage.RunIdOf(dBView) == RunId;
        }
        catch { return false; }
    }

    public void RenderScene(View dBView, DisplayStyle displayStyle)
    {
        try
        {
            if (_batches == null || _batches.Any(b => !b.Vb.IsValid() || !b.Ib.IsValid()))
                _batches = BuildBatches();

            foreach (var b in _batches)
                DrawContext.FlushBuffer(b.Vb, b.VertexCount, b.Ib, b.IndexCount, b.Format, b.Effect,
                    PrimitiveType.TriangleList, 0, b.PrimitiveCount);
        }
        catch
        {
            // Un error aquí no debe tumbar el redibujo de Revit.
        }
    }
}
