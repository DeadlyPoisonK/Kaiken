using System.Globalization;
using System.Windows;

namespace Kaiken;

public class CeilingAltChoice
{
    public double ElevationM;
    public double OffsetCm;
    public bool InvertDirection;
}

public partial class CeilingAltWindow : Window
{
    public CeilingAltChoice Result { get; private set; } = new();

    public CeilingAltWindow(double suggestedElevationM)
    {
        InitializeComponent();
        ElevationBox.Text = suggestedElevationM.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static double Num(string s, double def) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = new CeilingAltChoice
        {
            ElevationM = Num(ElevationBox.Text, 3.05),
            OffsetCm = Num(OffsetBox.Text, 5.0),
            InvertDirection = InvertirBox.IsChecked == true
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
