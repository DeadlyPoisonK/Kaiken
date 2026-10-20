using System;
using System.Linq;
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
    internal static App? Instance { get; private set; }
    private LiveBridgeServer? _liveBridgeServer;
    private ExternalEvent? _liveBridgeEvent;

    // Nombre real del assembly (no "Kaiken" fijo): los builds del modo desarrollo se llaman
    // KaikenDev_<hora> y cada uno debe leer sus propios recursos.
    private static readonly string AssemblyShortName = typeof(App).Assembly.GetName().Name ?? "Kaiken";

    internal static BitmapImage LoadIcon(string fileName)
    {
        var uri = new Uri($"pack://application:,,,/{AssemblyShortName};component/Resources/{fileName}", UriKind.Absolute);
        return new BitmapImage(uri);
    }

    /// <summary>
    /// Deja un panel de la cinta siempre colapsado, vía AdWindows (la API de Revit no lo ofrece).
    /// Si algo falla el panel queda normal: nunca debe tumbar la carga del add-in.
    /// </summary>
    private static void CollapsePanel(string tabName, string panelTitle, string iconFile)
    {
        try
        {
            var tab = Autodesk.Windows.ComponentManager.Ribbon?.FindTab(tabName);
            var adPanel = tab?.Panels.FirstOrDefault(p => p.Source?.Title == panelTitle);
            if (adPanel == null)
            {
                BridgeLog($"cinta: no se encontró el panel {panelTitle} para colapsarlo");
                return;
            }
            adPanel.CollapsedPanelImage = LoadIcon(iconFile);
            adPanel.IsCollapsed = true;
        }
        catch (Exception ex)
        {
            BridgeLog($"cinta: no se pudo colapsar {panelTitle}: {ex.Message}");
        }
    }

    public Result OnStartup(UIControlledApplication application)
    {
        Instance = this;
        const string tabName = "Kaiken";
        application.CreateRibbonTab(tabName);

        string assemblyPath = Assembly.GetExecutingAssembly().Location;

        // --- Marca: ícono de Kaiken al inicio de la cinta; con clic prueba la conexión del puente en vivo ---
        RibbonPanel brandPanel = application.CreateRibbonPanel(tabName, "Kaiken");

        var brandButtonData = new PushButtonData(
            "KaikenBrand",
            "Kaiken",
            assemblyPath,
            "Kaiken.KaikenBrandCommand"
        )
        {
            LargeImage = LoadIcon("kaiken_32.png"),
            ToolTip    = "Kaiken. Clic para probar la conexión con el puente en vivo.",
        };

        brandPanel.AddItem(brandButtonData);

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

        // Exportar se usa poco y ocupa mucho: queda colapsado, un solo botón que despliega
        // los íconos en fila (como cuando Revit colapsa un panel por falta de espacio).
        CollapsePanel(tabName, "Exportar", "exportar_pdf_32.png");

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
            "Pontifex",
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
            ToolTip = "Gira el ramal de una o varias Te y lo reconecta solo. Ramal horizontal: lo deja hacia arriba o abajo, midiendo el espacio real necesario, y reconecta con codo. " +
                      "Ramal vertical: lo invierte (arriba/abajo) o lo deja horizontal a la derecha o izquierda, según la vista actual. Ctrl+Z deshace todo.",
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

        var connectSprinklersButton = new PushButtonData(
            "ConnectSprinklersCommand",
            "Conectar\nRociadores",
            assemblyPath,
            "Kaiken.ConnectSprinklersCommand"
        )
        {
            LargeImage = LoadIcon("conectar_rociadores_32.png"),
            ToolTip = "Conecta en lote los rociadores de la vista actual (por Familia/Tipo) a la tubería de incendio que pasa directamente sobre ellos, con bajada y Te o Tap según las preferencias de ruteo. " +
                      "Descarta los ya conectados, avanza uno por uno con barra de progreso y marca en morado los que no se pudieron conectar. Quita el morado de los que ya estén conectados (también los conectados a mano).",
        };
        mepPanel.AddItem(connectSprinklersButton);

        var alcButton = new PushButtonData(
            "AlcCommand",
            "Alc",
            assemblyPath,
            "Kaiken.AlcCommand"
        )
        {
            LargeImage = LoadIcon("alc_32.png"),
            ToolTip = "Alcantarillado: elige las bajantes (tuberías verticales) y luego la troncal. Arma por cada una la ruta bajante → codo → diagonal → codo → horizontal con 1 % de pendiente → Te/Tap en la troncal, " +
                      "para ajustarla después en 3D. Prefiere codos de 45°; si no caben, prueba otros ángulos. Ajusta el largo de la bajante. Ctrl+Z deshace todo.",
        };
        mepPanel.AddItem(alcButton);

        // --- Panel Coordinación: interferencias ---
        RibbonPanel coordinacionPanel = application.CreateRibbonPanel(tabName, "Coordinación");

        var clashButton = new PushButtonData(
            "ClashMarkerCommand",
            "Interfe-\nrencias",
            assemblyPath,
            "Kaiken.ClashMarkerCommand"
        )
        {
            LargeImage = LoadIcon("interferencias_32.png"),
            ToolTip = "Revisa escalerillas (o ductos, tuberías, conduits) del modelo contra las categorías y vínculos que elijas, y muestra el resultado en una vista 3D propia: " +
                      "todo semitransparente, los elementos con choque en naranjo y el volumen de cada choque en rojo. El resultado queda guardado en esa vista: " +
                      "una nueva revisión la reemplaza, y para conservarla basta con cambiarle el nombre. No se toca ninguna otra vista.",
        };
        coordinacionPanel.AddItem(clashButton);

        var clashNavButton = new PushButtonData(
            "ClashNavigateCommand",
            "Navegar\nInterferencias",
            assemblyPath,
            "Kaiken.ClashNavigateCommand"
        )
        {
            LargeImage = LoadIcon("interferencias_navegar_32.png"),
            ToolTip = "Abre la lista de interferencias de la vista activa (o de la vista por defecto) para revisarlas una por una: " +
                      "va a cada choque con caja de sección y zoom, lo destaca en amarillo y permite marcarlo resuelto o re-verificar todo después de corregir.",
        };
        coordinacionPanel.AddItem(clashNavButton);

        var clashClearButton = new PushButtonData(
            "ClashClearCommand",
            "Limpiar\nInterferencias",
            assemblyPath,
            "Kaiken.ClashClearCommand"
        )
        {
            LargeImage = LoadIcon("interferencias_limpiar_32.png"),
            ToolTip = "Quita las marcas de Interferencias (el volumen rojo y los colores) de la vista activa, aunque esté renombrada; si la vista activa no tiene marcas, de la vista de interferencias por defecto.",
        };
        coordinacionPanel.AddItem(clashClearButton);

        // El rojo de Interferencias vive guardado en la vista: se vuelve a dibujar al abrirla.
        application.ViewActivated += (sender, e) =>
        {
            try
            {
                ClashDisplay.Ensure(e.CurrentActiveView);
                (sender as UIApplication)?.ActiveUIDocument?.RefreshActiveView();
            }
            catch { /* nunca bloquear el cambio de vista */ }
        };
        application.ControlledApplication.DocumentClosing += (_, e) =>
        {
            ClashNavigator.CloseFor(e.Document);
            ClashDisplay.Forget(e.Document);
        };

        // --- Panel Rooms: buscar rooms y etiquetar las de vínculos ---
        RibbonPanel navegarPanel = application.CreateRibbonPanel(tabName, "Rooms");

        var findRoomButton = new PushButtonData(
            "FindRoomCommand",
            "Buscar\nRoom",
            assemblyPath,
            "Kaiken.FindRoomCommand"
        )
        {
            LargeImage = LoadIcon("buscar_room_32.png"),
            ToolTip = "Busca rooms por número, nombre, nivel o texto de sus etiquetas, en el modelo actual y en los vínculos cargados. " +
                      "Te lleva a una planta donde se ve la room (primero las que tienen su etiqueta) o la destaca en la vista actual: hace zoom y la deja seleccionada. No modifica el modelo.",
        };
        navegarPanel.AddItem(findRoomButton);

        var linkedRoomTagsButton = new PushButtonData(
            "LinkedRoomTagsCommand",
            "Etiquetar\nRooms Vínculo",
            assemblyPath,
            "Kaiken.LinkedRoomTagsCommand"
        )
        {
            LargeImage = LoadIcon("etiquetar_rooms_32.png"),
            ToolTip = "Etiqueta en la planta activa las habitaciones de un vínculo que están en su nivel y aún no tienen etiqueta, con el tipo de etiqueta que elijas " +
                      "(opcionalmente solo las que caen en la región de recorte). Sirve donde Tag All Not Tagged no encuentra las habitaciones del vínculo. Un Ctrl+Z deshace todo.",
        };
        navegarPanel.AddItem(linkedRoomTagsButton);

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

        // --- Panel Vistas: crear varias dependientes de una vez ---
        RibbonPanel vistasPanel = application.CreateRibbonPanel(tabName, "Vistas");
        var dependentViewsButton = new PushButtonData(
            "DependentViewsCommand",
            "Crear\nDependientes",
            assemblyPath,
            "Kaiken.DependentViewsCommand"
        )
        {
            LargeImage = LoadIcon("dependientes_32.png"),
            ToolTip = "Crea de una sola vez varias vistas dependientes de la vista principal (la marcada en el Project Browser o la activa), sin abrirlas. " +
                      "Los nombres se escriben, se generan (\"<vista> - Dependent N\") o se copian de otras vistas del mismo tipo; si una vista ya usa el nombre, " +
                      "se renombra con \" (antigua)\", se elimina o se omite, a elección. Un Ctrl+Z deshace todo.",
        };
        vistasPanel.AddItem(dependentViewsButton);

        // Alinear / Guardar Alineación en un botón dividido: abajo se elige el otro.
        var alignViewportsButton = new PushButtonData(
            "AlignViewportsCommand",
            "Alinear\nViewports",
            assemblyPath,
            "Kaiken.AlignViewportsCommand"
        )
        {
            LargeImage = LoadIcon("alinear_viewports_32.png"),
            ToolTip = "Aplica una alineación guardada (archivo .json de Guardar Alineación) a las láminas de este modelo. Cada lámina cuyo sector " +
                      "(última palabra del nombre, ej. A1) esté en el archivo toma el scope box guardado y su viewport queda exactamente en el mismo lugar del papel. " +
                      "Además queda con viewport \"Sin título\", recorte oculto y annotation crop marcado. Un Ctrl+Z deshace todo.",
        };
        var saveLayoutButton = new PushButtonData(
            "SaveViewportLayoutCommand",
            "Guardar\nAlineación",
            assemblyPath,
            "Kaiken.SaveViewportLayoutCommand"
        )
        {
            LargeImage = LoadIcon("guardar_alineacion_32.png"),
            ToolTip = "Marca en el Project Browser las láminas ya alineadas (una por sector) y guarda en un .json, por sector, su scope box y dónde queda el viewport en el papel, " +
                      "para aplicarlo con Alinear Viewports en cualquier modelo. Si el archivo ya existe se combina. No modifica el modelo.",
        };
        var alignSplit = (SplitButton)vistasPanel.AddItem(new SplitButtonData("AlignViewportsSplit", "Alinear Viewports"));
        alignSplit.IsSynchronizedWithCurrentItem = false;
        alignSplit.AddPushButton(alignViewportsButton);
        alignSplit.AddPushButton(saveLayoutButton);

        // Capas tipo Photoshop: panel acoplable (se registra aquí) y botón que lo muestra u oculta.
        LayersPane.Register(application);
        var layersButton = new PushButtonData(
            "LayersCommand",
            "Capas",
            assemblyPath,
            "Kaiken.LayersCommand"
        )
        {
            LargeImage = LoadIcon("capas_32.png"),
            ToolTip = "Muestra u oculta el panel Capas: agrupa a mano los elementos que te importan ahora y préndelos, apágalos, " +
                      "colorealos o déjalos en semitono con un clic en la vista activa. Cada capa es un filtro de vista sobre el parámetro " +
                      "Kaiken_Capa (queda en el modelo, se imprime y funciona con plantillas); las etiquetas y textos se guardan en la capa.",
        };
        vistasPanel.AddItem(layersButton);

        // --- Panel Ventanas: guardar/restaurar las vistas abiertas ---
        RibbonPanel ventanasPanel = application.CreateRibbonPanel(tabName, "Ventanas");
        var saveViewsButton = new PushButtonData(
            "SaveViewsCommand",
            "Guardar\nVistas",
            assemblyPath,
            "Kaiken.SaveViewsCommand"
        )
        {
            LargeImage = LoadIcon("guardar_vistas_32.png"),
            ToolTip = "Guarda las vistas abiertas del modelo con su zoom, cuál está activa y dónde está cada ventana, " +
                      "incluidas las vistas arrastradas a otros monitores (un estado por modelo). No modifica el modelo.",
        };
        ventanasPanel.AddItem(saveViewsButton);

        var restoreViewsButton = new PushButtonData(
            "RestoreViewsCommand",
            "Restaurar\nVistas",
            assemblyPath,
            "Kaiken.RestoreViewsCommand"
        )
        {
            LargeImage = LoadIcon("restaurar_vistas_32.png"),
            ToolTip = "Reabre las vistas guardadas con Guardar Vistas, devuelve cada ventana a su lugar (también las flotantes en otros monitores), " +
                      "restaura su zoom y deja activa la que lo estaba. Si el monitor de una ventana ya no está conectado, esa ventana queda donde Revit la abra.",
        };
        ventanasPanel.AddItem(restoreViewsButton);

        // Modo desarrollo: panel "Dev" al final de la cinta y panel acoplable contenedor, solo si
        // "devMode": { "enabled": true } en settings.json. Apagado no registra nada.
        if (KaikenSettings.Current.DevMode.Enabled)
            DevMode.Setup(application, tabName, assemblyPath);

        // Puente en vivo: socket TCP local (127.0.0.1) + ExternalEvent, para que Claude
        // pueda leer y editar el modelo mientras Revit está abierto. Apagado por defecto;
        // se activa con "liveBridge": { "enabled": true } en %APPDATA%\Kaiken\settings.json.
        // Si el puerto ya está tomado (otra instancia de Revit, ej. 2025 y 2026 abiertos
        // a la vez) el puente queda desactivado en esta instancia, pero la cinta sigue
        // funcionando: una excepción aquí haría que Revit descarte todo el add-in.
        var bridgeSettings = KaikenSettings.Current.LiveBridge;
        BridgeLog($"arranque: enabled={bridgeSettings.Enabled} puerto={bridgeSettings.Port}");
        if (bridgeSettings.Enabled)
        {
            string? error = StartBridge(bridgeSettings.Port);
            if (error == null) BridgeLog($"puente escuchando en 127.0.0.1:{bridgeSettings.Port}");
            else BridgeLog($"puerto {bridgeSettings.Port}: {error}");
        }

        return Result.Succeeded;
    }

    /// <summary>Arranca el puente en vivo. Devuelve null si quedó escuchando o el error si falló. Requiere contexto de la API de Revit.</summary>
    internal string? StartBridge(int port)
    {
        if (_liveBridgeServer != null) return null;
        try
        {
            var handler = new LiveBridgeEventHandler();
            _liveBridgeEvent = ExternalEvent.Create(handler);
            _liveBridgeServer = new LiveBridgeServer(_liveBridgeEvent, handler, port);
            _liveBridgeServer.Start();
            return null;
        }
        catch (Exception ex)
        {
            _liveBridgeServer = null;
            System.Diagnostics.Debug.WriteLine($"Kaiken: puente en vivo desactivado ({ex.Message})");
            return ex.ToString();
        }
    }

    internal static void BridgeLog(string line)
    {
        try
        {
            System.IO.Directory.CreateDirectory(KaikenSettings.FolderPath);
            System.IO.File.AppendAllText(System.IO.Path.Combine(KaikenSettings.FolderPath, "bridge.log"),
                $"{DateTime.Now:s} {line}{Environment.NewLine}");
        }
        catch { /* el log es solo diagnóstico */ }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        _liveBridgeServer?.Stop();
        return Result.Succeeded;
    }
}
