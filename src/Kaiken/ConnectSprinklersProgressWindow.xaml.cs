using System;
using System.Windows;
using System.Windows.Threading;

namespace Kaiken;

/// <summary>
/// Ventana no modal de progreso. El trabajo corre en el hilo de Revit, así que después de
/// cada rociador se llama a <see cref="Report"/>, que deja a WPF redibujar y atender el
/// botón Detener.
/// </summary>
public partial class ConnectSprinklersProgressWindow : Window
{
    public bool CancelRequested { get; private set; }

    public ConnectSprinklersProgressWindow(int total)
    {
        InitializeComponent();
        Bar.Maximum = Math.Max(total, 1);
    }

    public void Report(int done, int total, string counts)
    {
        Bar.Value = done;
        StatusText.Text = $"Rociador {done} de {total}";
        CountsText.Text = counts;
        Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
    }

    /// <summary>Cierra la ventana al terminar (la X durante el proceso solo equivale a Detener).</summary>
    public void Finish()
    {
        _finished = true;
        Close();
    }

    private bool _finished;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true;
            Cancel_Click(this, new RoutedEventArgs());
        }
        base.OnClosing(e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelRequested = true;
        CancelButton.IsEnabled = false;
        StatusText.Text = "Deteniendo…";
    }
}
