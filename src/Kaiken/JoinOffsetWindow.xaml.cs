using System.Globalization;
using System.Windows;

namespace Kaiken;

public class JoinOffsetChoice
{
    /// <summary>Ángulo de los dos codos; null = "Cualquier °" (se elige según desnivel y distancia).</summary>
    public double? AngleDeg = 45.0;
}

public partial class JoinOffsetWindow : Window
{
    public JoinOffsetChoice Result { get; private set; } = new();

    public JoinOffsetWindow(double offsetCm)
    {
        InitializeComponent();
        OffsetText.Text = $"Desnivel detectado: {offsetCm.ToString("0.0", CultureInfo.InvariantCulture)} cm";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        double? angle = Radio45.IsChecked == true ? 45.0 : Radio90.IsChecked == true ? 90.0 : null;
        Result = new JoinOffsetChoice { AngleDeg = angle };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
