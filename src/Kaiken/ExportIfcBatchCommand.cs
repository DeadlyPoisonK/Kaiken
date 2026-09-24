using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Exportación IFC batch de todos los modelos abiertos en la sesión.
///
/// Historia: la primera versión abría cada modelo por API
/// (ModelPathUtils.ConvertCloudGUIDsToCloudPath + Application.OpenDocumentFile)
/// y lo cerraba al terminar. Se probó el 2026-08-03 y murió en el primer
/// modelo con "Hosting components are already initialized.
/// Re-initialization for an app is not allowed." — un fallo de bajo nivel de
/// .NET (ver dotnet/runtime#99858) que ni siquiera pasa por el try/catch.
/// Conclusión: abrir/cerrar documentos por API de forma repetida dentro de un
/// add-in ya alojado en el proceso de Revit no es confiable.
///
/// Diseño actual: el comando NO abre ni cierra documentos. El usuario abre los
/// modelos a mano (como pestañas, en la misma ventana — eso sí es estable) y el
/// botón recorre Application.Documents, exporta cada uno a IFC y no los cierra.
///
/// Todo lo específico de un proyecto sale de la sección "ifcBatch" de
/// %APPDATA%\Kaiken\settings.json (ver KaikenSettings.IfcBatchSettings):
/// - exportFolder: carpeta raíz (vacío = se pregunta).
/// - modelCodeRegex: extrae un código del título (grupo 1); con código el IFC va a
///   &lt;exportFolder&gt;\&lt;código&gt;\IFC. Vacío = todos los documentos abiertos.
/// - modelCodes: códigos esperados; los que falten se reportan como salteados.
/// - viewNameRegex: vista 3D a exportar (vacío = la vista "{3D}"). Si un modelo no
///   la tiene, se SALTA y se anota en el log, sin abortar el resto.
///
/// Muestra una consola aparte (AllocConsole) con progreso en vivo y además
/// escribe todo a un archivo de log en disco.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ExportIfcBatchCommand : IExternalCommand
{
    private const string Title = "Exportar IFC — Batch";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Autodesk.Revit.ApplicationServices.Application app = commandData.Application.Application;
        IfcBatchSettings cfg = KaikenSettings.Current.IfcBatch;

        string baseExportPath = cfg.ExportFolder;
        if (string.IsNullOrWhiteSpace(baseExportPath))
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Carpeta de exportación IFC" };
            if (dialog.ShowDialog() != true) return Result.Cancelled;
            baseExportPath = dialog.FolderName;
        }

        string logPath = string.IsNullOrWhiteSpace(cfg.LogPath)
            ? Path.Combine(baseExportPath, "ifc_batch_log.txt")
            : cfg.LogPath;

        List<(string Key, Document Doc)> abiertos = FindOpenDocuments(app, cfg, out List<string> duplicados);

        // Con códigos esperados configurados se sigue ese orden y los que falten se
        // reportan; sin ellos, se exporta lo que haya abierto.
        List<string> claves = cfg.ModelCodes.Length > 0
            ? cfg.ModelCodes.Select(c => c.ToUpperInvariant()).ToList()
            : abiertos.Select(a => a.Key).ToList();

        if (claves.Count == 0)
        {
            TaskDialog.Show(Title, "No hay modelos abiertos que coincidan con la configuración de ifcBatch en settings.json.");
            return Result.Cancelled;
        }

        TaskDialogResult confirm = TaskDialog.Show(
            Title,
            "Este comando NO abre ni cierra documentos — solo exporta a IFC los que YA estén abiertos " +
            "en esta sesión de Revit.\n\n" +
            $"Modelos: {string.Join(", ", claves)}\n" +
            $"Carpeta: {baseExportPath}\n\n" +
            "Al terminar, ciérralos tú manualmente sin guardar.\n\n" +
            "¿Continuar?",
            TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No);
        if (confirm != TaskDialogResult.Yes) return Result.Cancelled;

        using var progress = BatchProgressConsole.Start(claves.Count, logPath);
        foreach (string aviso in duplicados) progress.Log(aviso);

        var porClave = abiertos.ToDictionary(a => a.Key, a => a.Doc, StringComparer.OrdinalIgnoreCase);
        progress.Log($"Documentos detectados abiertos y reconocidos: {porClave.Count} " +
                     $"({(porClave.Count == 0 ? "ninguno" : string.Join(", ", porClave.Keys))})");

        bool usaCodigos = !string.IsNullOrWhiteSpace(cfg.ModelCodeRegex);
        int ok = 0, fail = 0, skip = 0;

        for (int i = 0; i < claves.Count; i++)
        {
            string clave = claves[i];
            progress.StartModel(i + 1, clave);

            if (!porClave.TryGetValue(clave, out Document? doc))
            {
                progress.SkipModel(clave, "No está abierto en esta sesión de Revit.");
                skip++;
                continue;
            }

            try
            {
                View3D? vista = FindExportView(doc, cfg.ViewNameRegex);
                if (vista == null)
                {
                    progress.SkipModel(clave, string.IsNullOrWhiteSpace(cfg.ViewNameRegex)
                        ? "No se encontró la vista 3D \"{3D}\"."
                        : $"No se encontró ninguna vista 3D que coincida con \"{cfg.ViewNameRegex}\".");
                    skip++;
                    continue;
                }

                string folder = usaCodigos ? Path.Combine(baseExportPath, clave, "IFC") : baseExportPath;
                Directory.CreateDirectory(folder);
                string fileName = ExportHelpers.LimpiarNombre(doc.Title) + ".ifc";

                IFCExportOptions ifcOptions = ExportHelpers.BuildIfcOptions(vista);

                using (var tx = new Transaction(doc, "Exportar IFC batch"))
                {
                    tx.Start();
                    doc.Export(folder, fileName, ifcOptions);
                    tx.Commit();
                }

                progress.CompleteModel(clave);
                ok++;
            }
            catch (Exception ex)
            {
                progress.FailModel(clave, ex.InnerException?.Message ?? ex.Message);
                fail++;
            }
        }

        progress.Finish(ok, fail, skip);

        TaskDialog.Show(
            Title,
            $"Terminado.\n✔ {ok}   ✘ {fail}   ⚠ {skip}\n\n" +
            "Los documentos siguen abiertos — ciérralos manualmente sin guardar cuando quieras.");

        return Result.Succeeded;
    }

    /// <summary>
    /// Recorre los documentos abiertos (excluyendo los cargados solo como vínculo
    /// de otro y las familias). Con modelCodeRegex, la clave es el código extraído
    /// del título (grupo 1) y se ignoran los que no coinciden; sin él, la clave es
    /// el título. Si dos documentos dan la misma clave se usa el primero y se
    /// devuelve un aviso para el log.
    /// </summary>
    private static List<(string Key, Document Doc)> FindOpenDocuments(
        Autodesk.Revit.ApplicationServices.Application app, IfcBatchSettings cfg, out List<string> duplicados)
    {
        var result = new List<(string Key, Document Doc)>();
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        duplicados = new List<string>();

        foreach (Document d in app.Documents)
        {
            if (d.IsLinked || d.IsFamilyDocument) continue;

            string clave;
            if (string.IsNullOrWhiteSpace(cfg.ModelCodeRegex))
            {
                clave = d.Title;
            }
            else
            {
                Match match = Regex.Match(d.Title, cfg.ModelCodeRegex, RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                clave = (match.Groups.Count > 1 ? match.Groups[1].Value : match.Value).ToUpperInvariant();
            }

            if (!vistos.Add(clave))
            {
                duplicados.Add($"    ⚠ Más de un documento abierto coincide con {clave}: se ignora \"{d.Title}\".");
                continue;
            }

            result.Add((clave, d));
        }

        return result;
    }

    /// <summary>
    /// Con patrón: la primera vista 3D no-template cuyo nombre coincida (sin
    /// distinguir mayúsculas). Sin patrón: la vista 3D por defecto "{3D}".
    /// </summary>
    private static View3D? FindExportView(Document doc, string viewNameRegex)
    {
        var vistas = new FilteredElementCollector(doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .Where(v => !v.IsTemplate);

        return string.IsNullOrWhiteSpace(viewNameRegex)
            ? vistas.FirstOrDefault(v => v.Name == "{3D}")
            : vistas.FirstOrDefault(v => Regex.IsMatch(v.Name, viewNameRegex, RegexOptions.IgnoreCase));
    }
}

// ────────────────────────────────────────────────────────────────────────────
// Consola de progreso para el batch — extiende la idea de ExportProgressConsole
// (ExportIfcCommand.cs) pero con noción de "modelo N de TOTAL" y log en disco.
// ────────────────────────────────────────────────────────────────────────────

internal sealed class BatchProgressConsole : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleTitle(string lpConsoleTitle);

    private readonly int _total;
    private readonly string _logPath;
    private readonly Stopwatch _stopwatchTotal = Stopwatch.StartNew();
    private readonly Stopwatch _stopwatchModel = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _heartbeat;

    private volatile int _current;
    private volatile string _currentCodigo = "";

    private BatchProgressConsole(int total, string logPath)
    {
        _total = total;
        _logPath = logPath;
        _heartbeat = new Thread(RunHeartbeat) { IsBackground = true };
    }

    public static BatchProgressConsole Start(int total, string logPath)
    {
        if (GetConsoleWindow() == IntPtr.Zero)
            AllocConsole();
        SetConsoleTitle("Exportar IFC — Batch");

        var progress = new BatchProgressConsole(total, logPath);
        progress.Log(new string('─', 70));
        progress.Log($"Batch IFC — {total} modelos — inicio {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        progress.Log(new string('─', 70));

        progress._heartbeat.Start();
        return progress;
    }

    public void StartModel(int numero, string codigo)
    {
        _current = numero;
        _currentCodigo = codigo;
        _stopwatchModel.Restart();
        Log($"[{numero}/{_total}] {codigo}: exportando...");
    }

    public void CompleteModel(string codigo) =>
        Log($"    ✔ {codigo} exportado en {FormatElapsed(_stopwatchModel.Elapsed)}");

    public void SkipModel(string codigo, string motivo) =>
        Log($"    ⚠ {codigo} SALTEADO: {motivo}");

    public void FailModel(string codigo, string error) =>
        Log($"    ✘ {codigo} FALLÓ tras {FormatElapsed(_stopwatchModel.Elapsed)}: {error}");

    public void Log(string linea)
    {
        Console.WriteLine(linea);
        try { File.AppendAllText(_logPath, linea + Environment.NewLine); }
        catch { /* si falla escribir a disco, seguimos igual — la consola sigue mostrando todo */ }
    }

    public void Finish(int ok, int fail, int skip)
    {
        Stop();
        Log(new string('─', 70));
        Log($"Terminado en {FormatElapsed(_stopwatchTotal.Elapsed)} — ✔ {ok}  ✘ {fail}  ⚠ {skip}");
        Log("Puedes cerrar esta ventana.");
    }

    public void Dispose() => Stop();

    private void Stop()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        _heartbeat.Join(TimeSpan.FromSeconds(2));
    }

    private void RunHeartbeat()
    {
        while (!_cts.IsCancellationRequested)
        {
            int pct = _total == 0 ? 0 : (int)((_current - 1) / (double)_total * 100);
            Console.Write($"\r  ⏱ [{_current}/{_total}] {_currentCodigo} — {pct}% del batch — {FormatElapsed(_stopwatchModel.Elapsed)} en este modelo, {FormatElapsed(_stopwatchTotal.Elapsed)} total   ");
            _cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
        }
    }

    private static string FormatElapsed(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
}
