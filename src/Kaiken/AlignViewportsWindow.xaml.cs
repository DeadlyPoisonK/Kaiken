using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;

namespace Kaiken;

/// <summary>Una lámina en la lista de Alinear Viewports, con la entrada del archivo que le toca.</summary>
public sealed class AlignViewportRow : INotifyPropertyChanged
{
    private bool _isChecked;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    /// <summary>La lámina de la que se guardó la alineación de su sector (se resalta).</summary>
    public bool IsReference { get; init; }
    public bool CanToggle { get; init; } = true;
    public string Sector { get; init; } = "";
    public string SheetLabel { get; init; } = "";
    public string ViewName { get; init; } = "";
    public string Scale { get; init; } = "";
    public string ScopeBox { get; init; } = "";
    public string Note { get; init; } = "";
    internal ViewportLayoutEntry Entry { get; init; } = new();
    internal ElementId ViewportId { get; init; } = ElementId.InvalidElementId;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class AlignViewportsWindow : Window
{
    private readonly List<AlignViewportRow> _rows;

    public bool HideBoxes => HideBoxesCheck.IsChecked == true;

    public AlignViewportsWindow(List<AlignViewportRow> rows, string fileName)
    {
        InitializeComponent();
        Title = $"Alinear Viewports · {fileName}";
        _rows = rows;
        RowsGrid.ItemsSource = rows;
        foreach (var row in rows) row.PropertyChanged += (_, _) => UpdateCount();
        UpdateCount();
    }

    private void UpdateCount()
    {
        CountText.Text = $"{_rows.Count(r => r.IsChecked)} de {_rows.Count} lámina(s) marcadas";
    }

    private void SetSelected(bool value)
    {
        foreach (var row in RowsGrid.SelectedItems.OfType<AlignViewportRow>().Where(r => r.CanToggle))
            row.IsChecked = value;
    }

    private void CheckSelected_Click(object sender, RoutedEventArgs e) => SetSelected(true);
    private void UncheckSelected_Click(object sender, RoutedEventArgs e) => SetSelected(false);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
