using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Todas las operaciones que el puente en vivo soporta hoy. Para agregar una
/// nueva: escribe el método y agrégalo al switch de Dispatch(). Todo esto corre
/// ya sobre el hilo principal de Revit (llamado desde LiveBridgeEventHandler.Execute),
/// así que aquí sí se puede usar la API libremente (incluidas transacciones).
/// </summary>
public static class LiveBridgeOperations
{
    // Operaciones que NO necesitan ningún documento abierto en la UI (trabajan
    // a nivel Application, o directamente no tocan ningún Document). Todas las
    // demás sí requieren un documento activo — si no hay uno, se corta antes
    // de llegar a ellas en vez de tirar NullReferenceException.
    private static readonly HashSet<string> OperacionesSinDocumentoActivo = new()
    {
        "test_reopen",
        "get_export_info_all",
    };

    public static string Dispatch(UIApplication app, string operation, JsonElement args)
    {
        Document? doc = app.ActiveUIDocument?.Document;

        if (doc == null && !OperacionesSinDocumentoActivo.Contains(operation))
        {
            return JsonSerializer.Serialize(new { error = "No hay ningún documento abierto en Revit (estás en la pantalla Home)." });
        }

        return operation switch
        {
            "ping" => Ping(app, doc!),
            "get_selection" => GetSelection(app, doc!),
            "get_export_info" => GetExportInfo(doc!),
            "get_export_info_all" => GetExportInfoAll(app),
            "test_reopen" => TestReopen(app, args),
            "get_elements_by_category" => GetElementsByCategory(doc!, args),
            "get_parameters" => GetParameters(doc!, args),
            "set_parameter" => SetParameter(doc!, args),
            "fix_branch_tee_up" => FixBranchTeeUp(doc!, args),
            "reconnect_orphan_to_stub" => ReconnectOrphanToStub(doc!, args),
            "suggest_tee_rise" => SuggestTeeRise(doc!, args),
            "set_sheets_rvt_links_underlay" => SetSheetsRvtLinksUnderlay(doc!, args),
            "modify_sheet_revision" => ModifySheetRevision(doc!, args),
            "change_sheet_date_and_rev" => ChangeSheetDateAndRev(doc!, args),
            "diagnose_cad_import" => DiagnoseCadImport(doc!, args),
            _ => JsonSerializer.Serialize(new { error = $"Operación desconocida: '{operation}'" }),
        };
    }

    private static string Ping(UIApplication app, Document doc)
    {
        var viewMethods = typeof(View).GetMethods().Where(m => m.Name.Contains("Link") || m.Name.Contains("Override")).Select(m => m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")").Distinct().ToList();
        var types = typeof(View).Assembly.GetTypes().Where(t => t.Name.Contains("RevitLink") || t.Name.Contains("LinkGraphics")).Select(t => t.FullName + " [Props: " + string.Join(", ", t.GetProperties().Select(p => p.Name + ":" + p.PropertyType.Name)) + "] [Methods: " + string.Join(", ", t.GetMethods().Select(m => m.Name)) + "]").ToList();

        return JsonSerializer.Serialize(new
        {
            ok = true,
            revitVersion = app.Application.VersionNumber,
            document = doc.Title,
            elementCount = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount(),
            viewMethods,
            types
        });
    }

    private static string GetSelection(UIApplication app, Document doc)
    {
        var selectedIds = app.ActiveUIDocument.Selection.GetElementIds();
        var elements = selectedIds.Select(id =>
        {
            var el = doc.GetElement(id);
            return new
            {
                id = id.Value,
                name = el?.Name ?? "?",
                category = el?.Category?.Name ?? "?",
                typeName = el != null ? doc.GetElement(el.GetTypeId())?.Name ?? "?" : "?",
            };
        }).ToList();

        return JsonSerializer.Serialize(new { count = elements.Count, elements });
    }

    /// <summary>
    /// Para la rutina de exportación IFC batch: sobre el documento
    /// abierto, busca vistas 3D cuyo nombre contenga "no editar" (tolerando
    /// espacios múltiples/variantes entre las dos palabras — se probó contra
    /// los 13 modelos reales y el formato varía: "3D- NO EDITAR- PUBLICADO",
    /// "NO  EDITAR-PUBLICADO" con doble espacio, "3D - PUBLICADO - NO EDITAR"
    /// con el orden invertido, etc.) y captura el "user visible path" del
    /// modelo en la nube — es la única forma de reconstruir el ModelPath más
    /// tarde con Application.OpenDocumentFile, porque la API de Revit 2025 no
    /// expone ningún método para decomponer un ModelPath en GUIDs de
    /// hub/proyecto/modelo (se verificó por reflexión, no existe).
    /// </summary>
    private static string GetExportInfo(Document doc) => JsonSerializer.Serialize(BuildExportInfo(doc));

    /// <summary>
    /// Igual que get_export_info pero recorre TODOS los documentos abiertos en
    /// la sesión de Revit (app.Application.Documents), no solo el activo. Sirve
    /// para cuando Kevin tiene varios modelos abiertos a la vez (varias
    /// ventanas): en una sola llamada se captura vista + cloud path de todos,
    /// en vez de pedirle abrir uno por uno.
    /// </summary>
    private static string GetExportInfoAll(UIApplication app)
    {
        var docs = app.Application.Documents.Cast<Document>().ToList();

        var results = docs.Select(d =>
        {
            try { return BuildExportInfo(d); }
            catch (Exception ex) { return (object)new { title = d.Title, error = ex.Message }; }
        }).ToList();

        return JsonSerializer.Serialize(new { count = results.Count, documents = results });
    }

