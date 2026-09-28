using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

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
    /// <summary>Solo si el ramal era vertical: dirección elegida (sentido contrario o un lado). Null = usar Downward.</summary>
    public Autodesk.Revit.DB.XYZ? Direction;
    /// <summary>Texto de la opción elegida, para el resumen final.</summary>
    public string DirectionLabel = "";
}

/// <summary>Una dirección posible para el ramal nuevo, con su texto según la vista activa.</summary>
public class TeeDirectionOption
{
    public string Label = "";
    public Autodesk.Revit.DB.XYZ Dir = Autodesk.Revit.DB.XYZ.BasisZ;
}

public partial class TeeRiseWindow : Window
{
    public TeeRiseChoice Result { get; private set; } = new();
    private readonly bool _hasReduction;
    private readonly List<(RadioButton rb, TeeDirectionOption opt)> _dirButtons = new();

    /// <param name="s">Sugerencia calculada por LiveBridgeOperations.SuggestTeeRiseCore.</param>
    /// <param name="multiInfo">Texto extra para selección múltiple (vacío si es 1 sola Te).</param>
    /// <param name="teeCount">Cantidad de Te a procesar.</param>
    /// <param name="dirOptions">Si el ramal actual es vertical: las opciones de dirección (se reemplaza el check "hacia abajo"). Null si es horizontal.</param>
    /// <param name="dirHeader">Texto sobre las opciones (p. ej. "El ramal hoy baja. Dejarlo:").</param>
    internal TeeRiseWindow(LiveBridgeOperations.TeeRiseSuggestion s, string multiInfo = "", int teeCount = 1,
        IList<TeeDirectionOption>? dirOptions = null, string dirHeader = "")
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

        if (dirOptions != null && dirOptions.Count > 0)
        {
            DownCheck.Visibility = Visibility.Collapsed;
            DirPanel.Visibility = Visibility.Visible;
            DirHeader.Text = dirHeader;
            for (int i = 0; i < dirOptions.Count; i++)
            {
                var rb = new RadioButton
                {
                    Content = dirOptions[i].Label,
                    GroupName = "TeeDir",
                    IsChecked = i == 0,
                    Margin = new Thickness(0, 2, 0, 2),
                };
                DirOptions.Children.Add(rb);
                _dirButtons.Add((rb, dirOptions[i]));
            }
            FooterText.Text = "El troncal no se toca. Se rehace la Te, se borra el tramo y el codo viejos, y la tubería del ramal se reconecta sola " +
                              "(si va hacia el lado contrario queda abierta y se avisa). Todo en una sola transacción (Ctrl+Z deshace todo).";
        }

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
        foreach (var (rb, opt) in _dirButtons)
        {
            if (rb.IsChecked != true) continue;
            Result.Direction = opt.Dir;
            Result.DirectionLabel = opt.Label;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
