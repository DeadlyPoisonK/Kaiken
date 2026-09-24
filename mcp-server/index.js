// Servidor MCP local (dev-ops) para Kaiken.
//
// Expone un puñado de herramientas ACOTADAS (nada de "ejecutar comando arbitrario")
// para que Claude pueda compilar e instalar el add-in directo en esta máquina:
//   - check_dotnet_sdk
//   - build_addin
//   - install_addin
//   - read_latest_revit_journal
//   - list_dynamo_graphs
//   - read_dynamo_graph_summary
//   - check_inno_setup
//   - build_installer
//   - revit_live_ping
//   - revit_get_selection
//   - revit_get_export_info
//   - revit_get_elements_by_category
//   - revit_get_parameters
//   - revit_set_parameter
//   - revit_suggest_tee_rise
//   - revit_fix_branch_tee_up
//   - revit_reconnect_branch
//
// No abre ningún puerto de red: MCP por stdio corre como subproceso local,
// invocado únicamente por la app de Claude en esta misma máquina.

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { exec } from "node:child_process";
import { promisify } from "node:util";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import net from "node:net";

const execAsync = promisify(exec);

// Rutas calculadas relativas a este archivo (mcp-server/index.js vive dentro del repo Kaiken/)
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PROJECT_DIR = path.resolve(__dirname, "..");
const SRC_DIR = path.join(PROJECT_DIR, "src", "Kaiken");
const ADDIN_MANIFEST = path.join(PROJECT_DIR, "manifest", "Kaiken.addin");
const BUILD_OUTPUT_DLL = path.join(SRC_DIR, "bin", "Release", "net8.0-windows", "Kaiken.dll");

function revitAddinsDir(version) {
  return path.join(process.env.APPDATA, "Autodesk", "Revit", "Addins", version);
}

function revitJournalsDir(version) {
  return path.join(process.env.LOCALAPPDATA, "Autodesk", "Revit", `Autodesk Revit ${version}`, "Journals");
}

const server = new McpServer({ name: "kaiken-dev", version: "0.1.0" });

server.tool(
  "check_dotnet_sdk",
  "Verifica si el SDK de .NET 8 está instalado (requisito para compilar el add-in).",
  {},
  async () => {
    try {
      const { stdout } = await execAsync("dotnet --version");
      return { content: [{ type: "text", text: `dotnet SDK version: ${stdout.trim()}` }] };
    } catch (err) {
      return {
        content: [{
          type: "text",
          text: `No se encontró el comando 'dotnet'. Instala el SDK de .NET 8 desde https://dotnet.microsoft.com/download/dotnet/8.0 (elige "SDK").\n\nDetalle: ${err.message}`,
        }],
        isError: true,
      };
    }
  }
);

server.tool(
  "build_addin",
  "Compila el proyecto Kaiken en modo Release (dotnet build), contra los contratos de la API de la versión de Revit indicada.",
  { revitVersion: z.string().default("2025").describe("Versión de Revit contra la que compilar, ej. '2025' o '2026'") },
  async ({ revitVersion }) => {
    try {
      const { stdout, stderr } = await execAsync(
        `dotnet build -c Release -p:RevitVersion=${revitVersion}`,
        { cwd: SRC_DIR, timeout: 120000 }
      );
      const ok = fs.existsSync(BUILD_OUTPUT_DLL);
      return {
        content: [{
          type: "text",
          text: `${stdout}${stderr ? "\n--STDERR--\n" + stderr : ""}\n\n${ok ? "OK (Revit " + revitVersion + "): " + BUILD_OUTPUT_DLL : "ADVERTENCIA: no se encontró el .dll de salida esperado."}`,
        }],
      };
    } catch (err) {
      return {
        content: [{ type: "text", text: `Build falló.\n${err.stdout || ""}\n${err.stderr || err.message}` }],
        isError: true,
      };
    }
  }
);

