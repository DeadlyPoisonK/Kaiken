using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace Kaiken;

/// <summary>Una categoría que se puede elegir en la ventana de Interferencias.</summary>
internal sealed record ClashCategory(BuiltInCategory Bic, string Label, bool DefaultOn);

/// <summary>Grupo de elementos a revisar (A): la categoría principal más sus fittings.</summary>
internal sealed record ClashGroup(string Label, BuiltInCategory[] Categories, bool DefaultOn);

/// <summary>Lo que el usuario eligió en la ventana.</summary>
internal sealed class ClashOptions
{
    public List<BuiltInCategory> ACategories { get; } = new();
    public List<BuiltInCategory> BCategories { get; } = new();
    public bool IncludeHost { get; set; } = true;
    public List<RevitLinkInstance> Links { get; } = new();
    public double ToleranceMm { get; set; } = 10;
}

/// <summary>Un choque entre un elemento A (del modelo) y un elemento B (del modelo o de un vínculo).</summary>
internal sealed class ClashResult
{
    public ElementId AId { get; init; } = ElementId.InvalidElementId;
    public string ALabel { get; init; } = "";
    /// <summary>Elemento B: en el modelo actual, o dentro del vínculo <see cref="LinkId"/>.</summary>
    public ElementId BId { get; init; } = ElementId.InvalidElementId;
    public string BLabel { get; init; } = "";
    /// <summary>Instancia del vínculo donde está B; InvalidElementId si B es de este modelo.</summary>
    public ElementId LinkId { get; init; } = ElementId.InvalidElementId;
    /// <summary>Categoría de B (las integradas tienen el mismo id en todos los modelos).</summary>
    public ElementId BCategory { get; init; } = ElementId.InvalidElementId;
    public string Source { get; init; } = "";
    /// <summary>Volumen(es) de la intersección, en coordenadas del modelo actual.</summary>
    public List<Solid> Solids { get; init; } = new();
    public XYZ Min { get; init; } = XYZ.Zero;
    public XYZ Max { get; init; } = XYZ.Zero;
}

/// <summary>Última configuración usada, en %APPDATA%\Kaiken\interferencias.json.</summary>
internal sealed class ClashPrefs
{
    public List<string> ACategories { get; set; } = new();
    public List<string> BCategories { get; set; } = new();
    public bool IncludeHost { get; set; } = true;
    /// <summary>Vínculos desmarcados (por nombre): así un vínculo nuevo entra marcado por defecto.</summary>
    public List<string> ExcludedLinks { get; set; } = new();
    public double ToleranceMm { get; set; } = 10;

    private static string FilePath => Path.Combine(KaikenSettings.FolderPath, "interferencias.json");

    public static ClashPrefs? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<ClashPrefs>(File.ReadAllText(FilePath))
                : null;
        }
        catch { return null; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(KaikenSettings.FolderPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* las preferencias son solo una comodidad */ }
    }
}

internal static class ClashHelpers
{
    public const double MmToFeet = 1.0 / 304.8;

    /// <summary>Grupos que se pueden revisar (A).</summary>
    public static readonly ClashGroup[] AGroups =
    {
        new("Escalerillas (bandejas y sus fittings)", new[] { BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting }, true),
        new("Ductos (y sus fittings)", new[] { BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_FlexDuctCurves }, false),
        new("Tuberías (y sus fittings)", new[] { BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_FlexPipeCurves }, false),
        new("Conduits (y sus fittings)", new[] { BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting }, false),
        new("Accesorios de ducto y tubería (válvulas, dampers…)", new[] { BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_PipeAccessory }, false),
        new("Equipos (mecánicos, eléctricos, sanitarios)", new[] { BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_PlumbingFixtures }, false),
        new("Terminales (rejillas, rociadores, luminarias)", new[] { BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_Sprinklers, BuiltInCategory.OST_LightingFixtures }, false),
    };

