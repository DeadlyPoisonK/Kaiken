using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Kaiken;

/// <summary>
/// Botón Dev: elegir qué correr del build de prueba cargado. Los comandos se ejecutan; los
/// paneles (FrameworkElement) se muestran en el panel acoplable Kaiken Dev.
/// </summary>
public partial class DevWindow : Window
{
    private sealed record Entry(string Label, Type Type);

    private readonly List<Entry> _entries;

    public Type? Selected { get; private set; }

    internal DevWindow(Assembly asm, IEnumerable<Type> commands, IEnumerable<Type> panels, string? lastChoice)
    {
        InitializeComponent();

        string built = File.Exists(asm.Location) ? File.GetLastWriteTime(asm.Location).ToString("dd-MM HH:mm:ss") : "?";
        HeaderText.Text = $"Build de prueba: {asm.GetName().Name} (compilado {built})";

        _entries = panels.Select(t => new Entry($"Panel     {t.FullName}", t))
            .Concat(commands.Select(t => new Entry($"Comando   {t.FullName}", t)))
            .ToList();
        EntryList.ItemsSource = _entries;
        EntryList.SelectedItem = _entries.FirstOrDefault(e => e.Type.FullName == lastChoice) ?? _entries.FirstOrDefault();
        EntryList.ScrollIntoView(EntryList.SelectedItem);

        Loaded += (_, _) => FilterBox.Focus();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string text = FilterBox.Text.Trim();
        var visible = text.Length == 0
            ? _entries
            : _entries.Where(x => x.Label.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        EntryList.ItemsSource = visible;
        if (EntryList.SelectedItem == null) EntryList.SelectedItem = visible.FirstOrDefault();
    }

    private void Run_Click(object sender, RoutedEventArgs e) => Accept();

    private void EntryList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void Accept()
    {
        if (EntryList.SelectedItem is not Entry entry)
        {
            MessageBox.Show("Elige un comando o panel.", "Kaiken Dev", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Selected = entry.Type;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
