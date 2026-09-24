using System.Collections.Generic;
using System.Windows;

namespace Kaiken;

/// <summary>
/// Paso 1 de "Editar Revisiones del Set": elegir un Sheet Set (Publish Set) puntual.
/// A diferencia de los combos "Aplicar a" de Agregar/Modificar Revisión, acá no hay
/// opción "Todas las láminas": esta herramienta siempre opera sobre un set específico.
/// </summary>
public partial class SelectPublishSetWindow : Window
{
    public string SelectedSetName { get; private set; } = "";

    public SelectPublishSetWindow(IEnumerable<string> sheetSetNames)
    {
        InitializeComponent();

        foreach (var name in sheetSetNames)
            SetComboBox.Items.Add(name);

        if (SetComboBox.Items.Count > 0)
            SetComboBox.SelectedIndex = 0;
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        string? selected = SetComboBox.SelectedItem as string;
        if (string.IsNullOrEmpty(selected))
        {
            MessageBox.Show("Elegí un Set para continuar.", "Editar Revisiones del Set", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedSetName = selected;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