server.tool(
  "install_addin",
  "Copia el .dll compilado y el manifiesto .addin a la carpeta de Addins de Revit.",
  { revitVersion: z.string().default("2025").describe("Versión de Revit, ej. '2025' o '2026'") },
  async ({ revitVersion }) => {
    if (!fs.existsSync(BUILD_OUTPUT_DLL)) {
      return {
        content: [{ type: "text", text: `No encuentro ${BUILD_OUTPUT_DLL}. Corre build_addin primero.` }],
        isError: true,
      };
    }
    const targetDir = revitAddinsDir(revitVersion);
    fs.mkdirSync(targetDir, { recursive: true });
    fs.copyFileSync(BUILD_OUTPUT_DLL, path.join(targetDir, "Kaiken.dll"));
    fs.copyFileSync(ADDIN_MANIFEST, path.join(targetDir, "Kaiken.addin"));
    return {
      content: [{
        type: "text",
        text: `Instalado en ${targetDir}.\nAbre (o reinicia) Revit ${revitVersion} y busca la pestaña "Kaiken" .`,
      }],
    };
  }
);

server.tool(
  "read_latest_revit_journal",
  "Lee el final del journal más reciente de Revit, útil para diagnosticar por qué un add-in no cargó.",
  { revitVersion: z.string().default("2025").describe("Versión de Revit, ej. '2025' o '2026'") },
  async ({ revitVersion }) => {
    const dir = revitJournalsDir(revitVersion);
    if (!fs.existsSync(dir)) {
      return { content: [{ type: "text", text: `No encuentro la carpeta de journals: ${dir}` }], isError: true };
    }
    const files = fs.readdirSync(dir)
      .filter((f) => f.toLowerCase().endsWith(".txt"))
      .map((f) => ({ f, mtime: fs.statSync(path.join(dir, f)).mtimeMs }))
      .sort((a, b) => b.mtime - a.mtime);
    if (files.length === 0) {
      return { content: [{ type: "text", text: "No hay archivos de journal todavía." }], isError: true };
    }
    const latestPath = path.join(dir, files[0].f);
    const raw = fs.readFileSync(latestPath, "utf-8");
    const tail = raw.slice(-8000);
    return { content: [{ type: "text", text: `Journal: ${files[0].f}\n\n${tail}` }] };
  }
);

// Carpeta de grafos .dyn por defecto: se define con la variable de entorno KAIKEN_DYNAMO_DIR.
const DEFAULT_DYNAMO_DIR = process.env.KAIKEN_DYNAMO_DIR || "";

server.tool(
  "list_dynamo_graphs",
  "Lista los archivos .dyn encontrados en una carpeta (por defecto, la de la variable de entorno KAIKEN_DYNAMO_DIR).",
  { folder: z.string().default(DEFAULT_DYNAMO_DIR).describe("Carpeta donde buscar archivos .dyn") },
  async ({ folder }) => {
    if (!fs.existsSync(folder)) {
      return { content: [{ type: "text", text: `No existe la carpeta: ${folder}` }], isError: true };
    }
    const results = [];
    function walk(dir) {
      for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) walk(full);
        else if (entry.name.toLowerCase().endsWith(".dyn")) {
          const stat = fs.statSync(full);
          results.push(`${full}  (${(stat.size / 1024).toFixed(1)} KB, modificado ${stat.mtime.toISOString().slice(0, 10)})`);
        }
      }
    }
    walk(folder);
    return {
      content: [{ type: "text", text: results.length ? results.join("\n") : "No se encontraron archivos .dyn en esa carpeta." }],
    };
  }
);

