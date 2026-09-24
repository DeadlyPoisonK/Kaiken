using System;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Punto de entrada del add-in. En la Fase 1 solo agrega un botón a la cinta
/// para poder probar que el comando funciona. En fases siguientes, aquí se
/// arrancará el servidor HTTP local (HttpListener) que el puente MCP consulta.
/// </summary>
public class App : IExternalApplication
{
    private LiveBridgeServer? _liveBridgeServer;
    private ExternalEvent? _liveBridgeEvent;

    private static BitmapImage LoadIcon(string fileName)
    {
        var uri = new Uri($"pack://application:,,,/Kaiken;component/Resources/{fileName}", UriKind.Absolute);
        return new BitmapImage(uri);
    }

    public Result OnStartup(UIControlledApplication application)
    {
        const string tabName = "Kaiken";
        application.CreateRibbonTab(tabName);

        string assemblyPath = Assembly.GetExecutingAssembly().Location;

        // --- Marca: ícono de Kaiken al inicio de la cinta (no es un comando, solo identidad visual) ---
        RibbonPanel brandPanel = application.CreateRibbonPanel(tabName, "Kaiken");

        var brandButtonData = new PushButtonData(
            "KaikenBrand",
            "Kaiken",
            assemblyPath,
            "Kaiken.KaikenBrandCommand"
        )
        {
            LargeImage = LoadIcon("kaiken_32.png"),
            ToolTip    = "Kaiken",
        };

        PushButton brandButton = (PushButton)brandPanel.AddItem(brandButtonData);
        brandButton.Enabled = false;

        RibbonPanel panel = application.CreateRibbonPanel(tabName, "Exportar");

        var exportDwgButtonData = new PushButtonData(
            "ExportDwgCommand",
            "Exportar\nDWG",
            assemblyPath,
            "Kaiken.ExportDwgCommand"
        )
        {
            LargeImage = LoadIcon("exportar_dwg_32.png"),
        };

        panel.AddItem(exportDwgButtonData);

        var exportPdfButtonData = new PushButtonData(
            "ExportPdfCommand",
            "Exportar\nPDF",
            assemblyPath,
            "Kaiken.ExportPdfCommand"
        )
        {
            LargeImage = LoadIcon("exportar_pdf_32.png"),
        };

        panel.AddItem(exportPdfButtonData);

        var exportDwfButtonData = new PushButtonData(
            "ExportDwfCommand",
            "Exportar\nDWF",
            assemblyPath,
            "Kaiken.ExportDwfCommand"
        )
        {
            LargeImage = LoadIcon("exportar_dwf_32.png"),
        };

        panel.AddItem(exportDwfButtonData);

        var exportIfcButtonData = new PushButtonData(
            "ExportIfcCommand",
            "Exportar\nIFC",
            assemblyPath,
            "Kaiken.ExportIfcCommand"
        )
        {
            LargeImage = LoadIcon("exportar_ifc_32.png"),
            ToolTip    = "Exporta la vista 3D activa (o la que elijas si la activa no es 3D) al formato IFC con la configuración fija del proyecto: " +
                         "IFC4x3 Experimental · Coordinate Base = Internal Origin · " +
                         "Use Type name only for IFCType name · Property Sets: Revit + IFC common + base quantities.",
        };

        panel.AddItem(exportIfcButtonData);

        var exportIfcBatchButtonData = new PushButtonData(
            "ExportIfcBatchCommand",
            "Exportar\nIFC Batch",
            assemblyPath,
            "Kaiken.ExportIfcBatchCommand"
        )
        {
            LargeImage = LoadIcon("exportar_ifc_32.png"),
            ToolTip    = "Exporta a IFC todos los modelos ya abiertos manualmente (uno por pestaña), con la misma configuración del botón manual. " +
                         "No abre ni cierra documentos — muestra una consola de progreso y deja un log en disco. " +
                         "Carpeta, códigos de modelo y vista a exportar se configuran en %APPDATA%\\Kaiken\\settings.json.",
        };

        panel.AddItem(exportIfcBatchButtonData);

        // --- Panel Detalles: recuperar textos desde DXF ---
        RibbonPanel detallesPanel = application.CreateRibbonPanel(tabName, "Detalles");

        var importDetailButton = new PushButtonData(
            "ImportDetailCommand",
            "Recuperar\nTextos",
            assemblyPath,
            "Kaiken.ImportDetailCommand"
        )
        {
            LargeImage = LoadIcon("import_detail_32.png"),
            ToolTip    = "Lee un archivo DXF y recrea todas sus notas de texto en la vista actual " +
                         "con la fuente correcta (Arial, etc.) y la posición exacta. Úsalo " +
                         "después de importar y explotar el plano manualmente.",
        };
        detallesPanel.AddItem(importDetailButton);

        var dwgToBlocksButton = new PushButtonData(
            "DwgToBlocksCommand",
            "DWG a\nBloques",
            assemblyPath,
            "Kaiken.DwgToBlocksCommand"
        )
        {
            LargeImage = LoadIcon("import_detail_32.png"),
            ToolTip    = "Lee un plano DWG vinculado o importado, lista sus bloques y capas, " +
                         "y te permite asociar e insertar Familias de Revit en la posición y rotación exactas del CAD.",
        };
        detallesPanel.AddItem(dwgToBlocksButton);

        // --- Panel Enchufes: escanear DXF + colocar familias ---
        RibbonPanel enchufesPanel = application.CreateRibbonPanel(tabName, "Enchufes");

        var scanEnchufesButton = new PushButtonData(
            "ScanEnchufesCommand",
            "Escanear\nEnchufes",
            assemblyPath,
            "Kaiken.ScanEnchufesCommand"
        )
        {
            LargeImage = LoadIcon("enchufe_scan_32.png"),
            ToolTip = "Lee el DXF: cuenta bloques por capa, lista familias eléctricas cargadas y auto-calibra la alineación. Genera la plantilla de mapeo.",
        };
        enchufesPanel.AddItem(scanEnchufesButton);

        var placeEnchufesButton = new PushButtonData(
            "PlaceEnchufesCommand",
            "Colocar\nEnchufes",
            assemblyPath,
            "Kaiken.PlaceEnchufesCommand"
        )
        {
            LargeImage = LoadIcon("enchufe_place_32.png"),
            ToolTip = "Coloca una familia por cada bloque de enchufe del DXF, en su posición y rotación, según enchufes_mapeo.json.",
        };
        enchufesPanel.AddItem(placeEnchufesButton);

        var alignToWallButton = new PushButtonData(
            "AlignToWallCommand",
            "AlignTo\nWall",
            assemblyPath,
            "Kaiken.AlignToWallCommand"
        )
        {
            LargeImage = LoadIcon("align_to_wall_32.png"),
            ToolTip = "Alinea elementos al muro más cercano (incluyendo vínculos) conservando su altura y orientándolos hacia el exterior.",
        };
        enchufesPanel.AddItem(alignToWallButton);

        var alignToWallCenterButton = new PushButtonData(
            "AlignToWallCenterCommand",
            "AlignTo\nWall Center",
            assemblyPath,
            "Kaiken.AlignToWallCenterCommand"
        )
        {
            LargeImage = LoadIcon("align_to_wall_32.png"),
            ToolTip = "Alinea elementos al eje (centro) del muro más cercano (incluyendo vínculos) conservando su altura y orientándolos hacia el lado donde ya estaban.",
        };
        enchufesPanel.AddItem(alignToWallCenterButton);

        var alignToCeilingButton = new PushButtonData(
            "AlignToCeilingCommand",
            "AlignTo\nCeiling",
            assemblyPath,
            "Kaiken.AlignToCeilingCommand"
        )
        {
            LargeImage = LoadIcon("align_to_ceiling_32.png"),
            ToolTip = "Alinea elementos al cielo raso más cercano (incluyendo vínculos) desplazándolos de manera puramente vertical.",
        };
        enchufesPanel.AddItem(alignToCeilingButton);

        // --- Panel MEP: Pontifex ---
        RibbonPanel mepPanel = application.CreateRibbonPanel(tabName, "MEP");

        var pontifexButton = new PushButtonData(
            "PontifexCommand",
            "Pontifex\nMEP",
            assemblyPath,
            "Kaiken.PontifexCommand"
        )
        {
            LargeImage = LoadIcon("pontifex_32.png"),
            ToolTip = "Reenruta el elemento MEP seleccionado (tubería, ducto, escalerilla o conduit) evitando cruces con MEP y estructura: crea saltos con codos al ángulo, distancia y dirección que elijas.",
        };
        mepPanel.AddItem(pontifexButton);

        var fixTeeButton = new PushButtonData(
            "FixBranchTeeCommand",
            "Rou-T",
            assemblyPath,
            "Kaiken.FixBranchTeeCommand"
        )
        {
            LargeImage = LoadIcon("fix_tee_up_32.png"),
            ToolTip = "Gases medicinales: corrige una Te de ramal horizontal (cruza otras tuberías) rotándola hacia arriba, midiendo el espacio real necesario y reconectando con el codo — todo en un click.",
        };
        mepPanel.AddItem(fixTeeButton);

        var fixElbowButton = new PushButtonData(
            "FixBranchElbowCommand",
            "Rou-C",
            assemblyPath,
            "Kaiken.FixBranchElbowCommand"
        )
        {
            LargeImage = LoadIcon("fix_elbow_up_32.png"),
            ToolTip = "Gases medicinales: cambia la altura de una conexión de codo de 90° insertando dos codos a la altura especificada.",
        };
        mepPanel.AddItem(fixElbowButton);

        var bayonetButton = new PushButtonData(
            "CeilingAltCommand",
            "Ceiling-Alt",
            assemblyPath,
            "Kaiken.CeilingAltCommand"
        )
        {
            LargeImage = LoadIcon("bayonet_32.png"),
            ToolTip = "Corta una tubería continua antes de un muro, la eleva al nivel de oficina configurado relativo a su nivel, y la conecta con dos codos de 90°.",
        };
        mepPanel.AddItem(bayonetButton);

        var joinOffsetButton = new PushButtonData(
            "JoinOffsetCommand",
            "Unir",
            assemblyPath,
            "Kaiken.JoinOffsetCommand"
        )
        {
            LargeImage = LoadIcon("join_offset_32.png"),
            ToolTip = "Une dos tramos de tubería/ducto/bandeja/conduit paralelos que están a distinta altura, con un salto de dos codos (45°+45° o 90°+90°, a elección). Ninguno de los dos tramos cambia de altura: solo se estiran a lo largo de su propio eje.",
        };
        mepPanel.AddItem(joinOffsetButton);

        var changePipeTypeButton = new PushButtonData(
            "ChangePipeTypeCommand",
            "Cambiar\nFamilia Tubería",
            assemblyPath,
            "Kaiken.ChangePipeTypeCommand"
        )
        {
            LargeImage = LoadIcon("swap_pipe_32.png"),
            ToolTip = "Cambia en lote la Familia (Tipo) de las tuberías de la vista actual: elige la familia de origen, filtra por diámetro y elige la familia destino. Solo cambia la familia — el diámetro y demás parámetros de instancia se conservan.",
        };
        mepPanel.AddItem(changePipeTypeButton);

        // --- Panel Revisiones: Modificar Revisión / Cambiar Fecha y REV ---
        RibbonPanel revisionesPanel = application.CreateRibbonPanel(tabName, "Revisiones");

        var modifyRevButton = new PushButtonData(
            "ModifyRevisionCommand",
            "Modificar\nRevisión",
            assemblyPath,
            "Kaiken.ModifyRevisionCommand"
        )
        {
            LargeImage = LoadIcon("modificar_revision_32.png"),
            ToolTip = "Modifica el parámetro de revisión de las láminas y actualiza el segmento de revisión del Número de Plano (Sheet Number). Nombres y formato en %APPDATA%\\Kaiken\\settings.json.",
        };
        revisionesPanel.AddItem(modifyRevButton);

        var changeDateRevButton = new PushButtonData(
            "ChangeDateAndRevCommand",
            "Cambiar\nFecha/REV",
            assemblyPath,
            "Kaiken.ChangeDateAndRevCommand"
        )
        {
            LargeImage = LoadIcon("cambiar_fecha_rev_32.png"),
            ToolTip = "Modifica el parámetro de fecha de las láminas y sincroniza el parámetro de revisión con el segmento de revisión del Número de Plano.",
        };
        revisionesPanel.AddItem(changeDateRevButton);

        var addRevisionButton = new PushButtonData(
            "AddRevisionCommand",
            "Agregar\nRevisión",
            assemblyPath,
            "Kaiken.AddRevisionCommand"
        )
        {
            LargeImage = LoadIcon("agregar_revision_32.png"),
            ToolTip = "Todo en uno: pide Revisión y Fecha, actualiza los parámetros de revisión/fecha de las láminas y el segmento de revisión del Número de Plano donde aplique, crea o reutiliza la Revisión nativa, y la sincroniza a todas las láminas.",
        };
        revisionesPanel.AddItem(addRevisionButton);

        var manageSetRevisionsButton = new PushButtonData(
            "ManageSetRevisionsCommand",
            "Editar\nRevisiones Set",
            assemblyPath,
            "Kaiken.ManageSetRevisionsCommand"
        )
        {
            LargeImage = LoadIcon("modificar_revision_32.png"),
            ToolTip = "Elegí un Sheet Set (Publish Set) y destildá de una sola vez las revisiones marcadas en todas sus láminas, " +
                      "sin tener que abrir 'Revisions on Sheet' lámina por lámina. No afecta revisiones forzadas por una nube de revisión (Revision Cloud).",
        };
        revisionesPanel.AddItem(manageSetRevisionsButton);

        // Puente en vivo: socket TCP local (127.0.0.1) + ExternalEvent, para que Claude
        // pueda leer y editar el modelo mientras Revit está abierto. Apagado por defecto;
        // se activa con "liveBridge": { "enabled": true } en %APPDATA%\Kaiken\settings.json.
        // Si el puerto ya está tomado (otra instancia de Revit, ej. 2025 y 2026 abiertos
        // a la vez) el puente queda desactivado en esta instancia, pero la cinta sigue
        // funcionando: una excepción aquí haría que Revit descarte todo el add-in.
        var bridgeSettings = KaikenSettings.Current.LiveBridge;
        if (bridgeSettings.Enabled)
        {
            try
            {
                var liveBridgeHandler = new LiveBridgeEventHandler();
                _liveBridgeEvent = ExternalEvent.Create(liveBridgeHandler);
                _liveBridgeServer = new LiveBridgeServer(_liveBridgeEvent, liveBridgeHandler, bridgeSettings.Port);
                _liveBridgeServer.Start();
            }
            catch (Exception ex)
            {
                _liveBridgeServer = null;
                System.Diagnostics.Debug.WriteLine($"Kaiken: puente en vivo desactivado ({ex.Message})");
            }
        }

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        _liveBridgeServer?.Stop();
        return Result.Succeeded;
    }
}