    /// <summary>Contra qué se revisa (B).</summary>
    public static readonly ClashCategory[] BCategories =
    {
        new(BuiltInCategory.OST_StructuralFraming,    "Vigas",                  true),
        new(BuiltInCategory.OST_StructuralColumns,    "Pilares estructurales",  true),
        new(BuiltInCategory.OST_Columns,              "Pilares arquitectónicos", true),
        new(BuiltInCategory.OST_Floors,               "Losas / pisos",          true),
        new(BuiltInCategory.OST_Walls,                "Muros",                  true),
        new(BuiltInCategory.OST_Roofs,                "Techos",                 true),
        new(BuiltInCategory.OST_StructuralFoundation, "Fundaciones",            false),
        new(BuiltInCategory.OST_Ceilings,             "Cielos",                 false),
        new(BuiltInCategory.OST_Stairs,               "Escaleras",              false),
        new(BuiltInCategory.OST_DuctCurves,           "Ductos",                 true),
        new(BuiltInCategory.OST_DuctFitting,          "Fittings de ducto",      true),
        new(BuiltInCategory.OST_FlexDuctCurves,       "Ductos flexibles",       false),
        new(BuiltInCategory.OST_PipeCurves,           "Tuberías",               true),
        new(BuiltInCategory.OST_PipeFitting,          "Fittings de tubería",    true),
        new(BuiltInCategory.OST_FlexPipeCurves,       "Tuberías flexibles",     false),
        new(BuiltInCategory.OST_DuctAccessory,        "Accesorios de ducto",    true),
        new(BuiltInCategory.OST_PipeAccessory,        "Accesorios de tubería",  true),
        new(BuiltInCategory.OST_CableTray,            "Escalerillas",           true),
        new(BuiltInCategory.OST_CableTrayFitting,     "Fittings de escalerilla", true),
        new(BuiltInCategory.OST_Conduit,              "Conduits",               false),
        new(BuiltInCategory.OST_ConduitFitting,       "Fittings de conduit",    false),
        new(BuiltInCategory.OST_MechanicalEquipment,  "Equipos mecánicos",      true),
        new(BuiltInCategory.OST_ElectricalEquipment,  "Equipos eléctricos",     false),
        new(BuiltInCategory.OST_PlumbingFixtures,     "Artefactos sanitarios",  false),
        new(BuiltInCategory.OST_LightingFixtures,     "Luminarias",             false),
        new(BuiltInCategory.OST_Sprinklers,           "Rociadores",             false),
        new(BuiltInCategory.OST_DuctTerminal,         "Rejillas / difusores",   false),
        new(BuiltInCategory.OST_GenericModel,         "Modelos genéricos",      false),
    };

    /// <summary>Todas las categorías que pueden quedar marcadas (para limpiar sin recordar la corrida).</summary>
    public static IEnumerable<BuiltInCategory> AllACategories => AGroups.SelectMany(g => g.Categories);

    // ── Geometría ──────────────────────────────────────────────────────────────

    private static readonly Options MediumOpts = new() { DetailLevel = ViewDetailLevel.Medium, ComputeReferences = false };
    private static readonly Options FineOpts   = new() { DetailLevel = ViewDetailLevel.Fine,   ComputeReferences = false };

    /// <summary>
    /// Sólidos del elemento. Primero en detalle Medio: las escalerillas ahí son una caja
    /// (el espacio que ocupan), en Fino serían peldaños sueltos. Las tuberías y conduits en
    /// Medio son solo una línea, así que si no hay sólidos se repite en Fino.
    /// </summary>
    public static List<Solid> GetSolids(Element e)
    {
        var solids = new List<Solid>();
        Collect(e.get_Geometry(MediumOpts), solids);
        if (solids.Count == 0) Collect(e.get_Geometry(FineOpts), solids);
        return solids;
    }

    private static void Collect(GeometryElement? geo, List<Solid> into)
    {
        if (geo == null) return;
        foreach (GeometryObject obj in geo)
        {
            switch (obj)
            {
                case Solid s when s.Volume > 1e-9:
                    into.Add(s);
                    break;
                case GeometryInstance gi:
                    Collect(gi.GetInstanceGeometry(), into);
                    break;
            }
        }
    }

    public static (XYZ min, XYZ max) Aabb(Solid s)
    {
        BoundingBoxXYZ bb = s.GetBoundingBox();
        return Aabb(Corners(bb));
    }

