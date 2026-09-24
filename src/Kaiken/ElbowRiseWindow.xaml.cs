using System.Globalization;
using System.Windows;

namespace Kaiken;

public class ElbowRiseChoice
{
    public double RiseCm;
    public bool Downward;
    public bool Invert;
}

public partial class ElbowRiseWindow : Window
{
    public ElbowRiseChoice Result { get; private set; } = new();

    internal ElbowRiseWindow(LiveBridgeOperations.ElbowRiseSuggestion s, string multiInfo = "", int elbowCount = 1)
    {
        InitializeComponent();

        string header = elbowCount > 1 ? multiInfo : "";

        InfoText.Text = header + $"El codo inicial come ≈{s.Elbow1StandoffCm:F1} cm, el codo final come ≈{s.Elbow2StandoffCm:F1} cm. " +
                      "Valores sugeridos = mínimo medido + 1 cm, sin inflar.";

        RiseBox.Text = Fmt(s.SuggestedRiseCm);

        if (elbowCount > 1)
            Title = $"Rou-C — {elbowCount} codos seleccionados";
    }

    private static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static double Num(string s, double def) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = new ElbowRiseChoice
        {
            RiseCm = Num(RiseBox.Text, 12),
            Downward = DownCheck.IsChecked == true,
            Invert = InvertCheck.IsChecked == true,
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