server.tool(
  "read_dynamo_graph_summary",
  "Lee un archivo .dyn (es JSON) y devuelve un resumen: nombre, nodos (tipo y nombre), código de nodos Python/CodeBlock si existen, y cantidad de conectores. No devuelve los datos de vista/geometría (serían demasiado pesados).",
  { filePath: z.string().describe("Ruta completa al archivo .dyn") },
  async ({ filePath }) => {
    if (!fs.existsSync(filePath)) {
      return { content: [{ type: "text", text: `No existe el archivo: ${filePath}` }], isError: true };
    }
    let json;
    try {
      json = JSON.parse(fs.readFileSync(filePath, "utf-8"));
    } catch (err) {
      return { content: [{ type: "text", text: `No se pudo parsear como JSON: ${err.message}` }], isError: true };
    }
    const nodes = json.Nodes || [];
    const nodeLines = nodes.map((n) => {
      const name = n.Name || n.ConcreteType || "?";
      const type = n.NodeType || n.ConcreteType || "?";
      const extra = n.Code ? `\n  --- código completo ---\n${String(n.Code)}\n  --- fin código ---` : "";
      return `- [${type}] ${name}${extra}`;
    });
    const summary = [
      `Archivo: ${filePath}`,
      `Nombre del grafo: ${json.Name || path.basename(filePath)}`,
      `Descripción: ${json.Description || "(sin descripción)"}`,
      `Nodos (${nodes.length}):`,
      ...nodeLines,
      `Conectores: ${(json.Connectors || []).length}`,
    ].join("\n");
    return { content: [{ type: "text", text: summary.slice(0, 60000) }] };
  }
);

const INSTALLER_DIR = path.join(PROJECT_DIR, "installer");
const ISS_FILE = path.join(INSTALLER_DIR, "Kaiken.iss");

function findIscc() {
  const fixedCandidates = [
    "C:\\Program Files (x86)\\Inno Setup 6\\ISCC.exe",
    "C:\\Program Files\\Inno Setup 6\\ISCC.exe",
  ];
  const found = fixedCandidates.find((p) => fs.existsSync(p));
  if (found) return found;

  // Busca cualquier carpeta "Inno Setup*" en las ubicaciones típicas (versión distinta,
  // instalación per-user, etc.)
  const searchRoots = [
    process.env["ProgramFiles(x86)"],
    process.env.ProgramFiles,
    process.env.LOCALAPPDATA && path.join(process.env.LOCALAPPDATA, "Programs"),
  ].filter(Boolean);

  for (const root of searchRoots) {
    if (!fs.existsSync(root)) continue;
    let entries;
    try {
      entries = fs.readdirSync(root, { withFileTypes: true });
    } catch {
      continue;
    }
    for (const entry of entries) {
      if (entry.isDirectory() && /inno\s*setup/i.test(entry.name)) {
        const candidate = path.join(root, entry.name, "ISCC.exe");
        if (fs.existsSync(candidate)) return candidate;
      }
    }
  }

  return "ISCC"; // último recurso: confía en que esté en el PATH
}

server.tool(
  "check_inno_setup",
  "Verifica si Inno Setup (compilador de instaladores) está disponible.",
  {},
  async () => {
    const iscc = findIscc();
    if (iscc !== "ISCC" && fs.existsSync(iscc)) {
      return { content: [{ type: "text", text: `Inno Setup encontrado: ${iscc}` }] };
    }
    return {
      content: [{
        type: "text",
        text: `No se encontró Inno Setup. Descárgalo (gratis) de https://jrsoftware.org/isdl.php e instálalo con las opciones por defecto.`,
      }],
      isError: true,
    };
  }
);

