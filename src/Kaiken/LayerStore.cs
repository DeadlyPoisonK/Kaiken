using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace Kaiken;

/// <summary>Una capa tal como la ve el panel: datos del modelo más su estado en la vista activa.</summary>
internal sealed class LayerInfo
{
    public ElementId FilterId = ElementId.InvalidElementId;
    public string Name = "";
    public Color Color = new(255, 0, 0);
    /// <summary>Anotaciones (etiquetas, textos, cotas) de la capa: no admiten el parámetro ni filtros.</summary>
    public List<ElementId> Annotations = new();
    public int Count;

    // Estado en la vista activa
    public bool Visible = true;
    public bool ColorOn;
    public bool Halftone;
}

/// <summary>Estado de la vista activa para el panel de capas.</summary>
internal sealed class LayerViewState
{
    public string ViewName = "";
    /// <summary>Por qué no se pueden tocar los filtros en esta vista (null si se puede).</summary>
    public string? FilterBlock;
    /// <summary>La vista no admite capas en absoluto (planillas, láminas sin overrides, etc.).</summary>
    public bool Unsupported;
}

/// <summary>Resultado de asignar/quitar elementos: cuántos y por qué se omitieron otros.</summary>
internal sealed class LayerAssignResult
{
    public int Done;
    public List<string> Skipped = new();
}

/// <summary>
/// Capas tipo Photoshop sobre lo que Revit ya tiene. Cada capa es un filtro de vista
/// (ParameterFilterElement) con la regla Kaiken_Capa = nombre, sobre un parámetro compartido de
/// texto de instancia: queda en el modelo, se imprime, viaja con el central y funciona en
/// plantillas. Las anotaciones (etiquetas, textos, cotas) no admiten parámetros ni filtros: sus
/// ids se guardan en el propio filtro (Extensible Storage) y en la vista se ocultan y colorean
/// elemento por elemento; como cada anotación es de una sola vista, el alcance es el mismo.
/// El ojo, el color y el semitono actúan sobre la vista activa.
/// </summary>
internal static class LayerStore
{
    public const string ParamName = "Kaiken_Capa";
    private const string FilterPrefix = "Capa - ";

    // Fijos para siempre: el GUID identifica el parámetro en todos los modelos, y los esquemas de
    // Revit no se pueden modificar (Extra queda para lo que se agregue después, en JSON).
    private static readonly Guid ParamGuid = new("5E0C7A41-3B2D-4F8E-9A61-D2C4B7E19F35");
    private static readonly Guid SchemaGuid = new("A7F3C2E8-61B4-4D9A-8E25-0C9B3D7F4A16");
    private const string FColor = "Color", FAnn = "Annotations", FExtra = "Extra";

    /// <summary>Colores que se van asignando a las capas nuevas.</summary>
    public static readonly Color[] Palette =
    {
        new(230, 25, 75), new(60, 180, 75), new(0, 130, 200), new(245, 130, 48),
        new(145, 30, 180), new(70, 200, 200), new(240, 50, 230), new(160, 160, 0),
        new(0, 128, 128), new(170, 110, 40), new(128, 0, 0), new(0, 0, 128),
    };

    // ── Esquema y parámetro ────────────────────────────────────────────────────

    private static Schema GetSchema()
    {
        Schema? s = Schema.Lookup(SchemaGuid);
        if (s != null) return s;
        var b = new SchemaBuilder(SchemaGuid);
        b.SetSchemaName("KaikenCapa");
        b.SetReadAccessLevel(AccessLevel.Public);
        b.SetWriteAccessLevel(AccessLevel.Public);
        b.AddSimpleField(FColor, typeof(int));
        b.AddArrayField(FAnn, typeof(ElementId));
        b.AddSimpleField(FExtra, typeof(string));
        return b.Finish();
    }

    private static int Pack(Color c) => (c.Red << 16) | (c.Green << 8) | c.Blue;
    private static Color Unpack(int v) => new((byte)(v >> 16), (byte)(v >> 8), (byte)v);

