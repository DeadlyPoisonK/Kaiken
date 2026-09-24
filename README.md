# Kaiken

Add-in gratuito y de código abierto para **Autodesk® Revit® 2025 y 2026**. Automatiza tareas repetitivas de exportación, documentación y coordinación MEP.

## Herramientas

Todos los botones están en la pestaña **Kaiken** de la cinta.

### Exportar

| Botón | Qué hace |
|---|---|
| **Exportar DWG** | Exporta a DWG todas las láminas de un Sheet Set. |
| **Exportar PDF** | Exporta a PDF todas las láminas de un Sheet Set. |
| **Exportar DWF** | Exporta a DWF todas las láminas de un Sheet Set. |
| **Exportar IFC** | Exporta la vista 3D activa a IFC con una configuración fija y reproducible. |
| **Exportar IFC Batch** | Exporta a IFC todos los modelos abiertos en la sesión, con consola de progreso y log. |

### Detalles

| Botón | Qué hace |
|---|---|
| **Recuperar Textos** | Lee un DXF y recrea sus textos en la vista actual con la fuente y posición originales. |
| **DWG a Bloques** | Lista los bloques de un DWG vinculado o importado y los reemplaza por familias de Revit en su posición y rotación. |

### Enchufes

| Botón | Qué hace |
|---|---|
| **Escanear Enchufes** | Lee los bloques de un DXF, lista las familias eléctricas cargadas y genera la plantilla de mapeo. |
| **Colocar Enchufes** | Coloca una familia por cada bloque del DXF, en su posición y rotación. |
| **AlignTo Wall** | Alinea los elementos seleccionados a la cara del muro más cercano (incluidos vínculos). |
| **AlignTo Wall Center** | Alinea los elementos seleccionados al eje del muro más cercano. |
| **AlignTo Ceiling** | Mueve verticalmente los elementos seleccionados hasta el cielo más cercano. |

### MEP

| Botón | Qué hace |
|---|---|
| **Pontifex** | Reenruta una tubería, ducto, escalerilla o conduit para esquivar cruces, con codos al ángulo y distancia elegidos. |
| **Rou-T** | Corrige una Te de ramal que cruza otras tuberías: la gira hacia arriba (o abajo) y reconecta el ramal. |
| **Rou-C** | Cambia la altura de una conexión de codo de 90° insertando dos codos. |
| **Ceiling-Alt** | Corta una tubería antes de un muro y la sube a la altura configurada con dos codos de 90°. |
| **Unir** | Une dos tramos paralelos a distinta altura con un salto de dos codos (45° o 90°). |
| **Cambiar Familia Tubería** | Cambia en lote el tipo de las tuberías de la vista, filtrando por tipo de origen y diámetro. |

### Revisiones

| Botón | Qué hace |
|---|---|
| **Modificar Revisión** | Cambia el código de revisión en el parámetro de las láminas y en su Número de Plano. |
| **Cambiar Fecha/REV** | Cambia la fecha de revisión y sincroniza el código de revisión desde el Número de Plano. |
| **Agregar Revisión** | Todo en uno: actualiza parámetros y Número de Plano, crea la Revisión nativa y la asigna a las láminas. |
| **Editar Revisiones Set** | Desmarca de una vez revisiones en todas las láminas de un Sheet Set. |

Los comandos de Revisiones usan parámetros de lámina configurables (ver [Configuración](#configuración)).

## Instalación

1. Cierra Revit.
2. Descarga `Kaiken-Setup-x.y.z.exe` desde [Releases](https://github.com/DeadlyPoisonK/Kaiken/releases) y ejecútalo. No requiere permisos de administrador.
3. Abre Revit y busca la pestaña **Kaiken**.

Instalación manual: copia `Kaiken.dll` y `Kaiken.addin` a `%APPDATA%\Autodesk\Revit\Addins\<año>\`.

## Configuración

La primera vez que se usa, Kaiken crea `%APPDATA%\Kaiken\settings.json` para adaptarlo a los estándares de tu oficina:

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

Con `liveBridge.enabled = true`, Kaiken abre un servidor TCP **solo en 127.0.0.1** para que un agente de IA (por ejemplo Claude vía MCP, ver `mcp-server/`) pueda leer y editar el modelo abierto. Está apagado por defecto.

## Compilar desde el código

Requisitos: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). No hace falta tener Revit instalado para compilar: los contratos de la API vienen de los paquetes NuGet de Nice3point.

```bash
dotnet build src/Kaiken/Kaiken.csproj -c Release -p:RevitVersion=2025
dotnet build src/Kaiken/Kaiken.csproj -c Release -p:RevitVersion=2026
```

El instalador se genera con [Inno Setup 6](https://jrsoftware.org/isinfo.php) a partir de `installer/Kaiken.iss`.

## Licencia

[MIT](LICENSE): úsalo, modifícalo y compártelo libremente.

## Aviso legal

Autodesk, Revit, AutoCAD, DWG y DWF son marcas registradas o marcas comerciales de Autodesk, Inc. y/o sus subsidiarias y afiliadas en EE. UU. y/o en otros países. Todas las demás marcas pertenecen a sus respectivos dueños.

Kaiken es un proyecto independiente: **no está afiliado, patrocinado ni respaldado por Autodesk, Inc.** Las referencias a productos de Autodesk solo indican compatibilidad.

Este repositorio no incluye software, bibliotecas ni archivos de Autodesk. Para usar Kaiken necesitas una instalación de Revit con licencia válida.

Kaiken se entrega "tal cual", sin garantía de ningún tipo (ver [LICENSE](LICENSE)). Modifica los modelos de Revit: revisa los resultados y respalda tus archivos antes de usarlo en proyectos reales.
