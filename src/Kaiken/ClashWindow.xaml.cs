using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using CheckBox = System.Windows.Controls.CheckBox;

namespace Kaiken;

public partial class ClashWindow : Window
{
    private const string HostKey = "\u0000host";

    private readonly List<(CheckBox box, ClashGroup group)> _aBoxes = new();
    private readonly List<(CheckBox box, ClashCategory cat)> _bBoxes = new();
    private readonly List<(CheckBox box, string key, RevitLinkInstance? link)> _modelBoxes = new();

    internal ClashOptions? Options { get; private set; }

    public ClashWindow(Document doc)
    {
        InitializeComponent();
        ClashPrefs? prefs = ClashPrefs.Load();

        foreach (var g in ClashHelpers.AGroups)
        {
            var box = new CheckBox
            {
                Content = g.Label, Margin = new Thickness(0, 2, 0, 2),
                IsChecked = prefs == null ? g.DefaultOn : prefs.ACategories.Contains(g.Categories[0].ToString()),
            };
            APanel.Children.Add(box);
            _aBoxes.Add((box, g));
        }

        foreach (var c in ClashHelpers.BCategories)
        {
            var box = new CheckBox
            {
                Content = c.Label, Width = 160, Margin = new Thickness(0, 2, 6, 2),
                IsChecked = prefs == null ? c.DefaultOn : prefs.BCategories.Contains(c.Bic.ToString()),
            };
            BPanel.Children.Add(box);
            _bBoxes.Add((box, c));
        }

        AddModel($"Este modelo ({doc.Title})", HostKey, null, prefs?.IncludeHost ?? true);

        var links = new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .Select(l => (link: l, linkDoc: l.GetLinkDocument()))
            .OrderBy(x => x.linkDoc?.Title ?? x.link.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var (link, linkDoc) in links)
        {
            if (linkDoc == null)
            {
                AddModel($"{link.Name} (no cargado)", link.Name, null, false).IsEnabled = false;
                continue;
            }
            // Si el mismo vínculo está colocado varias veces, cada copia sale por separado.
            bool repeated = links.Count(x => x.linkDoc?.Title == linkDoc.Title) > 1;
            string label = repeated ? $"{linkDoc.Title}  [{link.Id}]" : linkDoc.Title;
            bool on = prefs == null || !prefs.ExcludedLinks.Contains(linkDoc.Title);
            AddModel(label, linkDoc.Title, link, on);
        }

        ToleranceBox.Text = (prefs?.ToleranceMm ?? 10).ToString("0.#", CultureInfo.InvariantCulture);
    }

    private CheckBox AddModel(string label, string key, RevitLinkInstance? link, bool on)
    {
        var box = new CheckBox { Content = label, IsChecked = on, Margin = new Thickness(0, 2, 0, 2) };
        ModelPanel.Children.Add(box);
        _modelBoxes.Add((box, key, link));
        return box;
    }

    private void AllA_Click(object sender, RoutedEventArgs e) => _aBoxes.ForEach(b => b.box.IsChecked = true);
    private void NoA_Click(object sender, RoutedEventArgs e) => _aBoxes.ForEach(b => b.box.IsChecked = false);

    private void AllCats_Click(object sender, RoutedEventArgs e) => _bBoxes.ForEach(b => b.box.IsChecked = true);
    private void NoCats_Click(object sender, RoutedEventArgs e) => _bBoxes.ForEach(b => b.box.IsChecked = false);

    private void AllModels_Click(object sender, RoutedEventArgs e) =>
        _modelBoxes.Where(m => m.box.IsEnabled).ToList().ForEach(m => m.box.IsChecked = true);
    private void NoModels_Click(object sender, RoutedEventArgs e) => _modelBoxes.ForEach(m => m.box.IsChecked = false);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var opts = new ClashOptions();
        foreach (var (box, g) in _aBoxes.Where(x => x.box.IsChecked == true)) opts.ACategories.AddRange(g.Categories);
        foreach (var (box, c) in _bBoxes.Where(x => x.box.IsChecked == true)) opts.BCategories.Add(c.Bic);
        opts.IncludeHost = _modelBoxes.Any(m => m.key == HostKey && m.box.IsChecked == true);
        foreach (var m in _modelBoxes.Where(m => m.link != null && m.box.IsChecked == true)) opts.Links.Add(m.link!);

        if (opts.ACategories.Count == 0) { Warn("Elige qué revisar (paso 1)."); return; }
        if (opts.BCategories.Count == 0) { Warn("Elige al menos una categoría contra la que revisar (paso 2)."); return; }
        if (!opts.IncludeHost && opts.Links.Count == 0) { Warn("Elige al menos un modelo (paso 3)."); return; }
        if (!double.TryParse(ToleranceBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double tol) || tol < 0)
        {
            Warn("La tolerancia debe ser un número en mm (0 o más).");
            return;
        }
        opts.ToleranceMm = tol;

        new ClashPrefs
        {
            ACategories = _aBoxes.Where(x => x.box.IsChecked == true).Select(x => x.group.Categories[0].ToString()).ToList(),
            BCategories = opts.BCategories.Select(b => b.ToString()).ToList(),
            IncludeHost = opts.IncludeHost,
            ExcludedLinks = _modelBoxes.Where(m => m.link != null && m.box.IsChecked != true).Select(m => m.key).Distinct().ToList(),
            ToleranceMm = tol,
        }.Save();

        Options = opts;
        DialogResult = true;
        Close();
    }

    private static void Warn(string text) =>
        MessageBox.Show(text, "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
