using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace Kaiken;

public partial class ChangePipeTypeWindow : Window
{
    private const string AllDiametersSentinel = "(Todos los diámetros)";

    private readonly Document _doc;
    private readonly List<Pipe> _pipesInView;
    private readonly List<PipeType> _allTypes;
    private List<string> _sourceTypeNames = new();

    public PipeType? SelectedSourceType { get; private set; }
    public double? SelectedDiameterMm { get; private set; }
    public PipeType? SelectedTargetType { get; private set; }

    public ChangePipeTypeWindow(Document doc, List<Pipe> pipesInView, List<PipeType> allTypes)
    {
        InitializeComponent();
        _doc = doc;
        _pipesInView = pipesInView;
        _allTypes = allTypes;

        LoadSourceTypes();
        TargetCombo.ItemsSource = _allTypes.Select(t => t.Name).ToList();
    }

    private PipeType? TypeOf(Pipe p) => _doc.GetElement(p.GetTypeId()) as PipeType;

    private void LoadSourceTypes()
    {
        _sourceTypeNames = _pipesInView
            .Select(p => TypeOf(p)?.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SourceCombo.ItemsSource = _sourceTypeNames;
        if (_sourceTypeNames.Count > 0)
            SourceCombo.SelectedIndex = 0;
    }

    private List<Pipe> PipesOfSelectedSource() =>
        SourceCombo.SelectedItem is string name
            ? _pipesInView.Where(p => TypeOf(p)?.Name == name).ToList()
            : new List<Pipe>();

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var pipes = PipesOfSelectedSource();

        var diameters = pipes
            .Select(ChangePipeTypeHelpers.DiameterMm)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        var items = new List<string> { AllDiametersSentinel };
        items.AddRange(diameters.Select(d => $"{d:0.#} mm ({pipes.Count(p => ChangePipeTypeHelpers.DiameterMm(p) == d)} tuberías)"));

        DiameterCombo.ItemsSource = items;
        DiameterCombo.SelectedIndex = items.Count > 0 ? 0 : -1;
    }

    private void DiameterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePreview();
        TryGuessTarget();
    }

    private double? ParseSelectedDiameter()
    {
        if (DiameterCombo.SelectedItem is not string text || text == AllDiametersSentinel)
            return null;

        string mmPart = text.Split(' ')[0];
        return double.TryParse(mmPart, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
    }

    private void UpdatePreview()
    {
        var pipes = PipesOfSelectedSource();
        double? diameter = ParseSelectedDiameter();
        if (diameter.HasValue)
            pipes = pipes.Where(p => ChangePipeTypeHelpers.DiameterMm(p) == diameter.Value).ToList();

        PreviewText.Text = pipes.Count == 1
            ? "Se cambiará 1 tubería."
            : $"Se cambiarán {pipes.Count} tuberías.";
    }

    private void TryGuessTarget()
    {
        double? diameter = ParseSelectedDiameter();
        if (!diameter.HasValue) return;

        string? sourceName = SourceCombo.SelectedItem as string;
        var guess = ChangePipeTypeHelpers.GuessTargetType(_allTypes, diameter.Value, sourceName);
        if (guess != null)
            TargetCombo.SelectedItem = guess.Name;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string? sourceName = SourceCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(sourceName))
        {
            MessageBox.Show("Selecciona una Familia de origen.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string? targetName = TargetCombo.SelectedItem as string ?? TargetCombo.Text?.Trim();
        var targetType = _allTypes.FirstOrDefault(t => string.Equals(t.Name, targetName, StringComparison.OrdinalIgnoreCase));
        if (targetType == null)
        {
            MessageBox.Show("Selecciona una Familia destino válida (existente en el proyecto).", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var sourcePipe = _pipesInView.First(p => TypeOf(p)?.Name == sourceName);
        SelectedSourceType = TypeOf(sourcePipe);
        SelectedDiameterMm = ParseSelectedDiameter();
        SelectedTargetType = targetType;

        if (SelectedSourceType!.Id == SelectedTargetType.Id)
        {
            MessageBox.Show("La Familia de origen y destino son la misma.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
