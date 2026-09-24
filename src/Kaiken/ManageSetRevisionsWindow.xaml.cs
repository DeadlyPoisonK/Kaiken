using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;

namespace Kaiken;

/// <summary>Una fila tildable de la lista de revisiones a editar.</summary>
public class RevisionCheckItem
{
    public ElementId Id { get; set; } = ElementId.InvalidElementId;
    public string Label { get; set; } = "";
    public bool IsChecked { get; set; } = true;
}

/// <summary>
/// Paso 2 de "Editar Revisiones del Set": muestra la unión de revisiones marcadas
/// manualmente (GetAdditionalRevisionIds) en las láminas del set elegido, todas
/// tildadas por defecto. Al Aplicar, las destildadas se sacan de una sola vez de
/// todas las láminas del set (en vez de abrir "Revisions on Sheet" lámina por lámina).
/// </summary>
public partial class ManageSetRevisionsWindow : Window
{
    private readonly List<RevisionCheckItem> _items;

    /// <summary>Ids de revisión que deben quedar marcadas en las láminas del set tras Aplicar.</summary>
    public IReadOnlyList<ElementId> IdsToKeep { get; private set; } = new List<ElementId>();

    public ManageSetRevisionsWindow(string setName, List<RevisionCheckItem> items, bool hasLockedByCloud)
    {
        InitializeComponent();

        SetNameText.Text = $"Set: {setName}";
        _items = items;
        RevisionsList.ItemsSource = _items;

        if (hasLockedByCloud)
        {
            NoteText.Text = "Aviso: además de estas, hay revisiones que se siguen mostrando en alguna lámina del set porque " +
                             "tienen una nube de revisión (Revision Cloud) asociada ahí; esas no se pueden destildar desde acá.";
            NoteText.Visibility = System.Windows.Visibility.Visible;
        }
    }

    private void CheckAll_Click(object sender, RoutedEventArgs e) => SetAll(true);

    private void UncheckAll_Click(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool value)
    {
        foreach (var item in _items) item.IsChecked = value;
        RevisionsList.Items.Refresh();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        IdsToKeep = _items.Where(i => i.IsChecked).Select(i => i.Id).ToList();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