server.tool(
  "build_installer",
  "Compila installer/Kaiken.iss con Inno Setup, generando el .exe instalable en installer/output/. Corre build_addin antes (con la misma revitVersion) si el .dll no está actualizado.",
  { revitVersion: z.string().default("2025").describe("Versión de Revit para la que empaquetar el instalador, ej. '2025' o '2026'. Debe coincidir con la versión usada en el build_addin más reciente.") },
  async ({ revitVersion }) => {
    if (!fs.existsSync(BUILD_OUTPUT_DLL)) {
      return {
        content: [{ type: "text", text: `No encuentro ${BUILD_OUTPUT_DLL}. Corre build_addin primero.` }],
        isError: true,
      };
    }
    const iscc = findIscc();
    try {
      const { stdout, stderr } = await execAsync(
        `"${iscc}" "/DRevitVersion=${revitVersion}" "${ISS_FILE}"`,
        { cwd: INSTALLER_DIR, timeout: 120000 }
      );
      const outputDir = path.join(INSTALLER_DIR, "output");
      const exeFiles = fs.existsSync(outputDir)
        ? fs.readdirSync(outputDir).filter((f) => f.toLowerCase().endsWith(".exe"))
        : [];
      return {
        content: [{
          type: "text",
          text: `${stdout}${stderr ? "\n--STDERR--\n" + stderr : ""}\n\nArchivos generados en ${outputDir}:\n${exeFiles.join("\n") || "(ninguno encontrado)"}`,
        }],
      };
    } catch (err) {
      return {
        content: [{
          type: "text",
          text: `La compilación del instalador falló.\n${err.stdout || ""}\n${err.stderr || err.message}`,
        }],
        isError: true,
      };
    }
  }
);

// --- Puente en vivo: habla con el add-in mientras Revit está abierto ---
// Protocolo: una línea de JSON de entrada por socket TCP, una línea de JSON de
// salida, se cierra la conexión. Ver LiveBridgeServer.cs en el add-in.

const LIVE_BRIDGE_PORT = 5551;

function callLiveBridge(op, args = {}, timeoutMs = 35000) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: "127.0.0.1", port: LIVE_BRIDGE_PORT }, () => {
      socket.write(JSON.stringify({ op, args }) + "\n");
    });
    let buffer = "";
    socket.setTimeout(timeoutMs, () => {
      socket.destroy();
      reject(new Error(`Timeout (${timeoutMs}ms) esperando respuesta del add-in. ¿Está Revit abierto, y el add-in instalado (con esta versión)?`));
    });
    socket.on("data", (chunk) => {
      buffer += chunk.toString("utf-8");
    });
    socket.on("end", () => resolve(buffer.trim()));
    socket.on("error", (err) => {
      reject(new Error(`No se pudo conectar al add-in en el puerto ${LIVE_BRIDGE_PORT}: ${err.message}. ¿Está Revit abierto?`));
    });
  });
}

server.tool(
  "revit_live_ping",
  "Verifica que el add-in está corriendo en Revit y responde en vivo (Revit debe estar abierto con un documento).",
  {},
  async () => {
    try {
      const raw = await callLiveBridge("ping");
      return { content: [{ type: "text", text: raw }] };
    } catch (err) {
      return { content: [{ type: "text", text: err.message }], isError: true };
    }
  }
);

server.tool(
  "revit_get_selection",
  "Obtiene los elementos que el usuario tiene seleccionados ahora mismo en Revit (id, nombre, categoría, tipo).",
  {},
  async () => {
    try {
      const raw = await callLiveBridge("get_selection");
      return { content: [{ type: "text", text: raw }] };
    } catch (err) {
      return { content: [{ type: "text", text: err.message }], isError: true };
    }
  }
);

server.tool(
  "revit_get_export_info",
  "Diagnóstico para la rutina de exportación IFC batch: sobre el documento actualmente ACTIVO, devuelve título, vistas 3D cuyo nombre contiene 'no editar' (tolerando espacios/variantes, con conteo para detectar ambigüedad), y el 'user visible path' del cloud model (para reconstruir el ModelPath más adelante con Application.OpenDocumentFile).",
  {},
  async () => {
    try {
      const raw = await callLiveBridge("get_export_info");
      return { content: [{ type: "text", text: raw }] };
    } catch (err) {
      return { content: [{ type: "text", text: err.message }], isError: true };
    }
  }
);

