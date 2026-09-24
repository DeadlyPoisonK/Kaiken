using System.Collections.Generic;
using System.Windows;
using Microsoft.Win32;

namespace Kaiken;

/// <summary>
/// Ventana genérica de "elige carpeta + Sheet Set", reutilizada por los comandos
/// de exportación (DWG, PDF, DWF). El título distingue cuál formato se está exportando.
/// </summary>
public partial class ExportSheetSetWindow : Window
{
    public string SelectedFolder { get; private set; } = string.Empty;
    public string SelectedSetName { get; private set; } = string.Empty;

    public ExportSheetSetWindow(IEnumerable<string> sheetSetNames, string title, string exportButtonLabel = "Exportar")
    {
        InitializeComponent();
        Title = title;
        ExportButton.Content = exportButtonLabel;

        foreach (var name in sheetSetNames)
            SetComboBox.Items.Add(name);
        if (SetComboBox.Items.Count > 0)
            SetComboBox.SelectedIndex = 0;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Selecciona la carpeta de destino" };
        if (dialog.ShowDialog() == true)
        {
            FolderTextBox.Text = dialog.FolderName;
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        SelectedFolder = FolderTextBox.Text.Trim();
        SelectedSetName = SetComboBox.SelectedItem as string ?? string.Empty;

        if (string.IsNullOrWhiteSpace(SelectedFolder) || string.IsNullOrWhiteSpace(SelectedSetName))
        {
            MessageBox.Show("Selecciona una carpeta y un set de hojas.", "Faltan datos", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
