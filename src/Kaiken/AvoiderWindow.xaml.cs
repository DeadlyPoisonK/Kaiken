using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Kaiken;

public partial class AvoiderWindow : Window
{
    public AvoiderParams Result { get; private set; } = new();

    public AvoiderWindow(double sugStartAngle = 45.0, double sugEndAngle = 45.0)
    {
        InitializeComponent();
        SetAngleSelection(StartAngleBox, sugStartAngle);
        SetAngleSelection(EndAngleBox, sugEndAngle);
    }

    private void SetAngleSelection(ComboBox box, double angle)
    {
        string angleStr = angle.ToString();
        foreach (ComboBoxItem item in box.Items)
        {
            if (item.Content.ToString() == angleStr)
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 1; // Default a 45
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        double startAngle = 45;
        if (StartAngleBox.SelectedItem is ComboBoxItem sItem && double.TryParse(sItem.Content.ToString(), out double sa))
            startAngle = sa;

        double endAngle = 45;
        if (EndAngleBox.SelectedItem is ComboBoxItem eItem && double.TryParse(eItem.Content.ToString(), out double ea))
            endAngle = ea;

        var dir = (DirectionBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Abajo";
        Result = new AvoiderParams
        {
            DistanceFt = 0,
            AngleDegStart = startAngle,
            AngleDegEnd = endAngle,
            ClearanceFt = 0,
            MergeClose = MergeBox.IsChecked == true,
            RotateTee = ChkRotateTee.IsChecked == true,
            MergeGapFt = 30.0 / 304.8, // 30 cm por defecto
            Direction = dir switch
            {
                "Arriba" => JogDir.Arriba,
                "Izquierda" => JogDir.Izquierda,
                "Derecha" => JogDir.Derecha,
                _ => JogDir.Abajo, // Abajo por defecto
            },
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