    private static (XYZ min, XYZ max) Aabb(IEnumerable<XYZ> pts)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
        double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        foreach (var p in pts)
        {
            x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
            x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
        }
        return (new XYZ(x0, y0, z0), new XYZ(x1, y1, z1));
    }

    private static IEnumerable<XYZ> Corners(BoundingBoxXYZ bb)
    {
        Transform t = bb.Transform;
        foreach (var x in new[] { bb.Min.X, bb.Max.X })
            foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
                foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                    yield return t.OfPoint(new XYZ(x, y, z));
    }

    private static Solid CreateBox(XYZ min, XYZ max)
    {
        var p0 = new XYZ(min.X, min.Y, min.Z);
        var p1 = new XYZ(max.X, min.Y, min.Z);
        var p2 = new XYZ(max.X, max.Y, min.Z);
        var p3 = new XYZ(min.X, max.Y, min.Z);
        var loop = CurveLoop.Create(new List<Curve>
        {
            Line.CreateBound(p0, p1), Line.CreateBound(p1, p2),
            Line.CreateBound(p2, p3), Line.CreateBound(p3, p0),
        });
        return GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, XYZ.BasisZ, max.Z - min.Z);
    }

    public static List<BoxedSolid> Boxed(IEnumerable<Solid> solids) =>
        solids.Select(s => { var (min, max) = Aabb(s); return new BoxedSolid(s, min, max); }).ToList();

    private static List<Solid> Intersect(List<Solid> aSolids, List<Solid> bSolids, double tolFt) =>
        Intersect(Boxed(aSolids), Boxed(bSolids), tolFt);

    /// <summary>
    /// Volúmenes de intersección entre los sólidos de A y de B. Antes del cálculo booleano
    /// (lo caro) se descartan los pares cuyas cajas se cruzan menos que la tolerancia en algún
    /// eje: el volumen real sería aún menor. Si la operación booleana falla (geometría que
    /// Revit no logra cortar) se usa la caja común, para no perder el choque.
    /// </summary>
    private static List<Solid> Intersect(List<BoxedSolid> aSolids, List<BoxedSolid> bSolids, double tolFt)
    {
        var pieces = new List<Solid>();
        foreach (var a in aSolids)
        {
            foreach (var b in bSolids)
            {
                var oMin = new XYZ(Math.Max(a.Min.X, b.Min.X), Math.Max(a.Min.Y, b.Min.Y), Math.Max(a.Min.Z, b.Min.Z));
                var oMax = new XYZ(Math.Min(a.Max.X, b.Max.X), Math.Min(a.Max.Y, b.Max.Y), Math.Min(a.Max.Z, b.Max.Z));
                XYZ overlap = oMax - oMin;
                if (Math.Min(overlap.X, Math.Min(overlap.Y, overlap.Z)) < Math.Max(tolFt, 1e-6)) continue;

                Solid? piece = null;
                try
                {
                    piece = BooleanOperationsUtils.ExecuteBooleanOperation(a.Solid, b.Solid, BooleanOperationsType.Intersect);
                    if (piece == null || piece.Volume < 1e-9) continue;   // solo se tocan
                }
                catch
                {
                    try { piece = CreateBox(oMin, oMax); } catch { continue; }
                }

                var (pMin, pMax) = Aabb(piece);
                XYZ size = pMax - pMin;
                if (Math.Min(size.X, Math.Min(size.Y, size.Z)) < tolFt) continue;
                pieces.Add(piece);
            }
        }
        return pieces;
    }

    private static HashSet<ElementId> ConnectedIds(Element e)
    {
        var ids = new HashSet<ElementId>();
        ConnectorManager? cm = e switch
        {
            MEPCurve c => c.ConnectorManager,
            FamilyInstance fi => fi.MEPModel?.ConnectorManager,
            _ => null,
        };
        if (cm == null) return ids;
        foreach (Connector c in cm.Connectors)
        {
            if (!c.IsConnected) continue;
            foreach (Connector r in c.AllRefs)
                if (r.Owner != null && r.Owner.Id != e.Id) ids.Add(r.Owner.Id);
        }
        return ids;
    }

    private static string Describe(Element e)
    {
        string cat = e.Category?.Name ?? "Elemento";
        string type = e.Document.GetElement(e.GetTypeId()) is ElementType t ? t.Name : e.Name;
        return $"{cat}: {type} [{e.Id}]";
    }

    // ── Detección ──────────────────────────────────────────────────────────────

    public static List<Element> CollectA(Document doc, ClashOptions opts) =>
        new FilteredElementCollector(doc)
            .WherePasses(new ElementMulticategoryFilter(opts.ACategories))
            .WhereElementIsNotElementType()
            .ToList();

    /// <summary>
    /// Busca los choques de un elemento A contra el índice de elementos B. Solo calcula la
    /// geometría de A si su caja toca algún candidato. <paramref name="seenPairs"/> evita contar
    /// dos veces un choque entre dos elementos que están en ambos grupos (escalerilla contra escalerilla).
    /// </summary>
    public static List<ClashResult> FindClashes(Element a, ClashIndex index, double tolFt, HashSet<(long, long)> seenPairs)
    {
        var result = new List<ClashResult>();
        BoundingBoxXYZ? bb = a.get_BoundingBox(null);
        if (bb == null) return result;

        var candidates = index.Query(bb.Min, bb.Max, tolFt);
        if (candidates.Count == 0) return result;

        var aSolids = Boxed(GetSolids(a));
        if (aSolids.Count == 0) return result;

        string aLabel = Describe(a);
        HashSet<ElementId>? connected = null;
        foreach (var c in candidates)
        {
            Element b = c.Element;
            if (c.LinkId == ElementId.InvalidElementId)
            {
                if (b.Id == a.Id) continue;
                connected ??= ConnectedIds(a);
                if (connected.Contains(b.Id)) continue;
                long ia = a.Id.Value, ib = b.Id.Value;
                if (!seenPairs.Add((Math.Min(ia, ib), Math.Max(ia, ib)))) continue;
            }

            var pieces = Intersect(aSolids, c.Solids, tolFt);
            if (pieces.Count > 0) result.Add(Build(a.Id, aLabel, b, c.LinkId, c.Source, pieces));
        }
        return result;
    }

    /// <summary>
    /// Vuelve a intersectar un choque guardado (mismo A, mismo B). Devuelve los volúmenes
    /// actuales en coordenadas del modelo: vacío si ya no chocan o si alguno se borró.
    /// </summary>
    public static List<Solid> Recheck(Document doc, ClashItem item, double tolFt)
    {
        Element? a = doc.GetElement(item.AId);
        if (a == null) return new List<Solid>();
        var aSolids = GetSolids(a);

        if (item.LinkId == ElementId.InvalidElementId)
        {
            Element? b = doc.GetElement(item.BId);
            return b == null ? new List<Solid>() : Intersect(aSolids, GetSolids(b), tolFt);
        }

        if (doc.GetElement(item.LinkId) is not RevitLinkInstance link || link.GetLinkDocument() is not Document linkDoc)
            return new List<Solid>();
        Element? lb = linkDoc.GetElement(item.BId);
        if (lb == null) return new List<Solid>();

        Transform toHost = link.GetTotalTransform();
        var aLocal = aSolids.Select(s => SolidUtils.CreateTransformed(s, toHost.Inverse)).ToList();
        return Intersect(aLocal, GetSolids(lb), tolFt).Select(p => SolidUtils.CreateTransformed(p, toHost)).ToList();
    }

    public static (XYZ min, XYZ max) Aabb(IEnumerable<Solid> solids) =>
        Aabb(solids.SelectMany(p => { var (lo, hi) = Aabb(p); return new[] { lo, hi }; }));

    private static ClashResult Build(ElementId aId, string aLabel, Element b, ElementId linkId, string source, List<Solid> pieces)
    {
        var (min, max) = Aabb(pieces);
        return new ClashResult
        {
            AId = aId, ALabel = aLabel, BId = b.Id, BLabel = Describe(b), LinkId = linkId,
            BCategory = b.Category?.Id ?? ElementId.InvalidElementId,
            Source = source, Solids = pieces, Min = min, Max = max,
        };
    }

    // ── Vista y colores ────────────────────────────────────────────────────────

    /// <summary>Una vista por usuario: con modelo central, dos personas no se pisan la vista.</summary>
    public static string ViewName(Document doc)
    {
        string user = doc.Application.Username;
        if (string.IsNullOrWhiteSpace(user)) user = Environment.UserName;
        return $"Kaiken - Interferencias ({user})";
    }

    public static View3D? FindView(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .FirstOrDefault(v => !v.IsTemplate && v.Name == ViewName(doc));

    /// <summary>Crea (o reutiliza) la vista 3D de interferencias. Requiere transacción abierta.</summary>
    public static View3D GetOrCreateView(Document doc)
    {
        View3D? view = FindView(doc);
        if (view == null)
        {
            var vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
            view = View3D.CreateIsometric(doc, vft.Id);
            view.Name = ViewName(doc);
        }

        if (view.ViewTemplateId != ElementId.InvalidElementId) view.ViewTemplateId = ElementId.InvalidElementId;
        view.DetailLevel = ViewDetailLevel.Fine;
        view.DisplayStyle = DisplayStyle.Shading;
        return view;
    }

    /// <summary>Color de los elementos con choque. Se usa también para reconocerlos al limpiar.</summary>
    public static readonly Color MarkColor = new(255, 140, 0);
    private const int GhostTransparency = 75;

    private static ElementId? SolidFill(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(FillPatternElement))
            .Cast<FillPatternElement>()
            .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill)
            ?.Id;

    private static readonly ElementId LinksCategory = new(BuiltInCategory.OST_RvtLinks);

    /// <summary>
    /// Deja la vista mostrando solo lo relevante: las categorías revisadas (A) completas, las
    /// categorías con las que hubo choque, los vínculos con choques y, de este modelo, solo los
    /// elementos B que chocan. Sin anotaciones (niveles, ejes…), con todos los subproyectos
    /// visibles, todo semitransparente y los A con choque en naranjo.
    /// Los vínculos no permiten ocultar elementos sueltos por API: de ellos se ve la categoría completa.
    /// </summary>
    public static void ApplyView(Document doc, View3D view, IEnumerable<BuiltInCategory> aCategories,
        List<ClashResult> clashes, ClashData data)
    {
        ResetView(doc, view);

        if (doc.IsWorkshared)
        {
            foreach (Workset ws in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
            {
                try { view.SetWorksetVisibility(ws.Id, WorksetVisibility.Visible); } catch { }
            }
        }

        var aCats = new HashSet<ElementId>(aCategories.Select(c => new ElementId(c)));
        var visible = new HashSet<ElementId>(aCats) { LinksCategory };
        foreach (var c in clashes) visible.Add(c.BCategory);

        var ghost = new OverrideGraphicSettings();
        // Sin medio tono: se heredaría también en los elementos marcados y apagaría el naranjo.
        ghost.SetSurfaceTransparency(GhostTransparency);

        foreach (Category cat in doc.Settings.Categories)
        {
            bool show = visible.Contains(cat.Id);
            try { if (view.CanCategoryBeHidden(cat.Id)) view.SetCategoryHidden(cat.Id, !show); } catch { }
            if (show && cat.CategoryType == CategoryType.Model && cat.Id != LinksCategory)
            {
                try { view.SetCategoryOverrides(cat.Id, ghost); } catch { }
            }
        }

        // Vínculos sin choques: fuera.
        var linksWithClash = new HashSet<ElementId>(clashes.Select(c => c.LinkId));
        var hideLinks = new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkInstance))
            .Where(l => !linksWithClash.Contains(l.Id) && l.CanBeHidden(view))
            .Select(l => l.Id)
            .ToList();
        if (hideLinks.Count > 0) view.HideElements(hideLinks);

        // De este modelo, en las categorías B (que no son A), solo los elementos que chocan.
        var hostB = new HashSet<ElementId>(clashes.Where(c => c.LinkId == ElementId.InvalidElementId).Select(c => c.BId));
        var bOnly = visible.Where(id => !aCats.Contains(id) && id != LinksCategory && id.Value < 0)
            .Select(id => (BuiltInCategory)id.Value)
            .ToList();
        if (bOnly.Count > 0)
        {
            var hideHost = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(bOnly))
                .WhereElementIsNotElementType()
                .Where(e => !hostB.Contains(e.Id) && e.CanBeHidden(view))
                .Select(e => e.Id)
                .ToList();
            if (hideHost.Count > 0) view.HideElements(hideHost);
        }

        UpdateMarks(view, data);
    }

    /// <summary>Naranjo para los A con algún choque pendiente; sin color para los que ya no tienen.</summary>
    public static void UpdateMarks(View view, ClashData data)
    {
        var mark = new OverrideGraphicSettings();
        mark.SetProjectionLineColor(MarkColor);
        mark.SetSurfaceTransparency(45);
        ElementId? solid = SolidFill(view.Document);
        if (solid != null)
        {
            mark.SetSurfaceForegroundPatternColor(MarkColor);
            mark.SetSurfaceForegroundPatternId(solid);
        }

        var pending = new HashSet<ElementId>(data.Items.Where(i => i.Status == ClashStatus.Pending).Select(i => i.AId));
        foreach (var id in data.Items.Select(i => i.AId).Distinct())
        {
            try { view.SetElementOverrides(id, pending.Contains(id) ? mark : new OverrideGraphicSettings()); }
            catch { /* elemento borrado */ }
        }
    }

    /// <summary>
    /// Devuelve la vista a su estado normal: muestra lo que se ocultó (categorías, vínculos y
    /// elementos) y quita la transparencia y el naranjo. Devuelve cuántos naranjos quitó.
    /// </summary>
    public static int ResetView(Document doc, View view)
    {
        var catalog = AllACategories.Concat(BCategories.Select(c => c.Bic)).Distinct().ToList();
        var hidden = new FilteredElementCollector(doc)
            .WherePasses(new LogicalOrFilter(
                new ElementMulticategoryFilter(catalog),
                new ElementClassFilter(typeof(RevitLinkInstance))))
            .WhereElementIsNotElementType()
            .Where(e => e.IsHidden(view))
            .Select(e => e.Id)
            .ToList();
        if (hidden.Count > 0) view.UnhideElements(hidden);

        foreach (Category cat in doc.Settings.Categories)
        {
            try { if (view.CanCategoryBeHidden(cat.Id) && view.GetCategoryHidden(cat.Id)) view.SetCategoryHidden(cat.Id, false); } catch { }
            if (cat.CategoryType != CategoryType.Model) continue;
            try
            {
                if (view.GetCategoryOverrides(cat.Id).Transparency == GhostTransparency)
                    view.SetCategoryOverrides(cat.Id, new OverrideGraphicSettings());
            }
            catch { }
        }

        int cleared = 0;
        var marked = new FilteredElementCollector(doc)
            .WherePasses(new ElementMulticategoryFilter(AllACategories.ToList()))
            .WhereElementIsNotElementType()
            .ToElementIds();
        foreach (var id in marked)
        {
            Color c = view.GetElementOverrides(id).ProjectionLineColor;
            if (c.IsValid && c.Red == MarkColor.Red && c.Green == MarkColor.Green && c.Blue == MarkColor.Blue)
            {
                view.SetElementOverrides(id, new OverrideGraphicSettings());
                cleared++;
            }
        }
        return cleared;
    }

    /// <summary>Subproyectos cerrados en este modelo o en los vínculos revisados: lo que tienen no se revisó.</summary>
    public static List<string> ClosedWorksets(Document doc, IEnumerable<RevitLinkInstance> links)
    {
        var warnings = new List<string>();
        void Check(Document d, string label)
        {
            if (!d.IsWorkshared) return;
            try
            {
                var closed = new FilteredWorksetCollector(d).OfKind(WorksetKind.UserWorkset)
                    .Where(ws => !ws.IsOpen).Select(ws => ws.Name).ToList();
                if (closed.Count > 0) warnings.Add($"{label}: {string.Join(", ", closed)}");
            }
            catch { }
        }

        Check(doc, "Este modelo");
        foreach (var l in links)
            if (l.GetLinkDocument() is Document ld) Check(ld, ld.Title);
        return warnings.Distinct().ToList();
    }
}
