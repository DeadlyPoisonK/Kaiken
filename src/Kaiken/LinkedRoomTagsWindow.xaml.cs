using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;

namespace Kaiken;

public partial class LinkedRoomTagsWindow : Window
{
    /// <summary>Último tipo usado en la sesión, para dejarlo elegido al volver a abrir.</summary>
    private static string? _lastTypeLabel;

    private readonly Document _doc;
    private readonly ViewPlan _view;
    private readonly List<FamilySymbol> _types;
    private List<LinkedRoomScan> _scans = new();

    internal LinkedRoomScan? SelectedScan { get; private set; }
    internal FamilySymbol? SelectedType { get; private set; }

    internal LinkedRoomTagsWindow(Document doc, ViewPlan view, List<FamilySymbol> types)
    {
        InitializeComponent();
        _doc = doc;
        _view = view;
        _types = types;

        HeaderText.Text = $"Etiqueta las habitaciones de un vínculo en la vista \"{view.Name}\" (nivel {view.GenLevel.Name}).";

        TypeCombo.ItemsSource = _types.Select(LinkedRoomTagsHelpers.TypeLabel).ToList();
        ElementId defaultId = doc.GetDefaultFamilyTypeId(new ElementId(BuiltInCategory.OST_RoomTags));
        string? defaultLabel = _types.FirstOrDefault(t => t.Id == defaultId) is { } d ? LinkedRoomTagsHelpers.TypeLabel(d) : null;
        TypeCombo.SelectedItem = _lastTypeLabel != null && _types.Any(t => LinkedRoomTagsHelpers.TypeLabel(t) == _lastTypeLabel)
            ? _lastTypeLabel
            : defaultLabel ?? (TypeCombo.Items.Count > 0 ? TypeCombo.Items[0] : null);

        CropCheck.IsEnabled = view.CropBoxActive;
        CropCheck.IsChecked = view.CropBoxActive; // dispara el primer escaneo si hay recorte
        if (!view.CropBoxActive) Rescan();
    }

    private void Rescan()
    {
        var previous = (LinkCombo.SelectedItem as ComboBoxItem)?.Tag as LinkedRoomScan;
        _scans = LinkedRoomTagsHelpers.Scan(_doc, _view, CropCheck.IsChecked == true);
        LinkCombo.ItemsSource = _scans.Select(s => new ComboBoxItem { Content = s.Label, Tag = s }).ToList();
        int keep = previous == null ? -1 : _scans.FindIndex(s => s.Link.Id == previous.Link.Id);
        LinkCombo.SelectedIndex = keep >= 0 ? keep : (_scans.Count > 0 ? 0 : -1);
        UpdatePreview();
    }

    private void CropCheck_Changed(object sender, RoutedEventArgs e) => Rescan();

    private void LinkCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (_scans.Count == 0)
        {
            PreviewText.Text = "Ningún vínculo visible en esta vista tiene habitaciones en este nivel.";
            OkButton.IsEnabled = false;
            return;
        }
        if ((LinkCombo.SelectedItem as ComboBoxItem)?.Tag is not LinkedRoomScan s) { PreviewText.Text = ""; return; }

        string levels = string.Join(", ", s.Levels.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}: {kv.Value}"));
        string text = s.Untagged.Count == 1 ? "Se etiquetará 1 habitación" : $"Se etiquetarán {s.Untagged.Count} habitaciones";
        text += $" de {s.OnLevel} en el nivel ({levels}).";
        if (s.AlreadyTagged > 0) text += $" {s.AlreadyTagged} ya tienen etiqueta.";
        if (s.OutsideCrop > 0) text += $" {s.OutsideCrop} quedan fuera del recorte.";
        PreviewText.Text = text;
        OkButton.IsEnabled = s.Untagged.Count > 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if ((LinkCombo.SelectedItem as ComboBoxItem)?.Tag is not LinkedRoomScan scan)
        {
            MessageBox.Show("Elige un vínculo.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string? label = TypeCombo.SelectedItem as string ?? TypeCombo.Text?.Trim();
        var type = _types.FirstOrDefault(t => string.Equals(LinkedRoomTagsHelpers.TypeLabel(t), label, StringComparison.OrdinalIgnoreCase));
        if (type == null)
        {
            MessageBox.Show("Elige un tipo de etiqueta de habitación del proyecto.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedScan = scan;
        SelectedType = type;
        _lastTypeLabel = LinkedRoomTagsHelpers.TypeLabel(type);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