    /// <summary>Id del parámetro Kaiken_Capa en el modelo, o null si todavía no existe.</summary>
    public static ElementId? ParamId(Document doc) => SharedParameterElement.Lookup(doc, ParamGuid)?.Id;

    /// <summary>Categorías donde se puede usar una capa: admiten parámetros y filtros.</summary>
    private static List<Category> LayerableCategories(Document doc)
    {
        var filterable = ParameterFilterUtilities.GetAllFilterableCategories().ToHashSet();
        return doc.Settings.Categories.Cast<Category>()
            .Where(c => c.AllowsBoundParameters && filterable.Contains(c.Id) && c.CategoryType != CategoryType.AnalyticalModel)
            .ToList();
    }

    /// <summary>
    /// Crea el parámetro (o amplía sus categorías) y pone al día las categorías de los filtros
    /// existentes. Devuelve las categorías con el parámetro. Requiere transacción abierta.
    /// </summary>
    public static List<ElementId> EnsureParameter(Document doc)
    {
        var app = doc.Application;
        var wanted = LayerableCategories(doc);
        var set = app.Create.NewCategorySet();
        foreach (var c in wanted) set.Insert(c);

        SharedParameterElement? existing = SharedParameterElement.Lookup(doc, ParamGuid);
        if (existing == null)
        {
            // Archivo de parámetros compartidos temporal: el del usuario no se toca (se restaura al final).
            string original = app.SharedParametersFilename;
            string temp = Path.Combine(Path.GetTempPath(), $"Kaiken_Capa_{Guid.NewGuid():N}.txt");
            File.WriteAllText(temp, "");
            try
            {
                app.SharedParametersFilename = temp;
                DefinitionFile file = app.OpenSharedParameterFile()
                    ?? throw new InvalidOperationException("No se pudo crear el archivo temporal de parámetros compartidos.");
                DefinitionGroup group = file.Groups.Create("Kaiken");
                var def = group.Definitions.Create(new ExternalDefinitionCreationOptions(ParamName, SpecTypeId.String.Text)
                {
                    GUID = ParamGuid,
                    Description = "Capa de Kaiken a la que pertenece el elemento (panel Capas).",
                });
                if (!doc.ParameterBindings.Insert(def, app.Create.NewInstanceBinding(set), GroupTypeId.IdentityData))
                    throw new InvalidOperationException($"Revit no aceptó el parámetro {ParamName}.");
            }
            finally
            {
                try { app.SharedParametersFilename = original; } catch { /* sin archivo original */ }
                try { File.Delete(temp); } catch { /* temporal */ }
            }
            existing = SharedParameterElement.Lookup(doc, ParamGuid)
                ?? throw new InvalidOperationException($"No se encontró {ParamName} después de crearlo.");
            // Sin esto no se puede asignar capa a los elementos que están dentro de grupos.
            try { existing.GetDefinition().SetAllowVaryBetweenGroups(doc, true); } catch { /* algunos modelos no lo permiten */ }
        }
        else
        {
            // Categorías nuevas (ej. una plantilla cargó categorías MEP): se suman al binding.
            InternalDefinition def = existing.GetDefinition();
            if (doc.ParameterBindings.get_Item(def) is InstanceBinding binding)
            {
                bool changed = false;
                foreach (var c in wanted)
                    if (!binding.Categories.Contains(c)) { binding.Categories.Insert(c); changed = true; }
                if (changed) doc.ParameterBindings.ReInsert(def, binding, GroupTypeId.IdentityData);
            }
        }

        List<ElementId> boundIds = BoundCategories(doc);
        foreach (var f in LayerFilters(doc))
        {
            var current = f.GetCategories();
            if (boundIds.Any(id => !current.Contains(id)))
                try { f.SetCategories(boundIds); } catch { /* si una categoría no es válida, queda como estaba */ }
        }
        return boundIds;
    }

