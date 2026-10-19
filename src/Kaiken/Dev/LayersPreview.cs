using System;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken.Dev;

/// <summary>
/// Vista previa del panel Capas con filas de muestra en memoria (no toca el modelo): sirve para
/// revisar el diseño con cualquier modelo abierto. Los botones actúan sobre el modelo real.
/// </summary>
public class LayersPreview : ContentControl, IDisposable
{
    private readonly LayersPanel _panel;

    public LayersPreview(UIApplication app)
    {
        _panel = new LayersPanel(app);
        Content = _panel;

        var rows = (ObservableCollection<LayerRow>)typeof(LayersPanel)
            .GetField("_rows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_panel)!;
        rows.Clear();
        void Add(string name, int color, bool visible, bool colorOn, bool half, int count) =>
            rows.Add(new LayerRow(new LayerInfo
            {
                Name = name, Color = LayerStore.Palette[color], Visible = visible, ColorOn = colorOn, Halftone = half, Count = count,
            }, viewSupported: true));
        Add("Rociadores", 2, true, true, false, 412);
        Add("Estructura", 9, true, false, true, 1280);
        Add("Bandejas eléctricas", 3, false, true, false, 96);
        Add("Etiquetas EP-N1", 0, true, false, false, 57);
        Add("Textos de coordinación", 4, true, true, true, 12);
        ((ListBox)_panel.FindName("LayerList")).SelectedIndex = 0;
        ((FrameworkElement)_panel.FindName("EmptyPanel")).Visibility = System.Windows.Visibility.Collapsed;
    }

    public void Dispose() => _panel.Dispose();
}
