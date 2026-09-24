using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;

namespace Kaiken;

public partial class DwgToBlocksWindow : Window
{
    private readonly Document _doc;
    private List<ImportInstance> _imports = new();
    private List<CadBlockInstanceInfo> _allBlocks = new();
    private List<FamilySymbol> _symbols = new();
    private List<Level> _levels = new();

    private const string AllBlocksSentinel = "(Todos los bloques de la capa)";

    public ImportInstance? SelectedImport { get; private set; }
    public string SelectedLayer { get; private set; } = "";
    public string SelectedBlockName { get; private set; } = "";
    public bool SelectAllBlocks { get; private set; } = false;
    public FamilySymbol? SelectedSymbol { get; private set; }
    public Level? SelectedLevel { get; private set; }
    public double ElevationOffsetMeters { get; private set; } = 0.0;
    public bool UseDefaultFamilyElevation { get; private set; } = false;
    public double AngleOffsetDeg { get; private set; } = 0.0;
    public bool PinElements { get; private set; } = false;

    public DwgToBlocksWindow(Document doc, ImportInstance? preSelectedImport = null)
    {
        InitializeComponent();
        _doc = doc;

        LoadInitialData(preSelectedImport);
    }

    private void LoadInitialData(ImportInstance? preSelectedImport)
    {
        // 1. Cargar Vínculos DWG
        _imports = DwgToBlocksHelpers.GetImportInstances(_doc);
        CadImportCombo.ItemsSource = _imports.Select(i => $"{i.Category?.Name ?? "DWG"} [Id: {i.Id}]").ToList();

        if (_imports.Count == 0)
        {
            MessageBox.Show("No se encontraron archivos DWG vinculados o importados en el proyecto.",
                "Sin Planos CAD", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        // Preselección si el usuario seleccionó un DWG en Revit antes de presionar el botón
        int initialIdx = 0;
        if (preSelectedImport != null)
        {
            int foundIdx = _imports.FindIndex(i => i.Id == preSelectedImport.Id);
            if (foundIdx >= 0) initialIdx = foundIdx;
        }
        CadImportCombo.SelectedIndex = initialIdx;

        // 2. Cargar Familias y Tipos de Revit
        _symbols = DwgToBlocksHelpers.GetAllFamilySymbols(_doc);
        FamilySymbolCombo.ItemsSource = _symbols.Select(s => $"{s.FamilyName}: {s.Name}").ToList();

        // 3. Cargar Niveles
        _levels = DwgToBlocksHelpers.GetLevels(_doc);
        LevelCombo.ItemsSource = _levels.Select(l => l.Name).ToList();

        // Intentar seleccionar el nivel de la vista activa
        View activeView = _doc.ActiveView;
        Level? viewLevel = activeView.GenLevel ?? _levels.FirstOrDefault();
        if (viewLevel != null)
        {
            int lvlIdx = _levels.FindIndex(l => l.Id == viewLevel.Id);
            if (lvlIdx >= 0) LevelCombo.SelectedIndex = lvlIdx;
        }
        else if (_levels.Count > 0)
        {
            LevelCombo.SelectedIndex = 0;
        }
    }

    private void CadImportCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CadImportCombo.SelectedIndex < 0 || CadImportCombo.SelectedIndex >= _imports.Count)
            return;

        SelectedImport = _imports[CadImportCombo.SelectedIndex];
        RefreshBlockScan();
    }

    private void RefreshBlockScan()
    {
        if (SelectedImport == null) return;

        string? prevLayer = LayerCombo.SelectedItem?.ToString();

        // Escanear bloques dentro del DWG (Revit convierte la geometría automáticamente a pies)
        _allBlocks = DwgToBlocksHelpers.ExtractBlocksFromImport(SelectedImport);

        // Cargar Capas
        var layers = _allBlocks.Select(b => b.LayerName).Distinct().OrderBy(l => l).ToList();
        layers.Insert(0, "(Todas las capas)");

        LayerCombo.ItemsSource = layers;

        if (prevLayer != null && layers.Contains(prevLayer))
        {
            LayerCombo.SelectedItem = prevLayer;
        }
        else
        {
            LayerCombo.SelectedIndex = 0;
        }
    }

