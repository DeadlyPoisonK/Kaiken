using System;
using System.Collections.Generic;
using System.Windows;

namespace Kaiken;

public partial class ChangeDateAndRevWindow : Window
{
    public string RevisionDate { get; private set; } = DateTime.Now.ToString("dd-MM-yyyy");
    public bool SyncRevFromSheetNumber => SyncRevCheckBox.IsChecked == true;
    public bool FilterLength29 => FilterLength29CheckBox.IsChecked == true;

    /// <summary>Nombre del Sheet Set (Publish Set) elegido, o ExportHelpers.AllSheetsOption si no se restringe.</summary>
    public string SelectedSetName { get; private set; } = ExportHelpers.AllSheetsOption;

    public ChangeDateAndRevWindow(IEnumerable<string> sheetSetNames)
    {
        InitializeComponent();
        FilterLength29CheckBox.Content = KaikenSettings.Current.Revisions.LengthFilterLabel;
        DateTextBox.Text = DateTime.Now.ToString("dd-MM-yyyy");

        SetComboBox.Items.Add(ExportHelpers.AllSheetsOption);
        foreach (var name in sheetSetNames)
            SetComboBox.Items.Add(name);
        SetComboBox.SelectedIndex = 0;

        DateTextBox.Focus();
        DateTextBox.SelectAll();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        string text = DateTextBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            MessageBox.Show("Por favor ingresa una fecha válida.", "Cambiar Fecha y REV", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RevisionDate = text;
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
