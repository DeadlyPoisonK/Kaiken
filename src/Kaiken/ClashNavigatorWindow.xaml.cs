using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Kaiken;

/// <summary>
/// Navegador no modal de Interferencias. No toca la API de Revit directamente: cada acción
/// se encola en <see cref="ClashNavigator"/>, que la ejecuta por ExternalEvent y luego llama
/// a <see cref="Load"/> con los datos actualizados de la vista.
/// </summary>
public partial class ClashNavigatorWindow : Window
{
    public sealed class Row
    {
        public int Index { get; init; }
        public int Number => Index + 1;
        public string StatusText { get; init; } = "";
        public string A { get; init; } = "";
        public string B { get; init; } = "";
        public string Source { get; init; } = "";
        public Brush Brush { get; init; } = Brushes.Black;
        internal ClashStatus Status { get; init; }
    }

    private static readonly Brush PendingBrush = new SolidColorBrush(Color.FromRgb(0x99, 0x1B, 0x1B));
    private static readonly Brush DoneBrush = new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D));

    private bool _loading;
    private List<Row> _all = new();

    internal string DocKey { get; }
    internal string ViewUid { get; }

    internal ClashNavigatorWindow(string docKey, string viewUid)
    {
        InitializeComponent();
        DocKey = docKey;
        ViewUid = viewUid;
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Top + 140;
    }

    /// <summary>Rellena la lista. Deja seleccionada la fila <paramref name="select"/> (o la siguiente visible) sin navegar.</summary>
    internal void Load(ClashData data, string viewName, int select, IList<string>? warnings = null, string? note = null)
    {
        _loading = true;
        try
        {
            _all = data.Items.Select((it, i) => new Row
            {
                Index = i,
                Status = it.Status,
                StatusText = it.Status switch
                {
                    ClashStatus.Resolved => "Resuelta",
                    ClashStatus.Gone => "Ya no choca",
                    _ => "Pendiente",
                },
                A = it.ALabel, B = it.BLabel, Source = it.Source,
                Brush = it.Status == ClashStatus.Pending ? PendingBrush : DoneBrush,
            }).ToList();

            int pending = _all.Count(r => r.Status == ClashStatus.Pending);
            var byModel = data.Items.Where(i => i.Status == ClashStatus.Pending)
                .GroupBy(i => i.Source).OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}: {g.Count()}");
            HeaderText.Text = $"{pending} pendientes de {_all.Count} · Vista «{viewName}»"
                              + (pending > 0 ? "\n" + string.Join(" · ", byModel) : "")
                              + (note != null ? "\n" + note : "");
            Title = $"Interferencias — {pending} pendientes";

            if (warnings != null && warnings.Count > 0)
            {
                WarningText.Text = "Subproyectos cerrados (lo que contienen no se revisó): " + string.Join(" | ", warnings);
                WarningText.Visibility = Visibility.Visible;
            }

            Refill(select);
        }
        finally { _loading = false; }
    }

    private void Refill(int select)
    {
        var visible = HideDoneCheck.IsChecked == true ? _all.Where(r => r.Status == ClashStatus.Pending).ToList() : _all;
        List.ItemsSource = visible;
        Row? row = visible.FirstOrDefault(r => r.Index == select)
                   ?? visible.FirstOrDefault(r => r.Index > select)
                   ?? visible.LastOrDefault();
        List.SelectedItem = row;
        if (row != null) List.ScrollIntoView(row);
        UpdateResolveButton();
    }

    internal int SelectedIndex => (List.SelectedItem as Row)?.Index ?? -1;

    private void UpdateResolveButton()
    {
        var row = List.SelectedItem as Row;
        ResolveButton.IsEnabled = row != null;
        ResolveButton.Content = row != null && row.Status != ClashStatus.Pending ? "Marcar pendiente" : "Marcar resuelta";
    }

    private void GoToSelected()
    {
        if (List.SelectedItem is Row row)
            ClashNavigator.GoTo(this, row.Index, SectionBoxCheck.IsChecked == true);
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateResolveButton();
        if (!_loading) GoToSelected();
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => GoToSelected();

    private void Move(int delta)
    {
        if (List.Items.Count == 0) return;
        int i = List.SelectedIndex < 0 ? 0 : Math.Clamp(List.SelectedIndex + delta, 0, List.Items.Count - 1);
        if (i == List.SelectedIndex) GoToSelected();   // en un extremo: vuelve a encuadrar
        else List.SelectedIndex = i;
        List.ScrollIntoView(List.SelectedItem);
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Move(+1);

    private void Resolve_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is Row row)
            ClashNavigator.ToggleResolved(this, row.Index, SectionBoxCheck.IsChecked == true);
    }

    private void Recheck_Click(object sender, RoutedEventArgs e) => ClashNavigator.RecheckAll(this);
    private void ShowAll_Click(object sender, RoutedEventArgs e) => ClashNavigator.ShowAll(this);

    private void HideDone_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try { Refill(SelectedIndex); }
        finally { _loading = false; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
