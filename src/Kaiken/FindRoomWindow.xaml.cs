using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;

namespace Kaiken;

public partial class FindRoomWindow : Window
{
    public enum FindRoomAction { None, HighlightHere, GoToView }

    // Se recuerdan entre usos para poder volver a la misma búsqueda.
    private static string _lastQuery = "";
    private static bool _lastIncludeLinks = true;

    private readonly Document _doc;
    private readonly List<RoomHit> _allHits;
    private readonly Dictionary<RoomHit, List<RoomViewOption>> _viewCache = new();

    public RoomHit? SelectedHit { get; private set; }
    public View? SelectedView { get; private set; }
    public FindRoomAction Action { get; private set; } = FindRoomAction.None;

    public FindRoomWindow(Document doc, List<RoomHit> hits)
    {
        InitializeComponent();
        _doc = doc;
        _allHits = hits;

        IncludeLinksCheck.IsChecked = _lastIncludeLinks;
        SearchBox.Text = _lastQuery;
        SearchBox.SelectAll();
        Loaded += (_, _) => SearchBox.Focus();
        Closing += (_, _) => Remember();

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        bool includeLinks = IncludeLinksCheck.IsChecked == true;
        string query = SearchBox.Text ?? "";

        var filtered = _allHits
            .Where(h => includeLinks || !h.IsLinked)
            .Where(h => FindRoomHelpers.Matches(h, query))
            .ToList();

        ResultsGrid.ItemsSource = filtered;
        int total = _allHits.Count(h => includeLinks || !h.IsLinked);
        CountText.Text = $"{filtered.Count} de {total} rooms";
        if (filtered.Count > 0) ResultsGrid.SelectedIndex = 0;
        else UpdateViews(null);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void IncludeLinksCheck_Click(object sender, RoutedEventArgs e) => ApplyFilter();

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Flechas desde el cuadro de búsqueda mueven la selección de la lista sin soltar el teclado.
        if (e.Key != Key.Down && e.Key != Key.Up) return;
        int count = ResultsGrid.Items.Count;
        if (count == 0) return;
        int i = ResultsGrid.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
        ResultsGrid.SelectedIndex = Math.Clamp(i, 0, count - 1);
        ResultsGrid.ScrollIntoView(ResultsGrid.SelectedItem);
        e.Handled = true;
    }

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateViews(ResultsGrid.SelectedItem as RoomHit);

    private void UpdateViews(RoomHit? hit)
    {
        if (hit == null)
        {
            ViewCombo.ItemsSource = null;
            InfoText.Text = "";
            GoButton.IsEnabled = HighlightButton.IsEnabled = false;
            return;
        }

        if (!_viewCache.TryGetValue(hit, out var views))
            _viewCache[hit] = views = FindRoomHelpers.CandidateViews(_doc, hit);

        ViewCombo.ItemsSource = views;
        ViewCombo.SelectedIndex = views.Count > 0 ? 0 : -1;
        GoButton.IsEnabled = views.Count > 0;
        HighlightButton.IsEnabled = true;

        var info = new List<string>();
        if (views.Count == 0) info.Add("No hay plantas en el modelo actual para el nivel de esta room.");
        if (hit.LinkedTagViews.Count > 0)
            info.Add("Etiquetada dentro del vínculo en: " + string.Join(", ", hit.LinkedTagViews.Take(4))
                     + (hit.LinkedTagViews.Count > 4 ? "…" : ""));
        InfoText.Text = string.Join("  ", info);
    }

    private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsGrid.SelectedItem is RoomHit && GoButton.IsEnabled) Finish(FindRoomAction.GoToView);
    }

    private void Go_Click(object sender, RoutedEventArgs e)
    {
        if (ViewCombo.SelectedItem is not RoomViewOption)
        {
            MessageBox.Show("Elige una vista destino.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Finish(FindRoomAction.GoToView);
    }

    private void Highlight_Click(object sender, RoutedEventArgs e) => Finish(FindRoomAction.HighlightHere);

    private void Finish(FindRoomAction action)
    {
        if (ResultsGrid.SelectedItem is not RoomHit hit) return;
        SelectedHit = hit;
        SelectedView = (ViewCombo.SelectedItem as RoomViewOption)?.View;
        Action = action;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Remember()
    {
        _lastQuery = SearchBox.Text ?? "";
        _lastIncludeLinks = IncludeLinksCheck.IsChecked == true;
    }
}
