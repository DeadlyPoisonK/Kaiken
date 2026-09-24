using System.Collections.Generic;
using System.Windows;

namespace Kaiken;

public partial class AddRevisionWindow : Window
{
    public string RevisionCode { get; private set; } = "";
    public string RevisionDate { get; private set; } = "";
    public bool FilterLength29 => FilterLength29CheckBox.IsChecked == true;

    /// <summary>Nombre del Sheet Set (Publish Set) elegido, o ExportHelpers.AllSheetsOption si no se restringe.</summary>
    public string SelectedSetName { get; private set; } = ExportHelpers.AllSheetsOption;

    public AddRevisionWindow(string suggestedRevision, string suggestedDate, IEnumerable<string> sheetSetNames)
    {
        InitializeComponent();
        var revisionSettings = KaikenSettings.Current.Revisions;
        FilterLength29CheckBox.Content = revisionSettings.LengthFilterLabel;
        AlwaysIncludeNote.Text = revisionSettings.AlwaysIncludeSheets.Length > 0
            ? $"Siempre se incluyen: {string.Join(", ", revisionSettings.AlwaysIncludeSheets)}. La Descripción de la Revisión queda vacía a propósito."
            : "La Descripción de la Revisión queda vacía a propósito.";
        RevisionTextBox.Text = suggestedRevision;
        DateTextBox.Text = suggestedDate;

        SetComboBox.Items.Add(ExportHelpers.AllSheetsOption);
        foreach (var name in sheetSetNames)
            SetComboBox.Items.Add(name);
        SetComboBox.SelectedIndex = 0;

        RevisionTextBox.Focus();
        RevisionTextBox.SelectAll();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        string rev = RevisionTextBox.Text.Trim();
        string date = DateTextBox.Text.Trim();

        if (string.IsNullOrEmpty(rev))
        {
            MessageBox.Show("Por favor ingresa un código de revisión válido.", "Agregar Revisión", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrEmpty(date))
        {
            MessageBox.Show("Por favor ingresa una fecha válida.", "Agregar Revisión", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RevisionCode = rev;
        RevisionDate = date;
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
