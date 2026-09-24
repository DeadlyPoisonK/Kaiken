using System.Globalization;
using System.Windows;

namespace Kaiken;

public class JoinOffsetChoice
{
    public bool Use45;
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
        Result = new JoinOffsetChoice { Use45 = Radio45.IsChecked == true };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
