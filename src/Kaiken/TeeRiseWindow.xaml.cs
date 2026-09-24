using System.Globalization;
using System.Windows;

namespace Kaiken;

/// <summary>Valores elegidos por el usuario tras revisar la sugerencia (puede haberlos editado).</summary>
public class TeeRiseChoice
{
    public bool HasReduction;
    public double RiseCm;
    public double BigStubCm;
    public double SmallStubCm;
    /// <summary>true = el ramal nuevo apunta hacia abajo (-Z); false (default) = hacia arriba (+Z).</summary>
    public bool Downward;
}

public partial class TeeRiseWindow : Window
{
    public TeeRiseChoice Result { get; private set; } = new();
    private readonly bool _hasReduction;

    /// <param name="s">Sugerencia calculada por LiveBridgeOperations.SuggestTeeRiseCore.</param>
    /// <param name="multiInfo">Texto extra para selección múltiple (vacío si es 1 sola Te).</param>
    /// <param name="teeCount">Cantidad de Te a procesar.</param>
    internal TeeRiseWindow(LiveBridgeOperations.TeeRiseSuggestion s, string multiInfo = "", int teeCount = 1)
    {
        InitializeComponent();
        _hasReduction = s.HasReduction;

        string header = teeCount > 1 ? multiInfo : "";

        InfoText.Text = header + (s.HasReduction
            ? $"Con reducción. La Te ocupa ≈{s.TeeBranchStandoffCm:F1} cm, la Transición ≈{s.TransitionBigSideCm + s.TransitionSmallSideCm:F1} cm, " +
              $"el codo final ≈{s.ElbowStandoffCm:F1} cm. Valores sugeridos = mínimo medido + 1 cm, sin inflar."
            : $"Sin reducción. La Te ocupa ≈{s.TeeBranchStandoffCm:F1} cm, el codo final ≈{s.ElbowStandoffCm:F1} cm. " +
              "Sugerido = mínimo medido + 1 cm, sin inflar.");

        SimplePanel.Visibility = s.HasReduction ? Visibility.Collapsed : Visibility.Visible;
        ReductionPanel.Visibility = s.HasReduction ? Visibility.Visible : Visibility.Collapsed;

        RiseBox.Text = Fmt(s.SuggestedRiseCm);
        BigStubBox.Text = Fmt(s.SuggestedBigStubCm);
        SmallStubBox.Text = Fmt(s.SuggestedSmallStubCm);

        // Actualizar título si es lote
        if (teeCount > 1)
            Title = $"Rou-T — {teeCount} Te seleccionadas";
    }

    private static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static double Num(string s, double def) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = new TeeRiseChoice
        {
            HasReduction = _hasReduction,
            RiseCm = Num(RiseBox.Text, 12),
            BigStubCm = Num(BigStubBox.Text, 10),
            SmallStubCm = Num(SmallStubBox.Text, 5),
            Downward = DownCheck.IsChecked == true,
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
