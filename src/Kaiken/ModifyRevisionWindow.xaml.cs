using System.Collections.Generic;
using System.Windows;

namespace Kaiken;

public partial class ModifyRevisionWindow : Window
{
    public string RevisionCode { get; private set; } = "RD";
    public bool FilterLength29 => FilterLength29CheckBox.IsChecked == true;
    public bool UpdateParam => UpdateParamCheckBox.IsChecked == true;
    public bool UpdateSheetNumber => UpdateSheetNumberCheckBox.IsChecked == true;

    /// <summary>Nombre del Sheet Set (Publish Set) elegido, o ExportHelpers.AllSheetsOption si no se restringe.</summary>
    public string SelectedSetName { get; private set; } = ExportHelpers.AllSheetsOption;

    public ModifyRevisionWindow(IEnumerable<string> sheetSetNames)
    {
        InitializeComponent();
        FilterLength29CheckBox.Content = KaikenSettings.Current.Revisions.LengthFilterLabel;

        SetComboBox.Items.Add(ExportHelpers.AllSheetsOption);
        foreach (var name in sheetSetNames)
            SetComboBox.Items.Add(name);
        SetComboBox.SelectedIndex = 0;

        RevisionTextBox.Focus();
        RevisionTextBox.SelectAll();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        string text = RevisionTextBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            MessageBox.Show("Por favor ingresa un código de revisión válido.", "Modificar Revisión", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RevisionCode = text;
        SelectedSetName = SetComboBox.SelectedItem as string ?? ExportHelpers.AllSheetsOption;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
