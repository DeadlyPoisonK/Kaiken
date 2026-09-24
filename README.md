# Kaiken

Add-in gratuito y de código abierto para **Autodesk Revit 2025 y 2026**, enfocado en tareas repetitivas de documentación y coordinación MEP.

## Herramientas

| Panel | Botón | Qué hace |
|---|---|---|
| Exportar | Exportar DWG / PDF / DWF | Exporta las láminas de un Sheet Set en un paso. |
| Exportar | Exportar IFC | Exporta la vista 3D a IFC con una configuración fija y reproducible. |
| Exportar | Exportar IFC Batch | Exporta a IFC todos los modelos abiertos en la sesión, con consola de progreso y log. |
| Detalles | Recuperar Textos | Recrea en la vista las notas de texto de un DXF con su fuente y posición exactas. |
| Detalles | DWG a Bloques | Reemplaza los bloques de un DWG por familias de Revit en su posición y rotación. |
| Enchufes | Escanear / Colocar Enchufes | Lee bloques de un DXF y coloca las familias eléctricas correspondientes. |
| Enchufes | AlignTo Wall / Wall Center / Ceiling | Alinea elementos al muro o cielo más cercano, incluidos vínculos. |
| MEP | Avoider MEP | Reenruta tuberías, ductos, escalerillas o conduits para evitar cruces. |
| MEP | Rou-T / Rou-C | Corrige ramales con Te o codo que cruzan otras tuberías. |
| MEP | Ceiling-Alt | Sube una tubería antes de un muro con dos codos de 90°. |
| MEP | Unir | Une dos tramos paralelos a distinta altura con un salto de dos codos. |
| MEP | Cambiar Familia Tubería | Cambia en lote el tipo de tubería filtrando por diámetro. |
| Revisiones | Modificar Revisión / Cambiar Fecha-REV / Agregar Revisión / Editar Revisiones Set | Mantiene sincronizados los parámetros de revisión, el Número de Plano y las Revisiones nativas. |

## Instalación

1. Cierra Revit.
2. Descarga el instalador `Kaiken-Setup-x.y.z.exe` desde [Releases](https://github.com/DeadlyPoisonK/Kaiken/releases) y ejecútalo. No requiere permisos de administrador.
3. Abre Revit y busca la pestaña **Kaiken**.

Instalación manual: copia `Kaiken.dll` y `Kaiken.addin` a `%APPDATA%\Autodesk\Revit\Addins\<año>\`.

## Configuración

La primera vez que se usa, Kaiken crea `%APPDATA%\Kaiken\settings.json`. Ahí se ajusta a los estándares de tu oficina:

```jsonc
{
  "liveBridge": { "enabled": false, "port": 5551 },
  "revisions": {
    "revisionParameter": "INFO_10_Revision",   // parámetro de texto de la lámina con el código de revisión
    "dateParameter": "INFO_Fecha",             // parámetro de texto de la lámina con la fecha
    "sheetNumberSeparator": "-",
    "sheetNumberRevisionSegment": 10,          // posición del código de revisión en el Número de Plano (0 = no usar)
    "minSheetNumberLength": 29,                // filtro opcional para omitir portadas
    "alwaysIncludeSheets": [ "PORTADA" ]
  },
  "ifcBatch": {
    "exportFolder": "",      // vacío = se pregunta cada vez
    "logPath": "",
    "modelCodeRegex": "",    // ej. "ZZ-\\d{2}([A-Za-z]{3})" para agrupar por código de disciplina
    "modelCodes": [],
    "viewNameRegex": ""      // vacío = vista "{3D}"
  }
}
```

Los cambios se aplican en el siguiente comando, sin reiniciar Revit (salvo `liveBridge`).

### Puente en vivo (opcional)

`liveBridge.enabled = true` abre un servidor TCP **solo en 127.0.0.1**, para que un agente de IA (por ejemplo Claude vía MCP, ver `mcp-server/`) pueda leer y editar el modelo abierto. Está apagado por defecto.

## Compilar desde el código

Requisitos: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). No hace falta tener Revit instalado para compilar: los contratos de la API vienen de los paquetes NuGet de Nice3point.

```bash
dotnet build src/Kaiken/Kaiken.csproj -c Release -p:RevitVersion=2025
dotnet build src/Kaiken/Kaiken.csproj -c Release -p:RevitVersion=2026
```

El instalador se genera con [Inno Setup 6](https://jrsoftware.org/isinfo.php) a partir de `installer/Kaiken.iss` (ver comentarios en el archivo).

## Licencia

[MIT](LICENSE): úsalo, modifícalo y compártelo libremente.