server.tool(
  "revit_test_reopen",
  "Prueba real del mecanismo del batch IFC: reconstruye un ModelPath de nube desde region+projectGuid+modelGuid (capturados con revit_get_export_info/_all) usando ModelPathUtils.ConvertCloudGUIDsToCloudPath, y lo abre con Application.OpenDocumentFile sin UI — no toca el documento activo, es seguro con otros modelos abiertos. Cierra sin guardar al terminar. Devuelve título, cantidad de elementos y tiempo tomado. (La ruta 'Autodesk Docs://...' NO sirve para esto — probado y falla con 'central server could not be reached'.)",
  {
    region: z.string().describe("Region devuelta por get_export_info (ej. 'US')"),
    projectGuid: z.string().describe("projectGuid devuelto por get_export_info"),
    modelGuid: z.string().describe("modelGuid devuelto por get_export_info"),
  },
  async ({ region, projectGuid, modelGuid }) => {
    try {
      // Modelos grandes pueden tardar varios minutos en abrir por API — 8 min de margen.
      const raw = await callLiveBridge("test_reopen", { region, projectGuid, modelGuid }, 8 * 60 * 1000);
      return { content: [{ type: "text", text: raw }] };
    } catch (err) {
      return { content: [{ type: "text", text: err.message }], isError: true };
    }
  }
);

server.tool(
  "revit_get_export_info_all",
  "Igual que revit_get_export_info pero recorre TODOS los documentos abiertos en la sesión de Revit (no solo el activo) — útil cuando Kevin tiene varios modelos abiertos a la vez en distintas ventanas, para capturar vista + cloud path de todos en una sola llamada.",
  {},
  async () => {
    try {
      const raw = await callLiveBridge("get_export_info_all");
      return { content: [{ type: "text", text: raw }] };
    } catch (err) {
      return { content: [{ type: "text", text: err.message }], isError: true };
    }
  }
);

server.tool(
  "revit_diagnose_cad_import",
  "Diagnóstico temporal para el bug 'DWG a Bloques inserta lejos de donde se ve el plano': compara el BoundingBox real (siempre correcto) del vínculo/importación CAD contra el BoundingBox que calcula el addon a partir de la geometría, para confirmar el desfase exacto.",
  { elementId: z.number().describe("Element Id del ImportInstance (vínculo/importación CAD) a diagnosticar") },
  async ({ elementId }) => {
    try {
      const raw = await callLiveBridge("diagnose_cad_import", { elementId });
      return { content: [{ type: "text", text: raw }] };
    } catch (err) {
      return { content: [{ type: "text", text: err.message }], isError: true };
    }
  }
);

server.tool(
  "revit_get_elements_by_category",
  "Lista elementos (id y nombre) de una categoría en el documento activo de Revit, en vivo.",
  { category: z.string().describe("Nombre de categoría: BuiltInCategory tipo 'OST_Walls', o el nombre visible como 'Walls'") },
  async ({ category }) => {
    try {
      const raw = await callLiveBridge("get_elements_by_category", { category });
      return { content: [{ type: "text", text: raw }] };
    } catch (err) {
      return { content: [{ type: "text", text: err.message }], isError: true };
    }
  }
);

server.tool(
  "revit_get_parameters",
  "Obtiene todos los parámetros (nombre, valor, si es solo lectura) de un elemento del documento activo, en vivo.",
  { elementId: z.number().describe("Element Id de Revit") },
  async ({ elementId }) => {
    try {
      const raw = await callLiveBridge("get_parameters", { elementId });
      return { content: [{ type: "text", text: raw }] };
    } catch (err) {
      return { content: [{ type: "text", text: err.message }], isError: true };
    }
  }
);

server.tool(
  "revit_set_parameter",
  "Modifica el valor de un parámetro de un elemento en el documento activo, en vivo. Escribe directo en el modelo abierto (dentro de una transacción).",
  {
    elementId: z.number().describe("Element Id de Revit"),
    paramName: z.string().describe("Nombre del parámetro (el que se ve en Revit)"),
    value: z.string().describe("Nuevo valor como texto; se convierte según el tipo real del parámetro (texto, número, entero, o Element Id)"),
  },
  async ({ elementId, paramName, value }) => {
    try {
      const raw = await callLiveBridge("set_parameter", { elementId, paramName, value });
      return { content: [{ type: "text", text: raw }] };
    } catch (err) {
      return { content: [{ type: "text", text: err.message }], isError: true };
    }
  }
);

