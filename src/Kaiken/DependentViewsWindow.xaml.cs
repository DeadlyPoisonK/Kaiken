using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;

namespace Kaiken;

/// <summary>Una vista del modelo, tal como la necesita la ventana (sin tocar la API).</summary>
public sealed class DependentViewInfo
{
    public ElementId Id { get; init; } = ElementId.InvalidElementId;
    public string Name { get; init; } = "";
    public ViewType ViewType { get; init; }
    public ElementId PrimaryId { get; init; } = ElementId.InvalidElementId; // vista principal si es dependiente
}

/// <summary>Vista que admite dependientes, para el combo de vista principal.</summary>
public sealed class DependentParentOption
{
    public DependentViewInfo View { get; init; } = new();
    public string Label { get; init; } = ""; // View.Title, como en el Project Browser
}

/// <summary>Fila tildable de la lista de vistas de las que se toma el nombre.</summary>
public sealed class DependentSourceItem
{
    public string Name { get; init; } = "";
    public bool IsChecked { get; set; }
}

public enum DependentNameConflict { RenameExisting, DeleteExisting, Skip }

/// <summary>
/// Ventana de "Crear Dependientes": elige la vista principal y arma la lista de nombres, a mano,
/// con nombres automáticos ("&lt;vista&gt; - Dependent N", como Revit) o copiando los de otras vistas
/// del mismo tipo.
/// </summary>
public partial class DependentViewsWindow : Window
{
    // Caracteres que Revit no acepta en el nombre de una vista.
    private static readonly char[] InvalidChars = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

    private readonly List<DependentViewInfo> _allViews;
    private List<DependentSourceItem> _sources = new();

    public ElementId ParentId { get; private set; } = ElementId.InvalidElementId;
    public List<string> Names { get; private set; } = new();
    public DependentNameConflict Conflict { get; private set; }

    public DependentViewsWindow(List<DependentParentOption> parents, DependentParentOption initial, List<DependentViewInfo> allViews)
    {
        InitializeComponent();
        _allViews = allViews;
        ParentCombo.ItemsSource = parents;
        ParentCombo.SelectedItem = initial;
    }

    private DependentViewInfo? Primary => (ParentCombo.SelectedItem as DependentParentOption)?.View;

    private void ParentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Primary is not { } p) return;
        int deps = _allViews.Count(v => v.PrimaryId == p.Id);
        ParentInfoText.Text = deps == 0 ? "Todavía no tiene dependientes." : $"Ya tiene {deps} dependiente(s).";

        // Vistas del mismo tipo, sin la principal ni sus dependientes.
        _sources = _allViews
            .Where(v => v.ViewType == p.ViewType && v.Id != p.Id && v.PrimaryId != p.Id)
            .OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(v => new DependentSourceItem { Name = v.Name })
            .ToList();
        ApplyFilter();
        UpdateStatus();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string f = FilterBox.Text.Trim();
        SourceList.ItemsSource = f.Length == 0
            ? _sources
            : _sources.Where(s => s.Name.Contains(f, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private IEnumerable<DependentSourceItem> VisibleSources() =>
        SourceList.ItemsSource as IEnumerable<DependentSourceItem> ?? Enumerable.Empty<DependentSourceItem>();

    private void CheckVisible_Click(object sender, RoutedEventArgs e)
    {
        foreach (var s in VisibleSources()) s.IsChecked = true;
        SourceList.Items.Refresh();
    }

    private void UncheckAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var s in _sources) s.IsChecked = false;
        SourceList.Items.Refresh();
    }

    private void AddChecked_Click(object sender, RoutedEventArgs e)
    {
        var current = new HashSet<string>(ReadNames(), StringComparer.OrdinalIgnoreCase);
        AppendNames(_sources.Where(s => s.IsChecked && current.Add(s.Name)).Select(s => s.Name));
        foreach (var s in _sources) s.IsChecked = false;
        SourceList.Items.Refresh();
    }

    private void AddAuto_Click(object sender, RoutedEventArgs e)
    {
        if (Primary is not { } p) return;
        if (!int.TryParse(CountBox.Text.Trim(), out int n) || n < 1 || n > 200)
        {
            MessageBox.Show(this, "Indica una cantidad entre 1 y 200.", Title);
            return;
        }
        // Sigue la numeración de Revit: el siguiente N libre entre las vistas y la lista.
        var pattern = new Regex("^" + Regex.Escape(p.Name) + @" - Dependent (\d+)$", RegexOptions.IgnoreCase);
        int next = _allViews.Where(v => v.ViewType == p.ViewType).Select(v => v.Name).Concat(ReadNames())
            .Select(s => pattern.Match(s))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .DefaultIfEmpty(0).Max() + 1;
        AppendNames(Enumerable.Range(next, n).Select(i => $"{p.Name} - Dependent {i}"));
    }

    private void ClearNames_Click(object sender, RoutedEventArgs e) => NamesBox.Clear();

    private void NamesBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateStatus();

    private List<string> ReadNames() =>
        NamesBox.Text.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    private void AppendNames(IEnumerable<string> names)
    {
        var lines = ReadNames();
        lines.AddRange(names);
        NamesBox.Text = string.Join(Environment.NewLine, lines);
        NamesBox.CaretIndex = NamesBox.Text.Length;
        NamesBox.ScrollToEnd();
    }

    private void UpdateStatus()
    {
        if (Primary is not { } p || StatusText == null) return;
        var names = ReadNames();
        var taken = new HashSet<string>(_allViews.Where(v => v.ViewType == p.ViewType).Select(v => v.Name));
        int existing = names.Count(taken.Contains);
        StatusText.Text = names.Count == 0 ? "Agrega al menos un nombre."
            : existing == 0 ? ""
            : $"{existing} de {names.Count} nombres ya los usa otra vista.";
        CreateButton.Content = names.Count == 0 ? "Crear" : $"Crear {names.Count}";
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (Primary is not { } p) return;
        var names = ReadNames();
        if (names.Count == 0)
        {
            MessageBox.Show(this, "Agrega al menos un nombre.", Title);
            return;
        }
        var bad = names.Where(n => n.IndexOfAny(InvalidChars) >= 0).ToList();
        if (bad.Count > 0)
        {
            MessageBox.Show(this, "Revit no acepta estos caracteres en el nombre de una vista: \\ : { } [ ] | ; < > ? ` ~\n\n" +
                                  string.Join("\n", bad.Take(10)), Title);
            return;
        }
        var dup = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dup.Count > 0)
        {
            MessageBox.Show(this, "Hay nombres repetidos en la lista:\n\n" + string.Join("\n", dup.Take(10)), Title);
            return;
        }
        if (names.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Una dependiente no puede llamarse igual que su vista principal.", Title);
            return;
        }

        ParentId = p.Id;
        Names = names;
        Conflict = DeleteRadio.IsChecked == true ? DependentNameConflict.DeleteExisting
                 : SkipRadio.IsChecked == true ? DependentNameConflict.Skip
                 : DependentNameConflict.RenameExisting;
        DialogResult = true;
    }
}