    private static List<ElementId> BoundCategories(Document doc)
    {
        var sp = SharedParameterElement.Lookup(doc, ParamGuid);
        if (sp == null || doc.ParameterBindings.get_Item(sp.GetDefinition()) is not ElementBinding binding) return new();
        var filterable = ParameterFilterUtilities.GetAllFilterableCategories().ToHashSet();
        return binding.Categories.Cast<Category>().Select(c => c.Id).Where(filterable.Contains).ToList();
    }

    // ── Lectura ────────────────────────────────────────────────────────────────

    private static IEnumerable<ParameterFilterElement> LayerFilters(Document doc)
    {
        Schema? s = Schema.Lookup(SchemaGuid);
        if (s == null) return Enumerable.Empty<ParameterFilterElement>();
        return new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement))
            .Cast<ParameterFilterElement>()
            .Where(f => f.GetEntity(s).IsValid());
    }

    public static string DisplayName(ParameterFilterElement f) =>
        f.Name.StartsWith(FilterPrefix, StringComparison.Ordinal) ? f.Name[FilterPrefix.Length..] : f.Name;

    private static LayerInfo Read(Document doc, ParameterFilterElement f, Schema s)
    {
        Entity e = f.GetEntity(s);
        return new LayerInfo
        {
            FilterId = f.Id,
            Name = DisplayName(f),
            Color = Unpack(e.Get<int>(FColor)),
            Annotations = e.Get<IList<ElementId>>(FAnn).Where(id => doc.GetElement(id) != null).ToList(),
        };
    }

    /// <summary>Capas del modelo con su estado en <paramref name="view"/> y, si se pide, cuántos elementos tiene cada una.</summary>
    public static List<LayerInfo> Load(Document doc, View? view, bool counts)
    {
        Schema? s = Schema.Lookup(SchemaGuid);
        if (s == null) return new();
        var layers = LayerFilters(doc).Select(f => Read(doc, f, s))
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (counts)
        {
            var byName = CountByLayer(doc);
            foreach (var l in layers)
                l.Count = (byName.TryGetValue(l.Name, out int n) ? n : 0) + l.Annotations.Count;
        }
        if (view != null)
            foreach (var l in layers) ReadViewState(doc, view, l);
        return layers;
    }

    /// <summary>Elementos por valor de Kaiken_Capa, en una sola pasada por el modelo.</summary>
    private static Dictionary<string, int> CountByLayer(Document doc)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var cats = BoundCategories(doc);
        if (cats.Count == 0) return result;
        foreach (Element el in new FilteredElementCollector(doc).WhereElementIsNotElementType()
                     .WherePasses(new ElementMulticategoryFilter(cats)))
        {
            string? v = el.get_Parameter(ParamGuid)?.AsString();
            if (string.IsNullOrEmpty(v)) continue;
            result[v!] = result.TryGetValue(v!, out int n) ? n + 1 : 1;
        }
        return result;
    }

    private static void ReadViewState(Document doc, View view, LayerInfo l)
    {
        bool applied = false;
        try { applied = view.GetFilters().Contains(l.FilterId); } catch { /* vista sin filtros */ }
        if (applied)
        {
            l.Visible = view.GetFilterVisibility(l.FilterId);
            var ogs = view.GetFilterOverrides(l.FilterId);
            l.ColorOn = ogs.ProjectionLineColor.IsValid;
            l.Halftone = ogs.Halftone;
            return;
        }
        // Sin filtro en la vista: el estado lo dan sus anotaciones de esta vista, si tiene.
        var own = AnnotationsIn(doc, view, l).ToList();
        if (own.Count == 0) return;
        l.Visible = own.Any(e => !e.IsHidden(view));
        var first = view.GetElementOverrides(own[0].Id);
        l.ColorOn = first.ProjectionLineColor.IsValid;
        l.Halftone = first.Halftone;
    }

    private static IEnumerable<Element> AnnotationsIn(Document doc, View view, LayerInfo l) =>
        l.Annotations.Select(doc.GetElement).Where(e => e != null && e.OwnerViewId == view.Id)!;

    public static LayerViewState ViewState(Document doc, View? view)
    {
        var state = new LayerViewState { ViewName = view?.Name ?? "" };
        if (view == null || view.IsTemplate || view is ViewSchedule || !view.AreGraphicsOverridesAllowed())
        {
            state.Unsupported = true;
            return state;
        }
        if (view.ViewTemplateId != ElementId.InvalidElementId && doc.GetElement(view.ViewTemplateId) is View template)
        {
            var free = template.GetNonControlledTemplateParameterIds();
            if (!free.Contains(new ElementId(BuiltInParameter.VIS_GRAPHICS_FILTERS)))
                state.FilterBlock = $"La plantilla «{template.Name}» controla los filtros de esta vista: el ojo, el color y el semitono " +
                                    "solo actúan sobre las anotaciones. Para el resto, quita el control de filtros en la plantilla o desasígnala.";
        }
        return state;
    }

    public static string DefaultLayerName(Document doc)
    {
        var used = LayerFilters(doc).Select(DisplayName).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        for (int i = 1; ; i++)
            if (!used.Contains($"Capa {i}")) return $"Capa {i}";
    }

    // ── Escritura (todas requieren transacción abierta) ────────────────────────

    /// <summary>Nombres no válidos en Revit, más los que ya usa otra capa o filtro.</summary>
    public static string? ValidateName(Document doc, string name, ElementId? except = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Escribe un nombre para la capa.";
        const string bad = "\\:{}[]|;<>?`~";
        if (name.IndexOfAny(bad.ToCharArray()) >= 0) return $"El nombre no puede tener ninguno de estos caracteres: {bad}";
        var other = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement))
            .FirstOrDefault(f => f.Name.Equals(FilterPrefix + name, StringComparison.CurrentCultureIgnoreCase) && f.Id != except);
        return other == null ? null : $"Ya existe una capa o filtro llamado «{FilterPrefix + name}».";
    }

    public static ParameterFilterElement Create(Document doc, string name)
    {
        string? error = ValidateName(doc, name);
        if (error != null) throw new InvalidOperationException(error);
        var cats = EnsureParameter(doc);
        if (cats.Count == 0) throw new InvalidOperationException("Este modelo no tiene categorías que admitan capas.");

        int used = LayerFilters(doc).Count();
        var filter = ParameterFilterElement.Create(doc, FilterPrefix + name, cats, Rule(doc, name));
        var e = new Entity(GetSchema());
        e.Set(FColor, Pack(Palette[used % Palette.Length]));
        e.Set<IList<ElementId>>(FAnn, new List<ElementId>());
        e.Set(FExtra, "");
        filter.SetEntity(e);
        return filter;
    }

    private static ElementFilter Rule(Document doc, string name) =>
        new ElementParameterFilter(ParameterFilterRuleFactory.CreateEqualsRule(
            ParamId(doc) ?? throw new InvalidOperationException($"Falta el parámetro {ParamName}."), name));

    private static ParameterFilterElement Filter(Document doc, ElementId id) =>
        doc.GetElement(id) as ParameterFilterElement ?? throw new InvalidOperationException("La capa ya no existe (¿se deshizo o se borró?).");

    private static void WriteAnnotations(ParameterFilterElement f, List<ElementId> ids)
    {
        Entity e = f.GetEntity(GetSchema());
        e.Set<IList<ElementId>>(FAnn, ids.Distinct().ToList());
        f.SetEntity(e);
    }

    public static void SetColor(Document doc, ElementId filterId, Color color)
    {
        var f = Filter(doc, filterId);
        Entity e = f.GetEntity(GetSchema());
        e.Set(FColor, Pack(color));
        f.SetEntity(e);
    }

    /// <summary>Por qué no se puede editar el elemento en un modelo con worksharing (null si se puede).</summary>
    private static string? WorksharingBlock(Document doc, ElementId id)
    {
        if (!doc.IsWorkshared) return null;
        var status = WorksharingUtils.GetCheckoutStatus(doc, id, out string owner);
        if (status == CheckoutStatus.OwnedByOtherUser) return $"lo tiene {owner}";
        if (WorksharingUtils.GetModelUpdatesStatus(doc, id) == ModelUpdatesStatus.UpdatedInCentral)
            return "cambió en el central (Volver a cargar lo último)";
        return null;
    }

    private static string Label(Element e) => $"{e.Category?.Name ?? "?"} «{e.Name}» ({e.Id.Value})";

    /// <summary>
    /// Pone los elementos en la capa (los saca de la que tuvieran). Los de modelo reciben el
    /// parámetro; las anotaciones de vista quedan guardadas en el filtro. El resto se omite y se dice por qué.
    /// </summary>
    public static LayerAssignResult Assign(Document doc, ElementId filterId, ICollection<ElementId> ids, View? view)
    {
        var result = new LayerAssignResult();
        var target = Filter(doc, filterId);
        string name = DisplayName(target);
        Schema s = GetSchema();
        var annotationsToAdd = new List<ElementId>();

        // Estado de la capa en la vista antes de sumarle nada: lo nuevo lo toma (oculta, color...).
        LayerInfo? before = null;
        if (view != null)
        {
            before = Read(doc, target, s);
            ReadViewState(doc, view, before);
        }

        foreach (ElementId id in ids)
        {
            Element? el = doc.GetElement(id);
            if (el == null) continue;
            string? block = WorksharingBlock(doc, id);
            if (block != null) { result.Skipped.Add($"{Label(el)}: {block}"); continue; }

            Parameter? p = el.get_Parameter(ParamGuid);
            if (p != null)
            {
                if (p.IsReadOnly) { result.Skipped.Add($"{Label(el)}: el parámetro es de solo lectura (¿grupo?)"); continue; }
                p.Set(name);
                result.Done++;
            }
            else if (el.ViewSpecific && el.OwnerViewId != ElementId.InvalidElementId)
            {
                annotationsToAdd.Add(id);
            }
            else
            {
                result.Skipped.Add($"{Label(el)}: su categoría no admite capas");
            }
        }

        if (annotationsToAdd.Count > 0)
        {
            // Una anotación está en una sola capa: se saca de las demás.
            var others = LayerFilters(doc).Where(f => f.Id != filterId).ToList();
            foreach (var other in others)
            {
                var list = other.GetEntity(s).Get<IList<ElementId>>(FAnn).ToList();
                if (list.RemoveAll(annotationsToAdd.Contains) > 0)
                {
                    string? block = WorksharingBlock(doc, other.Id);
                    if (block != null) throw new InvalidOperationException($"No se puede modificar la capa «{DisplayName(other)}»: {block}.");
                    WriteAnnotations(other, list);
                }
            }
            string? targetBlock = WorksharingBlock(doc, filterId);
            if (targetBlock != null)
            {
                result.Skipped.Add($"{annotationsToAdd.Count} anotación(es): la capa {targetBlock}");
            }
            else
            {
                var current = target.GetEntity(s).Get<IList<ElementId>>(FAnn).ToList();
                WriteAnnotations(target, current.Concat(annotationsToAdd).ToList());
                result.Done += annotationsToAdd.Count;
            }
        }

        if (view != null && before != null)
        {
            before.Annotations = Read(doc, target, s).Annotations;
            ApplyAnnotations(doc, view, before, annotationsToAdd);
        }
        return result;
    }

    /// <summary>Saca los elementos de su capa, sea cual sea.</summary>
    public static LayerAssignResult Remove(Document doc, ICollection<ElementId> ids)
    {
        var result = new LayerAssignResult();
        Schema s = GetSchema();
        var remaining = ids.ToHashSet();

        foreach (ElementId id in ids)
        {
            Element? el = doc.GetElement(id);
            Parameter? p = el?.get_Parameter(ParamGuid);
            if (el == null || p == null || string.IsNullOrEmpty(p.AsString())) continue;
            remaining.Remove(id);
            string? block = WorksharingBlock(doc, id);
            if (block != null) { result.Skipped.Add($"{Label(el)}: {block}"); continue; }
            if (p.IsReadOnly) { result.Skipped.Add($"{Label(el)}: el parámetro es de solo lectura (¿grupo?)"); continue; }
            p.Set("");
            result.Done++;
        }

        if (remaining.Count == 0) return result;
        foreach (var f in LayerFilters(doc).ToList())
        {
            var list = f.GetEntity(s).Get<IList<ElementId>>(FAnn).ToList();
            var removed = list.Where(remaining.Contains).ToList();
            if (removed.Count == 0) continue;
            string? block = WorksharingBlock(doc, f.Id);
            if (block != null) { result.Skipped.Add($"{removed.Count} anotación(es) de «{DisplayName(f)}»: la capa {block}"); continue; }
            WriteAnnotations(f, list.Except(removed).ToList());
            ResetAnnotations(doc, removed);
            result.Done += removed.Count;
        }
        return result;
    }

    /// <summary>Renombra la capa y reescribe el parámetro de todos sus elementos. Todo o nada.</summary>
    public static void Rename(Document doc, ElementId filterId, string newName)
    {
        var f = Filter(doc, filterId);
        string old = DisplayName(f);
        if (old == newName) return;
        string? error = ValidateName(doc, newName, filterId);
        if (error != null) throw new InvalidOperationException(error);

        var members = Members(doc, f, null).Where(e => e.get_Parameter(ParamGuid) != null).ToList();
        var blocked = members.Select(e => (e, block: WorksharingBlock(doc, e.Id))).Where(x => x.block != null).ToList();
        string? filterBlock = WorksharingBlock(doc, filterId);
        if (filterBlock != null || blocked.Count > 0)
            throw new InvalidOperationException("No se puede renombrar: " + (filterBlock != null ? $"la capa {filterBlock}. " : "") +
                string.Join("; ", blocked.Take(10).Select(x => $"{Label(x.e)} {x.block}")) + (blocked.Count > 10 ? $" (y {blocked.Count - 10} más)" : ""));

        foreach (var e in members) e.get_Parameter(ParamGuid)!.Set(newName);
        f.Name = FilterPrefix + newName;
        f.SetElementFilter(Rule(doc, newName));
    }

    /// <summary>Borra la capa: sus elementos quedan sin capa y sus anotaciones vuelven a verse.</summary>
    public static LayerAssignResult Delete(Document doc, ElementId filterId)
    {
        var f = Filter(doc, filterId);
        string? filterBlock = WorksharingBlock(doc, filterId);
        if (filterBlock != null) throw new InvalidOperationException($"No se puede borrar: la capa {filterBlock}.");
        var layer = Read(doc, f, GetSchema());
        var result = new LayerAssignResult();
        foreach (var e in Members(doc, f, null))
        {
            Parameter? p = e.get_Parameter(ParamGuid);
            if (p == null) continue;
            string? block = WorksharingBlock(doc, e.Id);
            if (block != null || p.IsReadOnly) { result.Skipped.Add($"{Label(e)}: {block ?? "solo lectura"} (queda con «{layer.Name}» escrito)"); continue; }
            p.Set("");
            result.Done++;
        }
        ResetAnnotations(doc, layer.Annotations);
        doc.Delete(filterId);
        return result;
    }

    /// <summary>Elementos de la capa: de todo el modelo, o solo los visibles en la vista si se indica.</summary>
    public static List<Element> Members(Document doc, ParameterFilterElement f, View? view)
    {
        var cats = f.GetCategories();
        var list = new List<Element>();
        if (cats.Count > 0)
        {
            var collector = view == null ? new FilteredElementCollector(doc) : new FilteredElementCollector(doc, view.Id);
            list.AddRange(collector.WhereElementIsNotElementType()
                .WherePasses(new ElementMulticategoryFilter(cats))
                .WherePasses(f.GetElementFilter()));
        }
        var layer = Read(doc, f, GetSchema());
        list.AddRange(layer.Annotations.Select(doc.GetElement)
            .Where(e => e != null && (view == null || e.OwnerViewId == view.Id))!);
        return list;
    }

    public static List<ElementId> MemberIds(Document doc, ElementId filterId, View? view) =>
        Members(doc, Filter(doc, filterId), view).Select(e => e.Id).ToList();

    // ── Vista activa: ojo, color y semitono ────────────────────────────────────

    private static ElementId? _solidFill;

    private static ElementId SolidFill(Document doc)
    {
        if (_solidFill != null && doc.GetElement(_solidFill) is FillPatternElement) return _solidFill;
        _solidFill = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
            .FirstOrDefault(p => p.GetFillPattern().IsSolidFill)?.Id ?? ElementId.InvalidElementId;
        return _solidFill;
    }

    private static OverrideGraphicSettings Overrides(Document doc, LayerInfo l, bool surfaces)
    {
        var ogs = new OverrideGraphicSettings();
        if (l.ColorOn)
        {
            ogs.SetProjectionLineColor(l.Color);
            ogs.SetCutLineColor(l.Color);
            ElementId solid = SolidFill(doc);
            if (surfaces && solid != ElementId.InvalidElementId)
            {
                ogs.SetSurfaceForegroundPatternId(solid);
                ogs.SetSurfaceForegroundPatternColor(l.Color);
                ogs.SetCutForegroundPatternId(solid);
                ogs.SetCutForegroundPatternColor(l.Color);
            }
        }
        ogs.SetHalftone(l.Halftone);
        return ogs;
    }

    /// <summary>
    /// Aplica a la vista el estado de la capa (Visible, ColorOn, Halftone ya cambiados en
    /// <paramref name="l"/>). Devuelve un aviso si los filtros los controla la plantilla.
    /// </summary>
    public static string? Apply(Document doc, View view, LayerInfo l)
    {
        string? warning = null;
        if (ViewState(doc, view).FilterBlock is { } block)
        {
            warning = block;
        }
        else
        {
            bool applied = view.GetFilters().Contains(l.FilterId);
            bool neutral = l.Visible && !l.ColorOn && !l.Halftone;
            if (!applied && !neutral) { view.AddFilter(l.FilterId); applied = true; }
            if (applied)
            {
                view.SetFilterVisibility(l.FilterId, l.Visible);
                view.SetFilterOverrides(l.FilterId, Overrides(doc, l, surfaces: true));
            }
        }
        ApplyAnnotations(doc, view, l, null);
        return warning;
    }

    /// <summary>Oculta/muestra y colorea las anotaciones de la capa que son de esta vista (todas, o solo <paramref name="only"/>).</summary>
    private static void ApplyAnnotations(Document doc, View view, LayerInfo l, ICollection<ElementId>? only)
    {
        var own = AnnotationsIn(doc, view, l).Where(e => only == null || only.Contains(e.Id)).ToList();
        if (own.Count == 0) return;
        var ogs = Overrides(doc, l, surfaces: false);
        foreach (var e in own) view.SetElementOverrides(e.Id, ogs);
        var toHide = own.Where(e => l.Visible ? false : !e.IsHidden(view) && e.CanBeHidden(view)).Select(e => e.Id).ToList();
        var toShow = own.Where(e => l.Visible && e.IsHidden(view)).Select(e => e.Id).ToList();
        if (toHide.Count > 0) view.HideElements(toHide);
        if (toShow.Count > 0) view.UnhideElements(toShow);
    }

    /// <summary>Anotaciones que salen de una capa: se vuelven a ver y pierden el color en su vista.</summary>
    private static void ResetAnnotations(Document doc, IEnumerable<ElementId> ids)
    {
        foreach (var id in ids)
        {
            if (doc.GetElement(id) is not { } e || doc.GetElement(e.OwnerViewId) is not View v) continue;
            try
            {
                v.SetElementOverrides(id, new OverrideGraphicSettings());
                if (e.IsHidden(v)) v.UnhideElements(new List<ElementId> { id });
            }
            catch { /* vista que no admite overrides: queda como estaba */ }
        }
    }
}
