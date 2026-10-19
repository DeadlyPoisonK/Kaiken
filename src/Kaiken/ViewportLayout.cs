using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace Kaiken;

/// <summary>
/// Alineación estándar de viewports, guardada en un JSON para aplicarla en cualquier modelo del
/// proyecto (todas las especialidades). Una entrada por sector: el scope box que lleva la vista y
/// dónde cae en el papel el centro de su recorte, relativo al origen del cajetín.
/// </summary>
public sealed class ViewportLayoutFile
{
    public int Version { get; set; } = 1;
    public string SourceModel { get; set; } = "";
    public DateTime Created { get; set; }
    public List<ViewportLayoutEntry> Entries { get; set; } = new();

    public static string DefaultFolder => Path.Combine(KaikenSettings.FolderPath, "Alineaciones");
    private static string LastFilePath => Path.Combine(DefaultFolder, "ultimo.txt");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // tildes legibles en el archivo
    };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        RememberLast(path);
    }

    public static ViewportLayoutFile Load(string path)
    {
        var file = JsonSerializer.Deserialize<ViewportLayoutFile>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidDataException("El archivo está vacío.");
        RememberLast(path);
        return file;
    }

    /// <summary>El último archivo guardado o aplicado, para proponerlo en el diálogo.</summary>
    public static string? LastFile()
    {
        try
        {
            string path = File.Exists(LastFilePath) ? File.ReadAllText(LastFilePath).Trim() : "";
            return path.Length > 0 && File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    private static void RememberLast(string path)
    {
        try
        {
            Directory.CreateDirectory(DefaultFolder);
            File.WriteAllText(LastFilePath, path);
        }
        catch { /* solo es una sugerencia para el diálogo */ }
    }
}

public sealed class ViewportLayoutEntry
{
    public string Sector { get; set; } = "";
    public string ScopeBox { get; set; } = "";
    public int Scale { get; set; }
    public string Rotation { get; set; } = nameof(ViewportRotation.None);
    /// <summary>Centro del recorte en el papel, en mm, relativo al origen del cajetín (o de la lámina si no tiene).</summary>
    public double SheetXmm { get; set; }
    public double SheetYmm { get; set; }
    public string SourceSheet { get; set; } = "";
    public string SourceView { get; set; } = "";
}

/// <summary>Piezas comunes de Guardar Alineación y Alinear Viewports.</summary>
internal static class ViewportLayoutHelpers
{
    public const string NoTitleTypeName = "Sin título";
    private const double MmPerFoot = 304.8;
    private static readonly Regex SectorPattern = new(@"^[A-Z]{1,3}\d{1,3}$", RegexOptions.IgnoreCase);

    /// <summary>Sector de la lámina: la última palabra de su nombre si tiene forma de sector (A1, B3, AB12).</summary>
    public static string? Sector(ViewSheet sheet)
    {
        string last = sheet.Name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return SectorPattern.IsMatch(last) ? last.ToUpperInvariant() : null;
    }

    /// <summary>El viewport de planta de la lámina; si hay varios, el más grande.</summary>
    public static Viewport? MainViewport(Document doc, ViewSheet sheet)
    {
        return sheet.GetAllViewports()
            .Select(id => doc.GetElement(id)).OfType<Viewport>()
            .Where(vp => doc.GetElement(vp.ViewId) is ViewPlan)
            .OrderByDescending(vp =>
            {
                Outline o = vp.GetBoxOutline();
                XYZ d = o.MaximumPoint - o.MinimumPoint;
                return d.X * d.Y;
            })
            .FirstOrDefault();
    }

    public static ElementId ScopeBoxId(View view) =>
        view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP)?.AsElementId() ?? ElementId.InvalidElementId;

    public static string ScopeBoxName(Document doc, View view)
    {
        ElementId id = ScopeBoxId(view);
        return id == ElementId.InvalidElementId ? "" : doc.GetElement(id)?.Name ?? "";
    }

    /// <summary>Scope boxes del modelo por nombre (sin distinguir mayúsculas).</summary>
    public static Dictionary<string, ElementId> ScopeBoxesByName(Document doc) =>
        new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType()
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

    public static ElementId? FindNoTitleType(Document doc, Viewport sample)
    {
        string wanted = Normalize(NoTitleTypeName);
        return sample.GetValidTypes()
            .FirstOrDefault(id => doc.GetElement(id) is ElementType t && Normalize(t.Name) == wanted);
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Dónde cae en el papel el centro del recorte de la vista, relativo al cajetín, en mm.
    /// Con el scope box asignado, el recorte es el del scope box: el mismo en todos los modelos.
    /// </summary>
    public static (double X, double Y)? CropCenterOnSheetMm(Document doc, ViewSheet sheet, Viewport vp, View view)
    {
        BoundingBoxXYZ box = view.CropBox;
        XYZ model = box.Transform.OfPoint((box.Min + box.Max) / 2);
        IList<TransformWithBoundary> transforms = view.GetModelToProjectionTransforms();
        if (transforms.Count == 0) return null;
        XYZ projected = transforms[0].GetModelToProjectionTransform().OfPoint(model);
        XYZ p = vp.GetProjectionToSheetTransform().OfPoint(projected) - TitleBlockOrigin(doc, sheet);
        return (p.X * MmPerFoot, p.Y * MmPerFoot);
    }

    public static XYZ MmToFeet(double x, double y) => new(x / MmPerFoot, y / MmPerFoot, 0);

    /// <summary>Punto de inserción del cajetín de la lámina; el origen si no tiene.</summary>
    public static XYZ TitleBlockOrigin(Document doc, ViewSheet sheet)
    {
        var titleBlock = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType().OfType<FamilyInstance>().FirstOrDefault();
        XYZ p = (titleBlock?.Location as LocationPoint)?.Point ?? XYZ.Zero;
        return new XYZ(p.X, p.Y, 0);
    }

    /// <summary>Recorte activo y oculto, annotation crop marcado y, si se pide, scope/section boxes ocultos.</summary>
    public static void ConfigureView(View view, bool hideBoxes, SortedSet<string> templateWarnings)
    {
        if (!view.CropBoxActive) view.CropBoxActive = true;
        if (view.CropBoxVisible) view.CropBoxVisible = false;
        Parameter? annotation = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
        if (annotation != null && !annotation.IsReadOnly && annotation.AsInteger() != 1) annotation.Set(1);

        if (!hideBoxes) return;
        foreach (var bic in new[] { BuiltInCategory.OST_VolumeOfInterest, BuiltInCategory.OST_SectionBox })
        {
            var catId = new ElementId(bic);
            if (!view.CanCategoryBeHidden(catId) || view.GetCategoryHidden(catId)) continue;
            try { view.SetCategoryHidden(catId, true); }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // La plantilla de vista controla V/G: se oculta en la plantilla, no en la vista.
                templateWarnings.Add(view.Document.GetElement(view.ViewTemplateId)?.Name ?? view.Name);
            }
        }
    }
}