    private void LayerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LayerCombo.SelectedItem == null) return;

        string layer = LayerCombo.SelectedItem.ToString() ?? "";
        IEnumerable<CadBlockInstanceInfo> filtered = _allBlocks;

        if (!string.IsNullOrEmpty(layer) && layer != "(Todas las capas)")
        {
            filtered = _allBlocks.Where(b => string.Equals(b.LayerName, layer, StringComparison.OrdinalIgnoreCase));
        }

        // Agrupar por nombre de bloque y mostrar cantidad
        var filteredList = filtered.ToList();
        var blockGroups = filteredList.GroupBy(b => b.BlockName)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderBy(b => b.Name)
            .ToList();

        // Opción para insertar TODOS los bloques de la capa, sin importar su nombre.
        // Útil cuando el DWG tiene nombres de bloque inconsistentes (ej. bloques anónimos
        // tipo "*U10" generados por AutoCAD para inserciones dinámicas) que no calzan
        // exactamente con el nombre esperado y quedarían fuera de un filtro por nombre.
        var items = new List<string> { $"{AllBlocksSentinel} ({filteredList.Count} elem)" };
        items.AddRange(blockGroups.Select(b => $"{b.Name} ({b.Count} elem)"));

        BlockCombo.ItemsSource = items;
        if (items.Count > 0)
        {
            BlockCombo.SelectedIndex = 0;
        }
    }

    private void UseDefaultElevationCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        bool isDefault = UseDefaultElevationCheckBox.IsChecked == true;
        ElevationBox.IsEnabled = !isDefault;
    }

    private static double ParseDoubleInvariant(string text, double defaultValue = 0.0)
    {
        if (string.IsNullOrWhiteSpace(text)) return defaultValue;
        string clean = text.Trim().Replace(',', '.');
        if (double.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out double res))
            return res;
        return defaultValue;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedImport == null)
        {
            MessageBox.Show("Selecciona un plano CAD válido.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (BlockCombo.SelectedItem == null && string.IsNullOrWhiteSpace(BlockCombo.Text))
        {
            MessageBox.Show("Selecciona un Bloque CAD.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (FamilySymbolCombo.SelectedIndex < 0 && string.IsNullOrWhiteSpace(FamilySymbolCombo.Text))
        {
            MessageBox.Show("Selecciona una Familia y Tipo de Revit.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (LevelCombo.SelectedIndex < 0 || LevelCombo.SelectedIndex >= _levels.Count)
        {
            MessageBox.Show("Selecciona un Nivel de Base.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Determinar Bloque CAD seleccionado
        string rawBlock = BlockCombo.SelectedItem != null ? BlockCombo.SelectedItem.ToString()! : BlockCombo.Text;
        if (rawBlock.Contains(" (") && rawBlock.EndsWith(" elem)"))
        {
            int lastParen = rawBlock.LastIndexOf(" (");
            rawBlock = rawBlock.Substring(0, lastParen);
        }
        rawBlock = rawBlock.Trim();

        SelectAllBlocks = rawBlock == AllBlocksSentinel;
        SelectedBlockName = SelectAllBlocks ? "" : rawBlock;

        // Determinar Familia de Revit elegida
        if (FamilySymbolCombo.SelectedIndex >= 0)
        {
            SelectedSymbol = _symbols[FamilySymbolCombo.SelectedIndex];
        }
        else
        {
            string typedText = FamilySymbolCombo.Text.Trim();
            SelectedSymbol = _symbols.FirstOrDefault(s =>
                $"{s.FamilyName}: {s.Name}".Equals(typedText, StringComparison.OrdinalIgnoreCase) ||
                s.Name.Equals(typedText, StringComparison.OrdinalIgnoreCase) ||
                s.FamilyName.Equals(typedText, StringComparison.OrdinalIgnoreCase));
        }

        if (SelectedSymbol == null)
        {
            MessageBox.Show("No se encontró la Familia/Tipo especificado en el proyecto.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Capa seleccionada
        string layer = LayerCombo.SelectedItem?.ToString() ?? "";
        SelectedLayer = (layer == "(Todas las capas)") ? "" : layer;

        // Nivel
        SelectedLevel = _levels[LevelCombo.SelectedIndex];

        // Parámetros rellenables (Parseando con soporte para punto '.' y coma ',')
        UseDefaultFamilyElevation = UseDefaultElevationCheckBox.IsChecked == true;
        ElevationOffsetMeters = ParseDoubleInvariant(ElevationBox.Text, 0.0);
        AngleOffsetDeg = ParseDoubleInvariant(AngleOffsetBox.Text, 0.0);

        PinElements = PinCheckBox.IsChecked == true;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