    /// <summary>
    /// Prueba real del mecanismo que va a usar el batch de exportación IFC:
    /// reconstruye un ModelPath a partir de un "user visible path" (string
    /// capturado con get_export_info / get_export_info_all, o armado a mano
    /// con el mismo formato) y lo abre con Application.OpenDocumentFile — sin
    /// UI, no toca ni depende del documento activo, así que es seguro correrlo
    /// aunque haya otros modelos abiertos en pantalla. Cierra el documento sin
    /// guardar al terminar. Reporta título, cantidad de elementos y tiempo.
    /// </summary>
    private static string TestReopen(UIApplication app, JsonElement args)
    {
        // NOTA (2026-08-03): ConvertUserVisiblePathToModelPath NO sirve para
        // modelos de nube (ACC/BIM360) — falló con "The central server could
        // not be reached" al probarlo. Confirmado por documentación de
        // Autodesk y foros: esa función es para rutas de archivo local /
        // Revit Server, no para nube. El mecanismo correcto para reabrir un
        // modelo de nube es reconstruir el ModelPath desde region+projectGuid+
        // modelGuid con ModelPathUtils.ConvertCloudGUIDsToCloudPath — por eso
        // este operation ahora recibe esos tres valores en vez de un string.
        string region = args.TryGetProperty("region", out var r) ? (r.GetString() ?? "") : "";
        string projectGuid = args.TryGetProperty("projectGuid", out var pg) ? (pg.GetString() ?? "") : "";
        string modelGuid = args.TryGetProperty("modelGuid", out var mg) ? (mg.GetString() ?? "") : "";

        if (string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(projectGuid) || string.IsNullOrWhiteSpace(modelGuid))
            return JsonSerializer.Serialize(new { ok = false, error = "Faltan 'region', 'projectGuid' y/o 'modelGuid'." });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ModelPath modelPath = ModelPathUtils.ConvertCloudGUIDsToCloudPath(region, Guid.Parse(projectGuid), Guid.Parse(modelGuid));
            var openOptions = new OpenOptions();

            Document newDoc = app.Application.OpenDocumentFile(modelPath, openOptions);
            long openElapsedMs = sw.ElapsedMilliseconds;

            string title = newDoc.Title;
            int elementCount = new FilteredElementCollector(newDoc).WhereElementIsNotElementType().GetElementCount();

            newDoc.Close(false);
            sw.Stop();

            return JsonSerializer.Serialize(new
            {
                ok = true,
                title,
                elementCount,
                openElapsedMs,
                totalElapsedMs = sw.ElapsedMilliseconds,
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = ex.InnerException?.Message ?? ex.Message,
                elapsedMs = sw.ElapsedMilliseconds,
            });
        }
    }

    private static object BuildExportInfo(Document doc)
    {
        var vistas3D = new FilteredElementCollector(doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .Where(v => !v.IsTemplate)
            .OrderBy(v => v.Name)
            .ToList();

        var candidatas = vistas3D
            .Where(v => Regex.IsMatch(v.Name, @"no\s+editar", RegexOptions.IgnoreCase))
            .Select(v => new { id = v.Id.Value, name = v.Name })
            .ToList();

        string? userVisiblePath = null;
        string? region = null;
        string? projectGuid = null;
        string? modelGuid = null;
        string? cloudError = null;
        bool isCloud = false;

        try
        {
            ModelPath cloudPath = doc.GetCloudModelPath();
            if (cloudPath != null && !cloudPath.Empty)
            {
                isCloud = true;
                userVisiblePath = ModelPathUtils.ConvertModelPathToUserVisiblePath(cloudPath);
                region = cloudPath.Region;
                // Métodos de INSTANCIA de ModelPath, no estáticos de
                // ModelPathUtils — esto es lo que se me pasó la primera vez.
                projectGuid = cloudPath.GetProjectGUID().ToString();
                modelGuid = cloudPath.GetModelGUID().ToString();
            }
        }
        catch (Exception ex)
        {
            cloudError = ex.InnerException?.Message ?? ex.Message;
        }

        return new
        {
            title = doc.Title,
            pathName = doc.PathName,
            isWorkshared = doc.IsWorkshared,
            isCloud,
            userVisiblePath,
            region,
            projectGuid,
            modelGuid,
            cloudError,
            vistas3DTotal = vistas3D.Count,
            vistas3DTodas = vistas3D.Select(v => v.Name).ToList(),
            vistasNoEditarCandidatas = candidatas,
        };
    }

    private static string GetElementsByCategory(Document doc, JsonElement args)
    {
        string categoryName = args.GetProperty("category").GetString() ?? "";
        var builtInCategory = FindBuiltInCategory(doc, categoryName);
        if (builtInCategory == null)
            return JsonSerializer.Serialize(new { error = $"No se encontró la categoría '{categoryName}'" });

        var elements = new FilteredElementCollector(doc)
            .OfCategory(builtInCategory.Value)
            .WhereElementIsNotElementType()
            .ToElements()
            .Select(e => new { id = e.Id.Value, name = e.Name })
            .ToList();

        return JsonSerializer.Serialize(new { count = elements.Count, elements });
    }

    private static string GetParameters(Document doc, JsonElement args)
    {
        long id = args.GetProperty("elementId").GetInt64();
        var element = doc.GetElement(new ElementId(id));
        if (element == null)
            return JsonSerializer.Serialize(new { error = $"No existe el elemento {id}" });

        var parameters = element.Parameters
            .Cast<Parameter>()
            .Select(p => new
            {
                name = p.Definition?.Name ?? "?",
                value = ParameterToString(p),
                readOnly = p.IsReadOnly,
            })
            .ToList();

        return JsonSerializer.Serialize(new { elementId = id, parameters });
    }

    private static string SetParameter(Document doc, JsonElement args)
    {
        long id = args.GetProperty("elementId").GetInt64();
        string paramName = args.GetProperty("paramName").GetString() ?? "";
        string value = args.GetProperty("value").GetString() ?? "";

        var element = doc.GetElement(new ElementId(id));
        if (element == null)
            return JsonSerializer.Serialize(new { error = $"No existe el elemento {id}" });

        var param = element.LookupParameter(paramName);
        if (param == null)
            return JsonSerializer.Serialize(new { error = $"El elemento {id} no tiene el parámetro '{paramName}'" });

        if (param.IsReadOnly)
            return JsonSerializer.Serialize(new { error = $"El parámetro '{paramName}' es de solo lectura" });

        using var t = new Transaction(doc, $"Kaiken: set {paramName}");
        t.Start();
        try
        {
            bool ok = param.StorageType switch
            {
                StorageType.String => param.Set(value),
                StorageType.Double => param.Set(double.Parse(value)),
                StorageType.Integer => param.Set(int.Parse(value)),
                StorageType.ElementId => param.Set(new ElementId(long.Parse(value))),
                _ => false,
            };
            t.Commit();
            return JsonSerializer.Serialize(new { ok });
        }
        catch (Exception ex)
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private static string ParameterToString(Parameter p)
    {
        try
        {
            return p.StorageType switch
            {
                StorageType.String => p.AsString() ?? "",
                StorageType.Double => p.AsDouble().ToString(),
                StorageType.Integer => p.AsInteger().ToString(),
                StorageType.ElementId => p.AsElementId().Value.ToString(),
                _ => p.AsValueString() ?? "",
            };
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Diagnóstico temporal para el bug "DWG a Bloques inserta lejos de donde se ve el
    /// plano": compara el BoundingBox real del ImportInstance (que la API SIEMPRE reporta
    /// en coordenadas de mundo/proyecto correctas, sea cual sea el estado interno de su
    /// geometría) contra el BoundingBox que arma DwgToBlocksHelpers.ExtractBlocksFromImport
    /// a partir del árbol de transforms de GeometryInstance. Si difieren por mucho, confirma
    /// que el árbol de transforms no está reflejando la posición real del vínculo/importación,
    /// y por cuánto/en qué dirección — sin eso, cualquier "arreglo" sería adivinar a ciegas.
    /// </summary>
    private static string DiagnoseCadImport(Document doc, JsonElement args)
    {
        long id = args.GetProperty("elementId").GetInt64();
        if (doc.GetElement(new ElementId(id)) is not ImportInstance importInst)
            return JsonSerializer.Serialize(new { error = $"El elemento {id} no es un ImportInstance (vínculo/importación CAD)." });

        BoundingBoxXYZ? realBox = importInst.get_BoundingBox(null);
        object? realBoxInfo = realBox == null ? null : new
        {
            min = new { x = realBox.Min.X, y = realBox.Min.Y, z = realBox.Min.Z },
            max = new { x = realBox.Max.X, y = realBox.Max.Y, z = realBox.Max.Z },
        };

        object? locationInfo = importInst.Location switch
        {
            LocationPoint lp => new { type = "LocationPoint", point = new { x = lp.Point.X, y = lp.Point.Y, z = lp.Point.Z }, rotation = lp.Rotation },
            LocationCurve => new { type = "LocationCurve" },
            null => null,
            _ => new { type = importInst.Location.GetType().Name },
        };

        List<CadBlockInstanceInfo> rawBlocks;
        try
        {
            rawBlocks = DwgToBlocksHelpers.ExtractBlocksFromImport(importInst);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"ExtractBlocksFromImport falló: {ex.Message}" });
        }

        object? rawBoxInfo = null;
        object? firstBlocks = null;
        if (rawBlocks.Count > 0)
        {
            rawBoxInfo = new
            {
                min = new { x = rawBlocks.Min(b => b.Position.X), y = rawBlocks.Min(b => b.Position.Y), z = rawBlocks.Min(b => b.Position.Z) },
                max = new { x = rawBlocks.Max(b => b.Position.X), y = rawBlocks.Max(b => b.Position.Y), z = rawBlocks.Max(b => b.Position.Z) },
            };
            firstBlocks = rawBlocks.Take(3).Select(b => new
            {
                b.BlockName,
                b.LayerName,
                position = new { x = b.Position.X, y = b.Position.Y, z = b.Position.Z },
                b.RotationDeg,
            }).ToList();
        }

        return JsonSerializer.Serialize(new
        {
            elementId = id,
            importBoundingBoxWorld_pies = realBoxInfo,
            importLocation = locationInfo,
            rawExtractedBlocksCount = rawBlocks.Count,
            rawExtractedBoundingBox_pies = rawBoxInfo,
            firstThreeBlocks = firstBlocks,
            nota = "Todas las coordenadas en PIES (unidad interna de Revit). Si importBoundingBoxWorld y rawExtractedBoundingBox no se superponen (ni cerca), confirma el desfase.",
        });
    }

    private static BuiltInCategory? FindBuiltInCategory(Document doc, string name)
    {
        if (Enum.TryParse<BuiltInCategory>(name, out var direct))
            return direct;

        foreach (Category cat in doc.Settings.Categories)
        {
            if (string.Equals(cat.Name, name, StringComparison.OrdinalIgnoreCase))
                return (BuiltInCategory)(int)cat.Id.Value;
        }
        return null;
    }

    /// <summary>
    /// Caso "Te de ramal horizontal mal modelada" (gases medicinales): borra la Te y la
    /// reducción (Transición) inmediatamente conectada al ramal, crea una Te nueva con el
    /// ramal apuntando hacia arriba (troncal intacto), agrega un tramo del tamaño grande,
    /// una reducción nueva, y un tramo chico — dejando el extremo de arriba SIN conectar
    /// para que el usuario lo extienda a mano a la altura real y reconecte el resto.
    /// La tubería vieja del ramal (al otro lado de la Transición) NO se toca ni se mueve,
    /// solo queda desconectada donde estaba.
    /// </summary>
    private static string FixBranchTeeUp(Document doc, JsonElement args)
    {
        long teeId = args.GetProperty("teeElementId").GetInt64();
        double? riseCmArg = args.TryGetProperty("riseCm", out var r) ? r.GetDouble() : (double?)null;
        double? bigStubCmArg = args.TryGetProperty("bigStubCm", out var bg) ? bg.GetDouble() : (double?)null;
        double? smallStubCmArg = args.TryGetProperty("smallStubCm", out var sm) ? sm.GetDouble() : (double?)null;

        using var t = new Transaction(doc, "Kaiken: Te ramal hacia arriba");
        t.Start();
        try
        {
            var result = FixBranchTeeUpCore(doc, teeId, riseCmArg, bigStubCmArg, smallStubCmArg);
            t.Commit();

            if (result.HadReduction)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    hadReduction = true,
                    newTeeId = result.NewTeeId.Value,
                    newTransitionId = result.NewTransitionId!.Value,
                    bigStubId = result.BigStubId!.Value,
                    smallStubId = result.OpenStubId.Value,
                    openTopElevationM = result.OpenTopElevationFt,
                    orphanedOldChainStartId = result.OrphanedOldChainStartId.Value,
                    note = "Queda el extremo de arriba sin conectar: extiéndelo a mano a la altura real y reconecta con el resto del ramal.",
                });
            }
            else
            {
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    hadReduction = false,
                    newTeeId = result.NewTeeId.Value,
                    stubId = result.OpenStubId.Value,
                    openTopElevationM = result.OpenTopElevationFt,
                    orphanedOldChainStartId = result.OrphanedOldChainStartId.Value,
                    note = "Queda el extremo de arriba sin conectar: extiéndelo a mano a la altura real y reconecta con el resto del ramal.",
                });
            }
        }
        catch (Exception ex)
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    /// <summary>Resultado estructurado (no-JSON) del paso 1 — para reusar desde comandos de la cinta.</summary>
    internal class TeeFixResult
    {
        public bool HadReduction;
        public ElementId NewTeeId = ElementId.InvalidElementId;
        public ElementId? NewTransitionId;
        public ElementId? BigStubId;
        /// <summary>El tramo con el extremo de arriba SIN conectar: stubId (sin reducción) o smallStubId (con reducción).</summary>
        public ElementId OpenStubId = ElementId.InvalidElementId;
        public double OpenTopElevationFt;
        public ElementId OrphanedOldChainStartId = ElementId.InvalidElementId;
    }

    /// <summary>
    /// Núcleo de fix_branch_tee_up SIN transacción propia — el caller (el wrapper JSON de
    /// arriba, o un comando de la cinta) debe envolver la llamada en su propia Transaction.
    /// Lanza excepción si algo falla; no devuelve JSON.
    /// </summary>
    internal static TeeFixResult FixBranchTeeUpCore(Document doc, long teeId, double? riseCmArg, double? bigStubCmArg, double? smallStubCmArg, bool downward = false)
    {
        var parts = IdentifyTeeParts(doc, teeId);
        if (parts.Error != null)
            throw new InvalidOperationException(parts.Error);

        // Dirección del ramal nuevo: hacia arriba (+Z) por defecto, o hacia abajo (-Z) si se pide.
        XYZ dir = downward ? -XYZ.BasisZ : XYZ.BasisZ;

        Element trunkPipeA = parts.TrunkPipeA!;
        Element trunkPipeB = parts.TrunkPipeB!;
        MEPCurve trunkTemplateMc = parts.TrunkTemplateMc!;
        MEPCurve smallTemplateMc = parts.SmallTemplateMc!;
        bool hasReduction = parts.HasReduction;
        XYZ teeLocation = parts.TeeLocation;

        var trunkOps = MepOps.For(trunkTemplateMc) ?? new PipeOps();
        var smallOps = MepOps.For(smallTemplateMc) ?? new PipeOps();
        ElementId trunkAId = trunkPipeA.Id, trunkBId = trunkPipeB.Id;

        doc.Delete(new ElementId(teeId));
        if (parts.TransicionId != null) doc.Delete(parts.TransicionId);
        doc.Regenerate();

        // Los vecinos del troncal pueden ser tubería (MEPCurve) o un fitting/accesorio
        // (válvula, tapa — troncal terminado en dead-end); cualquiera sirve para encontrar
        // el conector fresco tras borrar la Te.
        Connector FindNearest(ElementId pipeId, XYZ pt)
        {
            var el = doc.GetElement(pipeId);
            var cm = GetConnectorManager(el)
                ?? throw new InvalidOperationException($"El elemento {pipeId.Value} no tiene conectores.");
            return cm.Connectors.Cast<Connector>().OrderBy(c => c.Origin.DistanceTo(pt)).First();
        }

        var freshA = FindNearest(trunkAId, teeLocation);
        var freshB = FindNearest(trunkBId, teeLocation);

        XYZ dirA = -freshA.CoordinateSystem.BasisZ;
        XYZ dirB = -freshB.CoordinateSystem.BasisZ;

        if (hasReduction)
        {
            // Dos tramos (grande + chico) con una Transición nueva entre ambos.
            // bigStubCm/smallStubCm (de revit_suggest_tee_rise) tienen prioridad porque
            // pueden ser asimétricos; riseCm solo, a falta de esos, se reparte por igual.
            double bigCm = bigStubCmArg ?? (riseCmArg ?? 12.0) / 2.0;
            double smallCm = smallStubCmArg ?? (riseCmArg ?? 12.0) / 2.0;
            double bigFt = bigCm * AvoiderHelpers.CmToFeet;
            double smallFt = smallCm * AvoiderHelpers.CmToFeet;

            var bigStub = trunkOps.Create(doc, trunkTemplateMc, teeLocation, teeLocation + dir * bigFt);
            var bigBottom = AvoiderHelpers.ConnectorAt(bigStub, teeLocation)
                ?? throw new InvalidOperationException("No se encontró el conector inferior del tramo nuevo.");
            var bigTop = bigStub.ConnectorManager.Connectors.Cast<Connector>().First(c => c.Origin.DistanceTo(teeLocation) > 1e-6);

            var newTee = doc.Create.NewTeeFitting(freshA, freshB, bigBottom);
            doc.Regenerate();
            HealTrunkConnection(doc, trunkAId, newTee, teeLocation, dirA);
            HealTrunkConnection(doc, trunkBId, newTee, teeLocation, dirB);

            XYZ smallBottomPt = teeLocation + dir * bigFt;
            var smallStub = smallOps.Create(doc, smallTemplateMc, smallBottomPt, smallBottomPt + dir * smallFt);
            var smallBottom = AvoiderHelpers.ConnectorAt(smallStub, smallBottomPt)
                ?? throw new InvalidOperationException("No se encontró el conector inferior del tramo chico.");
            var smallTop = smallStub.ConnectorManager.Connectors.Cast<Connector>().First(c => c.Origin.DistanceTo(smallBottomPt) > 1e-6);

            var newTransition = doc.Create.NewTransitionFitting(bigTop, smallBottom);

            return new TeeFixResult
            {
                HadReduction = true,
                NewTeeId = newTee.Id,
                NewTransitionId = newTransition.Id,
                BigStubId = bigStub.Id,
                OpenStubId = smallStub.Id,
                OpenTopElevationFt = smallTop.Origin.Z,
                OrphanedOldChainStartId = smallTemplateMc.Id,
            };
        }
        else
        {
            // Sin reducción: un solo tramo del mismo tamaño, directo desde la Te nueva.
            double riseFt = (riseCmArg ?? 12.0) * AvoiderHelpers.CmToFeet;

            var stub = smallOps.Create(doc, smallTemplateMc, teeLocation, teeLocation + dir * riseFt);
            var stubBottom = AvoiderHelpers.ConnectorAt(stub, teeLocation)
                ?? throw new InvalidOperationException("No se encontró el conector inferior del tramo nuevo.");
            var stubTop = stub.ConnectorManager.Connectors.Cast<Connector>().First(c => c.Origin.DistanceTo(teeLocation) > 1e-6);

            var newTee = doc.Create.NewTeeFitting(freshA, freshB, stubBottom);
            doc.Regenerate();
            HealTrunkConnection(doc, trunkAId, newTee, teeLocation, dirA);
            HealTrunkConnection(doc, trunkBId, newTee, teeLocation, dirB);

            return new TeeFixResult
            {
                HadReduction = false,
                NewTeeId = newTee.Id,
                OpenStubId = stub.Id,
                OpenTopElevationFt = stubTop.Origin.Z,
                OrphanedOldChainStartId = smallTemplateMc.Id,
            };
        }
    }

    /// <summary>Resultado de identificar las 3 conexiones de una Te y el ramal que le sigue.</summary>
    internal class TeeParts
    {
        public string? Error;
        public Element? TrunkPipeA;
        public Element? TrunkPipeB;
        /// <summary>Cuál de los dos lados del troncal es una tubería real (MEPCurve), para copiar tamaño/sistema. El otro lado puede ser un fitting/accesorio (dead-end).</summary>
        public MEPCurve? TrunkTemplateMc;
        public MEPCurve? SmallTemplateMc;
        public bool HasReduction;
        public ElementId? TransicionId;
        public XYZ TeeLocation = XYZ.Zero;
    }

    /// <summary>
    /// Identifica el par troncal (antiparalelo) y el ramal de una Te de 3 conectores,
    /// más si el ramal tiene una Transición (reducción) inmediatamente después, y el
    /// punto real del eje troncal donde se asienta la Te (no el Origin del conector
    /// del ramal, que queda desplazado hacia afuera por el tamaño físico del fitting).
    /// Compartido por FixBranchTeeUp y SuggestTeeRise para no duplicar esta lógica.
    /// </summary>
    internal static TeeParts IdentifyTeeParts(Document doc, long teeId)
    {
        var result = new TeeParts();

        var teeElem = doc.GetElement(new ElementId(teeId)) as FamilyInstance;
        if (teeElem?.MEPModel?.ConnectorManager == null)
        {
            result.Error = $"El elemento {teeId} no es un fitting MEP válido.";
            return result;
        }

        var connectors = teeElem.MEPModel.ConnectorManager.Connectors.Cast<Connector>().ToList();
        if (connectors.Count != 3)
        {
            result.Error = $"Se esperaban 3 conectores (Te), se encontraron {connectors.Count}.";
            return result;
        }

        Connector? cA = null, cB = null, cBranch = null;
        for (int i = 0; i < connectors.Count && cA == null; i++)
        {
            for (int j = i + 1; j < connectors.Count; j++)
            {
                double dot = connectors[i].CoordinateSystem.BasisZ.DotProduct(connectors[j].CoordinateSystem.BasisZ);
                if (dot < -0.9)
                {
                    cA = connectors[i];
                    cB = connectors[j];
                    cBranch = connectors.Except(new[] { cA, cB }).First();
                    break;
                }
            }
        }
        if (cA == null || cB == null || cBranch == null)
        {
            result.Error = "No se pudo identificar el par troncal (¿la Te no es recta, o el ramal no es perpendicular?).";
            return result;
        }

        Connector? trunkAOther = cA.AllRefs.Cast<Connector>().FirstOrDefault(x => x.Owner?.Id != teeElem.Id);
        Connector? trunkBOther = cB.AllRefs.Cast<Connector>().FirstOrDefault(x => x.Owner?.Id != teeElem.Id);
        Connector? branchOther = cBranch.AllRefs.Cast<Connector>().FirstOrDefault(x => x.Owner?.Id != teeElem.Id);

        if (trunkAOther?.Owner == null || trunkBOther?.Owner == null)
        {
            result.Error = "No se pudo encontrar las tuberías troncales conectadas a la Te.";
            return result;
        }
        if (branchOther?.Owner == null)
        {
            result.Error = "El ramal de la Te no tiene nada conectado.";
            return result;
        }

        Element trunkPipeA = trunkAOther.Owner;
        Element trunkPipeB = trunkBOther.Owner;
        Element branchNeighbor = branchOther.Owner;

        // Un lado del troncal puede ser una tubería recta (MEPCurve) O un fitting/accesorio
        // (válvula, tapa, reducción — el troncal puede terminar en un dead-end). Lo único
        // que exigimos es que tenga conectores; para copiar tamaño/sistema al tramo nuevo
        // basta con que AL MENOS uno de los dos lados sea una tubería real.
        if (GetConnectorManager(trunkPipeA) == null || GetConnectorManager(trunkPipeB) == null)
        {
            result.Error = "No se encontraron conectores en las tuberías/accesorios troncales.";
            return result;
        }
        MEPCurve? trunkTemplateMc = (trunkPipeA as MEPCurve) ?? (trunkPipeB as MEPCurve);
        if (trunkTemplateMc == null)
        {
            result.Error = "Ninguno de los dos lados del troncal es una tubería real; no hay de dónde copiar el tamaño para el tramo nuevo.";
            return result;
        }

        Element? transicion = null;
        Element? smallTemplate = null;
        if (branchNeighbor is FamilyInstance transFi && transFi.MEPModel?.ConnectorManager != null
            && transFi.MEPModel.ConnectorManager.Connectors.Size == 2)
        {
            transicion = transFi;
            var transConns = transFi.MEPModel.ConnectorManager.Connectors.Cast<Connector>().ToList();
            var farConn = transConns.OrderByDescending(c => c.Origin.DistanceTo(branchOther.Origin)).First();
            var farOther = farConn.AllRefs.Cast<Connector>().FirstOrDefault(x => x.Owner?.Id != transFi.Id);
            smallTemplate = farOther?.Owner;
        }
        else if (branchNeighbor is MEPCurve)
        {
            smallTemplate = branchNeighbor;
        }

        if (smallTemplate is not MEPCurve smallTemplateMc)
        {
            result.Error = "No se pudo identificar la tubería del ramal (ni directa ni a través de una Transición de 2 conectores).";
            return result;
        }

        XYZ trunkA = trunkAOther.Origin, trunkB = trunkBOther.Origin;
        XYZ trunkDir = (trunkB - trunkA).Normalize();
        double tProj = (cBranch.Origin - trunkA).DotProduct(trunkDir);

        result.TrunkPipeA = trunkPipeA;
        result.TrunkPipeB = trunkPipeB;
        result.TrunkTemplateMc = trunkTemplateMc;
        result.SmallTemplateMc = smallTemplateMc;
        result.HasReduction = transicion != null;
        result.TransicionId = transicion?.Id;
        result.TeeLocation = trunkA + trunkDir * tProj;
        return result;
    }

    /// <summary>Conectores de un elemento MEP, sea tubería/ducto/etc. (MEPCurve) o un fitting/accesorio/válvula (FamilyInstance con MEPModel). Null si no tiene ninguno.</summary>
    internal static ConnectorManager? GetConnectorManager(Element e) =>
        (e as MEPCurve)?.ConnectorManager ?? (e as FamilyInstance)?.MEPModel?.ConnectorManager;

    /// <summary>
    /// Mide, en una transacción que SIEMPRE se descarta (RollBack), cuánto ocupa
    /// realmente una Te nueva a cada lado (troncal y ramal) para el tamaño de tubería
    /// dado: crea 3 tramos cortos lejos del modelo real, los conecta con NewTeeFitting,
    /// y mide la distancia entre el punto de la Te y cada uno de sus conectores. Mismo
    /// truco que AvoiderPipeCommand.MeasureElbowCtE, aplicado a una Te de 3 vías.
    /// </summary>
    internal static (double trunkStandoffFt, double branchStandoffFt) MeasureTeeStandoff(Document doc, MEPCurve trunkTemplate)
    {
        var mepOps = MepOps.For(trunkTemplate) ?? new PipeOps();
        double sizeFt = Math.Max(mepOps.NominalSize(trunkTemplate), 0.02);
        double fallback = sizeFt * 1.5;

        XYZ P1 = new XYZ(5000, 5000, 0); // lejos de cualquier geometría real, se descarta igual
        double len = 5.0;
        XYZ trunkA = P1 - XYZ.BasisX * len;
        XYZ trunkB = P1 + XYZ.BasisX * len;
        XYZ branchEnd = P1 + XYZ.BasisZ * len;

        double trunkD = 0, branchD = 0;
        using var t = new Transaction(doc, "Kaiken: medir Te (temporal)");
        try
        {
            t.Start();
            var segA = mepOps.Create(doc, trunkTemplate, trunkA, P1);
            var segB = mepOps.Create(doc, trunkTemplate, P1, trunkB);
            var segBranch = mepOps.Create(doc, trunkTemplate, P1, branchEnd);
            var cA = AvoiderHelpers.ConnectorAt(segA, P1);
            var cB = AvoiderHelpers.ConnectorAt(segB, P1);
            var cBr = AvoiderHelpers.ConnectorAt(segBranch, P1);
            if (cA != null && cB != null && cBr != null)
            {
                var tee = doc.Create.NewTeeFitting(cA, cB, cBr);
                doc.Regenerate();
                XYZ origin = (tee.Location as LocationPoint)?.Point ?? P1;
                foreach (Connector c in tee.MEPModel.ConnectorManager.Connectors)
                {
                    double dist = origin.DistanceTo(c.Origin);
                    if (dist < 1e-3) continue; // conector coincidente con el origen, no aporta info
                    bool isBranch = c.Origin.DistanceTo(branchEnd) < c.Origin.DistanceTo(trunkA)
                                  && c.Origin.DistanceTo(branchEnd) < c.Origin.DistanceTo(trunkB);
                    if (isBranch) branchD = Math.Max(branchD, dist);
                    else trunkD = Math.Max(trunkD, dist);
                }
            }
        }
        catch { /* nos quedamos con el fallback */ }
        finally { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); }
        if (trunkD < 1e-6) trunkD = fallback;
        if (branchD < 1e-6) branchD = fallback;
        return (trunkD, branchD);
    }

    /// <summary>
    /// Mide, igual que el Avoider, el center-to-end real de un codo de 90° (vertical
    /// a horizontal, el caso exacto de reconnect_orphan_to_stub) para el tamaño de
    /// tubería dado. Transacción de prueba, siempre se descarta.
    /// </summary>
    internal static double MeasureElbow90Standoff(Document doc, MEPCurve template)
    {
        var mepOps = MepOps.For(template) ?? new PipeOps();
        double sizeFt = Math.Max(mepOps.NominalSize(template), 0.02);
        double fallback = AvoiderHelpers.EstimateElbow(sizeFt, 90);

        XYZ P1 = new XYZ(5000, 5000, 0);
        double len = 5.0;
        XYZ P0 = P1 - XYZ.BasisZ * len; // tramo vertical
        XYZ P2 = P1 + XYZ.BasisX * len; // tramo horizontal

        double D = fallback;
        using var t = new Transaction(doc, "Kaiken: medir codo (temporal)");
        try
        {
            t.Start();
            var s1 = mepOps.Create(doc, template, P0, P1);
            var s2 = mepOps.Create(doc, template, P1, P2);
            var c1 = AvoiderHelpers.ConnectorAt(s1, P1);
            var c2 = AvoiderHelpers.ConnectorAt(s2, P1);
            if (c1 != null && c2 != null)
            {
                var elbow = doc.Create.NewElbowFitting(c1, c2);
                doc.Regenerate();
                XYZ origin = (elbow.Location as LocationPoint)?.Point ?? P1;
                double best = 0;
                foreach (Connector c in elbow.MEPModel.ConnectorManager.Connectors)
                    best = Math.Max(best, origin.DistanceTo(c.Origin));
                if (best > 1e-4) D = best;
            }
        }
        catch { /* nos quedamos con el fallback */ }
        finally { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); }
        return D;
    }

    /// <summary>
    /// Mide, igual que las anteriores, cuánto come la Transición de CADA lado (grande y
    /// chico) por separado: crea los dos tramos con un punto de unión común, inserta la
    /// Transición, y mide cuánto se corrió cada conector desde ese punto de unión hacia
    /// su lado. Transacción de prueba, siempre se descarta.
    /// </summary>
    internal static (double bigSideFt, double smallSideFt) MeasureTransitionSplit(Document doc, MEPCurve bigTemplate, MEPCurve smallTemplate)
    {
        var bigOps = MepOps.For(bigTemplate) ?? new PipeOps();
        var smallOps = MepOps.For(smallTemplate) ?? new PipeOps();
        double fallbackEach = Math.Max(bigOps.NominalSize(bigTemplate), smallOps.NominalSize(smallTemplate));

        XYZ P0 = new XYZ(5000, 5000, 0);
        double bigLen = 2.0, smallLen = 2.0;
        XYZ bigStart = P0 - XYZ.BasisZ * bigLen;
        XYZ smallEnd = P0 + XYZ.BasisZ * smallLen;

        double bigSideFt = fallbackEach, smallSideFt = fallbackEach;
        using var t = new Transaction(doc, "Kaiken: medir Transición (temporal)");
        try
        {
            t.Start();
            var bigSeg = bigOps.Create(doc, bigTemplate, bigStart, P0);
            var smallSeg = smallOps.Create(doc, smallTemplate, P0, smallEnd);
            var cBig = AvoiderHelpers.ConnectorAt(bigSeg, P0);
            var cSmall = AvoiderHelpers.ConnectorAt(smallSeg, P0);
            if (cBig != null && cSmall != null)
            {
                var trans = doc.Create.NewTransitionFitting(cBig, cSmall);
                doc.Regenerate();
                var conns = trans.MEPModel.ConnectorManager.Connectors.Cast<Connector>().ToList();
                if (conns.Count == 2)
                {
                    var nearBig = conns.OrderBy(c => c.Origin.DistanceTo(bigStart)).First();
                    var nearSmall = conns.OrderBy(c => c.Origin.DistanceTo(smallEnd)).First();
                    bigSideFt = P0.DistanceTo(nearBig.Origin);
                    smallSideFt = P0.DistanceTo(nearSmall.Origin);
                }
            }
        }
        catch { /* nos quedamos con el fallback */ }
        finally { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); }
        return (bigSideFt, smallSideFt);
    }

    /// <summary>Redondea hacia arriba al múltiplo de stepCm más cercano (default: 1 cm — ajustado, no generoso).</summary>
    internal static double RoundUpCm(double ft, double stepCm = 1.0) => Math.Ceiling(ft * 30.48 / stepCm) * stepCm;

    /// <summary>Resultado estructurado (no-JSON) de la sugerencia de riseCm — para reusar desde comandos de la cinta.</summary>
    internal class TeeRiseSuggestion
    {
        public bool HasReduction;
        public double TeeBranchStandoffCm;
        public double TransitionBigSideCm;
        public double TransitionSmallSideCm;
        public double ElbowStandoffCm;
        public double SuggestedBigStubCm;
        public double SuggestedSmallStubCm;
        public double SuggestedRiseCm;
    }

    private static string SuggestTeeRise(Document doc, JsonElement args)
    {
        long teeId = args.GetProperty("teeElementId").GetInt64();
        double marginCm = args.TryGetProperty("marginCm", out var m) ? m.GetDouble() : 1.0;

        TeeRiseSuggestion s;
        try
        {
            s = SuggestTeeRiseCore(doc, teeId, marginCm);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }

        if (s.HasReduction)
        {
            return JsonSerializer.Serialize(new
            {
                ok = true,
                hasReduction = true,
                teeBranchStandoffCm = s.TeeBranchStandoffCm,
                transitionBigSideCm = s.TransitionBigSideCm,
                transitionSmallSideCm = s.TransitionSmallSideCm,
                elbowStandoffCm = s.ElbowStandoffCm,
                suggestedBigStubCm = s.SuggestedBigStubCm,
                suggestedSmallStubCm = s.SuggestedSmallStubCm,
                suggestedRiseCm = s.SuggestedRiseCm,
                note = "Mínimos ajustados (fitting a fitting + 1 cm), sin inflar. Pásalos como bigStubCm y smallStubCm a fix_branch_tee_up (NO uses riseCm solo, que los repartiría por igual y no respeta esta asimetría).",
            });
        }
        else
        {
            return JsonSerializer.Serialize(new
            {
                ok = true,
                hasReduction = false,
                teeBranchStandoffCm = s.TeeBranchStandoffCm,
                elbowStandoffCm = s.ElbowStandoffCm,
                suggestedRiseCm = s.SuggestedRiseCm,
                note = "Mínimo ajustado: lo que come la Te + lo que come el codo final + 1 cm de margen. Pasa suggestedRiseCm como riseCm.",
            });
        }
    }

    /// <summary>
    /// Núcleo de suggest_tee_rise: mide en pruebas descartables (misma técnica que el
    /// Avoider) el espacio físico real que ocupan la Te nueva, la Transición (si aplica)
    /// y el codo final. Misma filosofía del Avoider: mínimo real + 1 cm de margen, SIN
    /// inflar de más — dos fittings consecutivos en el mismo tramo recto se "comen"
    /// espacio desde AMBOS extremos a la vez, así que el mínimo de cada tramo es la SUMA
    /// de lo que comen los dos fittings que lo limitan (no el máximo, que subestimaría,
    /// ni el doble de cada uno, que sobra). Lanza excepción si la Te no es válida.
    /// </summary>
    internal static TeeRiseSuggestion SuggestTeeRiseCore(Document doc, long teeId, double marginCm)
    {
        double marginFt = marginCm * AvoiderHelpers.CmToFeet;

        var parts = IdentifyTeeParts(doc, teeId);
        if (parts.Error != null)
            throw new InvalidOperationException(parts.Error);

        var trunkTemplate = parts.TrunkTemplateMc!;
        var smallTemplateMc = parts.SmallTemplateMc!;

        var (_, branchStandoffFt) = MeasureTeeStandoff(doc, trunkTemplate);
        double elbowFt = MeasureElbow90Standoff(doc, smallTemplateMc);

        if (parts.HasReduction)
        {
            var (transBigFt, transSmallFt) = MeasureTransitionSplit(doc, trunkTemplate, smallTemplateMc);

            // Tramo grande: entre la Te (come branchStandoffFt) y la Transición (come transBigFt de este lado).
            // Tramo chico: entre la Transición (come transSmallFt de este lado) y el codo final (come elbowFt).
            double bigMinFt = branchStandoffFt + transBigFt + marginFt;
            double smallMinFt = transSmallFt + elbowFt + marginFt;
            double bigCm = RoundUpCm(bigMinFt);
            double smallCm = RoundUpCm(smallMinFt);

            return new TeeRiseSuggestion
            {
                HasReduction = true,
                TeeBranchStandoffCm = branchStandoffFt * 30.48,
                TransitionBigSideCm = transBigFt * 30.48,
                TransitionSmallSideCm = transSmallFt * 30.48,
                ElbowStandoffCm = elbowFt * 30.48,
                SuggestedBigStubCm = bigCm,
                SuggestedSmallStubCm = smallCm,
                SuggestedRiseCm = bigCm + smallCm,
            };
        }
        else
        {
            double minFt = branchStandoffFt + elbowFt + marginFt;
            double suggestedRiseCm = RoundUpCm(minFt);

            return new TeeRiseSuggestion
            {
                HasReduction = false,
                TeeBranchStandoffCm = branchStandoffFt * 30.48,
                ElbowStandoffCm = elbowFt * 30.48,
                SuggestedRiseCm = suggestedRiseCm,
            };
        }
    }

    /// <summary>
    /// Segundo paso del arreglo de Te. Replica el proceso real de Kevin (no una
    /// diagonal): primero sube TODA la tubería vieja (rígido, sigue horizontal) hasta
    /// la altura del tramo nuevo, y recién ahí arrastra la punta abierta en planta
    /// (solo X/Y, mismo alto) hasta el conector del tramo nuevo — así el codo resultante
    /// es un giro limpio de 90° en vez de una diagonal que la familia de codo rechaza.
    /// El otro extremo de la tubería vieja (el que sigue conectado río abajo) se mueve
    /// con ella (mismo delta de altura) pero no cambia de posición en planta.
    /// </summary>
    private static string ReconnectOrphanToStub(Document doc, JsonElement args)
    {
        long orphanId = args.GetProperty("orphanPipeId").GetInt64();
        long stubId = args.GetProperty("stubElementId").GetInt64();

        using var t = new Transaction(doc, "Kaiken: reconectar ramal");
        t.Start();
        try
        {
            var result = ReconnectOrphanToStubCore(doc, orphanId, stubId);
            t.Commit();

            return JsonSerializer.Serialize(new
            {
                ok = true,
                elbowId = result.ElbowId.Value,
                movedPipeId = result.MovedPipeId.Value,
                note = "Codo creado, ramal reconectado.",
            });
        }
        catch (Exception ex)
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    /// <summary>Resultado estructurado (no-JSON) del paso 2 — para reusar desde comandos de la cinta.</summary>
    internal class ReconnectResult
    {
        public ElementId ElbowId = ElementId.InvalidElementId;
        public ElementId MovedPipeId = ElementId.InvalidElementId;
    }

    /// <summary>
    /// Núcleo de reconnect_orphan_to_stub SIN transacción propia. Replica el proceso real
    /// de Kevin (no una diagonal): primero sube TODA la tubería vieja (rígido, sigue
    /// horizontal) hasta la altura del tramo nuevo, y recién ahí arrastra la punta abierta
    /// en planta (solo X/Y, mismo alto) hasta el conector del tramo nuevo — así el codo
    /// resultante es un giro limpio de 90° en vez de una diagonal que la familia de codo
    /// rechaza. El otro extremo de la tubería vieja (el que sigue conectado río abajo) se
    /// mueve con ella (mismo delta de altura) pero no cambia de posición en planta.
    /// </summary>
    internal static ReconnectResult ReconnectOrphanToStubCore(Document doc, long orphanId, long stubId)
    {
        if (doc.GetElement(new ElementId(orphanId)) is not MEPCurve orphan)
            throw new InvalidOperationException($"El elemento {orphanId} no es una tubería/MEPCurve válida.");
        if (doc.GetElement(new ElementId(stubId)) is not MEPCurve stub)
            throw new InvalidOperationException($"El elemento {stubId} no es una tubería/MEPCurve válida.");

        var orphanOpen = orphan.ConnectorManager.Connectors.Cast<Connector>().FirstOrDefault(c => !c.IsConnected);
        var stubOpen = stub.ConnectorManager.Connectors.Cast<Connector>().FirstOrDefault(c => !c.IsConnected);
        if (orphanOpen == null)
            throw new InvalidOperationException($"La tubería {orphanId} no tiene ningún conector abierto (¿ya está conectada?).");
        if (stubOpen == null)
            throw new InvalidOperationException($"El tramo {stubId} no tiene ningún conector abierto (¿ya está conectado?).");

        XYZ target = stubOpen.Origin;
        double riseFt = target.Z - orphanOpen.Origin.Z;

        // Paso 1: subir TODA la tubería (rígido, sigue horizontal) a la altura del tramo nuevo.
        ElementTransformUtils.MoveElement(doc, orphan.Id, new XYZ(0, 0, riseFt));
        doc.Regenerate();

        // Paso 2: en planta, arrastrar solo la punta abierta hasta el X/Y del tramo nuevo
        // (el alto ya coincide desde el paso 1, así que este movimiento es puramente horizontal).
        var lc = (LocationCurve)orphan.Location;
        var line = (Line)lc.Curve;
        XYZ p0 = line.GetEndPoint(0), p1 = line.GetEndPoint(1);
        bool moveP0 = p0.DistanceTo(target) < p1.DistanceTo(target);
        XYZ movingPt = moveP0 ? p0 : p1;
        XYZ newPt = new XYZ(target.X, target.Y, movingPt.Z);
        lc.Curve = Line.CreateBound(moveP0 ? newPt : p0, moveP0 ? p1 : newPt);
        doc.Regenerate();

        var orphanFresh = orphan.ConnectorManager.Connectors.Cast<Connector>()
            .OrderBy(c => c.Origin.DistanceTo(target)).First();
        var stubFresh = stub.ConnectorManager.Connectors.Cast<Connector>()
            .OrderBy(c => c.Origin.DistanceTo(target)).First();

        var elbow = doc.Create.NewElbowFitting(orphanFresh, stubFresh);

        return new ReconnectResult { ElbowId = elbow.Id, MovedPipeId = orphan.Id };
    }

    /// <summary>
    /// Intenta reconectar la tubería troncal a la Te (o su cople) si NewTeeFitting
    /// falló en estirar la tubería automáticamente (común en tuberías ranuradas/PCI).
    /// </summary>
    private static void HealTrunkConnection(Document doc, ElementId trunkId, FamilyInstance tee, XYZ teeLocation, XYZ expectedDir)
    {
        if (doc.GetElement(trunkId) is not MEPCurve trunkPipe) return;

        var openConn = trunkPipe.ConnectorManager.Connectors.Cast<Connector>()
            .FirstOrDefault(c => !c.IsConnected && c.Origin.DistanceTo(teeLocation) < 2.0); // 2 ft ~ 60cm
        
        if (openConn == null) return; // Todo en orden (ya está conectado)

        // Buscar el conector abierto en la Te o en su cople/brida
        Connector? targetConn = null;
        foreach (Connector c in tee.MEPModel.ConnectorManager.Connectors)
        {
            if (c.CoordinateSystem.BasisZ.DotProduct(expectedDir) > 0.8)
            {
                if (!c.IsConnected)
                {
                    targetConn = c;
                    break;
                }
                else
                {
                    var other = c.AllRefs.Cast<Connector>().FirstOrDefault(x => x.Owner.Id != tee.Id);
                    if (other != null && other.Owner is FamilyInstance fi && fi.MEPModel?.ConnectorManager != null)
                    {
                        if (fi.MEPModel.ConnectorManager.Connectors.Size == 2)
                        {
                            var openC = fi.MEPModel.ConnectorManager.Connectors.Cast<Connector>().FirstOrDefault(x => !x.IsConnected);
                            if (openC != null && openC.CoordinateSystem.BasisZ.DotProduct(expectedDir) > 0.8)
                            {
                                targetConn = openC;
                                break;
                            }
                        }
                    }
                }
            }
        }

        if (targetConn != null)
        {
            var lc = (LocationCurve)trunkPipe.Location;
            if (lc.Curve is Line line)
            {
                XYZ p0 = line.GetEndPoint(0), p1 = line.GetEndPoint(1);
                bool moveP0 = p0.DistanceTo(openConn.Origin) < p1.DistanceTo(openConn.Origin);
                
                // Mover el extremo de la tubería hasta el objetivo
                lc.Curve = Line.CreateBound(moveP0 ? targetConn.Origin : p0, moveP0 ? p1 : targetConn.Origin);
                doc.Regenerate();
                try { openConn.ConnectTo(targetConn); } catch { }
            }
        }
    }

    internal class ElbowRiseSuggestion
    {
        public double Elbow1StandoffCm;
        public double Elbow2StandoffCm;
        public double SuggestedRiseCm;
    }

    internal class ElbowFixResult
    {
        public ElementId NewElbow1Id = ElementId.InvalidElementId;
        public ElementId NewElbow2Id = ElementId.InvalidElementId;
        public ElementId StubPipeId = ElementId.InvalidElementId;
    }

    internal static ElbowRiseSuggestion SuggestElbowRiseCore(Document doc, long elbowId, double marginCm)
    {
        double marginFt = marginCm * AvoiderHelpers.CmToFeet;

        var elbow = doc.GetElement(new ElementId(elbowId)) as FamilyInstance;
        if (elbow == null)
            throw new InvalidOperationException("El elemento seleccionado no es un fitting válido.");

        var connectors = elbow.MEPModel?.ConnectorManager?.Connectors.Cast<Connector>().ToList();
        if (connectors == null || connectors.Count != 2)
            throw new InvalidOperationException("El codo debe tener exactamente 2 conectores.");

        var refA = connectors[0].AllRefs.Cast<Connector>().FirstOrDefault(c => c.Owner.Id != elbow.Id);
        var refB = connectors[1].AllRefs.Cast<Connector>().FirstOrDefault(c => c.Owner.Id != elbow.Id);

        if (refA == null || refB == null)
            throw new InvalidOperationException("El codo debe estar conectado a dos tuberías.");

        if (refA.Owner is not MEPCurve pipeA || refB.Owner is not MEPCurve pipeB)
            throw new InvalidOperationException("Los elementos conectados al codo deben ser tuberías.");

        double elbow1Ft = MeasureElbow90Standoff(doc, pipeA);
        double elbow2Ft = MeasureElbow90Standoff(doc, pipeB);

        double minFt = elbow1Ft + elbow2Ft + marginFt;
        double suggestedRiseCm = RoundUpCm(minFt);

        return new ElbowRiseSuggestion
        {
            Elbow1StandoffCm = elbow1Ft * 30.48,
            Elbow2StandoffCm = elbow2Ft * 30.48,
            SuggestedRiseCm = suggestedRiseCm,
        };
    }

    internal static ElbowFixResult FixBranchElbowCore(Document doc, long elbowId, double riseCm, bool downward, bool invert, long? anchorPipeId = null)
    {
        // 1. Obtener la instancia del codo
        var elbow = doc.GetElement(new ElementId(elbowId)) as FamilyInstance;
        if (elbow == null)
            throw new InvalidOperationException("El elemento seleccionado no es un fitting válido.");

        // 2. Obtener conectores y las tuberías correspondientes
        var connectors = elbow.MEPModel?.ConnectorManager?.Connectors.Cast<Connector>().ToList();
        if (connectors == null || connectors.Count != 2)
            throw new InvalidOperationException("El codo debe tener exactamente 2 conectores.");

        var connElbowA = connectors[0];
        var connElbowB = connectors[1];

        var refA = connElbowA.AllRefs.Cast<Connector>().FirstOrDefault(c => c.Owner.Id != elbow.Id);
        var refB = connElbowB.AllRefs.Cast<Connector>().FirstOrDefault(c => c.Owner.Id != elbow.Id);

        if (refA == null || refB == null)
            throw new InvalidOperationException("El codo debe estar conectado a dos tuberías.");

        if (refA.Owner is not MEPCurve pipeA || refB.Owner is not MEPCurve pipeB)
            throw new InvalidOperationException("Los elementos conectados al codo deben ser tuberías.");

        // Si se especificó una tubería de anclaje, asegurar que sea pipeA (se mantiene fija)
        if (anchorPipeId != null)
        {
            if (pipeB.Id.Value == anchorPipeId.Value)
            {
                var tempPipe = pipeA;
                pipeA = pipeB;
                pipeB = tempPipe;

                var tempRef = refA;
                refA = refB;
                refB = tempRef;
            }
        }

        // Intercambiar si se solicita invertir cuál se mueve
        if (invert)
        {
            var tempPipe = pipeA;
            pipeA = pipeB;
            pipeB = tempPipe;

            var tempRef = refA;
            refA = refB;
            refB = tempRef;
        }

        // 3. Hallar intersección en XY del eje de ambas tuberías
        XYZ posA = refA.Origin;
        XYZ posB = refB.Origin;
        XYZ dirA = refA.CoordinateSystem.BasisZ; // Dirección del eje de Pipe A hacia el conector abierto (originalmente hacia el codo)

        // Hallar punto de intersección en planta
        double t = (posB - posA).DotProduct(dirA);
        XYZ C = posA + dirA * t;

        // 4. Borrar codo viejo
        doc.Delete(elbow.Id);
        doc.Regenerate();

        // 5. Crear tramo vertical (stub)
        double riseFt = riseCm * AvoiderHelpers.CmToFeet;
        XYZ dirZ = downward ? -XYZ.BasisZ : XYZ.BasisZ;
        XYZ stubStart = new XYZ(C.X, C.Y, posA.Z);
        XYZ stubEnd = stubStart + dirZ * riseFt;

        var mepOps = MepOps.For(pipeA) ?? new PipeOps();
        var stubPipe = mepOps.Create(doc, pipeA, stubStart, stubEnd);
        doc.Regenerate();

        // Obtener conectores frescos
        var freshConnPipeA = pipeA.ConnectorManager.Connectors.Cast<Connector>().OrderBy(c => c.Origin.DistanceTo(posA)).First();
        var freshConnStubBottom = stubPipe.ConnectorManager.Connectors.Cast<Connector>().OrderBy(c => c.Origin.DistanceTo(stubStart)).First();

        // 6. Conectar pipeA y el fondo del stub
        var elbow1 = doc.Create.NewElbowFitting(freshConnPipeA, freshConnStubBottom);
        doc.Regenerate();

        // 7. Mover pipeB (ramal libre) en Z
        double deltaZ = stubEnd.Z - posB.Z;
        ElementTransformUtils.MoveElement(doc, pipeB.Id, new XYZ(0, 0, deltaZ));
        doc.Regenerate();

        // 8. Estirar la punta abierta de pipeB en XY hasta C
        var lcB = (LocationCurve)pipeB.Location;
        var lineB = (Line)lcB.Curve;
        XYZ p0 = lineB.GetEndPoint(0), p1 = lineB.GetEndPoint(1);

        bool moveP0 = p0.DistanceTo(C) < p1.DistanceTo(C);
        XYZ movingPt = moveP0 ? p0 : p1;
        XYZ newPt = new XYZ(C.X, C.Y, movingPt.Z);
        lcB.Curve = Line.CreateBound(moveP0 ? newPt : p0, moveP0 ? p1 : newPt);
        doc.Regenerate();

        // Obtener conectores frescos para codo superior
        var freshConnStubTop = stubPipe.ConnectorManager.Connectors.Cast<Connector>().OrderBy(c => c.Origin.DistanceTo(stubEnd)).First();
        var freshConnPipeB = pipeB.ConnectorManager.Connectors.Cast<Connector>().OrderBy(c => c.Origin.DistanceTo(newPt)).First();

        // 9. Conectar stub top con pipeB
        var elbow2 = doc.Create.NewElbowFitting(freshConnStubTop, freshConnPipeB);

        return new ElbowFixResult
        {
            NewElbow1Id = elbow1.Id,
            NewElbow2Id = elbow2.Id,
            StubPipeId = stubPipe.Id
        };
    }

    private static string SetSheetsRvtLinksUnderlay(Document doc, JsonElement args)
    {
        var linkInstances = new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .ToList();

        if (!linkInstances.Any())
        {
            return JsonSerializer.Serialize(new { ok = false, message = "No hay vínculos de Revit (RevitLinkInstance) cargados en el modelo." });
        }

        var sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(s => !s.IsTemplate)
            .ToList();

        if (!sheets.Any())
        {
            return JsonSerializer.Serialize(new { ok = false, message = "No se encontraron láminas (ViewSheet) en el proyecto." });
        }

        var processedTargetViews = new System.Collections.Generic.HashSet<ElementId>();
        int sheetsProcessed = 0;
        int viewsProcessed = 0;
        int templatesProcessed = 0;

        using (Transaction t = new Transaction(doc, "Configurar RVT Links Underlay y Custom"))
        {
            t.Start();

            foreach (var sheet in sheets)
            {
                sheetsProcessed++;
                var viewports = sheet.GetAllViewports();
                foreach (var vpId in viewports)
                {
                    var vp = doc.GetElement(vpId) as Viewport;
                    if (vp == null) continue;

                    var view = doc.GetElement(vp.ViewId) as View;
                    if (view == null || view.IsTemplate) continue;

                    ElementId targetViewId = view.ViewTemplateId != ElementId.InvalidElementId ? view.ViewTemplateId : view.Id;

                    if (processedTargetViews.Contains(targetViewId))
                        continue;

                    var targetView = doc.GetElement(targetViewId) as View;
                    if (targetView == null || !targetView.AreGraphicsOverridesAllowed()) continue;

                    processedTargetViews.Add(targetViewId);

                    if (targetView.IsTemplate) templatesProcessed++;
                    else viewsProcessed++;

                    foreach (var link in linkInstances)
                    {
                        try
                        {
                            var settings = targetView.GetLinkOverrides(link.Id) ?? new RevitLinkGraphicsSettings();
                            settings.LinkVisibilityType = LinkVisibility.Custom;
                            targetView.SetLinkOverrides(link.Id, settings);
                        }
                        catch
                        {
                            // Omitir vistas o links específicos que no permitan overrides
                        }
                    }
                }
            }

            t.Commit();
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            sheetsProcessed,
            viewsProcessed,
            templatesProcessed,
            totalTargetsModified = processedTargetViews.Count,
            linkInstancesCount = linkInstances.Count
        });
    }

    private static string ModifySheetRevision(Document doc, JsonElement args)
    {
        string newRev = args.TryGetProperty("revision", out var r) ? r.GetString() ?? "RD" : "RD";
        bool filterLength = !args.TryGetProperty("filterLength29", out var f) || f.GetBoolean();
        bool updateParam = !args.TryGetProperty("updateParam", out var up) || up.GetBoolean();
        bool updateSheetNum = !args.TryGetProperty("updateSheetNumber", out var usn) || usn.GetBoolean();
        RevisionSettings cfg = KaikenSettings.Current.Revisions;

        var sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(s => !s.IsTemplate)
            .ToList();

        var targetSheets = filterLength
            ? sheets.Where(s => (s.SheetNumber ?? "").Length > cfg.MinSheetNumberLength).ToList()
            : sheets;

        int updatedCount = 0;
        int numParamUpdated = 0;
        int numSheetNumUpdated = 0;

        using (Transaction tx = new Transaction(doc, "Modificar Código de Revisión en Láminas (MCP)"))
        {
            tx.Start();
            foreach (var sheet in targetSheets)
            {
                bool modified = false;
                if (updateParam)
                {
                    Parameter pRev = sheet.LookupParameter(cfg.RevisionParameter);
                    if (pRev != null && !pRev.IsReadOnly)
                    {
                        pRev.Set(newRev);
                        numParamUpdated++;
                        modified = true;
                    }
                }

                if (updateSheetNum)
                {
                    string currentNum = sheet.SheetNumber ?? "";
                    string? newSheetNum = cfg.WithRevisionSegment(currentNum, newRev);
                    if (newSheetNum != null && newSheetNum != currentNum)
                    {
                        sheet.SheetNumber = newSheetNum;
                        numSheetNumUpdated++;
                        modified = true;
                    }
                }

                if (modified) updatedCount++;
            }
            tx.Commit();
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            processed = targetSheets.Count,
            updatedCount,
            numParamUpdated,
            numSheetNumUpdated
        });
    }

    private static string ChangeSheetDateAndRev(Document doc, JsonElement args)
    {
        string newDate = args.TryGetProperty("date", out var d) ? d.GetString() ?? DateTime.Now.ToString("dd-MM-yyyy") : DateTime.Now.ToString("dd-MM-yyyy");
        bool syncRev = !args.TryGetProperty("syncRevFromSheetNumber", out var sr) || sr.GetBoolean();
        bool filterLength = !args.TryGetProperty("filterLength29", out var f) || f.GetBoolean();
        RevisionSettings cfg = KaikenSettings.Current.Revisions;

        var sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(s => !s.IsTemplate)
            .ToList();

        var targetSheets = filterLength
            ? sheets.Where(s => (s.SheetNumber ?? "").Length > cfg.MinSheetNumberLength).ToList()
            : sheets;

        int updatedCount = 0;
        int dateUpdatedCount = 0;
        int revSyncedCount = 0;

        using (Transaction tx = new Transaction(doc, "Cambiar Fecha y REV en Parámetros (MCP)"))
        {
            tx.Start();
            foreach (var sheet in targetSheets)
            {
                bool modified = false;
                Parameter pDate = sheet.LookupParameter(cfg.DateParameter);
                if (pDate != null && !pDate.IsReadOnly)
                {
                    pDate.Set(newDate);
                    dateUpdatedCount++;
                    modified = true;
                }

                if (syncRev)
                {
                    string? extractedRev = cfg.GetRevisionSegment(sheet.SheetNumber ?? "");
                    if (extractedRev != null)
                    {
                        Parameter pRev = sheet.LookupParameter(cfg.RevisionParameter);
                        if (pRev != null && !pRev.IsReadOnly)
                        {
                            pRev.Set(extractedRev);
                            revSyncedCount++;
                            modified = true;
                        }
                    }
                }

                if (modified) updatedCount++;
            }
            tx.Commit();
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            processed = targetSheets.Count,
            updatedCount,
            dateUpdatedCount,
            revSyncedCount
        });
    }
}

