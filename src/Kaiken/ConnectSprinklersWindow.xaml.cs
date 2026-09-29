using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace Kaiken;

public partial class ConnectSprinklersWindow : Window
{
    private readonly List<(CheckBox Box, List<FamilyInstance> Items)> _typeRows = new();
    private readonly List<(CheckBox Box, PipingSystemType Type)> _systemRows = new();

    public List<FamilyInstance> SelectedSprinklers { get; private set; } = new();
    public HashSet<ElementId> SelectedSystemTypeIds { get; private set; } = new();
    public double ToleranceCm { get; private set; }
    public double MaxDistanceM { get; private set; }

    public ConnectSprinklersWindow(List<FamilyInstance> sprinklers, List<PipingSystemType> systemTypes)
    {
        InitializeComponent();

        var groups = sprinklers
            .GroupBy(s => $"{s.Symbol.FamilyName} : {s.Symbol.Name}")
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var g in groups)
        {
            var items = g.ToList();
            int pending = items.Count(s => !ConnectSprinklersHelpers.IsConnected(s));
            var box = new CheckBox
            {
                Content = $"{g.Key}   ({items.Count}, {pending} sin conectar)",
                IsChecked = true,
                Margin = new Thickness(0, 2, 0, 2),
            };
            box.Checked += (_, _) => UpdatePreview();
            box.Unchecked += (_, _) => UpdatePreview();
            SprinklerList.Children.Add(box);
            _typeRows.Add((box, items));
        }

        foreach (var t in systemTypes)
        {
            var box = new CheckBox
            {
                Content = t.Name,
                IsChecked = ConnectSprinklersHelpers.IsFireSystem(t),
                Margin = new Thickness(0, 2, 0, 2),
            };
            SystemList.Children.Add(box);
            _systemRows.Add((box, t));
        }

        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var chosen = _typeRows.Where(r => r.Box.IsChecked == true).SelectMany(r => r.Items).ToList();
        int pending = chosen.Count(s => !ConnectSprinklersHelpers.IsConnected(s));
        PreviewText.Text = $"{chosen.Count} rociadores elegidos: {chosen.Count - pending} ya conectados (se descartan), {pending} por conectar.";
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedSprinklers = _typeRows.Where(r => r.Box.IsChecked == true).SelectMany(r => r.Items).ToList();
        SelectedSystemTypeIds = _systemRows.Where(r => r.Box.IsChecked == true).Select(r => r.Type.Id).ToHashSet();

        if (SelectedSprinklers.Count == 0)
        {
            MessageBox.Show("Marca al menos una Familia/Tipo de rociador.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (SelectedSystemTypeIds.Count == 0)
        {
            MessageBox.Show("Marca al menos un Tipo de Sistema de tubería.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryParse(ToleranceBox.Text, out double tol) || !TryParse(MaxDistanceBox.Text, out double maxD))
        {
            MessageBox.Show("La tolerancia y la distancia máxima deben ser números mayores que cero.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ToleranceCm = tol;
        MaxDistanceM = maxD;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