server.tool(
  "revit_suggest_tee_rise",
  "Antes de correr revit_fix_branch_tee_up: mide (con piezas de prueba descartables, igual que el Pontifex) el espacio físico real que ocupan la Te nueva, la Transición (si aplica) y el codo final para el tamaño de tubería de esta Te, y sugiere el riseCm mínimo para que no queden solapados o casi sin espacio.",
  {
    teeElementId: z.number().describe("Element Id de la Te (fitting) a corregir"),
    marginCm: z.number().optional().describe("Margen de seguridad extra en cm además del mínimo geométrico (default 1)"),
  },
  async ({ teeElementId, marginCm }) => {
    try {
      const raw = await callLiveBridge("suggest_tee_rise", { teeElementId, ...(marginCm != null ? { marginCm } : {}) });
      return { content: [{ type: "text", text: raw }] };
    } catch (err) {
      return { content: [{ type: "text", text: err.message }], isError: true };
    }
  }
);

server.tool(
  "revit_fix_branch_tee_up",
  "Caso gases medicinales: borra una Te de ramal horizontal (mal modelada, cruza otras líneas) junto con su reducción, y las reemplaza por una Te con el ramal apuntando hacia arriba + reducción + un tramo corto, dejando el extremo abierto para continuar a mano. El troncal no se toca. La tubería vieja del ramal queda desconectada en su lugar (no se borra). Recomendado: correr antes revit_suggest_tee_rise y usar sus valores sugeridos.",
  {
    teeElementId: z.number().describe("Element Id de la Te (fitting) a corregir"),
    riseCm: z.number().optional().describe("Altura total del tramo nuevo en cm, repartida en mitades iguales entre el tramo grande y el chico (default 12). Ignorado si se pasan bigStubCm/smallStubCm."),
    bigStubCm: z.number().optional().describe("Caso CON reducción: largo del tramo grande (junto a la Te), en cm. Usa suggestedBigStubCm de revit_suggest_tee_rise."),
    smallStubCm: z.number().optional().describe("Caso CON reducción: largo del tramo chico (junto al futuro codo), en cm. Usa suggestedSmallStubCm de revit_suggest_tee_rise."),
  },
  async ({ teeElementId, riseCm, bigStubCm, smallStubCm }) => {
    try {
      const raw = await callLiveBridge("fix_branch_tee_up", {
        teeElementId,
        ...(riseCm != null ? { riseCm } : {}),
        ...(bigStubCm != null ? { bigStubCm } : {}),
        ...(smallStubCm != null ? { smallStubCm } : {}),
      });
      return { content: [{ type: "text", text: raw }] };
    } catch (err) {
      return { content: [{ type: "text", text: err.message }], isError: true };
    }
  }
);

server.tool(
  "revit_reconnect_branch",
  "Segundo paso del arreglo de Te: mueve el extremo desconectado de la tubería vieja del ramal hasta el conector abierto del tramo nuevo (creado por fix_branch_tee_up) y crea el codo que las une. El otro extremo de la tubería vieja no se toca.",
  {
    orphanPipeId: z.number().describe("Element Id de la tubería huérfana (orphanedOldChainStartId de fix_branch_tee_up)"),
    stubElementId: z.number().describe("Element Id del tramo nuevo con el extremo abierto (stubId o smallStubId de fix_branch_tee_up)"),
  },
  async ({ orphanPipeId, stubElementId }) => {
    try {
      const raw = await callLiveBridge("reconnect_orphan_to_stub", { orphanPipeId, stubElementId });
      return { content: [{ type: "text", text: raw }] };
    } catch (err) {
      return { content: [{ type: "text", text: err.message }], isError: true };
    }
  }
);

const transport = new StdioServerTransport();
await server.connect(transport);
