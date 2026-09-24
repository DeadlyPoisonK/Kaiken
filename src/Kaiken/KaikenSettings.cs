using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.DB;

namespace Kaiken;

/// <summary>
/// Configuración del usuario en %APPDATA%\Kaiken\settings.json. Todo lo que antes estaba
/// fijo para un proyecto concreto (nombres de parámetros, formato del Número de Plano,
/// rutas del batch IFC) vive aquí, para que Kaiken sirva en cualquier oficina.
/// Si el archivo no existe se crea con los valores por defecto la primera vez que se lee,
/// así el usuario tiene algo que editar. Un archivo inválido no rompe el add-in: se usan
/// los valores por defecto.
/// </summary>
public sealed class KaikenSettings
{
    public LiveBridgeSettings LiveBridge { get; set; } = new();
    public RevisionSettings Revisions { get; set; } = new();
    public IfcBatchSettings IfcBatch { get; set; } = new();

    public static string FolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kaiken");

    public static string FilePath => Path.Combine(FolderPath, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>
    /// Se relee en cada llamada (el archivo es chico) para que un cambio en settings.json
    /// se note en el próximo comando sin reiniciar Revit.
    /// </summary>
    public static KaikenSettings Current => Load();

    public static KaikenSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<KaikenSettings>(File.ReadAllText(FilePath), JsonOptions)
                       ?? new KaikenSettings();
            }

            var defaults = new KaikenSettings();
            Directory.CreateDirectory(FolderPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(defaults, JsonOptions));
            return defaults;
        }
        catch
        {
            return new KaikenSettings();
        }
    }
}

public sealed class LiveBridgeSettings
{
    /// <summary>
    /// Servidor TCP local para que un agente (Claude vía MCP) lea/edite el modelo abierto.
    /// Apagado por defecto: la mayoría de los usuarios no lo necesita.
    /// </summary>
    public bool Enabled { get; set; } = false;
    public int Port { get; set; } = 5551;
}

public sealed class RevisionSettings
{
    /// <summary>Parámetro de texto de las láminas con el código de revisión.</summary>
    public string RevisionParameter { get; set; } = "INFO_10_Revision";

    /// <summary>Parámetro de texto de las láminas con la fecha de revisión.</summary>
    public string DateParameter { get; set; } = "INFO_Fecha";

    /// <summary>Separador de segmentos del Número de Plano.</summary>
    public string SheetNumberSeparator { get; set; } = "-";

    /// <summary>
    /// Posición (contando desde 1) del segmento del Número de Plano que contiene la revisión.
    /// 0 desactiva la lectura/escritura del código de revisión en el Número de Plano.
    /// </summary>
    public int SheetNumberRevisionSegment { get; set; } = 10;

    /// <summary>
    /// Con el filtro activado en la ventana, solo se procesan láminas cuyo Número de Plano
    /// tenga más caracteres que esto (sirve para omitir portadas y láminas secundarias).
    /// </summary>
    public int MinSheetNumberLength { get; set; } = 29;

    /// <summary>Números de plano que se procesan siempre, aunque no pasen el filtro de longitud.</summary>
    public string[] AlwaysIncludeSheets { get; set; } = { "PORTADA" };

    public bool PassesLengthFilter(ViewSheet sheet) =>
        (sheet.SheetNumber ?? "").Length > MinSheetNumberLength
        || AlwaysIncludeSheets.Any(n => string.Equals((sheet.SheetNumber ?? "").Trim(), n, StringComparison.OrdinalIgnoreCase));

    /// <summary>Lee el código de revisión del Número de Plano, o null si no tiene ese segmento.</summary>
    public string? GetRevisionSegment(string sheetNumber)
    {
        if (SheetNumberRevisionSegment <= 0) return null;
        string[] parts = sheetNumber.Split(SheetNumberSeparator);
        return parts.Length >= SheetNumberRevisionSegment ? parts[SheetNumberRevisionSegment - 1] : null;
    }

    /// <summary>Devuelve el Número de Plano con el segmento de revisión reemplazado, o null si no aplica.</summary>
    public string? WithRevisionSegment(string sheetNumber, string revision)
    {
        if (SheetNumberRevisionSegment <= 0) return null;
        string[] parts = sheetNumber.Split(SheetNumberSeparator);
        if (parts.Length < SheetNumberRevisionSegment) return null;
        parts[SheetNumberRevisionSegment - 1] = revision;
        return string.Join(SheetNumberSeparator, parts);
    }

    public string LengthFilterLabel => $"Procesar solo láminas con Número de Plano > {MinSheetNumberLength} caracteres";
}

public sealed class IfcBatchSettings
{
    /// <summary>Carpeta raíz de exportación. Vacío = se pregunta cada vez.</summary>
    public string ExportFolder { get; set; } = "";

    /// <summary>
    /// Regex que extrae el código del modelo desde el título del documento (grupo 1).
    /// Con un código, el IFC va a &lt;ExportFolder&gt;\&lt;código&gt;\IFC. Vacío = todos los
    /// documentos abiertos, cada uno directo en ExportFolder.
    /// </summary>
    public string ModelCodeRegex { get; set; } = "";

    /// <summary>Códigos esperados (solo informativo/orden). Vacío = los que se encuentren abiertos.</summary>
    public string[] ModelCodes { get; set; } = Array.Empty<string>();

    /// <summary>Regex del nombre de la vista 3D a exportar. Vacío = la vista 3D por defecto "{3D}".</summary>
    public string ViewNameRegex { get; set; } = "";

    /// <summary>Archivo de log. Vacío = ifc_batch_log.txt dentro de ExportFolder.</summary>
    public string LogPath { get; set; } = "";
}
