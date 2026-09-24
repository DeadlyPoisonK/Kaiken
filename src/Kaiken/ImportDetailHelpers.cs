using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Microsoft.Win32;

namespace Kaiken;

/// <summary>
/// Utilidades para <see cref="ImportDetailCommand"/>:
/// validación de vista, selector de archivo, detección de ImportInstance existente,
/// y corrección de TextNoteTypes con fuentes SHX o inválidas generadas por el Explode.
/// </summary>
public static class ImportDetailHelpers
{
    // ─── Tabla de fuentes SHX → Windows ─────────────────────────────────────────
    // Cuando Revit explota un DWG con fuentes .shx (AutoCAD nativas), crea
    // TextNoteTypes con esas fuentes que Windows no reconoce — los textos se
    // muestran como rectángulos o con sustitución incorrecta.
    // Este mapa reemplaza cada fuente CAD por su equivalente Windows más fiel.
    private static readonly Dictionary<string, string> ShxToWindowsFont =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Familias romans — la más común en proyectos de ingeniería/arquitectura
            ["romans.shx"]   = "Arial",
            ["romand.shx"]   = "Arial",
            ["romant.shx"]   = "Times New Roman",
            ["romanc.shx"]   = "Times New Roman",
            // Italic
            ["italic.shx"]   = "Arial",
            ["italicc.shx"]  = "Times New Roman",
            ["italict.shx"]  = "Arial",
            // Simplex / txt — fuentes de trazo simple para bocetos
            ["simplex.shx"]  = "Arial Narrow",
            ["txt.shx"]      = "Arial",
            ["monotxt.shx"]  = "Courier New",
            // Complex
            ["complex.shx"]  = "Times New Roman",
            // Gothic
            ["gothice.shx"]  = "Arial",
            ["gothicg.shx"]  = "Arial",
            ["gothici.shx"]  = "Arial",
            // Greek / script / sym
            ["greeks.shx"]   = "Arial",
            ["scriptc.shx"]  = "Arial",
            ["scripts.shx"]  = "Arial",
            ["symap.shx"]    = "Arial",
            ["symath.shx"]   = "Arial",
            ["symeteo.shx"]  = "Arial",
            ["symusic.shx"]  = "Arial",
        };

    /// <summary>Fuente Windows usada cuando no hay mapeo explícito y la fuente es SHX.</summary>
    private const string FallbackFont = "Arial";

    // ─── Validación de vista ─────────────────────────────────────────────────────

    /// <summary>
    /// Devuelve true si el tipo de vista acepta importaciones CAD de Revit.
    /// Las vistas de tipo Sheet no están incluidas — importar directo a un plano
    /// no es el caso de uso de esta herramienta.
    /// </summary>
    public static bool ViewSupportsCadImport(View view) =>
        view.ViewType is ViewType.FloorPlan
            or ViewType.CeilingPlan
            or ViewType.Elevation
            or ViewType.Section
            or ViewType.Detail
            or ViewType.DraftingView
            or ViewType.Legend;

    // ─── Selector de archivo ─────────────────────────────────────────────────────

    /// <summary>
    /// Muestra un OpenFileDialog WPF para elegir un DWG o DXF.
    /// Devuelve la ruta completa, o null si el usuario cancela.
    /// </summary>
    public static string? ChooseCadFile()
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Seleccionar plano de detalle para insertar (DXF/DWG)",
            Filter = "Planos CAD (*.dwg;*.dxf)|*.dwg;*.dxf|Archivos DXF (*.dxf)|*.dxf|Archivos DWG (*.dwg)|*.dwg|Todos los archivos (*.*)|*.*",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    // ─── Snapshot de elementos ───────────────────────────────────────────────────

    /// <summary>
    /// Recoge los ElementIds de TODOS los elementos del tipo T presentes en el
    /// documento en este momento. Útil para calcular el diff después de un Explode.
    /// </summary>
    public static HashSet<ElementId> SnapshotElementIds<T>(Document doc) where T : Element =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(T))
            .ToElementIds()
            .ToHashSet();

    // ─── Detección de importación existente ─────────────────────────────────────

    /// <summary>
    /// Busca en la vista activa un ImportInstance (no vinculado) cuyo nombre de
    /// símbolo coincide sin extensión con el archivo elegido. Devuelve null si no
    /// hay ninguno.
    /// </summary>
    public static ImportInstance? FindExistingImport(Document doc, View view, string filePath)
    {
        string baseName = Path.GetFileNameWithoutExtension(filePath);

        return new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(ImportInstance))
            .Cast<ImportInstance>()
            .Where(i => !i.IsLinked)
            .FirstOrDefault(i =>
            {
                // El nombre del símbolo en Revit coincide con el nombre del archivo DWG
                var p = i.get_Parameter(BuiltInParameter.IMPORT_SYMBOL_NAME);
                string? symName = p?.AsString();
                return symName != null &&
                    string.Equals(
                        Path.GetFileNameWithoutExtension(symName),
                        baseName,
                        StringComparison.OrdinalIgnoreCase);
            });
    }

    // ─── Corrección de TextNoteTypes post-Explode ────────────────────────────────

    /// <summary>
    /// Encuentra los TextNoteType creados DESPUÉS del snapshot (es decir, los que
    /// generó el Explode a partir de estilos de texto CAD) y sustituye sus fuentes
    /// SHX o inválidas por equivalentes de Windows.
    ///
    /// Por qué funciona: Revit ya leyó la tabla STYLE del DWG durante el Explode y
    /// creó los TextNoteType con los nombres de estilo correctos — solo la fuente
    /// asignada puede ser inválida (SHX). Al fijar la fuente en el Type, todos los
    /// TextNote que lo usan se corrigen automáticamente.
    ///
    /// Devuelve (corregidos, fallidos).
    /// Debe llamarse dentro de una Transaction abierta.
    /// </summary>
    public static (int Fixed, int Failed) FixNewTextNoteTypes(
        Document doc,
        HashSet<ElementId> preSnapshot)
    {
        int fixedCount = 0, failedCount = 0;

        // Solo los TextNoteType que NO estaban antes del Explode
        var newTypes = new FilteredElementCollector(doc)
            .OfClass(typeof(TextNoteType))
            .Cast<TextNoteType>()
            .Where(t => !preSnapshot.Contains(t.Id))
            .ToList();

        foreach (var tnt in newTypes)
        {
            try
            {
                var fontParam = tnt.get_Parameter(BuiltInParameter.TEXT_FONT);
                if (fontParam is null || fontParam.IsReadOnly)
                    continue;

                string currentFont = fontParam.AsString() ?? string.Empty;
                string? replacement = GetWindowsFont(currentFont);

                // null = la fuente ya es válida en Windows, no se toca
                if (replacement is null)
                    continue;

                fontParam.Set(replacement);
                fixedCount++;
            }
            catch
            {
                failedCount++;
            }
        }

        return (fixedCount, failedCount);
    }

    // ─── Lógica de sustitución de fuentes ────────────────────────────────────────

    /// <summary>
    /// Dado el nombre de fuente que Revit asignó al TextNoteType, devuelve la fuente
    /// Windows de reemplazo, o null si la fuente ya es válida y no debe cambiarse.
    /// </summary>
    private static string? GetWindowsFont(string cadFont)
    {
        if (string.IsNullOrWhiteSpace(cadFont))
            return FallbackFont;

        // 1. Mapeo explícito (incluye las fuentes SHX más comunes)
        if (ShxToWindowsFont.TryGetValue(cadFont, out string? mapped))
            return mapped;

        // 2. Cualquier fuente que termine en ".shx" no listada → Arial
        if (cadFont.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
            return FallbackFont;

        // 3. Ruta completa que contenga ".shx" (ej. C:\fonts\romans.shx)
        if (cadFont.Contains(".shx", StringComparison.OrdinalIgnoreCase))
            return FallbackFont;

        // 4. La fuente parece válida para Windows → no cambiar
        return null;
    }

    /// <summary>
    /// Recrea todos los textos leídos del DXF como TextNotes nativos en la vista activa de Revit,
    /// aplicando la transformación (posición, escala, rotación) del ImportInstance seleccionado.
    /// Devuelve la cantidad de textos creados con éxito.
    /// </summary>
    public static int RecreateTextsFromDxf(Document doc, View view, string dxfPath, Transform transform, double? customFeetPerUnit = null)
    {
        if (!File.Exists(dxfPath)) return 0;

        DxfData dxfData;
        try
        {
            dxfData = EnchufesDxf.Read(dxfPath);
        }
        catch
        {
            return 0;
        }

        if (dxfData.Texts.Count == 0) return 0;

        if (customFeetPerUnit.HasValue)
        {
            dxfData.FeetPerUnit = customFeetPerUnit.Value;
        }

        int createdCount = 0;
        double viewScale = view.Scale;
        if (viewScale <= 0) viewScale = 1.0;

        // Calcular la rotación del ImportInstance en el plano XY
        double transformAngleRad = Math.Atan2(transform.BasisX.Y, transform.BasisX.X);
        // Obtener el factor de escala aplicado al transform del ImportInstance
        double transformScale = transform.BasisX.GetLength();

        foreach (var dxfText in dxfData.Texts)
        {
            if (string.IsNullOrWhiteSpace(dxfText.Text))
                continue;

            try
            {
                // 1. Calcular posición local en pies (Revit internal units)
                double xFeet = dxfText.X * dxfData.FeetPerUnit;
                double yFeet = dxfText.Y * dxfData.FeetPerUnit;
                var localPosition = new XYZ(xFeet, yFeet, 0);

                // 2. Transformar a la coordenada global del plano posicionado en Revit
                var globalPosition = transform.OfPoint(localPosition);

                // 3. Determinar la fuente y el estilo de texto
                string fontName = FallbackFont;
                if (!string.IsNullOrEmpty(dxfText.StyleName) &&
                    dxfData.TextStyles.TryGetValue(dxfText.StyleName, out var style))
                {
                    string fontFile = style.FontFile;
                    string? mappedFont = GetWindowsFont(fontFile);
                    fontName = mappedFont ?? (string.IsNullOrEmpty(fontFile) ? FallbackFont : Path.GetFileNameWithoutExtension(fontFile));
                }

                // 4. Calcular el tamaño en papel en pies (aplicando la escala de la vista y del plano)
                double heightFeet = dxfText.HeightCad * dxfData.FeetPerUnit;
                double scaledHeightFeet = heightFeet * transformScale;
                double paperSizeFeet = scaledHeightFeet / viewScale;

                // Evitar tamaños absurdamente pequeños/grandes
                if (paperSizeFeet < 0.001) paperSizeFeet = 0.005; // ~1.5mm
                if (paperSizeFeet > 1.0)   paperSizeFeet = 0.08;  // ~24mm

                // 5. Obtener o crear el TextNoteType
                ElementId textTypeId = GetOrCreateTextNoteType(doc, fontName, paperSizeFeet);

                // 6. Crear el TextNote
                var textNote = TextNote.Create(doc, view.Id, globalPosition, dxfText.Text, textTypeId);
                if (textNote == null)
                    continue;

                // 7. Aplicar la rotación total (rotación local del texto + rotación del plano)
                double totalAngleRad = (dxfText.RotationDeg * Math.PI / 180.0) + transformAngleRad;
                if (Math.Abs(totalAngleRad) > 0.01)
                {
                    var axis = Line.CreateBound(globalPosition, globalPosition + XYZ.BasisZ);
                    ElementTransformUtils.RotateElement(doc, textNote.Id, axis, totalAngleRad);
                }

                createdCount++;
            }
            catch
            {
                // Ignorar fallos individuales
            }
        }

        return createdCount;
    }

    /// <summary>
    /// Obtiene un TextNoteType con la fuente y tamaño indicados, o lo crea duplicando el tipo por defecto.
    /// </summary>
    public static ElementId GetOrCreateTextNoteType(Document doc, string fontName, double sizeFeet)
    {
        double sizeMm = sizeFeet * 304.8;
        string typeName = $"CAD_{fontName}_{sizeMm:F2}mm";

        var existingType = new FilteredElementCollector(doc)
            .OfClass(typeof(TextNoteType))
            .Cast<TextNoteType>()
            .FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));

        if (existingType != null)
            return existingType.Id;

        ElementId defaultTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        var defaultType = doc.GetElement(defaultTypeId) as TextNoteType;
        if (defaultType == null)
            return defaultTypeId;

        var newType = defaultType.Duplicate(typeName) as TextNoteType;
        if (newType == null)
            return defaultTypeId;

        newType.get_Parameter(BuiltInParameter.TEXT_FONT)?.Set(fontName);
        newType.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(sizeFeet);
        newType.get_Parameter(BuiltInParameter.TEXT_BACKGROUND)?.Set(1); // Transparent background

        return newType.Id;
    }
}
